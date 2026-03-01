using Scrinia.Core;

namespace Scrinia.Plugin.Abstractions;

// ── Store ────────────────────────────────────────────────────────────────────

public sealed class BeforeStoreContext
{
    public required IMemoryStore Store { get; init; }
    public required string Name { get; init; }
    public required string[] Content { get; init; }
    public required string? Description { get; init; }
    public required string[]? Tags { get; init; }
    public required string[]? Keywords { get; init; }

    /// <summary>Set to true to cancel the operation.</summary>
    public bool Cancel { get; set; }

    /// <summary>Reason for cancellation (returned in the error response).</summary>
    public string? CancelReason { get; set; }
}

public sealed class AfterStoreContext
{
    public required IMemoryStore Store { get; init; }
    public required string Name { get; init; }
    public required string QualifiedName { get; init; }
    public required int ChunkCount { get; init; }
    public required long OriginalBytes { get; init; }

    /// <summary>The original content elements that were stored.</summary>
    public required string[] Content { get; init; }
}

// ── Append ───────────────────────────────────────────────────────────────────

public sealed class BeforeAppendContext
{
    public required IMemoryStore Store { get; init; }
    public required string Name { get; init; }
    public required string Content { get; init; }

    public bool Cancel { get; set; }
    public string? CancelReason { get; set; }
}

public sealed class AfterAppendContext
{
    public required IMemoryStore Store { get; init; }
    public required string Name { get; init; }
    public required int ChunkCount { get; init; }
    public required long OriginalBytes { get; init; }

    /// <summary>The content that was appended.</summary>
    public required string Content { get; init; }
}

// ── Forget ───────────────────────────────────────────────────────────────────

public sealed class BeforeForgetContext
{
    public required IMemoryStore Store { get; init; }
    public required string Name { get; init; }

    public bool Cancel { get; set; }
    public string? CancelReason { get; set; }
}

public sealed class AfterForgetContext
{
    public required IMemoryStore Store { get; init; }
    public required string Name { get; init; }
    public required bool WasDeleted { get; init; }
}
