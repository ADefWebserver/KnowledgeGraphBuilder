using System.Net;
using System.Text.Json.Nodes;
using KnowledgeGraphBuilder.Shared.Chat;
using KnowledgeGraphBuilder.Shared.Knowledge;
using KnowledgeGraphBuilder.Shared.Llm;
using KnowledgeGraphBuilder.Shared.Settings;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using static KnowledgeGraphBuilder.Tests.ScriptedChatClient;

namespace KnowledgeGraphBuilder.Tests;

public sealed class ChatEngineTests : IDisposable
{
    readonly WorkspaceFixture _fx = new();
    readonly ScriptedChatClient _client = new();
    readonly ChatTurnContextAccessor _accessor = new();

    public void Dispose() => _fx.Dispose();

    ChatEngine CreateEngine(ChatOptionsConfig? config = null, IChatClientProvider? provider = null)
    {
        config ??= new ChatOptionsConfig();
        return new ChatEngine(provider ?? new FakeChatClientProvider(_client), _fx.Workspace,
            new KnowledgeTools(_fx.Workspace, config, _accessor), _accessor, config, NullLogger<ChatEngine>.Instance);
    }

    static List<ApprovalDecision> ApproveAll(ChatEngine engine) =>
        engine.PendingRequestIds.Select(id => new ApprovalDecision(id, true)).ToList();

    static string ResultText(FunctionResultContent r) => r.Result?.ToString() ?? "";

    static FunctionResultContent ResultFor(IEnumerable<ChatMessage> messages, string callId) =>
        Assert.Single(messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>(), r => r.CallId == callId);

    /// <summary>
    /// The history a provider receives must be acceptable: no approval content, unique call ids, and every
    /// tool call answered by a result in the very next (user) turn once mapped to the Anthropic format.
    /// </summary>
    static void AssertProviderAcceptable(IReadOnlyList<ChatMessage> messages)
    {
        var contents = messages.SelectMany(m => m.Contents).ToList();
        Assert.DoesNotContain(contents, c => c is ToolApprovalRequestContent or ToolApprovalResponseContent);

        var calls = contents.OfType<FunctionCallContent>().Select(c => c.CallId).ToList();
        Assert.Equal(calls.Count, calls.Distinct().Count());
        var results = contents.OfType<FunctionResultContent>().Select(r => r.CallId).ToList();
        Assert.Equal(results.Count, results.Distinct().Count());
        Assert.Equal(calls.OrderBy(c => c), results.OrderBy(r => r));

        var body = AnthropicChatClient.BuildRequest(messages, null, "m", stream: true);
        var turns = body["messages"]!.AsArray();
        for (var i = 0; i < turns.Count; i++)
        {
            var toolUses = turns[i]!["content"]!.AsArray().Where(b => (string?)b!["type"] == "tool_use").Select(b => (string)b!["id"]!).ToList();
            if (toolUses.Count == 0) continue;
            Assert.Equal("assistant", (string?)turns[i]!["role"]);
            Assert.True(i + 1 < turns.Count, "A tool_use is the last message.");
            Assert.Equal("user", (string?)turns[i + 1]!["role"]);
            var answered = turns[i + 1]!["content"]!.AsArray().Where(b => (string?)b!["type"] == "tool_result").Select(b => (string)b!["tool_use_id"]!).ToList();
            Assert.Equal(toolUses.OrderBy(x => x), answered.OrderBy(x => x));
        }
    }

    /// <summary>Sends one more message and checks the history the model receives.</summary>
    async Task AssertNextTurnAcceptedAsync(ChatEngine engine)
    {
        _client.EnqueueText("ok");
        var events = await engine.SendAsync("next question", default).ToListAsync();
        Assert.IsType<ChatCompleted>(events[^1]);
        AssertProviderAcceptable(_client.Requests[^1]);
    }

