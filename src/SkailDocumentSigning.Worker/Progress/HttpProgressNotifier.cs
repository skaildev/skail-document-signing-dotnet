using System.Net.Http.Json;
using SkailDocumentSigning.Contracts;

namespace SkailDocumentSigning.Worker.Progress;

/// <summary>Reports progress to the web app: POST {web}/internal/contracts/{id}/events.</summary>
public sealed class HttpProgressNotifier(HttpClient http) : IProgressNotifier
{
    public async Task NotifyAsync(ContractEvent evt, CancellationToken ct = default)
    {
        using var response = await http.PostAsJsonAsync($"internal/contracts/{Uri.EscapeDataString(evt.ContractId)}/events", evt, Json.Options, ct);
        // when a command fails, this message is what the skail Monitor shows
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Web app answered {(int)response.StatusCode} to the progress event", null, response.StatusCode);
    }
}
