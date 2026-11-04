using System.Text.Json;
using SkailDocumentSigning.Contracts;

namespace SkailDocumentSigning.Tests;

public class ContractsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);
    private static Signature Signed(string area) => new(area, true, $"someone ({area})", null, T0);
    private static Signature Refused(string area, string reason = "clause 4") => new(area, false, $"someone ({area})", reason, T0);

    [Fact]
    public void Event_names_are_shared_constants_and_match_the_areas()
    {
        Assert.Equal("LEGAL_SIGNATURE", Events.LegalSignature);
        Assert.Equal("FINANCE_SIGNATURE", Events.FinanceSignature);
        Assert.Equal(Events.LegalSignature, Areas.EventName(Areas.Legal));
        Assert.Equal(Events.FinanceSignature, Areas.EventName(Areas.Finance));
    }

    [Theory]
    [InlineData("legal", true)]
    [InlineData("finance", true)]
    [InlineData("Legal", false)]
    [InlineData("rh", false)]
    [InlineData("", false)]
    public void Only_the_two_areas_are_valid(string area, bool valid)
    {
        Assert.Equal(valid, Areas.IsValid(area));
    }

    [Fact]
    public void Other_returns_the_opposite_area()
    {
        Assert.Equal(Areas.Finance, Areas.Other(Areas.Legal));
        Assert.Equal(Areas.Legal, Areas.Other(Areas.Finance));
    }

    [Fact]
    public void Archived_result_carries_both_signatures_and_a_stable_protocol()
    {
        var a = ContractResult.Archived("abcdef12-0000", Signed(Areas.Finance), Signed(Areas.Legal));
        var b = ContractResult.Archived("abcdef12-0000", Signed(Areas.Finance), Signed(Areas.Legal));

        Assert.Equal(ContractEnd.Archived, a.End);
        Assert.Equal(2, a.Signatures.Count);
        Assert.Null(a.RefusedBy);
        Assert.Equal("ARC-ABCDEF12", a.Reference);
        Assert.Equal(a.Reference, b.Reference); // a retry of the command must not invent another protocol
    }

    [Fact]
    public void Cancelled_by_the_first_answer_withdraws_the_pending_request_of_the_other_area()
    {
        var r = ContractResult.Cancelled("c", Refused(Areas.Legal), otherAlreadySigned: false);

        Assert.Equal(ContractEnd.Cancelled, r.End);
        Assert.Equal(Areas.Legal, r.RefusedBy);
        Assert.Equal("clause 4", r.Reason);
        Assert.Equal(OtherAreaOutcome.Withdrawn, r.OtherArea);
        Assert.Null(r.Reference);
        Assert.Single(r.Signatures);
    }

    [Fact]
    public void Cancelled_by_the_second_answer_revokes_the_signature_already_given()
    {
        var r = ContractResult.Cancelled("c", Refused(Areas.Finance), otherAlreadySigned: true);

        Assert.Equal(Areas.Finance, r.RefusedBy);
        Assert.Equal(OtherAreaOutcome.Revoked, r.OtherArea);
    }

    [Fact]
    public void Signature_payload_round_trips_with_the_shared_json_options()
    {
        var json = JsonSerializer.Serialize(Refused(Areas.Legal), Json.Options);

        var back = JsonSerializer.Deserialize<Signature>(json, Json.Options);

        Assert.Equal(Refused(Areas.Legal), back);
    }

    [Fact]
    public void Contract_event_round_trips_with_the_result()
    {
        var evt = ContractEvent.Archived("c", ContractResult.Archived("c", Signed(Areas.Legal), Signed(Areas.Finance)));

        var back = JsonSerializer.Deserialize<ContractEvent>(JsonSerializer.Serialize(evt, Json.Options), Json.Options)!;

        Assert.Equal(ContractStage.Archived, back.Stage);
        Assert.Equal(evt.Result!.Reference, back.Result!.Reference);
        Assert.Equal(2, back.Result.Signatures.Count);
    }

    [Fact]
    public void Event_factories_set_stage_area_and_error()
    {
        Assert.Equal(ContractStage.Started, ContractEvent.Started("c", "Contrato").Stage);
        var req = ContractEvent.SignatureRequested("c", "Contrato", Areas.Finance);
        Assert.Equal((ContractStage.SignatureRequested, Areas.Finance), (req.Stage, req.Area));
        Assert.Equal("boom", ContractEvent.Failed("c", "boom").Error);
    }
}
