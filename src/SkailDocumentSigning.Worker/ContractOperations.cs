using Microsoft.Extensions.Logging;
using SkailDocumentSigning.Contracts;
using SkailDocumentSigning.Worker.Progress;

namespace SkailDocumentSigning.Worker;

/// <summary>
/// The I/O behind the commands, as plain tasks. Commands only run inside the skail runtime, so keeping the
/// logic here lets it be unit tested without skail. The web app plays the legal and finance inboxes: delivering
/// a request is the event it receives, and if it cannot be delivered the command throws and skail retries.
/// </summary>
public class ContractOperations(IProgressNotifier notifier, ILogger<ContractOperations> log)
{
    /// <summary>Idempotent: the web app records one request per contract and area.</summary>
    public async Task RequestSignatureAsync(string contractId, string title, string area)
    {
        if (!Areas.IsValid(area))
            throw new ArgumentException($"unknown area '{area}'", nameof(area));

        log.LogInformation("Signature requested from {Area}: contract {ContractId} ({Title})", Areas.Label(area), contractId, title);
        await notifier.NotifyAsync(ContractEvent.SignatureRequested(contractId, title, area));
    }

    /// <summary>Idempotent: the result derives from the arguments, and the web app ignores a repeated stage.</summary>
    public async Task<ContractResult> ArchiveContractAsync(string contractId, Signature first, Signature second)
    {
        if (!first.Signed || !second.Signed)
            throw new InvalidOperationException("only a contract with both signatures can be archived");

        var result = ContractResult.Archived(contractId, first, second);
        log.LogInformation("Contract {ContractId} archived ({Reference})", contractId, result.Reference);
        await notifier.NotifyAsync(ContractEvent.Archived(contractId, result));
        return result;
    }

    /// <summary>
    /// Compensation: a signature already given is revoked, a request still pending is withdrawn.
    /// Idempotent for the same reasons as <see cref="ArchiveContractAsync"/>.
    /// </summary>
    public async Task<ContractResult> CancelContractAsync(string contractId, Signature refusal, bool otherAlreadySigned)
    {
        if (refusal.Signed)
            throw new InvalidOperationException("only a refusal cancels the contract");

        var result = ContractResult.Cancelled(contractId, refusal, otherAlreadySigned);
        log.LogInformation("Contract {ContractId} cancelled: {Area} refused ({Reason}); other area: {Outcome}",
            contractId, Areas.Label(refusal.Area), refusal.Reason, result.OtherArea);
        await notifier.NotifyAsync(ContractEvent.Cancelled(contractId, result));
        return result;
    }

    /// <summary>Compensation when the deadline passes. Idempotent for the same reasons as <see cref="ArchiveContractAsync"/>.</summary>
    public async Task<ContractResult> ExpireContractAsync(string contractId, Signature? signature)
    {
        if (signature is { Signed: false })
            throw new InvalidOperationException("a refusal cancels the contract; it does not expire it");

        var result = ContractResult.Expired(contractId, signature);
        log.LogInformation("Contract {ContractId} expired: no answer before the deadline", contractId);
        await notifier.NotifyAsync(ContractEvent.Expired(contractId, result));
        return result;
    }

    public Task ReportStatusAsync(ContractEvent evt) => notifier.NotifyAsync(evt);
}
