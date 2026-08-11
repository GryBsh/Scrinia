using Scrinia.Merge;

// Usage: scri-merge meta %O %A %B %P
//        scri-merge nmp2 %O %A %B %P
// Exit 0 = merge resolved, Exit 1 = conflict

if (args.Length < 4)
{
    Console.Error.WriteLine("Usage: scri-merge <meta|nmp2> <ancestor> <ours> <theirs> <pathname>");
    return 1;
}

try
{
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

        string invokedMessage = kind switch
        {
            "meta" => metaMessage,
            "nmp2" => nmp2Message,
            _ => $"unknown driver kind '{kind}'"
        };
        string otherKind = kind == "meta" ? "nmp2" : "meta";
        string otherMessage = kind == "meta" ? nmp2Message : metaMessage;

        // Report the invoked key's outcome. The other key is repaired too — a legacy workspace
        // registered both at four arguments — but `git config --get` exits 1 with empty stderr on a
        // key that was simply never registered, so its message cannot distinguish "absent" from
        // "failed". Stating it as a plain note rather than an error stops a single-driver workspace
        // being told something is broken when nothing is.
        Console.Error.WriteLine(invokedRepaired
            ? $"[scrinia] merge driver invoked without %P — {invokedMessage}; " +
              $"also attempted {otherKind}: {otherMessage}; re-run your merge"
            : $"[scrinia:error] merge driver invoked without %P — {invokedMessage}. " +
              "Re-run: scri setup --multi-user");
        return 1;
    }

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
    // so report the message and let git mark the path unmerged. The four-argument
    // legacy branch is inside this try as well: it makes two git invocations, and
    // GitConfigRepair.TryGit's catch filter covers only Win32Exception and
    // InvalidOperationException — an IOException reading git's stdout would otherwise
    // reach git's merge output as a raw stack trace.
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
