using SkailDocumentSigning.Contracts;

namespace SkailDocumentSigning.Web.Skail;

public sealed class SkailGatewayException(string message, Exception? inner = null) : Exception(message, inner);

public interface ISkailGateway
{
    /// <summary>
    /// Starts the SignContract function; <paramref name="contractId"/> is the execution id. The deadline is an
    /// argument of the function (it cannot read configuration).
    /// </summary>
    Task TriggerAsync(string contractId, string title, int deadlineSeconds, CancellationToken ct = default);

    /// <summary>Fires LEGAL_SIGNATURE or FINANCE_SIGNATURE, by the signature's area; the instance id is the contract id.</summary>
    Task FireSignatureAsync(string contractId, Signature signature, CancellationToken ct = default);
}
