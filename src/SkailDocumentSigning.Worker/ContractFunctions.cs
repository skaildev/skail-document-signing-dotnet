using Skail.Platform.Runtime;
using Skail.Platform.Runtime.Threading;
using SkailDocumentSigning.Contracts;

namespace SkailDocumentSigning.Worker;

public class ContractFunctions(ContractCommands commands)
{
    /// <summary>
    /// Sends the contract to legal and finance at the same time and waits for both signatures, in any order, until a
    /// deadline. The second signature archives it, a refusal cancels it at once, and the deadline expires it. The
    /// deadline is an argument, so the same code serves the demo (2 minutes) and a real contract (days).
    /// </summary>
    [SkailFunction]
    public async SkailTask<ContractResult> SignContract(string contractId, string title, int deadlineSeconds)
    {
        try
        {
            await commands.ReportStatus(ContractEvent.Started(contractId, title));
            await commands.RequestSignature(contractId, title, Areas.Legal);
            await commands.RequestSignature(contractId, title, Areas.Finance);

            // The two waits and the deadline are created once, without await, and reused below.
            var legal = SkailTask.WaitForEvent<Signature>(Events.LegalSignature, contractId);
            var finance = SkailTask.WaitForEvent<Signature>(Events.FinanceSignature, contractId);
            var deadline = SkailTask.DelaySeconds((ulong)deadlineSeconds);

            // WhenAny returns the task that finished first. Decide by it, not by IsCompleted: on a resume,
            // IsCompleted already sees a signature that arrived after the deadline.
            var winner = await SkailTask.WhenAny(legal, finance, deadline);
            if (winner == deadline)
                return await commands.ExpireContract(contractId, null);

            var first = winner == legal ? await legal : await finance;
            if (!first.Signed)
                return await commands.CancelContract(contractId, first, otherAlreadySigned: false);

            var other = winner == legal ? finance : legal;
            if (await SkailTask.WhenAny(other, deadline) == deadline)
                return await commands.ExpireContract(contractId, first);

            var second = await other;
            if (!second.Signed)
                return await commands.CancelContract(contractId, second, otherAlreadySigned: true);

            return await commands.ArchiveContract(contractId, first, second);
        }
        catch (Exception ex) when (ex is not SkailNonDeterministicException)
        {
            await commands.ReportStatus(ContractEvent.Failed(contractId, ex.Message));
            throw;
        }
    }
}
