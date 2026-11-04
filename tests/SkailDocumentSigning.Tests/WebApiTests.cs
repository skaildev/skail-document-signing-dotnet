using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SkailDocumentSigning.Contracts;
using SkailDocumentSigning.Web.Skail;

namespace SkailDocumentSigning.Tests;

public sealed class FakeGateway : ISkailGateway
{
    public List<(string ContractId, string Title, int Deadline)> Triggers { get; } = [];
    public List<(string ContractId, Signature Signature)> Fires { get; } = [];
    public Exception? FailWith { get; set; }
    public Exception? FailFireWith { get; set; }

    public Task TriggerAsync(string contractId, string title, int deadlineSeconds, CancellationToken ct = default)
    {
        if (FailWith is not null) throw FailWith;
        Triggers.Add((contractId, title, deadlineSeconds));
        return Task.CompletedTask;
    }

    public Task FireSignatureAsync(string contractId, Signature signature, CancellationToken ct = default)
    {
        if (FailFireWith is not null) throw FailFireWith;
        Fires.Add((contractId, signature));
        return Task.CompletedTask;
    }
}

public sealed class WebFactory : WebApplicationFactory<Program>
{
    public FakeGateway Gateway { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<ISkailGateway>();
            s.AddSingleton<ISkailGateway>(Gateway);
        });
    }
}

public class WebApiTests : IClassFixture<WebFactory>
{
    private readonly WebFactory _factory;
    private readonly HttpClient _http;

    public WebApiTests(WebFactory factory)
    {
        _factory = factory;
        _http = factory.CreateClient();
    }

