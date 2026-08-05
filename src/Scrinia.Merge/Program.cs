using Scrinia.Merge;

// Usage: scri-merge meta %O %A %B %P
//        scri-merge nmp2 %O %A %B %P
// Exit 0 = merge resolved, Exit 1 = conflict

if (args.Length < 5)
{
    Console.Error.WriteLine("Usage: scri-merge <meta|nmp2> <ancestor> <ours> <theirs> <pathname>");
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
