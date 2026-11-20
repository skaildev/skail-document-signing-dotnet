namespace SkailDocumentSigning;

/// <summary>The two areas that sign, as plain strings: they travel in the payload of an external event.</summary>
public static class Areas
{
    public const string Legal = "legal";
    public const string Finance = "finance";

    public static string Label(string area) => area == Legal ? "Legal" : "Finance";

    public static string Other(string area) => area == Legal ? Finance : Legal;
}

/// <summary>External event names, shared by the function (WaitForEvent) and the console (fire).</summary>
public static class Events
{
    public const string LegalSignature = "LEGAL_SIGNATURE";
    public const string FinanceSignature = "FINANCE_SIGNATURE";

    public static string For(string area) => area == Areas.Legal ? LegalSignature : FinanceSignature;
}

/// <summary>
/// One area's answer, and the payload of its external event. It carries only what the decision needs, never the
/// contract content. At is stamped by whoever fires the event, since the function cannot read the clock.
/// </summary>
public sealed record Signature(string Area, bool Signed, string By, string? Reason, DateTimeOffset At);
