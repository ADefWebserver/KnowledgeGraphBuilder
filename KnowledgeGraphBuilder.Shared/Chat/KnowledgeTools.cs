using System.ComponentModel;
using System.Text.Json;
using KnowledgeGraphBuilder.Shared.Knowledge;
using KnowledgeGraphBuilder.Shared.Markdown;
using Microsoft.Extensions.AI;

namespace KnowledgeGraphBuilder.Shared.Chat;

/// <summary>Per-turn state shared by the engine and the write tools.</summary>
public sealed class ChatTurnContext
{
    /// <summary>Approved proposals by tool call id.</summary>
    public Dictionary<string, WriteProposal> Proposals { get; } = [];

    /// <summary>Request id by tool call id, for reporting outcomes.</summary>
    public Dictionary<string, string> RequestIds { get; } = [];

    public List<KnowledgeChange> Changes { get; } = [];
    public List<WriteOutcome> Outcomes { get; } = [];

    /// <summary>Tool call ids already reported as <see cref="ToolUsed"/>.</summary>
    public HashSet<string> Reported { get; } = [];
}

public sealed class ChatTurnContextAccessor
{
    public ChatTurnContext? Current { get; set; }
}

/// <summary>The tools the model can call. Writes need user approval (§6.3).</summary>
public sealed class KnowledgeTools(IKnowledgeWorkspace workspace, ChatOptionsConfig config, ChatTurnContextAccessor turn)
{
    public static readonly IReadOnlySet<string> ReadTools = new HashSet<string> { "search_pages", "read_page", "list_folder", "get_links" };
    public static readonly IReadOnlySet<string> WriteTools = new HashSet<string> { "create_page", "update_page", "create_folder" };

    public IList<AITool> Create() =>
    [
        AIFunctionFactory.Create(SearchPagesAsync, "search_pages", "Keyword search over page titles, aliases, tags, paths and text. Returns the best matches with snippets."),
        AIFunctionFactory.Create(ReadPageAsync, "read_page", "Reads a page's full Markdown, including front-matter."),
        AIFunctionFactory.Create(ListFolderAsync, "list_folder", "Lists the pages and folders in a folder. Use \"\" for the root."),
        AIFunctionFactory.Create(GetLinksAsync, "get_links", "Lists a page's outgoing links and backlinks."),
        new ApprovalRequiredAIFunction(AIFunctionFactory.Create(CreatePageAsync, "create_page", "Creates a new page. The user must approve it. Fails if the page exists.")),
        new ApprovalRequiredAIFunction(AIFunctionFactory.Create(UpdatePageAsync, "update_page", "Replaces the full Markdown of an existing page. The user must approve it. Read the page first.")),
        new ApprovalRequiredAIFunction(AIFunctionFactory.Create(CreateFolderAsync, "create_folder", "Creates a folder. The user must approve it.")),
    ];

    static string Json(object value) => JsonSerializer.Serialize(value, AIJsonUtilities.DefaultOptions);

    static string Error(string message) => Json(new { error = message });

