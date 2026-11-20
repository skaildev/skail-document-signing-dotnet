using SkailDocumentSigning.Skail;

namespace SkailDocumentSigning.Demo;

/// <summary>
/// Plays the two areas for the page: it sends the contract for signature and turns each button into the area's event.
/// It follows one contract at a time, the last one sent (or the one given on the command line).
/// </summary>
public sealed class ContractDesk(SkailApiClient skail, Timeline timeline)
{
    public const string Title = "Service agreement";

    /// <summary>Time both areas have to answer: 2 minutes in the demo; a real contract would use days.</summary>
    public const int DeadlineSeconds = 120;

    private readonly object gate = new();
    private string? contractId;
    private DateTimeOffset? sentAt;
    private readonly Dictionary<string, bool> decisions = [];   // area -> signed

    /// <summary>Follows a contract that already exists, to see it resume.</summary>
    public void Follow(string id)
    {
        lock (gate) { contractId = id; sentAt = null; decisions.Clear(); }
    }

    public async Task<string> SendAsync(CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString();
        await skail.TriggerAsync(id, Title, DeadlineSeconds, ct);
        lock (gate) { contractId = id; sentAt = DateTimeOffset.UtcNow; decisions.Clear(); }
        return id;
    }

    /// <summary>Sends an area's decision. Each area answers once: the first decision wins.</summary>
    public async Task<bool> DecideAsync(string area, bool signed, CancellationToken ct)
    {
        string id;
        lock (gate)
        {
            if (contractId is null || area is not (Areas.Legal or Areas.Finance) || !decisions.TryAdd(area, signed))
                return false;
            id = contractId;
        }

        var signature = new Signature(area, signed, $"{Areas.Label(area)} team",
            signed ? null : "refused in the demo", DateTimeOffset.UtcNow);
        await skail.FireSignatureAsync(id, signature, ct);
        timeline.Add(id, $"{Areas.Label(area)} {(signed ? "signed" : "refused")}: {Events.For(area)} sent", $"sent:{area}");
        return true;
    }

    /// <summary>What the page shows: the contract, each area and the timeline of the run.</summary>
    public object View()
    {
        lock (gate)
        {
            if (contractId is null) return new { contract = (object?)null };

            var entries = timeline.For(contractId);
            var end = entries.LastOrDefault(e => e.Step?.StartsWith("end:") == true)?.Step?[4..];
            return new
            {
                contract = new
                {
                    id = contractId,
                    title = Title,
                    deadline = sentAt?.AddSeconds(DeadlineSeconds),
                    end,
                },
                areas = new[] { Areas.Legal, Areas.Finance }.Select(area => new
                {
                    area,
                    label = Areas.Label(area),
                    status = AreaStatus(area, entries),
                    canDecide = end is null && !decisions.ContainsKey(area),
                }),
                timeline = entries.Select(e => new { at = e.At, text = e.Text }),
            };
        }
    }

    private string AreaStatus(string area, IReadOnlyList<TimelineEntry> entries)
    {
        if (entries.Any(e => e.Step == $"revoked:{area}")) return "revoked";
        if (entries.Any(e => e.Step == $"withdrawn:{area}")) return "withdrawn";
        if (decisions.TryGetValue(area, out var signed)) return signed ? "signed" : "refused";
        return "pending";
    }
}
