using Scrinia.ReleaseTools;

try
{
    if (args.Length != 13 || args[0] != "stage-embeddings")
        throw new ArgumentException("Usage: stage-embeddings --raw <directory> --root <directory> --rid <rid> --version <version> --nuget-root <directory> --policy <file>");
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 1; i < args.Length; i += 2)
        if (!options.TryAdd(args[i], args[i + 1])) throw new ArgumentException($"Duplicate option: {args[i]}");
    string[] names = ["--raw", "--root", "--rid", "--version", "--nuget-root", "--policy"];
    if (options.Keys.Any(key => !names.Contains(key, StringComparer.Ordinal)))
        throw new ArgumentException("Unknown staging option.");
    EmbeddingsBundleStager.Stage(options["--raw"], options["--root"], options["--rid"],
        options["--version"], options["--nuget-root"], options["--policy"]);
    return 0;
}
catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException)
{
    Console.Error.WriteLine($"Embeddings staging failed: {error.Message}");
    return 1;
}