    static bool TryPage(string path, out KnowledgePath page, [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? error) =>
        KnowledgePath.TryParse(path?.Trim().TrimStart('/'), isPage: true, out page, out error);

    public async Task<string> SearchPagesAsync(
        [Description("Keywords to search for")] string query,
        [Description("Maximum results, 1 to 20")] int top = 8,
        CancellationToken ct = default)
    {
        var hits = await workspace.Graph.SearchAsync(query, Math.Clamp(top, 1, 20), ct);
        return Json(hits.Select(h => new { path = h.Path, title = h.Title, snippet = h.Snippet }));
    }

    public async Task<string> ReadPageAsync([Description("Root-relative page path, for example \"Getting Started/Install.md\"")] string path, CancellationToken ct = default)
    {
        if (!TryPage(path, out var page, out var error)) return Error(error);
        try
        {
            var content = await workspace.ReadPageAsync(page.Value, ct);
            var info = await workspace.Graph.GetPageInfoAsync(page.Value, ct);
            var truncated = content.Markdown.Length > config.MaxToolResultChars;
            var markdown = truncated ? content.Markdown[..config.MaxToolResultChars] : content.Markdown;
            return Json(new { path = content.Path, title = info?.Title ?? MarkdownPageParser.DefaultTitle(content.Path), markdown, truncated });
        }
        catch (KnowledgeNotFoundException)
        {
            var resolved = await workspace.Graph.ResolveAsync("", KnowledgePath.StripExtension(page.Value), ct);
            return resolved is null ? Error($"No page at {page.Value}") : await ReadPageAsync(resolved, ct);
        }
    }

    public async Task<string> ListFolderAsync([Description("Root-relative folder path; \"\" is the root")] string path = "", CancellationToken ct = default)
    {
        if (!KnowledgePath.TryParse(path?.Trim().Trim('/'), isPage: false, out var folder, out var error)) return Error(error);
        try
        {
            var nodes = await workspace.GetChildrenAsync(folder.Value, ct);
            return Json(nodes.Select(n => new { name = n.Name, path = n.Path, isFolder = n.IsFolder }));
        }
        catch (KnowledgeNotFoundException) { return Error($"No folder at {folder.Value}"); }
    }

    public async Task<string> GetLinksAsync([Description("Root-relative page path")] string path, CancellationToken ct = default)
    {
        if (!TryPage(path, out var page, out var error)) return Error(error);
        var links = await workspace.Graph.GetLinksAsync(page.Value, ct);
        return Json(new
        {
            outgoing = links.Outgoing.Select(l => new { target = l.RawTarget, resolved = l.ResolvedPath, line = l.Line }),
            backlinks = links.Backlinks.Select(l => new { source = l.SourcePath, line = l.Line }),
        });
    }

    public Task<string> CreatePageAsync(
        [Description("Root-relative page path ending in .md")] string path,
        [Description("The full Markdown, including front-matter")] string markdown,
        CancellationToken ct = default) => WriteAsync("create_page", ct);

    public Task<string> UpdatePageAsync(
        [Description("Root-relative page path")] string path,
        [Description("The complete new Markdown for the page, including front-matter")] string markdown,
        CancellationToken ct = default) => WriteAsync("update_page", ct);

    public Task<string> CreateFolderAsync([Description("Root-relative folder path")] string path, CancellationToken ct = default) =>
        WriteAsync("create_folder", ct);

    /// <summary>Writes exactly what the user approved: the proposal is looked up by the current call id.</summary>
    async Task<string> WriteAsync(string tool, CancellationToken ct)
    {
        var callId = FunctionInvokingChatClient.CurrentContext?.CallContent.CallId;
        var context = turn.Current;
        if (callId is null || context is null || !context.Proposals.TryGetValue(callId, out var proposal) || proposal.Tool != tool)
            return Error("This change wasn't approved by the user.");

        context.RequestIds.TryGetValue(callId, out var requestId);
        void Outcome(bool applied, string? error)
        {
            if (requestId is not null) context.Outcomes.Add(new WriteOutcome(requestId, applied, error));
        }

        try
        {
            if (tool == "create_folder")
            {
                await workspace.CreateFolderAsync(proposal.Path, ct);
                context.Changes.Add(new KnowledgeChange(ChangeKind.FolderCreated, proposal.Path));
                Outcome(true, null);
                return Json(new { path = proposal.Path });
            }

            var result = await workspace.SavePageAsync(proposal.Path, proposal.NewMarkdown, proposal.ExpectedModifiedTicks, ct);
            switch (result.Status)
            {
                case SaveStatus.Saved or SaveStatus.Created:
                    context.Changes.Add(new KnowledgeChange(result.Status == SaveStatus.Created ? ChangeKind.Created : ChangeKind.Updated, proposal.Path));
                    Outcome(true, null);
                    return Json(new { path = proposal.Path, status = result.Status.ToString() });
                case SaveStatus.AlreadyExists:
                    Outcome(false, "AlreadyExists");
                    return Error("AlreadyExists: the page exists. Read it and use update_page instead.");
                default:
                    Outcome(false, "Conflict");
                    return Error("Conflict: the page changed after the change was proposed.");
            }
        }
        catch (StorageQuotaExceededException)
        {
            Outcome(false, "Storage full");
            return Error("Storage full");
        }
        catch (KnowledgeAlreadyExistsException)
        {
            Outcome(false, "AlreadyExists");
            return Error("AlreadyExists");
        }
        catch (KnowledgeException ex)
        {
            Outcome(false, ex.Message);
            return Error(ex.Message);
        }
    }
}

public static class ChatPrompts
{
    public static string System(DateOnly today) => $"""
        You are the assistant inside Knowledge Graph Builder, a knowledge graph made of Markdown pages that link to each other with [[wiki links]].
        Today is {today:yyyy-MM-dd}.

        Rules:
        - Use the tools to look things up. Search first, then read the pages you rely on.
        - Cite every page you use as [[path without .md]], for example [[Getting Started/Install]].
        - When the graph has nothing relevant, say "The knowledge graph doesn't cover this" and don't guess.
        - Never invent sources.
        - Use root-relative paths such as "Projects/Alpha/Overview.md".
        - When you create or update a page, write front-matter with title, tags, sources (only real sources) and updated: {today:yyyy-MM-dd},
          and link related pages with [[links]]. For update_page, read the page first and send the complete new text.
        - Every write is shown to the user as a diff and needs their approval. If they reject it, don't retry the same change.
        - Never output images or external URLs, except URLs listed in a page's sources.
        """;
}
