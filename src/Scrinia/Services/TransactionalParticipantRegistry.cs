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
        ArgumentNullException.ThrowIfNull(request);
        if (request.ParticipantIdentity is null || request.ContractVersion != 1 || request.OrderingKey is null || request.Payload is null || request.Metadata is null)
            throw new MultiMemoryParticipantMismatchException();
        if (!_participants.TryGetValue(request.ParticipantIdentity, out var participant) ||
            participant.Descriptor.ContractVersion != request.ContractVersion || participant.Descriptor.OrderingKey != request.OrderingKey)
            throw new MultiMemoryParticipantMismatchException();
        return Task.FromResult<IMultiMemoryParticipantAdapter>(new TransactionalParticipantAdapter(participant, request));
    }

    public Task<IMultiMemoryParticipantAdapter> RehydrateAsync(B4ParticipantRehydrationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var state = request.State;
        if (state.QuarantineState != TransactionalQuarantineState.None || state.PreparedHandleReference is null ||
            state.PreparedHandleVersion is null || state.ContractVersion != 1 || !state.Capabilities.SequenceEqual(request.Capabilities, StringComparer.Ordinal) ||
            !string.Equals(state.Address, request.Manifest.Subject, StringComparison.Ordinal) || state.OperationId != request.Manifest.OperationId || state.Kind != request.Manifest.Kind)
            throw new MultiMemoryParticipantMismatchException();
        if (!_participants.TryGetValue(state.ParticipantIdentity, out var participant) || participant.Descriptor.ContractVersion != state.ContractVersion ||
            participant.Descriptor.OrderingKey != state.OrderingKey || !participant.Descriptor.Capabilities.SequenceEqual(state.Capabilities, StringComparer.Ordinal))
            throw new MultiMemoryParticipantMismatchException();
        return Task.FromResult<IMultiMemoryParticipantAdapter>(new TransactionalParticipantAdapter(participant, state));
    }
}

internal sealed class TransactionalParticipantAdapter : IMultiMemoryParticipantAdapter
{
    private readonly ITransactionalMemoryParticipant _participant;
    private readonly TransactionalCommitContext _commitContext;
    private readonly TransactionalRollbackContext _rollbackContext;

    internal TransactionalParticipantAdapter(ITransactionalMemoryParticipant participant, MultiMemoryParticipantRequest request)
    {
        _participant = participant;
        var address = request.Address;
        var handle = new TransactionalPreparedHandle("pending", "1");
        _commitContext = new(request.OperationId, request.OperationId, address, request.OperationId, request.Kind, handle);
        _rollbackContext = new(request.OperationId, request.OperationId, address, request.OperationId, request.Kind, handle);
    }

    internal TransactionalParticipantAdapter(ITransactionalMemoryParticipant participant, B4ParticipantRecoveryState state)
    {
        _participant = participant;
        var address = MemoryAddress.Parse(state.Address);
        var handle = new TransactionalPreparedHandle(state.PreparedHandleReference!, state.PreparedHandleVersion!);
        _commitContext = new(state.TransactionId, state.Scope, address, state.OperationId, state.Kind, handle);
        _rollbackContext = new(state.TransactionId, state.Scope, address, state.OperationId, state.Kind, handle);
    }

    public string Kind => _commitContext.Kind; public string Scope => _commitContext.Scope; public string Subject => _commitContext.Address.ToString();
    public string OperationId => _commitContext.OperationId; public string EvidencePath => string.Empty;
    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        var result = await _participant.PrepareAsync(new(_commitContext.TransactionId, _commitContext.Scope, _commitContext.Address,
            _commitContext.OperationId, _commitContext.Kind, Array.Empty<TransactionalMemoryManifestEntry>()), cancellationToken);
        if (result.Handle is null) throw new MultiMemoryParticipantMismatchException();
    }
    public async Task<MultiMemoryCommitStatus> GetCommitStatusAsync(CancellationToken cancellationToken) =>
        (await _participant.GetCommitStatusAsync(_commitContext, cancellationToken)).State switch
        { TransactionalCommitStatus.Prepared => MultiMemoryCommitStatus.Prepared, TransactionalCommitStatus.Committed => MultiMemoryCommitStatus.Committed, _ => MultiMemoryCommitStatus.Unknown };
    public async Task CommitAsync(CancellationToken cancellationToken) { var result = await _participant.CommitAsync(_commitContext, cancellationToken); if (result.State != TransactionalParticipantState.Committed) throw new MultiMemoryParticipantMismatchException(); }
    public async Task VerifyCommittedAsync(CancellationToken cancellationToken) { if ((await GetCommitStatusAsync(cancellationToken)) != MultiMemoryCommitStatus.Committed) throw new MultiMemoryParticipantMismatchException(); }
    public async Task RollbackAsync(CancellationToken cancellationToken) { var result = await _participant.RollbackAsync(_rollbackContext, cancellationToken); if (result.State != TransactionalParticipantState.RolledBack) throw new MultiMemoryParticipantMismatchException(); }
    public Task CleanupAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
