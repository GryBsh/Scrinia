using Scrinia.Merge;

// Usage: scri-merge meta %O %A %B %P
//        scri-merge nmp2 %O %A %B %P
// Exit 0 = merge resolved, Exit 1 = conflict

if (args.Length < 4)
{
    Console.Error.WriteLine("Usage: scri-merge <meta|nmp2> <ancestor> <ours> <theirs> <pathname>");
    return 1;
}

if (args.Length < 5)
{
    // Registered before %P was passed. Without the pathname the artifact cannot be
    // identified, so repair the registration and stop rather than guess. Both keys are
    // repaired, not just the invoked one: a legacy workspace registered both at four
    // arguments, and leaving the other behind only defers the same failure to the next merge.
    string kind = args[0];
    bool metaRepaired = GitConfigRepair.TryAppendPathname("meta", out string metaMessage);
    bool nmp2Repaired = GitConfigRepair.TryAppendPathname("nmp2", out string nmp2Message);
    bool invokedRepaired = kind switch
    {
        "meta" => metaRepaired,
        "nmp2" => nmp2Repaired,
        _ => false
    };

    string outcomes = $"meta: {metaMessage}; nmp2: {nmp2Message}";
    Console.Error.WriteLine(invokedRepaired
        ? $"[scrinia] merge driver invoked without %P — {outcomes}; re-run your merge"
        : $"[scrinia:error] merge driver invoked without %P — {outcomes}. " +
          "Re-run: scri setup --multi-user");
    return 1;
}

try
{
    string command = args[0];
    string ancestor = args[1];
    string ours = args[2];
    string theirs = args[3];
    string mergedPath = Path.GetFullPath(args[4]);

    var config = MergeConfig.Load(FindScriniaDir(mergedPath));

    return command switch
    {
        "meta" => MetaJsonMerger.Merge(ancestor, ours, theirs, config) switch
        {
            MetaJsonMerger.MergeResult.Resolved => 0,
            MetaJsonMerger.MergeResult.Conflict => 1,
            _ => 1
        },
        "nmp2" => Nmp2ConflictHandler.Handle(ancestor, ours, theirs, mergedPath, config),
        _ => Error($"Unknown command: {command}")
    };
}
catch (Exception ex)
{
    // Git prints whatever the driver writes to stderr inside its merge output, and reads a
    // non-zero exit as a conflict. A raw stack trace there is unreadable and the resulting
    // state — ours' content, no markers, no copies — is the one this driver exists to avoid,
    // so report the message and let git mark the path unmerged.
    Console.Error.WriteLine($"[scrinia:error] {ex.Message}");
    return 1;
}

static int Error(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

static string FindScriniaDir(string filePath)
{
    var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
    while (dir is not null)
    {
        if (Path.GetFileName(dir) == ".scrinia")
            return dir;
        dir = Path.GetDirectoryName(dir);
    }
    // Fallback: assume .scrinia is a sibling of the working directory
    return Path.Combine(Directory.GetCurrentDirectory(), ".scrinia");
}
