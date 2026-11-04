using Skail.Platform.Runtime;
using Skail.Platform.Runtime.Threading;
using SkailDocumentSigning.Contracts;

namespace SkailDocumentSigning.Worker;

/// <summary>
/// The I/O of the function, one operation per command. A retry runs the whole command again, so every command is
/// idempotent. The commands delegate to <see cref="ContractOperations"/>, so their logic is tested without skail.
/// </summary>
public class ContractCommands(ContractOperations operations)
{
    [SkailCommand(retryCount: 3)]
    public async SkailTask RequestSignature(string contractId, string title, string area)
        => await operations.RequestSignatureAsync(contractId, title, area);

    /// <summary>Compensation: the signature the other area already gave is revoked, or its pending request is withdrawn.</summary>
    [SkailCommand(retryCount: 3)]
    public async SkailTask<ContractResult> CancelContract(string contractId, Signature refusal, bool otherAlreadySigned)
        => await operations.CancelContractAsync(contractId, refusal, otherAlreadySigned);

    /// <summary>Compensation: the signature already given, if any, is revoked and the pending requests are withdrawn.</summary>
    [SkailCommand(retryCount: 3)]
    public async SkailTask<ContractResult> ExpireContract(string contractId, Signature? signature)
        => await operations.ExpireContractAsync(contractId, signature);

    [SkailCommand(retryCount: 3)]
    public async SkailTask<ContractResult> ArchiveContract(string contractId, Signature first, Signature second)
        => await operations.ArchiveContractAsync(contractId, first, second);

    /// <summary>Idempotent: the web app ignores a repeated event (same contract, stage and area).</summary>
    [SkailCommand(retryCount: 3)]
    public async SkailTask ReportStatus(ContractEvent evt)
        => await operations.ReportStatusAsync(evt);
}
