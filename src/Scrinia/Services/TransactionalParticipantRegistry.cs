using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Plugin.Abstractions;

namespace Scrinia.Services;

internal sealed class TransactionalParticipantAdapterFactory : IMultiMemoryParticipantAdapterFactory
{
    private readonly Dictionary<string, ITransactionalMemoryParticipant> _participants;

    internal TransactionalParticipantAdapterFactory(IEnumerable<ITransactionalMemoryParticipant> participants)
    {
        ArgumentNullException.ThrowIfNull(participants);
        var supplied = participants.ToArray();
        if (supplied.Any(participant => participant is null)) throw new ArgumentException("Participants cannot contain null values.", nameof(participants));
        var ordered = supplied.OrderBy(participant => participant.Descriptor.ParticipantIdentity, StringComparer.Ordinal).ToArray();
        if (ordered.Select(participant => participant.Descriptor.ParticipantIdentity).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new ArgumentException("Participant identities must be unique.", nameof(participants));
        if (ordered.Select(participant => participant.Descriptor.OrderingKey).Distinct(StringComparer.Ordinal).Count() != ordered.Length)
            throw new ArgumentException("Participant ordering keys must be unique.", nameof(participants));
        _participants = ordered.ToDictionary(participant => participant.Descriptor.ParticipantIdentity, StringComparer.Ordinal);
    }

    public Task<IMultiMemoryParticipantAdapter> CreateAsync(MultiMemoryParticipantRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (request.ParticipantIdentity is null || request.ContractVersion != 1 || request.OrderingKey is null || request.Payload is null || request.Metadata is null)
            throw new MultiMemoryParticipantMismatchException();
        if (!_participants.TryGetValue(request.ParticipantIdentity, out var participant) ||
            participant.Descriptor.ContractVersion != request.ContractVersion || participant.Descriptor.OrderingKey != request.OrderingKey)
            throw new MultiMemoryParticipantMismatchException();
        if (request.TransactionId is null || request.Scope is null)
            throw new MultiMemoryParticipantMismatchException();
        return Task.FromResult<IMultiMemoryParticipantAdapter>(new TransactionalParticipantAdapter(participant, request));
    }

    public Task<IMultiMemoryParticipantAdapter> RehydrateAsync(B4ParticipantRehydrationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        var state = request.State;
        bool handlelessEvidenceOnly = state.PreparedHandleReference is null &&
            state.PreparedHandleVersion is null &&
            state.LifecycleState is TransactionalParticipantLifecycleState.Changed or TransactionalParticipantLifecycleState.Ambiguous;
        if ((!handlelessEvidenceOnly && state.QuarantineState != TransactionalQuarantineState.None) ||
            (!handlelessEvidenceOnly && (state.PreparedHandleReference is null || state.PreparedHandleVersion is null)) ||
            state.ContractVersion != 1 || !state.Capabilities.SequenceEqual(request.Capabilities, StringComparer.Ordinal) ||
            !string.Equals(state.Address, request.Manifest.Subject, StringComparison.Ordinal) || state.OperationId != request.Manifest.OperationId || state.Kind != request.Manifest.Kind)
            throw new MultiMemoryParticipantMismatchException();
        if (!_participants.TryGetValue(state.ParticipantIdentity, out var participant) || participant.Descriptor.ContractVersion != state.ContractVersion ||
            participant.Descriptor.OrderingKey != state.OrderingKey || !participant.Descriptor.Capabilities.SequenceEqual(state.Capabilities, StringComparer.Ordinal))
            throw new MultiMemoryParticipantMismatchException();
        return Task.FromResult<IMultiMemoryParticipantAdapter>(new TransactionalParticipantAdapter(participant, state));
    }
}
