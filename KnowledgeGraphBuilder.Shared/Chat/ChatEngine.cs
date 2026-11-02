using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using KnowledgeGraphBuilder.Shared.Knowledge;
using KnowledgeGraphBuilder.Shared.Settings;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace KnowledgeGraphBuilder.Shared.Chat;

/// <summary>
/// Runs chat turns locally: provider client → FunctionInvokingChatClient → <see cref="KnowledgeTools"/>.
/// Read tools are approved automatically; write tools pause the turn until the user decides (§5.7, D6).
/// </summary>
public sealed class ChatEngine : IChatOrchestrator
{
    sealed record Pending(ToolApprovalRequestContent Request, FunctionCallContent Call, WriteProposal Proposal);

    readonly IChatClientProvider _provider;
    readonly IKnowledgeWorkspace _workspace;
    readonly ChatTurnContextAccessor _accessor;
    readonly ChatOptionsConfig _config;
    readonly ILogger<ChatEngine> _log;
    readonly IList<AITool> _tools;
    readonly IHostCapabilities? _host;

    readonly List<ChatMessage> _history = [];
    readonly List<Pending> _pending = [];
    readonly List<AIContent> _held = [];
    int _turnStart;
    ChatTurnContext? _turn;

    public ChatEngine(IChatClientProvider provider, IKnowledgeWorkspace workspace, KnowledgeTools tools, ChatTurnContextAccessor accessor,
                      ChatOptionsConfig config, ILogger<ChatEngine> log, IHostCapabilities? host = null)
    {
        _provider = provider;
        _workspace = workspace;
        _accessor = accessor;
        _config = config;
        _log = log;
        _host = host;
        _tools = tools.Create();
    }

    public IReadOnlyList<ChatMessage> History => _history;

    public IReadOnlyList<string> PendingRequestIds => _pending.Select(p => p.Request.RequestId).ToList();

    public void Reset()
    {
        _history.Clear();
        _pending.Clear();
        _held.Clear();
        _turn = null;
        _accessor.Current = null;
    }

    /// <summary>Stop while approvals are pending: reject them as "Cancelled by user".</summary>
    public void CancelPending() => RejectPending("Cancelled by user");

    void RejectPending(string reason)
    {
        foreach (var p in _pending) _held.Add(p.Request.CreateResponse(false, reason));
        _pending.Clear();
    }

    public async IAsyncEnumerable<ChatEvent> SendAsync(string userText, [EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userText)) yield break;

        var client = await _provider.GetClientAsync();
        if (!client.Ok)
        {
            yield return new ChatError(client.Reason ?? "Add an API key in Settings", false);
            yield break;
        }

        if (_pending.Count > 0) RejectPending("Superseded: the user sent a new message instead of deciding");
        if (_held.Count > 0)
        {
            _history.Add(new ChatMessage(ChatRole.User, [.. _held]));
            _held.Clear();
        }

        _turnStart = _history.Count;
        _history.Add(new ChatMessage(ChatRole.User, userText.Trim()));
        _turn = new ChatTurnContext();

