#if DEBUG
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace KnowledgeGraphBuilder.Shared.Llm;

/// <summary>
/// DEBUG-only scripted client for demos without an API key. It searches, cites what it found,
/// and proposes a page when asked to create one.
/// </summary>
public sealed partial class FakeChatClient : IChatClient
{
    [GeneratedRegex(@"create (?:a |an |the )?(?<name>[\w ]+?) page", RegexOptions.IgnoreCase)]
    private static partial Regex CreatePattern();

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in GetStreamingResponseAsync(messages, options, cancellationToken)) updates.Add(u);
        return updates.ToChatResponse();
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var list = messages.ToList();
        var id = Guid.NewGuid().ToString("N");
        var last = list.LastOrDefault();
        var results = last?.Contents.OfType<FunctionResultContent>().ToList() ?? [];

        if (results.Count > 0)
        {
            var calls = list.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToDictionary(c => c.CallId, c => c.Name);
            var text = Answer(results, calls);
            foreach (var word in text.Split(' '))
            {
                await Task.Delay(25, cancellationToken);
                yield return new ChatResponseUpdate(ChatRole.Assistant, word + " ") { MessageId = id };
            }
            yield return new ChatResponseUpdate { Role = ChatRole.Assistant, MessageId = id, FinishReason = ChatFinishReason.Stop };
            yield break;
        }

        var userText = string.Concat(last?.Contents.OfType<TextContent>().Select(t => t.Text) ?? []);
        await Task.Delay(150, cancellationToken);
        FunctionCallContent call;
        if (CreatePattern().Match(userText) is { Success: true } m)
        {
            var name = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(m.Groups["name"].Value.Trim());
            call = new FunctionCallContent("call_" + Guid.NewGuid().ToString("N")[..8], "create_page", new Dictionary<string, object?>
            {
                ["path"] = name + ".md",
                ["markdown"] = $"---\ntitle: {name}\ntags: [draft]\nupdated: {DateTime.Now:yyyy-MM-dd}\n---\n# {name}\n\n- First step\n- Second step\n- Third step\n\nSee [[Welcome]].\n",
            });
        }
        else
        {
            call = new FunctionCallContent("call_" + Guid.NewGuid().ToString("N")[..8], "search_pages", new Dictionary<string, object?>
            {
                ["query"] = string.Join(' ', userText.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 3).Take(3)),
            });
        }
        var update = new ChatResponseUpdate { Role = ChatRole.Assistant, MessageId = id, FinishReason = ChatFinishReason.ToolCalls };
        update.Contents.Add(call);
        yield return update;
    }

    static string Answer(List<FunctionResultContent> results, Dictionary<string, string> calls)
    {
        var r = results[0];
        var tool = calls.TryGetValue(r.CallId, out var n) ? n : "";
        var json = r.Result switch
        {
            JsonElement { ValueKind: JsonValueKind.String } s => s.GetString() ?? "",
            JsonElement e => e.GetRawText(),
            var other => other?.ToString() ?? "",
        };
        if (tool == "search_pages")
        {
            var paths = Regex.Matches(json, "\"path\"\\s*:\\s*\"([^\"]+)\"").Select(x => x.Groups[1].Value.Replace(".md", "")).Distinct().Take(2).ToList();
            return paths.Count == 0
                ? "The knowledge graph doesn't cover this. (Fake provider)"
                : $"I found {string.Join(" and ", paths.Select(p => $"[[{p}]]"))}. (Fake provider: this is a scripted answer.)";
        }
        if (json.Contains("\"error\"", StringComparison.Ordinal)) return "The change wasn't applied: " + json;
        var path = Regex.Match(json, "\"path\"\\s*:\\s*\"([^\"]+)\"").Groups[1].Value.Replace(".md", "");
        return string.IsNullOrEmpty(path) ? "Done." : $"Done. I created [[{path}]].";
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("fake", null, "fake") : null;

    public void Dispose() { }
}
#endif
