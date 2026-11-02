using KnowledgeGraphBuilder.Shared.Knowledge;

namespace KnowledgeGraphBuilder.Shared.Chat;

public abstract record ChatEvent;
public sealed record TextDelta(string Text) : ChatEvent;
public sealed record ToolUsed(string Name, string ArgumentsSummary) : ChatEvent;
public sealed record ApprovalRequired(string RequestId, WriteProposal Proposal) : ChatEvent;

/// <summary>Ends the turn.</summary>
public sealed record AwaitingApproval(IReadOnlyList<string> RequestIds) : ChatEvent;

/// <summary>The outcome of an approved write tool.</summary>
public sealed record WriteOutcome(string RequestId, bool Applied, string? Error) : ChatEvent;
public sealed record PagesChanged(IReadOnlyList<KnowledgeChange> Changes) : ChatEvent;

/// <summary>Ends the turn.</summary>
public sealed record ChatError(string Message, bool Retryable) : ChatEvent;

/// <summary>Ends the turn.</summary>
public sealed record ChatCompleted : ChatEvent;

public sealed record WriteProposal(string Tool, string Path, string? OldMarkdown, string NewMarkdown, long? ExpectedModifiedTicks);

public sealed record ApprovalDecision(string RequestId, bool Approved, string? Reason = null);

public interface IChatOrchestrator
{
    IAsyncEnumerable<ChatEvent> SendAsync(string userText, CancellationToken ct);
    IAsyncEnumerable<ChatEvent> ResolveApprovalsAsync(IReadOnlyList<ApprovalDecision> decisions, CancellationToken ct);

    /// <summary>Runs the last user message again after a retryable error.</summary>
    IAsyncEnumerable<ChatEvent> RetryAsync(CancellationToken ct);
    IReadOnlyList<string> PendingRequestIds { get; }

    /// <summary>Stop while approvals are pending: they are rejected as "Cancelled by user" with the next message.</summary>
    void CancelPending();
    void Reset();
}

public sealed record ChatOptionsConfig(int MaxIterations = 10, int MaxToolResultChars = 20_000);
