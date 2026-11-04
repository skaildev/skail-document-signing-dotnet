using Microsoft.Extensions.Logging.Abstractions;
using SkailDocumentSigning.Contracts;
using SkailDocumentSigning.Worker;
using SkailDocumentSigning.Worker.Progress;

namespace SkailDocumentSigning.Tests;

public sealed class RecordingNotifier : IProgressNotifier
{
    public List<ContractEvent> Events { get; } = [];
    public Exception? FailWith { get; set; }

    public Task NotifyAsync(ContractEvent evt, CancellationToken ct = default)
    {
        if (FailWith is not null) throw FailWith;
        Events.Add(evt);
        return Task.CompletedTask;
    }
}

/// <summary>Commands need the skail runtime; the logic they delegate to is tested here.</summary>
public class OperationsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static Signature Signed(string area) => new(area, true, "Ana", null, T0);
    private static Signature Refused(string area) => new(area, false, "Beto", "wrong amount", T0);

    private static (ContractOperations Ops, RecordingNotifier Notifier) Create()
    {
        var notifier = new RecordingNotifier();
        return (new ContractOperations(notifier, NullLogger<ContractOperations>.Instance), notifier);
    }

    [Theory]
    [InlineData("legal")]
    [InlineData("finance")]
    public async Task RequestSignature_delivers_the_request_to_the_right_area(string area)
    {
        var (ops, notifier) = Create();

        await ops.RequestSignatureAsync("c1", "Agreement X", area);

        var evt = Assert.Single(notifier.Events);
        Assert.Equal((ContractStage.SignatureRequested, area, "c1", "Agreement X"), (evt.Stage, evt.Area, evt.ContractId, evt.Title));
    }

    [Fact]
    public async Task RequestSignature_rejects_an_unknown_area_without_notifying()
    {
        var (ops, notifier) = Create();

        await Assert.ThrowsAsync<ArgumentException>(() => ops.RequestSignatureAsync("c1", "t", "rh"));

        Assert.Empty(notifier.Events);
    }

    [Fact]
    public async Task RequestSignature_throws_when_the_delivery_fails_so_skail_retries()
    {
        var (ops, notifier) = Create();
        notifier.FailWith = new HttpRequestException("web down");

        await Assert.ThrowsAsync<HttpRequestException>(() => ops.RequestSignatureAsync("c1", "t", "legal"));
    }

    [Fact]
    public async Task Archive_returns_both_signatures_and_notifies_archived()
    {
        var (ops, notifier) = Create();

        var result = await ops.ArchiveContractAsync("abcdef12", Signed("finance"), Signed("legal"));

        Assert.Equal(ContractEnd.Archived, result.End);
        Assert.Equal(2, result.Signatures.Count);
        var evt = Assert.Single(notifier.Events);
        Assert.Equal(ContractStage.Archived, evt.Stage);
        Assert.Equal(result.Reference, evt.Result!.Reference);
    }

    [Fact]
    public async Task Archive_refuses_a_refused_signature()
    {
        var (ops, notifier) = Create();

        await Assert.ThrowsAsync<InvalidOperationException>(() => ops.ArchiveContractAsync("c", Signed("legal"), Refused("finance")));

        Assert.Empty(notifier.Events);
    }

    [Fact]
    public async Task Archive_is_idempotent_for_the_result()
    {
        var (ops, _) = Create();

        var a = await ops.ArchiveContractAsync("abcdef12", Signed("legal"), Signed("finance"));
        var b = await ops.ArchiveContractAsync("abcdef12", Signed("legal"), Signed("finance"));

        Assert.Equal(a.Reference, b.Reference);
    }

    [Theory]
    [InlineData(false, OtherAreaOutcome.Withdrawn)]
    [InlineData(true, OtherAreaOutcome.Revoked)]
    public async Task Cancel_withdraws_or_revokes_the_other_area_and_reports_who_refused(bool otherAlreadySigned, OtherAreaOutcome expected)
    {
        var (ops, notifier) = Create();

        var result = await ops.CancelContractAsync("c", Refused("legal"), otherAlreadySigned);

        Assert.Equal(ContractEnd.Cancelled, result.End);
        Assert.Equal(("legal", "wrong amount", expected), (result.RefusedBy, result.Reason, result.OtherArea));
        var evt = Assert.Single(notifier.Events);
        Assert.Equal(ContractStage.Cancelled, evt.Stage);
        Assert.Equal(expected, evt.Result!.OtherArea);
    }

    [Fact]
    public async Task Cancel_refuses_a_signature_that_was_not_a_refusal()
    {
        var (ops, notifier) = Create();

        await Assert.ThrowsAsync<InvalidOperationException>(() => ops.CancelContractAsync("c", Signed("legal"), false));

        Assert.Empty(notifier.Events);
    }

    [Fact]
    public async Task Expire_reports_the_signature_already_given_or_none()
    {
        var (ops, notifier) = Create();

        var withOne = await ops.ExpireContractAsync("c", Signed("finance"));
        var withNone = await ops.ExpireContractAsync("c2", null);

        Assert.Equal((ContractEnd.Expired, "finance"), (withOne.End, Assert.Single(withOne.Signatures).Area));
        Assert.Empty(withNone.Signatures);
        Assert.Equal([ContractStage.Expired, ContractStage.Expired], notifier.Events.Select(e => e.Stage));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ops.ExpireContractAsync("c3", Refused("legal")));
    }

    [Fact]
    public async Task ReportStatus_forwards_the_event()
    {
        var (ops, notifier) = Create();

        await ops.ReportStatusAsync(ContractEvent.Started("c", "t"));

        Assert.Equal(ContractStage.Started, Assert.Single(notifier.Events).Stage);
    }
}
