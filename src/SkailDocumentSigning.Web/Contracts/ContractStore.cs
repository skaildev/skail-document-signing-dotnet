using System.Threading.Channels;
using SkailDocumentSigning.Contracts;

namespace SkailDocumentSigning.Web.Contracts;

/// <summary>
/// In-memory state of the contracts. The durable state lives in skail; this is the UI view and the "one decision
/// per area" guard. Decisions are registered here before the fire, and the final result comes from the worker.
/// </summary>
public sealed class ContractStore(TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private readonly Dictionary<string, ContractState> _contracts = [];
    private readonly Dictionary<string, List<Channel<ContractState>>> _subscribers = [];

    public ContractState Create(string contractId, string title, int deadlineSeconds)
    {
        lock (_gate)
        {
            var contract = new ContractState(contractId, title, deadlineSeconds, null, ContractPhase.Starting,
                AreaView.New(Areas.Legal), AreaView.New(Areas.Finance), [], null, null);
            _contracts[contractId] = contract;
            return contract;
        }
    }

    public ContractState? Get(string contractId)
    {
        lock (_gate) return _contracts.GetValueOrDefault(contractId);
    }

    public void Remove(string contractId)
    {
        lock (_gate)
        {
            _contracts.Remove(contractId);
            Complete(contractId);
        }
    }

    /// <summary>
    /// Registers an area's decision. The first one wins; a second from the same area, or one after the
    /// contract finished, is refused so no second event is fired.
    /// </summary>
    public DecisionOutcome TryDecide(string contractId, Signature signature)
    {
        lock (_gate)
        {
            if (!_contracts.TryGetValue(contractId, out var contract)) return DecisionOutcome.NotFound;
            if (contract.IsTerminal) return DecisionOutcome.Finished;
            if (contract.AreaOf(signature.Area).Status != AreaStatus.Pending) return DecisionOutcome.Duplicate;

            var status = signature.Signed ? AreaStatus.Signed : AreaStatus.Refused;
            var verb = signature.Signed ? "signed" : "refused";
            var reason = signature.Signed || string.IsNullOrWhiteSpace(signature.Reason) ? "" : $": {signature.Reason}";
            var next = contract.WithArea(signature.Area, a => a with { Status = status, By = signature.By, Reason = signature.Reason, At = signature.At });
            Publish(Add(next, status.ToString(), signature.Area, $"{Areas.Label(signature.Area)} {verb} ({signature.By}){reason}"));
            return DecisionOutcome.Accepted;
        }
    }

    /// <summary>Undoes <see cref="TryDecide"/> when the fire failed, so the same area can try again.</summary>
    public void Release(string contractId, string area)
    {
        lock (_gate)
        {
            if (!_contracts.TryGetValue(contractId, out var contract) || contract.IsTerminal) return;
            if (contract.AreaOf(area).Status is not (AreaStatus.Signed or AreaStatus.Refused)) return;

            var timeline = contract.Timeline.ToList();
            var last = timeline.FindLastIndex(t => t.Area == area && t.Kind is "Signed" or "Refused");
            if (last >= 0) timeline.RemoveAt(last);
            Publish((contract with { Timeline = timeline }).WithArea(area, _ => AreaView.New(area) with { Requested = contract.AreaOf(area).Requested }));
        }
    }

    /// <returns>true when the event changed the contract; false when ignored (unknown or finished contract, duplicate).</returns>
    public bool Apply(ContractEvent evt)
    {
        lock (_gate)
        {
            if (!_contracts.TryGetValue(evt.ContractId, out var contract) || contract.IsTerminal) return false;

            ContractState? next = evt.Stage switch
            {
                ContractStage.Started when contract.Phase == ContractPhase.Starting =>
                    Add(contract with { Phase = ContractPhase.Waiting, StartedAt = _clock.GetUtcNow() }, "Started", null,
                        $"Sent for signature to Legal and Finance (deadline {contract.DeadlineSeconds}s)"),
                ContractStage.SignatureRequested when Areas.IsValid(evt.Area) && !contract.AreaOf(evt.Area!).Requested =>
                    Add(contract.WithArea(evt.Area!, a => a with { Requested = true }), "Requested", evt.Area, $"Signature requested from {Areas.Label(evt.Area!)}"),
                ContractStage.Archived when evt.Result is { } r =>
                    Add(contract with { Phase = ContractPhase.Archived, Result = r }, "Archived", null, $"Contract archived ({r.Reference})"),
                ContractStage.Cancelled when evt.Result is { } r => Cancel(contract, r),
                ContractStage.Expired when evt.Result is { } r => Expire(contract, r),
                ContractStage.Failed => Add(contract with { Phase = ContractPhase.Failed, Error = evt.Error ?? "unknown error" }, "Failed", null, evt.Error ?? "unknown error"),
                _ => null,
            };
            if (next is null) return false;

            Publish(next);
            return true;
        }
    }

    public Subscription Subscribe(string contractId)
    {
        var channel = Channel.CreateUnbounded<ContractState>();
        lock (_gate)
        {
            if (!_subscribers.TryGetValue(contractId, out var list)) _subscribers[contractId] = list = [];
            list.Add(channel);
        }
        return new Subscription(channel.Reader, () =>
        {
            lock (_gate)
            {
                if (_subscribers.TryGetValue(contractId, out var list)) list.Remove(channel);
            }
        });
    }

    private ContractState Cancel(ContractState contract, ContractResult r)
    {
        var next = contract with { Phase = ContractPhase.Cancelled, Result = r };
        if (r.RefusedBy is { } refused && Areas.IsValid(refused) && r.OtherArea is { } outcome)
        {
            // the area that did not refuse: its signature is revoked or its pending request withdrawn
            var other = Areas.Other(refused);
            next = next.WithArea(other, a => a with { Status = outcome == OtherAreaOutcome.Revoked ? AreaStatus.Revoked : AreaStatus.Withdrawn });
        }
        var text = $"Contract cancelled: {Areas.Label(r.RefusedBy ?? "")} refused" + (string.IsNullOrWhiteSpace(r.Reason) ? "" : $" ({r.Reason})");
        return Add(next, "Cancelled", null, text);
    }

    private ContractState Expire(ContractState contract, ContractResult r)
    {
        // the area that signed has its signature revoked; the areas that did not answer have their requests withdrawn
        var signed = r.Signatures.Select(s => s.Area).ToHashSet();
        var next = contract with { Phase = ContractPhase.Expired, Result = r };
        foreach (var area in new[] { Areas.Legal, Areas.Finance })
            next = next.WithArea(area, a => a with { Status = signed.Contains(area) ? AreaStatus.Revoked : AreaStatus.Withdrawn });

        var missing = new[] { Areas.Legal, Areas.Finance }.Where(a => !signed.Contains(a)).Select(Areas.Label);
        return Add(next, "Expired", null, $"Contract expired: {string.Join(" and ", missing)} did not answer in time");
    }

    private ContractState Add(ContractState contract, string kind, string? area, string text) =>
        contract with { Timeline = [.. contract.Timeline, new TimelineEntry(kind, area, text, _clock.GetUtcNow())] };

    // must be called with _gate held
    private void Publish(ContractState state)
    {
        _contracts[state.ContractId] = state;
        if (_subscribers.TryGetValue(state.ContractId, out var list))
            foreach (var channel in list) channel.Writer.TryWrite(state);
        if (state.IsTerminal) Complete(state.ContractId);
    }

    private void Complete(string contractId)
    {
        if (!_subscribers.TryGetValue(contractId, out var list)) return;
        foreach (var channel in list) channel.Writer.TryComplete();
    }

    public sealed class Subscription(ChannelReader<ContractState> reader, Action onDispose) : IDisposable
    {
        public ChannelReader<ContractState> Reader { get; } = reader;
        public void Dispose() => onDispose();
    }
}
