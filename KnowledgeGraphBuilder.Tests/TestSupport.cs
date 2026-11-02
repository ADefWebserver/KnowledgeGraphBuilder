using System.Runtime.CompilerServices;
using KnowledgeGraphBuilder.Shared.Chat;
using KnowledgeGraphBuilder.Shared.Knowledge;
using KnowledgeGraphBuilder.Shared.Settings;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

namespace KnowledgeGraphBuilder.Tests;

/// <summary>A unique temporary directory that is deleted on dispose.</summary>
public sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kgb-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(string relative) => System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(Path)) return;
            foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>A file-system store plus workspace over a temp directory.</summary>
public sealed class WorkspaceFixture : IDisposable
{
    public WorkspaceFixture(bool seed = true)
    {
        Store = new FileSystemKnowledgeStore(new KnowledgeStoreOptions(Dir.Path), new PermanentFileDeleter());
        Graph = new KnowledgeGraphService(Store);
        Workspace = new LocalKnowledgeWorkspace(Store, Graph, NullLogger<LocalKnowledgeWorkspace>.Instance);
        if (seed) KnowledgeSeeder.SeedIfEmptyAsync(Store).GetAwaiter().GetResult();
    }

    public TempDir Dir { get; } = new();
    public FileSystemKnowledgeStore Store { get; }
    public KnowledgeGraphService Graph { get; }
    public LocalKnowledgeWorkspace Workspace { get; }

    public void Dispose()
    {
        Workspace.Dispose();
        Dir.Dispose();
    }
}

public sealed class ThrowingJsRuntime : IJSRuntime
{
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new NotSupportedException();
    public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new NotSupportedException();
}

public sealed class TestHost : IHostCapabilities
{
    public bool CanToggleTheme => true;
    public bool CanOpenFolder => false;
    public bool IsBrowser { get; set; }
    public bool HasBackup => true;
    public bool DeletesAreRecoverable => false;
    public string RootDisplay => "Test root";
    public string RootPath => "";
    public void OpenRootFolder() { }
}

/// <summary>One scripted model response: updates to stream, then optionally an exception.</summary>
public sealed record ScriptStep(IReadOnlyList<ChatResponseUpdate> Updates, Exception? ThrowAfter = null);

/// <summary>An <see cref="IChatClient"/> that plays back queued responses and records what it receives.</summary>
public sealed class ScriptedChatClient : IChatClient
{
    readonly Queue<ScriptStep> _steps = new();
    int _messageCounter;

    public List<List<ChatMessage>> Requests { get; } = [];
    public List<ChatOptions?> Options { get; } = [];

    /// <summary>Used when the queue is empty (null = throw).</summary>
    public Func<ScriptStep>? Fallback { get; set; }

    public int Remaining => _steps.Count;

    public ScriptedChatClient Enqueue(params ChatResponseUpdate[] updates)
    {
        _steps.Enqueue(new ScriptStep(updates));
        return this;
    }

    public ScriptedChatClient EnqueueText(string text) => Enqueue(Text(text));

    public ScriptedChatClient EnqueueCalls(params FunctionCallContent[] calls) => Enqueue(Calls(calls));

    public ScriptedChatClient EnqueueFailure(Exception ex, params ChatResponseUpdate[] before)
    {
        _steps.Enqueue(new ScriptStep(before, ex));
        return this;
    }

    public ChatResponseUpdate Text(string text) => new(ChatRole.Assistant, text) { MessageId = NextId(), FinishReason = ChatFinishReason.Stop };

    public ChatResponseUpdate Calls(params FunctionCallContent[] calls)
    {
        var update = new ChatResponseUpdate { Role = ChatRole.Assistant, MessageId = NextId(), FinishReason = ChatFinishReason.ToolCalls };
        foreach (var c in calls) update.Contents.Add(c);
        return update;
    }

    string NextId() => "msg_" + Interlocked.Increment(ref _messageCounter);

    public static FunctionCallContent Call(string id, string name, params (string Key, object? Value)[] args) =>
        new(id, name, args.ToDictionary(a => a.Key, a => a.Value));

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var updates = new List<ChatResponseUpdate>();
        await foreach (var u in GetStreamingResponseAsync(messages, options, cancellationToken)) updates.Add(u);
        return updates.ToChatResponse();
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Requests.Add(messages.Select(m => m.Clone()).ToList());
        Options.Add(options);
        ScriptStep step;
        if (_steps.Count > 0) step = _steps.Dequeue();
        else if (Fallback is not null) step = Fallback();
        else throw new InvalidOperationException("The scripted client has no more responses.");

        foreach (var update in step.Updates)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
        }
        if (step.ThrowAfter is not null) throw step.ThrowAfter;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}

public sealed class FakeChatClientProvider(IChatClient? client, string? reason = null) : IChatClientProvider
{
    public int Calls { get; private set; }

    public Task<ChatClientResult> GetClientAsync()
    {
        Calls++;
        return Task.FromResult(new ChatClientResult(client, client is null ? reason ?? "Add an API key in Settings" : null));
    }
}
