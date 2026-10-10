namespace Scrinia.ReleaseTools;

internal static class PublicationCommands
{
    internal static int Run(string[] args)
    {
        try
        {
            if (args.Length == 0 || args.Length % 2 != 1) throw new ArgumentException("Command and option/value pairs required.");
            var options = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 1; i < args.Length; i += 2)
                if (!options.TryAdd(args[i], args[i + 1])) throw new ArgumentException("Duplicate option.");
            string[] required = args[0] switch
            {
                "verify-artifacts" => ["--manifest", "--root", "--sha", "--version"],
                "verify-progress" => ["--progress", "--manifest", "--sha", "--version", "--run"],
                "compare-package" => ["--original", "--remote", "--output"],
                "verify-validation-archive" => ["--archive", "--manifest", "--digest"],
                _ => throw new ArgumentException("Unknown publication validator command.")
            };
            if (required.Length != options.Count || required.Any(n => !options.ContainsKey(n))) throw new ArgumentException("Invalid validator options.");
            switch (args[0])
            {
                case "verify-artifacts": ReleaseArtifactVerifier.Verify(options["--manifest"], options["--root"], options["--sha"], options["--version"]); break;
                case "verify-progress": PublicationProgressVerifier.Verify(options["--progress"], options["--manifest"], options["--sha"], options["--version"], options["--run"]); break;
                case "compare-package": PublicationProgressVerifier.ComparePackage(options["--original"], options["--remote"], options["--output"]); break;
                case "verify-validation-archive": PublicationProgressVerifier.VerifyValidationArchive(options["--archive"], options["--manifest"], options["--digest"]); break;
            }
            return 0;
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or System.Xml.XmlException)
        { Console.Error.WriteLine("Publication validation failed: " + error.Message); return 1; }
    }
}
