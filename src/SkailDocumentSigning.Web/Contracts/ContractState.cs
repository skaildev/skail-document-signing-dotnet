using SkailDocumentSigning.Contracts;

namespace SkailDocumentSigning.Web.Contracts;

public enum ContractPhase
{
    Starting,
    Waiting,
    Archived,
    Cancelled,
    Expired,
    Failed,
}

public enum AreaStatus
{
    Pending,
    Signed,
    Refused,

    /// <summary>The contract was cancelled or expired after this area had signed: its signature is invalidated.</summary>
    Revoked,

    /// <summary>The contract was cancelled or expired before this area answered: its request is withdrawn.</summary>
    Withdrawn,
}

public enum DecisionOutcome
{
    Accepted,
    Duplicate,
    Finished,
    NotFound,
}

public sealed record AreaView(string Area, AreaStatus Status, bool Requested, string? By, string? Reason, DateTimeOffset? At)
{
    public static AreaView New(string area) => new(area, AreaStatus.Pending, false, null, null, null);
}

public sealed record TimelineEntry(string Kind, string? Area, string Text, DateTimeOffset At);

public sealed record ContractState(
    string ContractId,
    string Title,
    int DeadlineSeconds,
    /// <summary>Stamped when the worker reports Started: the page countdown reads it.</summary>
    DateTimeOffset? StartedAt,
    ContractPhase Phase,
    AreaView Legal,
    AreaView Finance,
    IReadOnlyList<TimelineEntry> Timeline,
    ContractResult? Result,
    string? Error)
{
    public bool IsTerminal => Phase is ContractPhase.Archived or ContractPhase.Cancelled or ContractPhase.Expired
        or ContractPhase.Failed;
    public bool Active => !IsTerminal;

    public AreaView AreaOf(string area) => area == Areas.Legal ? Legal : Finance;

    public ContractState WithArea(string area, Func<AreaView, AreaView> change) =>
        area == Areas.Legal ? this with { Legal = change(Legal) } : this with { Finance = change(Finance) };
}
