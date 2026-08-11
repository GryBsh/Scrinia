using System.Text;

namespace Scrinia.Merge;

public static class Nmp2ConflictHandler
{
    /// <param name="mergedPath">
    /// Git's %P — the pathname the merged result will be stored under. %A is an extensionless
    /// temp file in the repo root, so every path derived from it loses the artifact's identity.
    /// </param>
    public static int Handle(string ancestorPath, string oursPath, string theirsPath,
        string mergedPath, MergeConfig config)
    {
        // 1. Trivial merge: if one side is unchanged from ancestor, take the other.
        // Not reached when git drives a two-branch merge — git resolves an unchanged side by
        // blob OID before consulting a driver. Retained for callers that do reach it.
        if (FilesAreIdentical(ancestorPath, oursPath))
        {
            File.Copy(theirsPath, oursPath, overwrite: true);
            CopySidecar(theirsPath, oursPath);
            return 0;
        }

        if (FilesAreIdentical(ancestorPath, theirsPath))
            return 0;

        // 2. Both changed: conflict-as-data
        string dir = Path.GetDirectoryName(mergedPath)!;
        string dirFull = Path.GetFullPath(dir);
        string conflictDir = Path.GetFullPath(Path.Combine(dirFull, config.ConflictDir));
        if (Path.IsPathRooted(config.ConflictDir) || !IsStrictlyWithin(dirFull, conflictDir))
            throw new InvalidOperationException(
                $"merge.config ConflictDir '{config.ConflictDir}' escapes the artifact directory.");
        string currentDir = Path.Combine(conflictDir, "current");
        string incomingDir = Path.Combine(conflictDir, "incoming");

        Directory.CreateDirectory(currentDir);
        Directory.CreateDirectory(incomingDir);

        // The copies are merge scratch. Committing them would leave stale ours/theirs bytes on
        // disk that reconcile() prefers over the actual conflict on any later merge the driver
        // did not handle. Writing the ignore file here — rather than into .scrinia/.gitignore —
        // covers every ConflictDir name and every workspace, however old.
        File.WriteAllText(Path.Combine(conflictDir, ".gitignore"), "*\n");

        string fileName = Path.GetFileName(mergedPath);

        File.Copy(oursPath, Path.Combine(currentDir, fileName), overwrite: true);
        File.Copy(theirsPath, Path.Combine(incomingDir, fileName), overwrite: true);

        MarkMetaConflicted(Path.GetFullPath(mergedPath));

        // Git adds no markers of its own for a custom driver — whatever the driver
        // leaves in %A becomes the working-tree file. The markers are the detection
        // signal reconcile() scans for; the decodable content normally lives in the conflict/
        // copies, and both callers fall back to the marker body when a copy is absent.
        WriteConflictMarkers(oursPath, theirsPath, fileName);

        return 1;
    }

    private static void WriteConflictMarkers(string oursPath, string theirsPath, string fileName)
    {
        string ours = File.ReadAllText(oursPath);
        string theirs = File.ReadAllText(theirsPath);

        var sb = new StringBuilder();
        sb.Append("<<<<<<< ours:").Append(fileName).Append('\n');
        sb.Append(ours);
        if (!ours.EndsWith('\n')) sb.Append('\n');
        sb.Append("=======\n");
        sb.Append(theirs);
        if (!theirs.EndsWith('\n')) sb.Append('\n');
        sb.Append(">>>>>>> theirs:").Append(fileName).Append('\n');

        File.WriteAllText(oursPath, sb.ToString());
    }

    private static bool FilesAreIdentical(string a, string b)
    {
        if (!File.Exists(a) || !File.Exists(b))
            return false;

        var infoA = new FileInfo(a);
        var infoB = new FileInfo(b);
        if (infoA.Length != infoB.Length)
            return false;

        return File.ReadAllBytes(a).SequenceEqual(File.ReadAllBytes(b));
    }

    private static void CopySidecar(string sourcePath, string destPath)
    {
        string sourceMeta = Path.ChangeExtension(sourcePath, ".meta.json");
        if (File.Exists(sourceMeta))
        {
            string destMeta = Path.ChangeExtension(destPath, ".meta.json");
            File.Copy(sourceMeta, destMeta, overwrite: true);
        }
    }

    private static void MarkMetaConflicted(string nmp2Path)
    {
        string metaPath = Path.ChangeExtension(nmp2Path, ".meta.json");
        if (!File.Exists(metaPath))
            return;

        string meta = File.ReadAllText(metaPath);
        File.WriteAllText(metaPath, InsertConflictedFlag(meta));
    }

    /// <summary>
    /// True when <paramref name="candidateFull"/> is strictly below <paramref name="baseFull"/>.
    ///
    /// <para>Local lexical containment check. Scrinia.Merge is a standalone AOT/trimmed executable
    /// with no Scrinia.Core dependency, so we deliberately do NOT pull in PathContainment here;
    /// the merge driver only needs to reject rooted/traversal ConflictDir values. Lexical only;
    /// does not resolve symlinks.</para>
    ///
    /// <para>Equality returns FALSE, deliberately. A ConflictDir of "." or "" resolves back to the
    /// artifact directory, and the caller writes a .gitignore of "*" into whatever this approves —
    /// so treating "the directory itself" as an acceptable conflict directory silently untracks
    /// every memory at and below it. The separate Scrinia.Core.PathContainment.IsWithin keeps its
    /// equality case; this one is private to this class and has a single caller.</para>
    ///
    /// <para>Both paths are trimmed of a trailing directory separator before comparison. Without
    /// this, a ConflictDir of "./" survives <see cref="Path.GetFullPath(string)"/> as
    /// <c>baseFull + separator</c> — not string-equal to <paramref name="baseFull"/> — and then
    /// trivially satisfies "starts with baseFull + separator" against itself, reopening the same
    /// hole the equality check above was meant to close.</para>
    /// </summary>
    private static bool IsStrictlyWithin(string baseFull, string candidateFull)
    {
        string baseTrimmed = Path.TrimEndingDirectorySeparator(baseFull);
        string candidateTrimmed = Path.TrimEndingDirectorySeparator(candidateFull);
        if (string.Equals(baseTrimmed, candidateTrimmed, StringComparison.Ordinal))
            return false;
        string withSep = baseTrimmed + Path.DirectorySeparatorChar;
        return candidateTrimmed.StartsWith(withSep, StringComparison.Ordinal);
    }

    internal static string InsertConflictedFlag(string metaJson)
    {
        string trimmed = metaJson.TrimEnd();
        int lastBrace = trimmed.LastIndexOf('}');
        if (lastBrace < 0)
            return metaJson; // not an object; leave untouched
        string head = trimmed[..lastBrace].TrimEnd();
        string sep = head.EndsWith('{') ? "" : ","; // empty object → no leading comma
        return head + sep + "\n  \"conflicted\": true\n}";
    }
}
