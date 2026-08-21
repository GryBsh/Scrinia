namespace Scrinia.Core.Formats;

/// <summary>What a reader should do with a durable file whose declared version has been
/// compared against the version this build writes.</summary>
public enum FormatDisposition
{
    /// <summary>Proceed normally.</summary>
    Load,

    /// <summary>Readable, but writes to the owning unit must refuse. For a source-of-truth
    /// format, where discarding the file is not an option.</summary>
    LoadReadOnly,

    /// <summary>Discard and re-derive. Valid ONLY for a rebuildable cache, never for a format
    /// that is the only copy of its data.</summary>
    Rebuild,

    /// <summary>Do not load at all.</summary>
    Refuse
}
