using Scrinia.Core.Models;

namespace Scrinia.Core.Formats;

/// <summary>The one place that decides what a declared format version means.</summary>
public static class FormatVersion
{
    /// <summary>
    /// Compares a file's declared version against its contract.
    ///
    /// <para>The arithmetic is deliberately trivial. The value is not the arithmetic — it is
    /// that a durable format cannot exist without answering four questions, and that the
    /// answers live in one readable table instead of inline at seven read sites.</para>
    /// </summary>
    public static FormatDisposition Evaluate(FormatContract contract, int? declared)
    {
        ArgumentNullException.ThrowIfNull(contract);

        // Absent is SUBSTITUTED, not short-circuited: a contract whose AbsentMeans is below
        // Current must still land on OnOlder. Returning Load here would silently exempt every
        // unversioned file from the older-version policy.
        int effective;
        if (declared is null)
        {
            if (contract.AbsentMeans is null) return FormatDisposition.Refuse;
            effective = contract.AbsentMeans.Value;
        }
        else
        {
            effective = declared.Value;
        }

        if (effective == contract.Current) return FormatDisposition.Load;
        return effective < contract.Current ? contract.OnOlder : contract.OnNewer;
    }
}

/// <summary>The artifact sidecar's half of the gate, in one place so no caller re-derives it.</summary>
public static class ArtifactSidecarPolicy
{
    /// <summary>True when this entry was written by a build newer than ours, so its sidecar
    /// must not be rewritten. Reading it is always allowed.</summary>
    public static bool IsReadOnly(ArtifactEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return FormatVersion.Evaluate(DurableFormats.ArtifactSidecar, entry.SchemaVersion)
            == FormatDisposition.LoadReadOnly;
    }
}
