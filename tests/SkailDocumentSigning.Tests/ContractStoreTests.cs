using SkailDocumentSigning.Contracts;
using SkailDocumentSigning.Web.Contracts;

namespace SkailDocumentSigning.Tests;

public class ContractStoreTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static Signature Signed(string area, string by = "Ana") => new(area, true, by, null, T0);
    private static Signature Refused(string area) => new(area, false, "Beto", "wrong amount", T0);

    private static ContractStore NewStore(out ContractState created, string id = "c1")
    {
        var store = new ContractStore();
        created = store.Create(id, "Agreement X", 120);
        return store;
    }

    private static ContractResult Archived(string id = "c1") =>
        ContractResult.Archived(id, Signed(Areas.Legal), Signed(Areas.Finance));

    [Fact]
    public void A_new_contract_has_both_areas_pending_and_is_starting()
    {
        NewStore(out var c);

        Assert.Equal(ContractPhase.Starting, c.Phase);
        Assert.True(c.Active);
        Assert.Equal(AreaStatus.Pending, c.Legal.Status);
        Assert.Equal(AreaStatus.Pending, c.Finance.Status);
        Assert.Equal("Agreement X", c.Title);
    }

    [Fact]
    public void Started_stamps_the_start_and_shows_the_deadline()
    {
        var store = NewStore(out var created);
        Assert.Equal((120, (DateTimeOffset?)null), (created.DeadlineSeconds, created.StartedAt));

        store.Apply(ContractEvent.Started("c1", "Agreement X"));

        var c = store.Get("c1")!;
        Assert.NotNull(c.StartedAt);
        Assert.Equal("Sent for signature to Legal and Finance (deadline 120s)", c.Timeline[^1].Text);
    }

    [Fact]
    public void Expired_revokes_the_signature_given_withdraws_the_pending_request_and_refuses_new_decisions()
    {
        var store = NewStore(out _);
        store.Apply(ContractEvent.Started("c1", "Agreement X"));
        store.TryDecide("c1", Signed(Areas.Finance));

        Assert.True(store.Apply(ContractEvent.Expired("c1", ContractResult.Expired("c1", Signed(Areas.Finance)))));

        var c = store.Get("c1")!;
        Assert.Equal((ContractPhase.Expired, AreaStatus.Withdrawn, AreaStatus.Revoked), (c.Phase, c.Legal.Status, c.Finance.Status));
        Assert.Equal("Contract expired: Legal did not answer in time", c.Timeline[^1].Text);
        Assert.Equal(DecisionOutcome.Finished, store.TryDecide("c1", Signed(Areas.Legal)));

        var other = NewStore(out _, "c2");
        other.Apply(ContractEvent.Expired("c2", ContractResult.Expired("c2", null)));
        Assert.Equal("Contract expired: Legal and Finance did not answer in time", other.Get("c2")!.Timeline[^1].Text);
    }

    [Fact]
    public void Started_moves_the_contract_to_waiting_and_a_duplicate_is_ignored()
    {
        var store = NewStore(out _);

        Assert.True(store.Apply(ContractEvent.Started("c1", "Agreement X")));
        Assert.False(store.Apply(ContractEvent.Started("c1", "Agreement X")));

        Assert.Equal(ContractPhase.Waiting, store.Get("c1")!.Phase);
    }

    [Fact]
    public void SignatureRequested_is_recorded_once_per_area()
    {
        var store = NewStore(out _);

        Assert.True(store.Apply(ContractEvent.SignatureRequested("c1", "t", Areas.Legal)));
        Assert.False(store.Apply(ContractEvent.SignatureRequested("c1", "t", Areas.Legal)));
        Assert.True(store.Apply(ContractEvent.SignatureRequested("c1", "t", Areas.Finance)));

        var c = store.Get("c1")!;
        Assert.True(c.Legal.Requested);
        Assert.True(c.Finance.Requested);
    }

    [Theory]
    [InlineData("legal", "finance")]
    [InlineData("finance", "legal")]
    public void Free_order_both_orders_end_with_both_signed_and_the_contract_archived(string first, string second)
    {
        var store = NewStore(out _);

        Assert.Equal(DecisionOutcome.Accepted, store.TryDecide("c1", Signed(first)));
        Assert.Equal(DecisionOutcome.Accepted, store.TryDecide("c1", Signed(second)));
        store.Apply(ContractEvent.Archived("c1", Archived()));

        var c = store.Get("c1")!;
        Assert.Equal(ContractPhase.Archived, c.Phase);
        Assert.Equal((AreaStatus.Signed, AreaStatus.Signed), (c.Legal.Status, c.Finance.Status));
        Assert.Equal("ARC-C1", c.Result!.Reference);
    }

    [Fact]
    public void One_signature_alone_does_not_finish_the_contract()
    {
        var store = NewStore(out _);

        store.TryDecide("c1", Signed(Areas.Finance));

        var c = store.Get("c1")!;
        Assert.True(c.Active);
        Assert.Equal(AreaStatus.Signed, c.Finance.Status);
        Assert.Equal(AreaStatus.Pending, c.Legal.Status);
    }

    [Fact]
    public void Refusal_is_recorded_with_who_and_why()
    {
        var store = NewStore(out _);

        store.TryDecide("c1", Refused(Areas.Legal));

        var area = store.Get("c1")!.Legal;
        Assert.Equal((AreaStatus.Refused, "Beto", "wrong amount"), (area.Status, area.By, area.Reason));
    }

    [Fact]
    public void A_second_decision_of_the_same_area_is_a_duplicate_and_the_first_prevails()
    {
        var store = NewStore(out _);
        store.TryDecide("c1", Signed(Areas.Legal, "Ana"));

        Assert.Equal(DecisionOutcome.Duplicate, store.TryDecide("c1", Refused(Areas.Legal)));

        var area = store.Get("c1")!.Legal;
        Assert.Equal((AreaStatus.Signed, "Ana"), (area.Status, area.By));
    }

    [Fact]
    public void A_decision_after_the_contract_finished_is_rejected()
    {
        var store = NewStore(out _);
        store.TryDecide("c1", Refused(Areas.Legal));
        store.Apply(ContractEvent.Cancelled("c1", ContractResult.Cancelled("c1", Refused(Areas.Legal), false)));

        Assert.Equal(DecisionOutcome.Finished, store.TryDecide("c1", Signed(Areas.Finance)));
    }

    [Fact]
    public void Deciding_an_unknown_contract_is_not_found()
    {
        Assert.Equal(DecisionOutcome.NotFound, new ContractStore().TryDecide("nope", Signed(Areas.Legal)));
    }

    [Fact]
    public void Release_puts_the_area_back_to_pending_so_the_fire_can_be_retried()
    {
        var store = NewStore(out _);
        store.TryDecide("c1", Signed(Areas.Legal));

        store.Release("c1", Areas.Legal);

        Assert.Equal(AreaStatus.Pending, store.Get("c1")!.Legal.Status);
        Assert.Equal(DecisionOutcome.Accepted, store.TryDecide("c1", Refused(Areas.Legal)));
    }

    [Fact]
    public void Cancelled_event_makes_the_contract_terminal_with_the_result()
    {
        var store = NewStore(out _);
        var refusal = Refused(Areas.Legal);
        store.TryDecide("c1", refusal);

        store.Apply(ContractEvent.Cancelled("c1", ContractResult.Cancelled("c1", refusal, false)));

        var c = store.Get("c1")!;
        Assert.Equal(ContractPhase.Cancelled, c.Phase);
        Assert.False(c.Active);
        Assert.Equal(OtherAreaOutcome.Withdrawn, c.Result!.OtherArea);
    }

    [Fact]
    public void Failed_event_is_terminal_with_the_error_and_later_events_are_ignored()
    {
        var store = NewStore(out _);

        store.Apply(ContractEvent.Failed("c1", "boom"));

        Assert.False(store.Apply(ContractEvent.Started("c1", "t")));
        var c = store.Get("c1")!;
        Assert.Equal((ContractPhase.Failed, "boom"), (c.Phase, c.Error));
    }

    [Fact]
    public void Timeline_lists_the_events_in_the_order_they_arrived()
    {
        var store = NewStore(out _);

        store.Apply(ContractEvent.Started("c1", "t"));
        store.Apply(ContractEvent.SignatureRequested("c1", "t", Areas.Legal));
        store.TryDecide("c1", Signed(Areas.Finance));
        store.TryDecide("c1", Signed(Areas.Legal));
        store.Apply(ContractEvent.Archived("c1", Archived()));

        var kinds = store.Get("c1")!.Timeline.Select(t => t.Kind).ToArray();
        Assert.Equal(["Started", "Requested", "Signed", "Signed", "Archived"], kinds);
        Assert.Equal([Areas.Finance, Areas.Legal], store.Get("c1")!.Timeline.Where(t => t.Kind == "Signed").Select(t => t.Area));
    }

    [Fact]
    public async Task Subscribers_receive_each_update_and_the_channel_closes_when_terminal()
    {
        var store = NewStore(out _);
        using var sub = store.Subscribe("c1");

        store.Apply(ContractEvent.Started("c1", "t"));
        store.TryDecide("c1", Signed(Areas.Legal));
        store.Apply(ContractEvent.Failed("c1", "boom"));

        var phases = new List<ContractPhase>();
        await foreach (var s in sub.Reader.ReadAllAsync()) phases.Add(s.Phase);

        Assert.Equal([ContractPhase.Waiting, ContractPhase.Waiting, ContractPhase.Failed], phases);
    }

    [Fact]
    public void Remove_forgets_the_contract()
    {
        var store = NewStore(out _);

        store.Remove("c1");

        Assert.Null(store.Get("c1"));
    }
}
