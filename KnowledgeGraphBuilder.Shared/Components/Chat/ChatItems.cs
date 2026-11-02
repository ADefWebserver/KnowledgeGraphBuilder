using System.Text;
using KnowledgeGraphBuilder.Shared.Chat;
using KnowledgeGraphBuilder.Shared.Knowledge;

namespace KnowledgeGraphBuilder.Shared.Components.Chat;

public enum CardState { Pending, Approved, Applied, Rejected, NotApplied, Cancelled, Superseded }

public abstract class ChatItem;

/// <summary>The chat transcript for this session (kept in memory only).</summary>
public sealed class ChatTranscript
{
    public List<ChatItem> Items { get; } = [];
}

public sealed class UserItem(string text) : ChatItem
{
    public string Text { get; } = text;
}

public sealed class AssistantItem : ChatItem
{
    public StringBuilder Text { get; } = new();
    public bool Streaming { get; set; } = true;
}

public sealed class ToolItem(string name, string args) : ChatItem
{
    public string Name { get; } = name;
    public string Args { get; } = args;
}

public sealed class ApprovalItem(string requestId, WriteProposal proposal) : ChatItem
{
    public string RequestId { get; } = requestId;
    public WriteProposal Proposal { get; } = proposal;
    public CardState State { get; set; } = CardState.Pending;
    public string? Error { get; set; }
    public bool Submitted { get; set; }

    public string Action => Proposal.Tool switch
    {
        "create_folder" => "Create folder",
        "update_page" => "Update page",
        _ => "Create page",
    };
}

public sealed class PagesChangedItem(IReadOnlyList<KnowledgeChange> changes) : ChatItem
{
    public IReadOnlyList<KnowledgeChange> Changes { get; } = changes;
}

public sealed class ErrorItem(string message, bool retryable) : ChatItem
{
    public string Message { get; } = message;
    public bool Retryable { get; set; } = retryable;
}

public sealed class NoKeyItem(string reason) : ChatItem
{
    public string Reason { get; } = reason;
}