    private async Task<JsonElement> StartAsync(string? title = null)
    {
        var res = await _http.PostAsJsonAsync("/api/contracts", title is null ? (object)new { } : new { title });
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<string> StartIdAsync(string? title = null) => (await StartAsync(title)).GetProperty("contractId").GetString()!;

    private Task<HttpResponseMessage> SignAsync(string id, string area, bool signed = true, string? by = "Ana", string? reason = null) =>
        _http.PostAsJsonAsync($"/api/contracts/{id}/signatures", new { area, signedBy = by, signed, reason });

    private Task<HttpResponseMessage> PostEventAsync(string id, ContractEvent evt) =>
        _http.PostAsJsonAsync($"/internal/contracts/{id}/events", evt, Json.Options);

    [Fact]
    public async Task Health_endpoint_is_ok()
    {
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public async Task Index_page_is_served_with_the_title_field_and_the_two_areas()
    {
        var html = await _http.GetStringAsync("/");

        Assert.Contains("id=\"title\"", html);
        Assert.Contains("id=\"start\"", html);
        Assert.Contains("data-area=\"legal\"", html);
        Assert.Contains("data-area=\"finance\"", html);
    }

    [Fact]
    public async Task Start_without_a_title_uses_the_default_and_triggers_skail()
    {
        var c = await StartAsync();

        Assert.Equal(ContractLimits.DefaultTitle, c.GetProperty("title").GetString());
        Assert.True(c.GetProperty("active").GetBoolean());
        Assert.Equal("Starting", c.GetProperty("phase").GetString());
        Assert.Contains(_factory.Gateway.Triggers, t => t.ContractId == c.GetProperty("contractId").GetString() && t.Title == ContractLimits.DefaultTitle
            && t.Deadline == ContractLimits.DefaultDeadlineSeconds);
    }

    [Fact]
    public async Task Start_uses_the_trimmed_title_and_a_blank_title_falls_back_to_the_default()
    {
        Assert.Equal("Supply agreement", (await StartAsync("  Supply agreement  ")).GetProperty("title").GetString());
        Assert.Equal(ContractLimits.DefaultTitle, (await StartAsync("   ")).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Start_rejects_a_title_that_is_too_long()
    {
        var res = await _http.PostAsJsonAsync("/api/contracts", new { title = new string('x', ContractLimits.MaxTitleLength + 1) });

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Start_failure_returns_502_and_forgets_the_contract()
    {
        _factory.Gateway.FailWith = new SkailGatewayException("skail answered 401");
        try
        {
            var res = await _http.PostAsJsonAsync("/api/contracts", new { });
            Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
            Assert.Contains("401", await res.Content.ReadAsStringAsync());
        }
        finally { _factory.Gateway.FailWith = null; }
    }

    [Fact]
    public async Task Get_unknown_contract_is_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/contracts/nope")).StatusCode);
    }

    [Theory]
    [InlineData("legal", "LEGAL_SIGNATURE")]
    [InlineData("finance", "FINANCE_SIGNATURE")]
    public async Task Signing_fires_the_event_of_that_area_and_marks_the_card(string area, string _)
    {
        var id = await StartIdAsync();

        var res = await SignAsync(id, area, by: "Ana");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var (firedId, sig) = Assert.Single(_factory.Gateway.Fires, f => f.ContractId == id);
        Assert.Equal(id, firedId);
        Assert.Equal((area, true, "Ana"), (sig.Area, sig.Signed, sig.By));
        Assert.NotEqual(default, sig.At);
        var view = await _http.GetFromJsonAsync<JsonElement>($"/api/contracts/{id}");
        Assert.Equal("Signed", view.GetProperty(area).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Refusing_fires_a_refusal_with_the_reason()
    {
        var id = await StartIdAsync();

        await SignAsync(id, "finance", signed: false, by: "Beto", reason: "wrong amount");

        var (_, sig) = Assert.Single(_factory.Gateway.Fires, f => f.ContractId == id);
        Assert.Equal((false, "wrong amount"), (sig.Signed, sig.Reason));
    }

    [Fact]
    public async Task Signing_without_a_name_falls_back_to_the_team_of_the_area()
    {
        var id = await StartIdAsync();

        await SignAsync(id, "legal", by: " ");

        var (_, sig) = Assert.Single(_factory.Gateway.Fires, f => f.ContractId == id);
        Assert.Equal("Legal team", sig.By);
    }

    [Fact]
    public async Task Invalid_area_is_400_and_unknown_contract_is_404()
    {
        var id = await StartIdAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await SignAsync(id, "rh")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await SignAsync("nope", "legal")).StatusCode);
    }

    [Fact]
    public async Task A_second_decision_of_the_same_area_is_409_and_does_not_fire_again()
    {
        var id = await StartIdAsync();
        await SignAsync(id, "legal");

        var res = await SignAsync(id, "legal", signed: false, reason: "mudei de ideia");

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        Assert.Single(_factory.Gateway.Fires, f => f.ContractId == id);
    }

    [Fact]
    public async Task Deciding_after_the_contract_finished_is_409()
    {
        var id = await StartIdAsync();
        await PostEventAsync(id, ContractEvent.Failed(id, "boom"));

        Assert.Equal(HttpStatusCode.Conflict, (await SignAsync(id, "legal")).StatusCode);
    }

    [Fact]
    public async Task A_failing_fire_returns_502_and_leaves_the_area_pending_for_a_new_attempt()
    {
        var id = await StartIdAsync();
        _factory.Gateway.FailFireWith = new SkailGatewayException("skail answered 503");
        try
        {
            var res = await SignAsync(id, "legal");
            Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
        }
        finally { _factory.Gateway.FailFireWith = null; }

        var view = await _http.GetFromJsonAsync<JsonElement>($"/api/contracts/{id}");
        Assert.Equal("Pending", view.GetProperty("legal").GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await SignAsync(id, "legal")).StatusCode);
    }

    [Fact]
    public async Task Internal_events_update_the_state_served_by_the_api()
    {
        var id = await StartIdAsync();
        var jur = new Signature(Areas.Legal, true, "Ana", null, DateTimeOffset.UtcNow);
        var fin = new Signature(Areas.Finance, true, "Beto", null, DateTimeOffset.UtcNow);

        Assert.Equal(HttpStatusCode.NoContent, (await PostEventAsync(id, ContractEvent.Started(id, "t"))).StatusCode);
        await PostEventAsync(id, ContractEvent.Archived(id, ContractResult.Archived(id, jur, fin)));

        var view = await _http.GetFromJsonAsync<JsonElement>($"/api/contracts/{id}");
        Assert.Equal("Archived", view.GetProperty("phase").GetString());
        Assert.False(view.GetProperty("active").GetBoolean());
        Assert.StartsWith("ARC-", view.GetProperty("result").GetProperty("reference").GetString());
    }

    [Fact]
    public async Task Stream_sends_the_snapshot_then_each_update_and_closes_when_the_contract_ends()
    {
        var id = await StartIdAsync();
        using var response = await _http.GetAsync($"/api/contracts/{id}/stream", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());

        await PostEventAsync(id, ContractEvent.Started(id, "t"));
        await SignAsync(id, "legal");
        await PostEventAsync(id, ContractEvent.Failed(id, "boom"));

        var phases = new List<string>();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (!line.StartsWith("data: ")) continue;
            phases.Add(JsonDocument.Parse(line[6..]).RootElement.GetProperty("phase").GetString()!);
        }

        Assert.Equal("Starting", phases[0]);
        Assert.Equal("Failed", phases[^1]);
        Assert.Contains("Waiting", phases);
    }

    [Fact]
    public async Task Stream_of_an_unknown_contract_is_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/api/contracts/nope/stream")).StatusCode);
    }

    [Fact]
    public async Task Config_has_the_default_title()
    {
        var config = await _http.GetFromJsonAsync<JsonElement>("/api/config");

        Assert.Equal(ContractLimits.DefaultTitle, config.GetProperty("defaultTitle").GetString());
    }
}