    [Fact]
    public async Task ReadTool_IsInvokedAutomatically_AndTheTurnContinues()
    {
        var engine = CreateEngine();
        _client.EnqueueCalls(Call("c1", "search_pages", ("query", "install")));
        _client.EnqueueText("See [[Getting Started/Install]].");

        var events = await engine.SendAsync("How do I install it?", default).ToListAsync();

        Assert.Contains(events, e => e is ToolUsed { Name: "search_pages" });
        Assert.DoesNotContain(events, e => e is ApprovalRequired or AwaitingApproval);
        Assert.Equal("See [[Getting Started/Install]].", string.Concat(events.OfType<TextDelta>().Select(t => t.Text)));
        Assert.IsType<ChatCompleted>(events[^1]);
        Assert.DoesNotContain(events, e => e is PagesChanged);

        Assert.Equal(2, _client.Requests.Count);
        Assert.Contains("Getting Started/Install.md", ResultText(ResultFor(_client.Requests[1], "c1")));
        AssertProviderAcceptable(_client.Requests[1]);
        Assert.Empty(engine.PendingRequestIds);
        await AssertNextTurnAcceptedAsync(engine);
    }

    [Fact]
    public async Task ReadPage_ReturnsTheMarkdown()
    {
        var engine = CreateEngine();
        _client.EnqueueCalls(Call("c1", "read_page", ("path", "Welcome")));
        _client.EnqueueText("done");

        await engine.SendAsync("read welcome", default).ToListAsync();

        var json = JsonNode.Parse(ResultText(ResultFor(_client.Requests[1], "c1")))!;
        Assert.Equal("Welcome.md", (string?)json["path"]);
        Assert.Contains("# Welcome", (string?)json["markdown"]);
        Assert.False((bool)json["truncated"]!);
    }

    [Fact]
    public async Task WriteTool_RequiresApproval_AndEndsTheTurn()
    {
        var engine = CreateEngine();
        _client.EnqueueCalls(Call("c1", "create_page", ("path", "Roadmap.md"), ("markdown", "# Roadmap\n")));

        var events = await engine.SendAsync("Create a roadmap", default).ToListAsync();

        var approval = Assert.Single(events.OfType<ApprovalRequired>());
        Assert.Equal("create_page", approval.Proposal.Tool);
        Assert.Equal("Roadmap.md", approval.Proposal.Path);
        Assert.Null(approval.Proposal.OldMarkdown);
        Assert.Equal("# Roadmap\n", approval.Proposal.NewMarkdown);
        Assert.Equal(0, approval.Proposal.ExpectedModifiedTicks);

        var awaiting = Assert.IsType<AwaitingApproval>(events[^1]);
        Assert.Equal([approval.RequestId], awaiting.RequestIds);
        Assert.Equal([approval.RequestId], engine.PendingRequestIds);
        Assert.DoesNotContain(events, e => e is ChatCompleted or ChatError);

        Assert.Single(_client.Requests);
        Assert.False(await _fx.Workspace.ExistsAsync("Roadmap.md"));
    }