        await foreach (var e in RunAsync(client.Client, ct)) yield return e;
    }

    public async IAsyncEnumerable<ChatEvent> RetryAsync([EnumeratorCancellation] CancellationToken ct)
    {
        if (_pending.Count > 0 || _history.Count == 0 || _history[^1].Role != ChatRole.User) yield break;
        var client = await _provider.GetClientAsync();
        if (!client.Ok)
        {
            yield return new ChatError(client.Reason ?? "Add an API key in Settings", false);
            yield break;
        }
        _turnStart = _history.Count - 1;
        _turn = new ChatTurnContext();
        await foreach (var e in RunAsync(client.Client, ct)) yield return e;
    }

    public async IAsyncEnumerable<ChatEvent> ResolveApprovalsAsync(IReadOnlyList<ApprovalDecision> decisions, [EnumeratorCancellation] CancellationToken ct)
    {
        if (_pending.Count == 0)
        {
            yield return new ChatError("Nothing is waiting for approval.", false);
            yield break;
        }
        var byId = decisions.ToDictionary(d => d.RequestId);
        if (_pending.Any(p => !byId.ContainsKey(p.Request.RequestId)))
        {
            yield return new ChatError("Decide on every pending change first.", false);
            yield break;
        }

        var client = await _provider.GetClientAsync();
        if (!client.Ok)
        {
            yield return new ChatError(client.Reason ?? "Add an API key in Settings", false);
            yield break;
        }

        _turn ??= new ChatTurnContext();
        foreach (var p in _pending)
        {
            var d = byId[p.Request.RequestId];
            _held.Add(p.Request.CreateResponse(d.Approved, d.Approved ? d.Reason : d.Reason ?? "User rejected"));
            if (d.Approved)
            {
                _turn.Proposals[p.Call.CallId] = p.Proposal;
                _turn.RequestIds[p.Call.CallId] = p.Request.RequestId;
            }
        }
        _pending.Clear();
        _history.Add(new ChatMessage(ChatRole.User, [.. _held]));
        _held.Clear();

        await foreach (var e in RunAsync(client.Client, ct)) yield return e;
    }

    async IAsyncEnumerable<ChatEvent> RunAsync(IChatClient inner, [EnumeratorCancellation] CancellationToken ct)
    {
        var client = inner.AsBuilder()
            .UseFunctionInvocation(configure: f =>
            {
                f.MaximumIterationsPerRequest = _config.MaxIterations;
                f.IncludeDetailedErrors = false;
            })
            .Build();
        var options = new ChatOptions { Tools = _tools, ToolMode = ChatToolMode.Auto };
        var turn = _turn!;
        _accessor.Current = turn;

        while (true)
        {
            var updates = new List<ChatResponseUpdate>();
            var partial = new StringBuilder();
            Exception? failure = null;
            NormalizeHistory(final: false);   // Approvals the client already processed must not be sent again.
            var stream =client.GetStreamingResponseAsync([SystemMessage(), .. _history], options, ct).GetAsyncEnumerator(ct);
            try
            {
                while (true)
                {
                    try
                    {
                        if (!await stream.MoveNextAsync()) break;
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                        break;
                    }
                    var update = stream.Current;
                    updates.Add(update);
                    foreach (var content in update.Contents)
                    {
                        switch (content)
                        {
                            case TextContent { Text.Length: > 0 } t when update.Role != ChatRole.Tool:
                                partial.Append(t.Text);
                                yield return new TextDelta(t.Text);
                                break;
                            case FunctionCallContent call when turn.Reported.Add(call.CallId):
                                yield return new ToolUsed(call.Name, Summarize(call));
                                break;
                        }
                    }
                    foreach (var outcome in DrainOutcomes(turn)) yield return outcome;
                }
            }
            finally
            {
                await stream.DisposeAsync();
            }

            foreach (var outcome in DrainOutcomes(turn)) yield return outcome;

            if (failure is not null)
            {
                foreach (var e in Fail(failure, partial.ToString(), turn)) yield return e;
                yield break;
            }

            var response = updates.ToChatResponse();
            _history.AddRange(response.Messages);

            foreach (var request in response.Messages.SelectMany(m => m.Contents).OfType<ToolApprovalRequestContent>())
            {
                if (request.ToolCall is not FunctionCallContent call)
                {
                    _held.Add(request.CreateResponse(false, "Unsupported tool call"));
                    continue;
                }
                if (KnowledgeTools.ReadTools.Contains(call.Name) || !KnowledgeTools.WriteTools.Contains(call.Name))
                {
                    _held.Add(request.CreateResponse(true));
                    if (turn.Reported.Add(call.CallId)) yield return new ToolUsed(call.Name, Summarize(call));
                    continue;
                }

                var (proposal, error) = await BuildProposalAsync(call, ct);
                if (proposal is null)
                {
                    _held.Add(request.CreateResponse(false, error));
                    continue;
                }
                _pending.Add(new Pending(request, call, proposal));
                yield return new ApprovalRequired(request.RequestId, proposal);
            }

            if (_pending.Count > 0)
            {
                // Hold auto-approvals until the user decides, then send everything together.
                yield return new AwaitingApproval(_pending.Select(p => p.Request.RequestId).ToList());
                yield break;
            }

            if (_held.Count > 0)
            {
                _history.Add(new ChatMessage(ChatRole.User, [.. _held]));
                _held.Clear();
                continue;
            }

            NormalizeHistory();
            if (turn.Changes.Count > 0) yield return new PagesChanged([.. turn.Changes]);
            _accessor.Current = null;
            _turn = null;
            yield return new ChatCompleted();
            yield break;
        }
    }

    static IEnumerable<WriteOutcome> DrainOutcomes(ChatTurnContext turn)
    {
        if (turn.Outcomes.Count == 0) return [];
        var list = turn.Outcomes.ToList();
        turn.Outcomes.Clear();
        return list;
    }

    IEnumerable<ChatEvent> Fail(Exception failure, string partialText, ChatTurnContext turn)
    {
        // Roll back to the user message of this turn so the history never ends with a tool call that has no result.
        if (_history.Count > _turnStart + 1) _history.RemoveRange(_turnStart + 1, _history.Count - _turnStart - 1);
        _pending.Clear();
        _held.Clear();
        _accessor.Current = null;
        _turn = null;
        NormalizeHistory();

        var changes = turn.Changes.ToList();
        if (failure is OperationCanceledException)
        {
            if (partialText.Length > 0) _history.Add(new ChatMessage(ChatRole.Assistant, partialText + " …"));
            if (changes.Count > 0) yield return new PagesChanged(changes);
            yield return new ChatCompleted();
            yield break;
        }

        _log.LogWarning("Chat request failed: {Type}", failure.GetType().Name);
        if (changes.Count > 0) yield return new PagesChanged(changes);
        yield return new ChatError(AppSettingsService.DescribeError(failure, null, _host?.IsBrowser ?? false), Retryable: true);
    }

    /// <summary>
    /// Replaces approval requests with their tool calls and drops approval responses, once the calls have results.
    /// Every tool call keeps a result. With <paramref name="final"/> false (between rounds of one turn), approvals
    /// that are still unresolved are kept so the function-invoking client can process them.
    /// </summary>
    void NormalizeHistory(bool final = true)
    {
        var calls = _history.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Select(c => c.CallId).ToHashSet();
        var results = _history.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.CallId).ToHashSet();

        for (var i = _history.Count - 1; i >= 0; i--)
        {
            var m = _history[i];
            if (!m.Contents.Any(c => c is ToolApprovalRequestContent or ToolApprovalResponseContent)) continue;
            var contents = new List<AIContent>();
            foreach (var c in m.Contents)
            {
                switch (c)
                {
                    case ToolApprovalResponseContent resp:
                        if (!final && !(resp.ToolCall is FunctionCallContent rc && results.Contains(rc.CallId))) contents.Add(c);
                        break;
                    case ToolApprovalRequestContent req:
                        if (req.ToolCall is FunctionCallContent fc && results.Contains(fc.CallId))
                        {
                            if (calls.Add(fc.CallId)) contents.Add(fc);
                        }
                        else if (!final) contents.Add(c);
                        break;
                    default:
                        contents.Add(c);
                        break;
                }
            }
            if (contents.Count == 0)
            {
                _history.RemoveAt(i);
                if (i < _turnStart) _turnStart--;
            }
            else _history[i] = new ChatMessage(m.Role, contents) { MessageId = m.MessageId, AuthorName = m.AuthorName };
        }

        if (!final) return;

        // Any call still without a result gets one, so providers accept the history.
        var orphans = _history.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Where(c => !results.Contains(c.CallId)).ToList();
        if (orphans.Count > 0)
        {
            var lastCallIndex = _history.FindLastIndex(m => m.Contents.OfType<FunctionCallContent>().Any());
            _history.Insert(lastCallIndex + 1, new ChatMessage(ChatRole.Tool,
                orphans.Select(o => (AIContent)new FunctionResultContent(o.CallId, "{\"error\":\"Not run\"}")).ToList()));
        }
    }

    ChatMessage SystemMessage() => new(ChatRole.System, ChatPrompts.System(DateOnly.FromDateTime(DateTime.Now)));

    async Task<(WriteProposal? Proposal, string? Error)> BuildProposalAsync(FunctionCallContent call, CancellationToken ct)
    {
        var path = Arg(call, "path")?.Trim().TrimStart('/');
        if (call.Name == "create_folder")
        {
            if (!KnowledgePath.TryParse(path, isPage: false, out var folder, out var folderError) || folder.IsRoot)
                return (null, folderError ?? "A folder path is required.");
            return (new WriteProposal(call.Name, folder.Value, null, "", null), null);
        }

        if (!KnowledgePath.TryParse(path, isPage: true, out var page, out var error)) return (null, error);
        var markdown = (Arg(call, "markdown") ?? "").Replace("\r\n", "\n");

        try
        {
            var current = await _workspace.ReadPageAsync(page.Value, ct);
            return call.Name == "create_page"
                ? (new WriteProposal(call.Name, page.Value, current.Markdown, markdown, 0), null)
                : (new WriteProposal(call.Name, page.Value, current.Markdown, markdown, current.ModifiedTicks), null);
        }
        catch (KnowledgeNotFoundException)
        {
            return (new WriteProposal(call.Name, page.Value, null, markdown, 0), null);
        }
    }

    static string? Arg(FunctionCallContent call, string name)
    {
        if (call.Arguments is null || !call.Arguments.TryGetValue(name, out var value) || value is null) return null;
        return value switch
        {
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
            JsonElement e => e.GetRawText(),
            _ => value.ToString(),
        };
    }

    static string Summarize(FunctionCallContent call)
    {
        if (call.Arguments is null || call.Arguments.Count == 0) return "";
        return string.Join(", ", call.Arguments
            .Where(a => a.Key != "markdown")
            .Select(a => $"{a.Key}={Truncate(a.Value switch { JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(), var v => v?.ToString() })}"));
    }

    static string Truncate(string? s) => s is null ? "" : s.Length > 60 ? s[..60] + "…" : s;
}
