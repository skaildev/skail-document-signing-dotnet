using Skail.Platform.Runtime;
using Skail.Platform.Runtime.Threading;
using SkailDocumentSigning.Demo;

namespace SkailDocumentSigning;

public class ContractFunctions(Timeline timeline)
{
    /// <summary>
    /// Sends the contract to Legal and Finance at the same time and waits for both signatures, in any order, until a
    /// deadline. The second signature archives it, a refusal cancels it at once, and the deadline expires it. The
    /// deadline is an argument, so the same code serves the demo (2 minutes) and a real contract (days).
    /// The name and the parameters are the trigger contract: ["{contractId}", "{title}", {deadlineSeconds}].
    /// </summary>
    [SkailFunction]
    public async SkailTask<string> SignContract(string contractId, string title, int deadlineSeconds)
    {
        await RequestSignature(contractId, title, Areas.Legal);
        await RequestSignature(contractId, title, Areas.Finance);

        // the two waits and the deadline are created once, without await, and reused below
        var legal = SkailTask.WaitForEvent<Signature>(Events.LegalSignature, contractId);
        var finance = SkailTask.WaitForEvent<Signature>(Events.FinanceSignature, contractId);
        var deadline = SkailTask.DelaySeconds((ulong)deadlineSeconds);

        // decide by the task WhenAny returns, not by IsCompleted: on a resume, IsCompleted already sees a signature
        // that arrived after the deadline
        var winner = await SkailTask.WhenAny(legal, finance, deadline);
        if (winner == deadline)
            return await ExpireContract(contractId, null);

        var first = winner == legal ? await legal : await finance;
        if (!first.Signed)
            return await CancelContract(contractId, first, otherAlreadySigned: false);

        var other = winner == legal ? finance : legal;
        if (await SkailTask.WhenAny(other, deadline) == deadline)
            return await ExpireContract(contractId, first);

        var second = await other;
        if (!second.Signed)
            return await CancelContract(contractId, second, otherAlreadySigned: true);

        return await ArchiveContract(contractId, first, second);
    }

    // The commands are the I/O: here they only report what would happen in the signature and archive systems, to the
    // console and to the page.
    // A completed command never runs again, so a replay does not ask the areas again nor print the same line twice.

    [SkailCommand(retryCount: 3)]
    public async SkailTask RequestSignature(string contractId, string title, string area) =>
        await Report(contractId, $"{Areas.Label(area)}: signature requested for \"{title}\"", $"requested:{area}");

    [SkailCommand(retryCount: 3)]
    public async SkailTask<string> ArchiveContract(string contractId, Signature first, Signature second)
    {
        var reference = "ARC-" + contractId[..8].ToUpperInvariant();
        await Report(contractId, $"archived as {reference}, signed by {first.By} and {second.By}", "end:archived");
        return "archived " + reference;
    }

    /// <summary>Compensation: the signature the other area already gave is revoked, or its pending request is withdrawn.</summary>
    [SkailCommand(retryCount: 3)]
    public async SkailTask<string> CancelContract(string contractId, Signature refusal, bool otherAlreadySigned)
    {
        var other = Areas.Label(Areas.Other(refusal.Area));
        var otherArea = Areas.Other(refusal.Area);
        await Report(contractId, otherAlreadySigned ? $"↩ {other}: signature revoked" : $"↩ {other}: request withdrawn",
            otherAlreadySigned ? $"revoked:{otherArea}" : $"withdrawn:{otherArea}");
        await Report(contractId, $"cancelled: {Areas.Label(refusal.Area)} refused ({refusal.Reason})", "end:cancelled");
        return "cancelled";
    }

    /// <summary>Compensation: the signature already given, if any, is revoked and the pending requests are withdrawn.</summary>
    [SkailCommand(retryCount: 3)]
    public async SkailTask<string> ExpireContract(string contractId, Signature? signature)
    {
        foreach (var area in new[] { Areas.Legal, Areas.Finance })
            await Report(contractId, signature?.Area == area
                ? $"↩ {Areas.Label(area)}: signature revoked"
                : $"↩ {Areas.Label(area)}: request withdrawn",
                signature?.Area == area ? $"revoked:{area}" : $"withdrawn:{area}");
        await Report(contractId, "expired: the deadline passed", "end:expired");
        return "expired";
    }

    private Task Report(string contractId, string message, string step)
    {
        timeline.Add(contractId, message, step);
        return Task.CompletedTask;
    }
}