    [Fact]
    public async Task Approve_WritesThePage_RaisesPagesChanged_ThenCompletes()
    {
        var engine = CreateEngine();
        var changes = new List<KnowledgeChange>();
        _fx.Workspace.Changed += changes.Add;
        _client.EnqueueCalls(Call("c1", "create_page", ("path", "Roadmap.md"), ("markdown", "# Roadmap\n")));
        _client.EnqueueText("Created [[Roadmap]].");

        var first = await engine.SendAsync("Create a roadmap", default).ToListAsync();
        var requestId = first.OfType<ApprovalRequired>().Single().RequestId;

        var events = await engine.ResolveApprovalsAsync([new ApprovalDecision(requestId, true)], default).ToListAsync();

        var outcome = Assert.Single(events.OfType<WriteOutcome>());
        Assert.Equal(requestId, outcome.RequestId);
        Assert.True(outcome.Applied);
        var pagesChanged = Assert.Single(events.OfType<PagesChanged>());
        Assert.Equal([new KnowledgeChange(ChangeKind.Created, "Roadmap.md")], pagesChanged.Changes);
        Assert.IsType<ChatCompleted>(events[^1]);
        Assert.True(events.IndexOf(pagesChanged) < events.Count - 1);

        var page = await _fx.Workspace.ReadPageAsync("Roadmap.md");
        Assert.Equal("# Roadmap\n", page.Markdown);
        Assert.Contains(new KnowledgeChange(ChangeKind.Created, "Roadmap.md"), changes);
        Assert.NotNull(await _fx.Workspace.Graph.GetPageInfoAsync("Roadmap.md"));

        Assert.Equal(2, _client.Requests.Count);
        Assert.Contains("Created", ResultText(ResultFor(_client.Requests[1], "c1")));
        AssertProviderAcceptable(_client.Requests[1]);
        Assert.Empty(engine.PendingRequestIds);
        await AssertNextTurnAcceptedAsync(engine);
    }

