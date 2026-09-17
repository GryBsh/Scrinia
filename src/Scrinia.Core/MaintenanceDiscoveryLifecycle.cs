namespace Scrinia.Core;

internal sealed record MaintenanceLifecyclePage(
    MaintenanceDiscoveryPage Page,
    string? ContinuationToken);

internal sealed class MaintenanceDiscoveryLifecycle
{
    internal static Action? ContinuationEncodingBarrierForTest { get; set; }

    private readonly MaintenanceDiscoveryPageService _service;
    private readonly MaintenanceRunRegistry _registry;

    internal MaintenanceDiscoveryLifecycle(
        MaintenanceDiscoveryPageService service, MaintenanceRunRegistry registry)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    internal MaintenanceLifecyclePage Start(
        MaintenanceWorkspaceKey workspace, string scope, CancellationToken cancellationToken)
    {
        MaintenanceDiscoveryPage page = _service.ReadFirstPage(workspace, scope, cancellationToken);
        return WithToken(page);
    }

    internal MaintenanceLifecyclePage Continue(string token, CancellationToken cancellationToken)
    {
        if (!MaintenanceCursorCodec.TryDecode(token, _registry, out MaintenanceCursor cursor, out _))
            return new(MaintenanceDiscoveryPageService.InvalidContinuation(), null);

        MaintenanceDiscoveryPage page = _service.ReadNextPage(cursor, cancellationToken);
        return WithToken(page);
    }

    private MaintenanceLifecyclePage WithToken(MaintenanceDiscoveryPage page)
    {
        if (page.Continuation is not { } cursor)
            return new(page, null);

        ContinuationEncodingBarrierForTest?.Invoke();
        try
        {
            return new(page, MaintenanceCursorCodec.Encode(cursor, _registry));
        }
        catch (InvalidOperationException)
        {
            return new(MaintenanceDiscoveryPageService.InvalidContinuation(), null);
        }
    }
}
