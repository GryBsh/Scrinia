namespace Scrinia.Commands;

/// <summary>Coordinates setup selection and activation around the immutable installer.</summary>
internal static class EmbeddingsBundleSetup
{
    internal static string? ResolveEmbeddingsSource(string? explicitSource, string executableDirectory)
    {
        if (explicitSource is not null) return Path.GetFullPath(explicitSource);
        string manifest = Path.Combine(executableDirectory, "embeddings-bundle.json");
        try
        {
            _ = File.GetAttributes(manifest);
            return Path.GetFullPath(executableDirectory);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    internal static EmbeddingsInstallResult? ValidateEmbeddingsSelection(string pluginVersion) =>
        ValidateEmbeddingsSelection(pluginVersion, WorkspaceSetup.Root);

    internal static EmbeddingsInstallResult? ValidateEmbeddingsSelection(string pluginVersion, string workspaceRoot)
    {
        string? plugin = WorkspaceSetup.GetEnvironmentValue("plugins:embeddings")
            ?? WorkspaceConfig.GetValue(workspaceRoot, "plugins:embeddings");
        string? version = WorkspaceSetup.GetEnvironmentValue("plugins:embeddings:version");
        string? provider = WorkspaceSetup.GetEnvironmentValue("Scrinia:Embeddings:Provider");
        if (provider is not null && !string.Equals(provider, "model2vec", StringComparison.OrdinalIgnoreCase))
            return new(EmbeddingsInstallStatus.SelectionConflict, null,
                "An environment override for Scrinia:Embeddings:Provider would bypass the bundled plugin. Check SCRINIA_EMBEDDINGS_PROVIDER and SCRINIA__EMBEDDINGS__PROVIDER, then retry setup.");
        if ((plugin is not null && plugin != "scri-plugin-embeddings") ||
            (version is not null && version != pluginVersion))
            return new(EmbeddingsInstallStatus.SelectionConflict, null,
                "The embeddings plugin settings conflict with this bundle. Check plugins:embeddings and plugins:embeddings:version, including environment overrides, then retry setup.");
        return null;
    }

    internal static EmbeddingsInstallResult InstallAndConfigure(string source, string home, string workspaceRoot,
        string version, string rid, CancellationToken ct)
    {
        EmbeddingsInstallResult installed = EmbeddingsBundleInstaller.Install(source, home, version, rid, ct);
        if (installed.Status != EmbeddingsInstallStatus.Success) return installed;
        try
        {
            Scrinia.Core.Configuration.WorkspaceConfig.SetValues(workspaceRoot, new Dictionary<string, string>
            {
                ["plugins:embeddings"] = "scri-plugin-embeddings",
                ["plugins:embeddings:version"] = version,
            });
            return installed;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Scrinia.Core.Configuration.ScriniaConfigurationException)
        {
            return installed with { Status = EmbeddingsInstallStatus.InstalledConfigurationFailed,
                Message = "The embeddings payload is installed, but workspace configuration failed: " + error.Message };
        }
    }

    internal static int ReportFailure(EmbeddingsInstallResult result, string source, string workspaceRoot)
    {
        Console.Error.WriteLine($"[scrinia:error] {result.Status}: {result.Message}");
        if (result.Destination is not null)
            Console.Error.WriteLine(result.Status is EmbeddingsInstallStatus.InstalledConfigurationFailed or EmbeddingsInstallStatus.InstalledModelPreparationFailed
                ? $"[scrinia] Installed embeddings payload retained at {result.Destination}."
                : $"[scrinia] Installation destination: {result.Destination}");
        string quotedSource = PlatformQuoting.Current(source);
        string quotedWorkspace = PlatformQuoting.Current(workspaceRoot);
        bool sourcePlaceholder = quotedSource == "<workspace-root>";
        bool workspacePlaceholder = quotedWorkspace == "<workspace-root>";
        if (sourcePlaceholder) quotedSource = "<embeddings-source>";
        Console.Error.WriteLine($"[scrinia] Retry: scri setup --embeddings-source {quotedSource} --workspace-root {quotedWorkspace}");
        if (sourcePlaceholder)
            Console.Error.WriteLine("[scrinia] Replace <embeddings-source> with the bundle directory, quoted for your shell.");
        if (workspacePlaceholder)
            Console.Error.WriteLine("[scrinia] Replace <workspace-root> with the workspace directory, quoted for your shell.");
        return 1;
    }
}
