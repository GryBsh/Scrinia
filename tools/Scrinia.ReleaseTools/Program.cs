using Scrinia.ReleaseTools;

try
{
    if (args.Length > 0 && args[0] is "verify-progress" or "compare-package" or "verify-validation-archive") return PublicationCommands.Run(args);
    if (args.Length == 0 || args.Length % 2 != 1)
        throw new ArgumentException("Supply a command followed by option/value pairs. Commands: stage-embeddings, verify-artifacts, lifecycle, native-probe.");
    var options = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 1; i < args.Length; i += 2)
        if (!options.TryAdd(args[i], args[i + 1])) throw new ArgumentException($"Duplicate option: {args[i]}");
    string[] names = args[0] switch
    {
        "stage-embeddings" => ["--raw", "--root", "--rid", "--version", "--nuget-root", "--policy"],
        "verify-artifacts" => ["--manifest", "--root", "--sha", "--version"],
        "lifecycle" => ["--package", "--baseline-package", "--bundle-root", "--output"],
        "native-probe" => ["--payload", "--rid"],
        _ => throw new ArgumentException($"Unknown release-tool command '{args[0]}'. Commands: stage-embeddings, verify-artifacts, lifecycle, native-probe.")
    };
    if (options.Count != names.Length || options.Keys.Any(key => !names.Contains(key, StringComparer.Ordinal)))
        throw new ArgumentException($"Invalid options for '{args[0]}'. Required options: {string.Join(", ", names)}.");
    switch (args[0])
    {
        case "verify-artifacts": ReleaseArtifactVerifier.Verify(options["--manifest"], options["--root"], options["--sha"], options["--version"]); break;
        case "native-probe": NativePayloadProbe.Run(options["--payload"], options["--rid"]); break;
        case "lifecycle": await ReleaseLifecycle.RunAsync(options["--package"], options["--baseline-package"], options["--bundle-root"], options["--output"]); break;
        case "stage-embeddings": EmbeddingsBundleStager.Stage(options["--raw"], options["--root"], options["--rid"],
            options["--version"], options["--nuget-root"], options["--policy"]); break;
    }
    return 0;
}
catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException or DllNotFoundException or BadImageFormatException or OperationCanceledException)
{
    Console.Error.WriteLine($"Release validation failed: {error.Message}");
    return 1;
}
