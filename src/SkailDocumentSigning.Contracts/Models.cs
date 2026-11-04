namespace SkailDocumentSigning.Contracts;

/// <summary>
/// The two areas that sign. They are plain strings (not an enum) because <see cref="Signature"/> is the
/// payload of an external event: strings survive any JSON setup on the skail side.
/// </summary>
public static class Areas
{
    public const string Legal = "legal";
    public const string Finance = "finance";

    public static bool IsValid(string? area) => area is Legal or Finance;

    public static string Other(string area) => area == Legal ? Finance : Legal;

    public static string EventName(string area) => area switch
    {
        Legal => Events.LegalSignature,
        Finance => Events.FinanceSignature,
        _ => throw new ArgumentException($"unknown area '{area}'", nameof(area)),
    };

    /// <summary>English label used by the page and by the default signer name.</summary>
    public static string Label(string area) => area == Legal ? "Legal" : "Finance";
}

/// <summary>External event names, shared by the function (WaitForEvent) and the web app (fire).</summary>
public static class Events
{
    public const string LegalSignature = "LEGAL_SIGNATURE";
    public const string FinanceSignature = "FINANCE_SIGNATURE";
}

public static class ContractLimits
{
    public const string DefaultTitle = "Service agreement";
    public const int MaxTitleLength = 120;

    /// <summary>Time both areas have to answer: 2 minutes in the demo; a real contract would use days.</summary>
    public const int DefaultDeadlineSeconds = 120;
    public const int MaxDeadlineSeconds = 60 * 60 * 24 * 366;
}

/// <summary>
/// One area's answer. It is the payload of the external event, so it only carries what the decision needs
/// (never the contract content): <c>At</c> is stamped by the web app, since the function cannot read the clock.
/// </summary>
public sealed record Signature(string Area, bool Signed, string By, string? Reason, DateTimeOffset At);

public enum ContractEnd
{
    Archived,
    Cancelled,

    /// <summary>The deadline passed before both areas answered.</summary>
    Expired,
}

/// <summary>What happened to the area that did not refuse when the contract was cancelled.</summary>
public enum OtherAreaOutcome
{
    /// <summary>It had already signed: the signature is invalidated.</summary>
    Revoked,

    /// <summary>It had not answered: its pending request is withdrawn.</summary>
    Withdrawn,
}

public sealed record ContractResult(
    string ContractId,
    ContractEnd End,
    string? Reference,
    IReadOnlyList<Signature> Signatures,
    string? RefusedBy,
    string? Reason,
    OtherAreaOutcome? OtherArea)
{
    /// <summary>The reference derives from the id, so a retried command returns the same one.</summary>
    public static ContractResult Archived(string contractId, Signature first, Signature second) =>
        new(contractId, ContractEnd.Archived, ReferenceFor(contractId), [first, second], null, null, null);

    public static ContractResult Cancelled(string contractId, Signature refusal, bool otherAlreadySigned) =>
        new(contractId, ContractEnd.Cancelled, null, [refusal], refusal.Area, refusal.Reason,
            otherAlreadySigned ? OtherAreaOutcome.Revoked : OtherAreaOutcome.Withdrawn);

    /// <summary>The deadline passed: the signature already given, if any, is revoked and the pending requests withdrawn.</summary>
    public static ContractResult Expired(string contractId, Signature? signature) =>
        new(contractId, ContractEnd.Expired, null, signature is null ? [] : [signature], null, null, null);

    private static string ReferenceFor(string contractId) =>
        "ARC-" + new string(contractId.Take(8).ToArray()).ToUpperInvariant();
}

public enum ContractStage
{
    Started,
    SignatureRequested,
    Archived,
    Cancelled,
    Expired,
    Failed,
}

/// <summary>Progress reported by the worker to the web app. The decisions themselves go straight to skail.</summary>
public sealed record ContractEvent(
    string ContractId,
    ContractStage Stage,
    string? Title,
    string? Area,
    ContractResult? Result,
    string? Error)
{
    public static ContractEvent Started(string contractId, string title) => new(contractId, ContractStage.Started, title, null, null, null);

    public static ContractEvent SignatureRequested(string contractId, string title, string area) =>
        new(contractId, ContractStage.SignatureRequested, title, area, null, null);

    public static ContractEvent Archived(string contractId, ContractResult result) =>
        new(contractId, ContractStage.Archived, null, null, result, null);

    public static ContractEvent Cancelled(string contractId, ContractResult result) =>
        new(contractId, ContractStage.Cancelled, null, null, result, null);

    public static ContractEvent Expired(string contractId, ContractResult result) =>
        new(contractId, ContractStage.Expired, null, null, result, null);

    public static ContractEvent Failed(string contractId, string error) =>
        new(contractId, ContractStage.Failed, null, null, null, error);
}
