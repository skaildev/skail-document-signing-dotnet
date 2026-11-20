using Skail.Platform.Runtime;
using Skail.Platform.Runtime.Hosting;
using SkailDocumentSigning;
using SkailDocumentSigning.Demo;
using SkailDocumentSigning.Skail;

[assembly: VisibleToSkailPlatform]

// usage: dotnet run [-- <contract id>]
//   then open http://localhost:8080; a contract id follows a contract that already exists (to see it resume)
var argument = args.FirstOrDefault(a => !a.StartsWith('-'));
if (argument is not null && !Guid.TryParse(argument, out _))
{
    Console.Error.WriteLine($"Unknown argument '{argument}'. Pass a contract id, or nothing.");
    return 1;
}

DotEnv.Load();
string[] required = ["SKAIL_WORKLOAD", "SKAIL_NAMESPACE", "SKAIL_KEY", "SKAIL_SIDECAR"];
var missing = required.Where(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))).ToArray();
if (missing.Length > 0)
{
    Console.Error.WriteLine($"Missing {string.Join(", ", missing)}. Copy .env.example to .env and fill it in.");
    return 1;
}

var builder = WebApplication.CreateBuilder(args);

// keep the console for the demo: only warnings and errors from the host and the libraries
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// the page: localhost only, unless DEMO_LISTEN says otherwise (the container listens on all interfaces)
var listen = Environment.GetEnvironmentVariable("DEMO_LISTEN") ?? "http://localhost:8080";
builder.WebHost.UseUrls(listen);

builder.Services.AddSingleton<Timeline>();
builder.Services.AddSingleton<ContractDesk>();
builder.Services.AddSingleton(SkailApiOptions.FromEnvironment());
builder.Services.AddHttpClient<SkailApiClient>(http => http.Timeout = TimeSpan.FromSeconds(10));

// hosts the worker that executes ContractFunctions; reads the SKAIL_* variables
builder.UseSkail();

var app = builder.Build();
var desk = app.Services.GetRequiredService<ContractDesk>();
if (argument is not null) desk.Follow(Guid.Parse(argument).ToString());

var page = Path.Combine(AppContext.BaseDirectory, "Page", "index.html");
app.MapGet("/", () => Results.File(page, "text/html; charset=utf-8"));
app.MapGet("/api/state", () => Results.Json(desk.View()));
app.MapPost("/api/contracts", async (CancellationToken ct) => Results.Json(new { id = await desk.SendAsync(ct) }));
app.MapPost("/api/decisions/{area}/{signed:bool}", async (string area, bool signed, CancellationToken ct) =>
    await desk.DecideAsync(area, signed, ct) ? Results.Ok() : Results.Conflict());

app.Lifetime.ApplicationStarted.Register(() =>
    Console.WriteLine("Open http://localhost:8080 to send a contract and play Legal and Finance. Ctrl+C exits."));

await app.RunAsync();
return 0;
