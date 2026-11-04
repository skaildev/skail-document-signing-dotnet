using SkailDocumentSigning.Contracts;

namespace SkailDocumentSigning.Worker.Progress;

public interface IProgressNotifier
{
    Task NotifyAsync(ContractEvent evt, CancellationToken ct = default);
}
