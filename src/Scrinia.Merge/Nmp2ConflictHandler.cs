namespace Scrinia.Merge;

public static class Nmp2ConflictHandler
{
    public static int Handle(string ancestorPath, string oursPath, string theirsPath, MergeConfig config)
    {
        // 1. Trivial merge: if one side is unchanged from ancestor, take the other
        if (FilesAreIdentical(ancestorPath, oursPath))
        {
            // Ours unchanged, take theirs
            File.Copy(theirsPath, oursPath, overwrite: true);
            CopySidecar(theirsPath, oursPath);
            return 0;
        }

        if (FilesAreIdentical(ancestorPath, theirsPath))
        {
            // Theirs unchanged, keep ours — already resolved
            return 0;
        }

        // 2. Both changed: conflict-as-data
        // %A path is something like .scrinia/topics/api/auth-flow.nmp2
        string dir = Path.GetDirectoryName(oursPath)!;
        string conflictDir = Path.Combine(dir, config.ConflictDir);
        string currentDir = Path.Combine(conflictDir, "current");
        string incomingDir = Path.Combine(conflictDir, "incoming");

        Directory.CreateDirectory(currentDir);
        Directory.CreateDirectory(incomingDir);

        string fileName = Path.GetFileName(oursPath);

        // Copy our version to conflict/current/
        File.Copy(oursPath, Path.Combine(currentDir, fileName), overwrite: true);
        CopySidecar(oursPath, Path.Combine(currentDir, fileName));

        // Copy their version to conflict/incoming/
        File.Copy(theirsPath, Path.Combine(incomingDir, fileName), overwrite: true);
        CopySidecar(theirsPath, Path.Combine(incomingDir, fileName));

        // Keep ours as the "current" version in the original location
        // (git will use %A as the merge result)

        // Write a conflict marker in the meta.json
        MarkMetaConflicted(oursPath);

        // Exit 0 so git sees a clean merge — conflict is tracked as data,
        // discovered by reconcile() on next agent session
        return 0;
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
        // Add "conflicted": true before the closing brace
        meta = meta.TrimEnd().TrimEnd('}') + ",\n  \"conflicted\": true\n}";
        File.WriteAllText(metaPath, meta);
    }
}