    [Fact]
    public async Task Reject_WritesNothing_AndTheModelSeesTheRejection()
    {
        var engine = CreateEngine();
        _client.EnqueueCalls(Call("c1", "create_page", ("path", "Roadmap.md"), ("markdown", "# Roadmap\n")));
        _client.EnqueueText("OK, I won't.");

        var first = await engine.SendAsync("Create a roadmap", default).ToListAsync();
        var requestId = first.OfType<ApprovalRequired>().Single().RequestId;

        var events = await engine.ResolveApprovalsAsync([new ApprovalDecision(requestId, false, "Not now")], default).ToListAsync();

        Assert.False(await _fx.Workspace.ExistsAsync("Roadmap.md"));
        Assert.DoesNotContain(events, e => e is PagesChanged or WriteOutcome);
        Assert.IsType<ChatCompleted>(events[^1]);

        var result = ResultText(ResultFor(_client.Requests[1], "c1"));
        Assert.Contains("reject", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Not now", result);
        AssertProviderAcceptable(_client.Requests[1]);
        await AssertNextTurnAcceptedAsync(engine);
    }

    [Fact]
    public async Task MixedReadAndWrite_InOneResponse_OnlyTheWriteNeedsApproval()
    {
        var engine = CreateEngine();
        _client.EnqueueCalls(
            Call("c1", "search_pages", ("query", "install")),
            Call("c2", "create_page", ("path", "Roadmap.md"), ("markdown", "# Roadmap\n")));
        _client.EnqueueText("Done.");

        var first = await engine.SendAsync("Search and create", default).ToListAsync();

        Assert.Contains(first, e => e is ToolUsed { Name: "search_pages" });
        var approval = Assert.Single(first.OfType<ApprovalRequired>());
        Assert.Equal("Roadmap.md", approval.Proposal.Path);
        Assert.IsType<AwaitingApproval>(first[^1]);
        Assert.Single(_client.Requests);

        var events = await engine.ResolveApprovalsAsync(ApproveAll(engine), default).ToListAsync();

        Assert.IsType<ChatCompleted>(events[^1]);
        Assert.True(await _fx.Workspace.ExistsAsync("Roadmap.md"));
        var request = _client.Requests[1];
        Assert.Contains("Getting Started/Install.md", ResultText(ResultFor(request, "c1")));
        Assert.Contains("Created", ResultText(ResultFor(request, "c2")));
        AssertProviderAcceptable(request);
        await AssertNextTurnAcceptedAsync(engine);
    }

    [Fact]
    public async Task TwoWrites_InOneResponse_NeedADecisionEach()
    {
        var engine = CreateEngine();
        _client.EnqueueCalls(
            Call("c1", "create_page", ("path", "A.md"), ("markdown", "# A\n")),
            Call("c2", "create_page", ("path", "B.md"), ("markdown", "# B\n")));
        _client.EnqueueText("Done.");

        var first = await engine.SendAsync("Create two pages", default).ToListAsync();
        var approvals = first.OfType<ApprovalRequired>().ToList();
        Assert.Equal(2, approvals.Count);
        Assert.Equal(approvals.Select(a => a.RequestId), Assert.IsType<AwaitingApproval>(first[^1]).RequestIds);

        // A partial decision is refused and nothing changes.
        var partial = await engine.ResolveApprovalsAsync([new ApprovalDecision(approvals[0].RequestId, true)], default).ToListAsync();
        Assert.IsType<ChatError>(Assert.Single(partial));
        Assert.Equal(2, engine.PendingRequestIds.Count);
        Assert.Single(_client.Requests);

        var byPath = approvals.ToDictionary(a => a.Proposal.Path, a => a.RequestId);
        var events = await engine.ResolveApprovalsAsync(
            [new ApprovalDecision(byPath["A.md"], true), new ApprovalDecision(byPath["B.md"], false)], default).ToListAsync();

        Assert.True(await _fx.Workspace.ExistsAsync("A.md"));
        Assert.False(await _fx.Workspace.ExistsAsync("B.md"));
        Assert.Equal([new KnowledgeChange(ChangeKind.Created, "A.md")], Assert.Single(events.OfType<PagesChanged>()).Changes);
        Assert.Equal(byPath["A.md"], Assert.Single(events.OfType<WriteOutcome>()).RequestId);
        Assert.IsType<ChatCompleted>(events[^1]);
        AssertProviderAcceptable(_client.Requests[1]);
        await AssertNextTurnAcceptedAsync(engine);
    }

    [Fact]
    public async Task TwoWrites_InSuccessiveResponses_AreEachAppliedOnce()
    {
        var engine = CreateEngine();
        _client.EnqueueCalls(Call("c1", "create_page", ("path", "A.md"), ("markdown", "# A\n")));
        _client.EnqueueCalls(Call("c2", "create_page", ("path", "B.md"), ("markdown", "# B\n")));
        _client.EnqueueText("Done.");

        await engine.SendAsync("Create two pages", default).ToListAsync();
        var second = await engine.ResolveApprovalsAsync(ApproveAll(engine), default).ToListAsync();
        Assert.True(Assert.Single(second.OfType<WriteOutcome>()).Applied);
        Assert.IsType<AwaitingApproval>(second[^1]);

        var third = await engine.ResolveApprovalsAsync(ApproveAll(engine), default).ToListAsync();

        // The first write must not run again when the second is approved.
        var outcome = Assert.Single(third.OfType<WriteOutcome>());
        Assert.True(outcome.Applied);
        Assert.IsType<ChatCompleted>(third[^1]);
        Assert.Equal(
            [new KnowledgeChange(ChangeKind.Created, "A.md"), new KnowledgeChange(ChangeKind.Created, "B.md")],
            Assert.Single(third.OfType<PagesChanged>()).Changes);
        Assert.True(await _fx.Workspace.ExistsAsync("A.md"));
        Assert.True(await _fx.Workspace.ExistsAsync("B.md"));
        AssertProviderAcceptable(_client.Requests[2]);
        await AssertNextTurnAcceptedAsync(engine);
    }

    [Fact]
    public async Task UpdatePage_ProposalCarriesCurrentTicks_AndApprovalApplies()
    {
        var engine = CreateEngine();
        var before = await _fx.Workspace.ReadPageAsync("Welcome.md");
        _client.EnqueueCalls(Call("c1", "update_page", ("path", "Welcome.md"), ("markdown", "# Welcome\n\nUpdated.\n")));
        _client.EnqueueText("Updated.");

        var first = await engine.SendAsync("Update welcome", default).ToListAsync();
        var proposal = first.OfType<ApprovalRequired>().Single().Proposal;
        Assert.Equal(before.ModifiedTicks, proposal.ExpectedModifiedTicks);
        Assert.Equal(before.Markdown, proposal.OldMarkdown);

        var events = await engine.ResolveApprovalsAsync(ApproveAll(engine), default).ToListAsync();

        Assert.True(Assert.Single(events.OfType<WriteOutcome>()).Applied);
        Assert.Equal([new KnowledgeChange(ChangeKind.Updated, "Welcome.md")], Assert.Single(events.OfType<PagesChanged>()).Changes);
        Assert.Equal("# Welcome\n\nUpdated.\n", (await _fx.Workspace.ReadPageAsync("Welcome.md")).Markdown);
    }

    [Fact]
    public async Task Conflict_WhenThePageChangesAfterTheProposal_NothingIsOverwritten()
    {
        var engine = CreateEngine();
        _client.EnqueueCalls(Call("c1", "update_page", ("path", "Welcome.md"), ("markdown", "# From the model\n")));
        _client.EnqueueText("It changed.");

        await engine.SendAsync("Update welcome", default).ToListAsync();
        var current = await _fx.Workspace.ReadPageAsync("Welcome.md");
        var save = await _fx.Workspace.SavePageAsync("Welcome.md", "# Edited by the user\n", current.ModifiedTicks);
        Assert.Equal(SaveStatus.Saved, save.Status);

        var events = await engine.ResolveApprovalsAsync(ApproveAll(engine), default).ToListAsync();

        var outcome = Assert.Single(events.OfType<WriteOutcome>());
        Assert.False(outcome.Applied);
        Assert.Equal("Conflict", outcome.Error);
        Assert.DoesNotContain(events, e => e is PagesChanged);
        Assert.IsType<ChatCompleted>(events[^1]);
        Assert.Equal("# Edited by the user\n", (await _fx.Workspace.ReadPageAsync("Welcome.md")).Markdown);
        Assert.Contains("Conflict", ResultText(ResultFor(_client.Requests[1], "c1")));
    }

    [Fact]
    public async Task CreatePage_WhenThePageAppearsAfterTheProposal_ReportsAlreadyExists()
    {
        var engine = CreateEngine();
        _client.EnqueueCalls(Call("c1", "create_page", ("path", "Roadmap.md"), ("markdown", "# From the model\n")));
        _client.EnqueueText("It exists.");

        await engine.SendAsync("Create a roadmap", default).ToListAsync();
        await _fx.Workspace.SavePageAsync("Roadmap.md", "# Mine\n", 0);

        var events = await engine.ResolveApprovalsAsync(ApproveAll(engine), default).ToListAsync();

        var outcome = Assert.Single(events.OfType<WriteOutcome>());
        Assert.False(outcome.Applied);
        Assert.Equal("AlreadyExists", outcome.Error);
        Assert.Equal("# Mine\n", (await _fx.Workspace.ReadPageAsync("Roadmap.md")).Markdown);
    }

    [Fact]
    public async Task NewMessage_WhilePending_SupersedesThePendingWrite()
    {
        var engine = CreateEngine();
        _client.EnqueueCalls(Call("c1", "create_page", ("path", "Roadmap.md"), ("markdown", "# Roadmap\n")));
        _client.EnqueueText("Fine.");

        await engine.SendAsync("Create a roadmap", default).ToListAsync();
        Assert.Single(engine.PendingRequestIds);

        var events = await engine.SendAsync("Actually, never mind", default).ToListAsync();

        Assert.IsType<ChatCompleted>(events[^1]);
        Assert.Empty(engine.PendingRequestIds);
        Assert.False(await _fx.Workspace.ExistsAsync("Roadmap.md"));

        var request = _client.Requests[1];
        var result = ResultText(ResultFor(request, "c1"));
        Assert.Contains("Superseded", result);
        Assert.Equal("Actually, never mind", request[^1].Text);
        AssertProviderAcceptable(request);

        // Resolving the superseded request afterwards is refused.
        var late = await engine.ResolveApprovalsAsync([new ApprovalDecision("ficc_c1", true)], default).ToListAsync();
        Assert.IsType<ChatError>(Assert.Single(late));
        Assert.False(await _fx.Workspace.ExistsAsync("Roadmap.md"));
        await AssertNextTurnAcceptedAsync(engine);
    }

    [Fact]
    public async Task CancelPending_ThenNewMessage_RejectsAsCancelled()
    {
        var engine = CreateEngine();
        _client.EnqueueCalls(Call("c1", "create_page", ("path", "Roadmap.md"), ("markdown", "# Roadmap\n")));
        _client.EnqueueText("OK.");

        await engine.SendAsync("Create a roadmap", default).ToListAsync();
        engine.CancelPending();
        Assert.Empty(engine.PendingRequestIds);

        await engine.SendAsync("Something else", default).ToListAsync();

        Assert.False(await _fx.Workspace.ExistsAsync("Roadmap.md"));
        Assert.Contains("Cancelled by user", ResultText(ResultFor(_client.Requests[1], "c1")));
        AssertProviderAcceptable(_client.Requests[1]);
    }

    [Fact]
    public async Task Reset_ClearsHistoryAndPending()
    {
        var engine = CreateEngine();
        _client.EnqueueCalls(Call("c1", "create_page", ("path", "Roadmap.md"), ("markdown", "# Roadmap\n")));
        await engine.SendAsync("Create a roadmap", default).ToListAsync();

        engine.Reset();

        Assert.Empty(engine.PendingRequestIds);
        Assert.Empty(engine.History);
        Assert.Null(_accessor.Current);

        _client.EnqueueText("Hello.");
        var events = await engine.SendAsync("Hi", default).ToListAsync();
        Assert.IsType<ChatCompleted>(events[^1]);
        var request = _client.Requests[^1];
        Assert.Equal(2, request.Count);
        Assert.Equal(ChatRole.System, request[0].Role);
        Assert.Equal("Hi", request[1].Text);
        Assert.False(await _fx.Workspace.ExistsAsync("Roadmap.md"));
    }

    [Fact]
    public async Task MissingKey_YieldsChatError_BeforeAnyModelCall()
    {
        var provider = new FakeChatClientProvider(null, "Add an API key in Settings");
        var engine = CreateEngine(provider: provider);

        var events = await engine.SendAsync("Hi", default).ToListAsync();

        var error = Assert.IsType<ChatError>(Assert.Single(events));
        Assert.Equal("Add an API key in Settings", error.Message);
        Assert.False(error.Retryable);
        Assert.Empty(engine.History);
        Assert.Empty(_client.Requests);
    }

    [Fact]
    public async Task ProviderException_IsRetryable_AndRollsBackToTheUserMessage()
    {
        var engine = CreateEngine();
        _client.EnqueueFailure(new HttpRequestException("Server exploded", null, HttpStatusCode.InternalServerError), _client.Text("Partial"));

        var events = await engine.SendAsync("Hi", default).ToListAsync();

        var error = Assert.IsType<ChatError>(events[^1]);
        Assert.True(error.Retryable);
        Assert.Contains("Server exploded", error.Message);
        var only = Assert.Single(engine.History);
        Assert.Equal(ChatRole.User, only.Role);
        Assert.Equal("Hi", only.Text);
    }

    [Fact]
    public async Task ProviderException_AfterAToolRound_LeavesNoDanglingToolCall_AndRetryWorks()
    {
        var engine = CreateEngine();
        _client.EnqueueCalls(Call("c1", "search_pages", ("query", "install")));
        _client.EnqueueFailure(new HttpRequestException("Overloaded", null, HttpStatusCode.ServiceUnavailable));

        var events = await engine.SendAsync("How do I install?", default).ToListAsync();

        Assert.True(Assert.IsType<ChatError>(events[^1]).Retryable);
        Assert.Single(engine.History);
        Assert.Empty(engine.History.SelectMany(m => m.Contents).OfType<FunctionCallContent>());

        _client.EnqueueText("Here you go.");
        var retry = await engine.RetryAsync(default).ToListAsync();
        Assert.IsType<ChatCompleted>(retry[^1]);
        var request = _client.Requests[^1];
        Assert.Equal(2, request.Count);
        Assert.Equal("How do I install?", request[1].Text);
        await AssertNextTurnAcceptedAsync(engine);
    }

    [Fact]
    public async Task ProviderException_AfterAnApprovedWrite_ReportsTheChange_AndKeepsHistoryValid()
    {
        var engine = CreateEngine();
        _client.EnqueueCalls(Call("c1", "create_page", ("path", "Roadmap.md"), ("markdown", "# Roadmap\n")));
        _client.EnqueueFailure(new HttpRequestException("Overloaded", null, HttpStatusCode.ServiceUnavailable));

        await engine.SendAsync("Create a roadmap", default).ToListAsync();
        var events = await engine.ResolveApprovalsAsync(ApproveAll(engine), default).ToListAsync();

        Assert.True(await _fx.Workspace.ExistsAsync("Roadmap.md"));
        Assert.Contains(events, e => e is PagesChanged);
        Assert.True(Assert.IsType<ChatError>(events[^1]).Retryable);
        Assert.Empty(engine.PendingRequestIds);
        await AssertNextTurnAcceptedAsync(engine);
    }

    [Fact]
    public async Task Cancellation_KeepsPartialText_AndCompletes()
    {
        var engine = CreateEngine();
        _client.Enqueue(_client.Text("Hello"), _client.Text(" world"), _client.Text(" again"));
        using var cts = new CancellationTokenSource();

        var events = new List<ChatEvent>();
        await foreach (var e in engine.SendAsync("Hi", cts.Token))
        {
            events.Add(e);
            if (e is TextDelta) cts.Cancel();
        }

        Assert.IsType<ChatCompleted>(events[^1]);
        Assert.Equal(2, engine.History.Count);
        Assert.Equal(ChatRole.Assistant, engine.History[1].Role);
        Assert.StartsWith("Hello", engine.History[1].Text);
        await AssertNextTurnAcceptedAsync(engine);
    }

    [Fact]
    public async Task IterationLimit_StopsInvokingTools()
    {
        var engine = CreateEngine(new ChatOptionsConfig(MaxIterations: 2));
        var n = 0;
        _client.Fallback = () => new ScriptStep([_client.Calls(Call("loop" + Interlocked.Increment(ref n), "search_pages", ("query", "install")))]);

        var events = await engine.SendAsync("Loop forever", default).ToListAsync();

        Assert.IsType<ChatCompleted>(events[^1]);
        Assert.InRange(_client.Requests.Count, 2, 3);
        _client.Fallback = null;
        await AssertNextTurnAcceptedAsync(engine);
    }

    [Fact]
    public async Task WriteTool_CalledWithoutApproval_ReturnsAnError()
    {
        var tools = new KnowledgeTools(_fx.Workspace, new ChatOptionsConfig(), _accessor);

        var result = await tools.CreatePageAsync("Sneaky.md", "# Sneaky\n");

        Assert.Contains("error", result);
        Assert.False(await _fx.Workspace.ExistsAsync("Sneaky.md"));
    }

    [Fact]
    public async Task InvalidWritePath_IsRejectedWithoutAskingTheUser()
    {
        var engine = CreateEngine();
        _client.EnqueueCalls(Call("c1", "create_page", ("path", "../outside.md"), ("markdown", "# x\n")));
        _client.EnqueueText("That path is invalid.");

        var events = await engine.SendAsync("Create outside", default).ToListAsync();

        Assert.DoesNotContain(events, e => e is ApprovalRequired);
        Assert.IsType<ChatCompleted>(events[^1]);
        Assert.Equal(2, _client.Requests.Count);
        AssertProviderAcceptable(_client.Requests[1]);
    }
}
