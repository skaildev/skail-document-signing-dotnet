using System.Text.Json;
using System.Text.Json.Serialization;
using SkailDocumentSigning.Contracts;
using SkailDocumentSigning.Web.Contracts;
using SkailDocumentSigning.Web.Skail;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddSingleton<ContractStore>();
builder.Services.AddSingleton(SkailOptions.FromEnvironment());
builder.Services.AddSingleton(SigningOptions.FromEnvironment());
builder.Services.AddHttpClient<ISkailGateway, SkailApiGateway>(c => c.Timeout = TimeSpan.FromSeconds(10));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache" });

app.MapGet("/healthz", () => Results.Ok("ok"));

app.MapGet("/api/config", () => Results.Ok(new
{
    defaultTitle = ContractLimits.DefaultTitle,
    monitorUrl = Environment.GetEnvironmentVariable("MONITOR_URL") is { Length: > 0 } url ? url : null,
}));

app.MapPost("/api/contracts", async (StartContractRequest? request, ContractStore store, ISkailGateway skail, SigningOptions signing, CancellationToken ct) =>
{
    var title = string.IsNullOrWhiteSpace(request?.Title) ? ContractLimits.DefaultTitle : request.Title.Trim();
    if (title.Length > ContractLimits.MaxTitleLength)
        return Results.BadRequest(new { error = $"The title can have at most {ContractLimits.MaxTitleLength} characters" });

    var contractId = Guid.NewGuid().ToString();
    var contract = store.Create(contractId, title, signing.DeadlineSeconds);
    try
    {
        await skail.TriggerAsync(contractId, title, signing.DeadlineSeconds, ct);
    }
    catch (SkailGatewayException e)
    {
        store.Remove(contractId);
        return Results.Json(new { error = e.Message }, statusCode: StatusCodes.Status502BadGateway);
    }
    return Results.Ok(contract);
});

app.MapGet("/api/contracts/{id}", (string id, ContractStore store) =>
    store.Get(id) is { } contract ? Results.Ok(contract) : Results.NotFound());

// An area's decision: registered here (first one wins) and fired to the function's WaitForEvent.
app.MapPost("/api/contracts/{id}/signatures", async (string id, SignRequest? request, ContractStore store, ISkailGateway skail, CancellationToken ct) =>
{
    if (!Areas.IsValid(request?.Area))
        return Results.BadRequest(new { error = $"Unknown area. Use '{Areas.Legal}' or '{Areas.Finance}'" });

    var area = request!.Area!;
    var signed = request.Signed ?? true;
    var by = string.IsNullOrWhiteSpace(request.SignedBy) ? $"{Areas.Label(area)} team" : request.SignedBy.Trim();
    var reason = signed || string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
    var signature = new Signature(area, signed, by, reason, DateTimeOffset.UtcNow);

    switch (store.TryDecide(id, signature))
    {
        case DecisionOutcome.NotFound: return Results.NotFound();
        case DecisionOutcome.Duplicate: return Results.Conflict(new { error = $"{Areas.Label(area)} already answered this contract" });
        case DecisionOutcome.Finished: return Results.Conflict(new { error = "The contract is already finished" });
    }

    try
    {
        await skail.FireSignatureAsync(id, signature, ct);
    }
    catch (SkailGatewayException e)
    {
        store.Release(id, area); // not delivered: the area can try again
        return Results.Json(new { error = e.Message }, statusCode: StatusCodes.Status502BadGateway);
    }
    return Results.Ok(store.Get(id));
});

// called by the worker's commands (ReportStatus, RequestSignature, ArchiveContract, CancelContract)
app.MapPost("/internal/contracts/{id}/events", (string id, ContractEvent evt, ContractStore store) =>
{
    store.Apply(evt with { ContractId = id });
    return Results.NoContent();
});

app.MapGet("/api/contracts/{id}/stream", async (string id, HttpContext http, ContractStore store, CancellationToken ct) =>
{
    using var subscription = store.Subscribe(id); // subscribe before reading the snapshot: no update is lost
    if (store.Get(id) is not { } current) return Results.NotFound();

    http.Response.ContentType = "text/event-stream";
    http.Response.Headers.CacheControl = "no-cache";

    async Task Send(ContractState state)
    {
        var json = JsonSerializer.Serialize(state, Json.Options);
        await http.Response.WriteAsync($"data: {json}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }

    try
    {
        await Send(current);
        if (current.IsTerminal) return Results.Empty;

        await foreach (var state in subscription.Reader.ReadAllAsync(ct))
        {
            await Send(state);
            if (state.IsTerminal) break;
        }
    }
    catch (OperationCanceledException)
    {
        // client disconnected
    }
    return Results.Empty;
});

app.Run();

public sealed record StartContractRequest(string? Title);

public sealed record SignRequest(string? Area, string? SignedBy, bool? Signed, string? Reason);

public partial class Program;
