using System.Reflection;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Commands;
using Scrinia.Core.Embeddings;
using Scrinia.TestDoubles;

namespace Scrinia.Tests;

[Collection(GlobalWorkspaceConfigCollection.Name)]
public sealed class SetupEmbeddingsBundleTests
{
    [Fact]
    public void ExplicitSourceWinsOverSiblingDiscovery()
    {
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", "win-x64");
        string explicitPath = Path.Combine(fixture.Root, "missing-explicit");
        Assert.Equal(Path.GetFullPath(explicitPath), EmbeddingsBundleSetup.ResolveEmbeddingsSource(explicitPath, fixture.Root));
        Assert.Throws<EmbeddingsBundleException>(() => EmbeddingsBundleValidator.Validate(explicitPath, "0.6.0", "win-x64"));
    }

    [Fact]
    public void SetupExposesExplicitBundleDirectoryBeforeCancellation()
    {
        ParameterInfo[] parameters = typeof(ScriniaCommands).GetMethod("Setup")!.GetParameters();
        Assert.Equal("embeddingsSource", parameters[^2].Name);
        Assert.Equal(typeof(string), parameters[^2].ParameterType);
    }

    private static string Rid => OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsMacOS() ? "osx-arm64" : "linux-x64";

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task PublicSetupRejectsHookConflictBeforeWorkspaceOrHomeMutation(bool hooks, bool uninstall)
    {
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        using var envLock = ProcessEnvironmentLock.Acquire();
        using var home = new EnvironmentVariableScope("SCRINIA_HOME", fixture.Home);
        int exit = await new ScriniaCommands().Setup(fixture.Workspace, hooks: hooks, uninstallHooks: uninstall, embeddingsSource: fixture.Root);
        Assert.Equal(1, exit);
        Assert.False(Directory.Exists(fixture.Home));
        Assert.False(Directory.Exists(Path.Combine(fixture.Workspace, ".scrinia")));
    }

    [Fact]
    public async Task InvalidExplicitSourceCannotFallBackOrCreateWorkspace()
    {
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        using var envLock = ProcessEnvironmentLock.Acquire();
        using var home = new EnvironmentVariableScope("SCRINIA_HOME", fixture.Home);
        int exit = await Run(fixture, Path.Combine(fixture.Root, "missing"));
        Assert.Equal(1, exit);
        Assert.False(Directory.Exists(fixture.Home));
        Assert.False(Directory.Exists(Path.Combine(fixture.Workspace, ".scrinia")));
    }

    [Theory]
    [InlineData("PLUGINS_EMBEDDINGS", "custom-plugin")]
    [InlineData("PLUGINS__EMBEDDINGS", "custom-plugin")]
    [InlineData("PLUGINS_EMBEDDINGS_VERSION", "0.5.0")]
    [InlineData("PLUGINS__EMBEDDINGS__VERSION", "0.5.0")]
    [InlineData("SCRINIA_EMBEDDINGS_PROVIDER", "openai")]
    [InlineData("SCRINIA__EMBEDDINGS__PROVIDER", "ollama")]
    public async Task EnvironmentSelectionConflictPreservesSelectionAndAllowsRetry(string key, string value)
    {
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        using var envLock = ProcessEnvironmentLock.Acquire();
        using var home = new EnvironmentVariableScope("SCRINIA_HOME", fixture.Home);
        using (var conflict = new EnvironmentVariableScope(key, value))
        {
            Assert.Equal(1, await Run(fixture, fixture.Root));
            Assert.Equal(value, Environment.GetEnvironmentVariable(key));
            Assert.False(Directory.Exists(fixture.Home));
            Assert.False(Directory.Exists(Path.Combine(fixture.Workspace, ".scrinia")));
        }
        Assert.Equal(0, await Run(fixture, fixture.Root));
        Assert.Equal("0.6.0", WorkspaceConfig.GetValue(fixture.Workspace, "plugins:embeddings:version"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectoryInjectedSetupInstallsActivatesAndPreservesBundle(bool explicitSource)
    {
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        using var envLock = ProcessEnvironmentLock.Acquire();
        using var home = new EnvironmentVariableScope("SCRINIA_HOME", fixture.Home);
        Dictionary<string, string> before = Snapshot(fixture.Root);
        Assert.Equal(0, await Run(fixture, explicitSource ? fixture.Root : null,
            executableDirectory: explicitSource ? fixture.Workspace : fixture.Root));
        Assert.Equal("scri-plugin-embeddings", WorkspaceConfig.GetValue(fixture.Workspace, "plugins:embeddings"));
        Assert.Equal("0.6.0", WorkspaceConfig.GetValue(fixture.Workspace, "plugins:embeddings:version"));
        Assert.Equal(Path.Combine(fixture.Home, "plugins", "scri-plugin-embeddings", "0.6.0", Rid,
            OperatingSystem.IsWindows() ? "scri-plugin-embeddings.exe" : "scri-plugin-embeddings"), EmbeddingsHost.ResolveEmbeddingsPluginExe());
        Assert.Equal(before.OrderBy(p => p.Key), Snapshot(fixture.Root).OrderBy(p => p.Key));
        Assert.False(File.Exists(Path.Combine(fixture.Home, "migration-state.json")));
    }

    [Fact]
    public async Task SetupPreservesChildConfigMutationAfterParentSnapshotWasCached()
    {
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        using var envLock = ProcessEnvironmentLock.Acquire();
        using var home = new EnvironmentVariableScope("SCRINIA_HOME", fixture.Home);
        WorkspaceConfig.SetValue(fixture.Workspace, "existing", "preserve");
        Assert.Null(WorkspaceConfig.Load(fixture.Workspace).GetValueOrDefault("child-setting"));
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add(typeof(ScriniaCommands).Assembly.Location);
        foreach (string arg in new[] { "config", "child-setting", "fresh", "--workspace-root", fixture.Workspace })
            start.ArgumentList.Add(arg);
        start.Environment["SCRINIA_HOME"] = fixture.Home;
        using var process = System.Diagnostics.Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task<string> output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            Assert.True(process.ExitCode == 0, await output + await error);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
        Assert.Null(WorkspaceConfig.Load(fixture.Workspace).GetValueOrDefault("child-setting"));
        string configPath = Path.Combine(fixture.Workspace, ".scrinia", "config.json");
        Assert.Contains("fresh", File.ReadAllText(configPath));

        Assert.Equal(0, await Run(fixture, fixture.Root));

        using var persisted = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(configPath));
        Assert.Equal("fresh", persisted.RootElement.GetProperty("child-setting").GetString());
        Assert.Equal("preserve", persisted.RootElement.GetProperty("existing").GetString());
        Assert.Equal("scri-plugin-embeddings", persisted.RootElement.GetProperty("plugins:embeddings").GetString());
        Assert.Equal("0.6.0", persisted.RootElement.GetProperty("plugins:embeddings:version").GetString());
    }

    [Fact]
    public async Task TrackingJournalBlocksConfigurationAfterInstallationAndPreservesOldConfig()
    {
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        using var envLock = ProcessEnvironmentLock.Acquire();
        using var home = new EnvironmentVariableScope("SCRINIA_HOME", fixture.Home);
        WorkspaceConfig.SetValue(fixture.Workspace, "other", "preserve");
        string configPath = Path.Combine(fixture.Workspace, ".scrinia", "config.json");
        byte[] before = File.ReadAllBytes(configPath);
        string journal = Path.Combine(fixture.Workspace, ".scrinia", ".tracking-recovery.json");
        File.WriteAllText(journal, "retained evidence");
        var refusal = await CaptureError(() => Run(fixture, fixture.Root));
        Assert.Equal(1, refusal.Exit);
        Assert.Contains("InstalledConfigurationFailed", refusal.Error);
        Assert.Contains("Retry: scri setup", refusal.Error);
        Assert.True(Directory.Exists(Path.Combine(fixture.Home, "plugins", "scri-plugin-embeddings", "0.6.0", Rid)));
        Assert.Equal(before, File.ReadAllBytes(configPath));
        Assert.Equal("retained evidence", File.ReadAllText(journal));
        File.Delete(journal);
        Assert.Equal(0, await Run(fixture, fixture.Root));
        Assert.Equal("preserve", WorkspaceConfig.GetValue(fixture.Workspace, "other"));
    }

    [Fact]
    public async Task FailedVerifiedModelDownloadPreservesInstalledPayloadAndRetrySucceeds()
    {
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        using var envLock = ProcessEnvironmentLock.Acquire();
        using var home = new EnvironmentVariableScope("SCRINIA_HOME", fixture.Home);
        var failure = await CaptureError(() => Run(fixture, fixture.Root, prepareModels: ct => PrepareTinyModel(fixture.Home, ct, fail: true)));
        Assert.Equal(1, failure.Exit);
        Assert.Contains("InstalledModelPreparationFailed", failure.Error);
        Assert.DoesNotContain("Setup complete", failure.Error);
        string destination = Path.Combine(fixture.Home, "plugins", "scri-plugin-embeddings", "0.6.0", Rid);
        Dictionary<string, string> installed = Snapshot(destination);
        Assert.Equal(0, await Run(fixture, fixture.Root));
        Assert.Equal(installed.OrderBy(p => p.Key), Snapshot(destination).OrderBy(p => p.Key));
    }

    [Fact]
    public async Task NoSourceKeepsNormalSetupWithoutPluginInstall()
    {
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        using var envLock = ProcessEnvironmentLock.Acquire();
        using var home = new EnvironmentVariableScope("SCRINIA_HOME", fixture.Home);
        Assert.Equal(0, await Run(fixture, null, executableDirectory: fixture.Workspace));
        Assert.False(Directory.Exists(Path.Combine(fixture.Home, "plugins")));
        Assert.Null(WorkspaceConfig.GetValue(fixture.Workspace, "plugins:embeddings:version"));
    }

    [Fact]
    public async Task PriorMigrationMarkerDoesNotSuppressNewBundleInstall()
    {
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        using var envLock = ProcessEnvironmentLock.Acquire();
        using var home = new EnvironmentVariableScope("SCRINIA_HOME", fixture.Home);
        Directory.CreateDirectory(fixture.Home);
        string marker = Path.Combine(fixture.Home, "migration-state.json");
        const string state = "{\"Models\":true,\"Plugins\":true,\"MigratedAt\":\"old\"}";
        File.WriteAllText(marker, state);
        Assert.Equal(0, await Run(fixture, null));
        Assert.True(Directory.Exists(fixture.PayloadDirectory));
        Assert.Equal(state, File.ReadAllText(marker));
    }

    [Fact]
    public async Task BundleIsInstalledBeforeOllamaCompletionAndProviderChoiceIsPreserved()
    {
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        using var envLock = ProcessEnvironmentLock.Acquire();
        using var home = new EnvironmentVariableScope("SCRINIA_HOME", fixture.Home);
        using var provider = EmbeddingsHost.OverrideEmbeddingProviderForTest(null);
        int exit = await new ScriniaCommands().SetupCoreAsync(fixture.Workspace, false, false, false, false,
            fixture.Root, fixture.Root, "0.6.0", Rid,
            prepareModels: _ => throw new InvalidOperationException("Ollama must skip local model preparation"),
            probeOllama: _ =>
            {
                WorkspaceConfig.SetValue(fixture.Workspace, "Scrinia:Embeddings:Provider", "ollama");
                return Task.FromResult(true);
            });
        Assert.Equal(0, exit);
        Assert.True(Directory.Exists(Path.Combine(fixture.Home, "plugins", "scri-plugin-embeddings", "0.6.0", Rid)));
        Assert.False(Directory.Exists(Path.Combine(fixture.Home, "models")));
        Assert.Equal("ollama", WorkspaceConfig.GetValue(fixture.Workspace, "Scrinia:Embeddings:Provider"));
    }

    [Fact]
    public async Task PublicSetupUsesPortableHostIdentityAndSelectsInstalledPath()
    {
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        using var envLock = ProcessEnvironmentLock.Acquire();
        using var home = new EnvironmentVariableScope("SCRINIA_HOME", fixture.Home);
        Directory.CreateDirectory(fixture.Home);
        File.WriteAllText(Path.Combine(fixture.Home, "models"), "stop before network model preparation");
        var result = await CaptureError(() => new ScriniaCommands().Setup(fixture.Workspace, noOllama: true, embeddingsSource: fixture.Root));
        Assert.Equal(1, result.Exit);
        Assert.Contains("InstalledModelPreparationFailed", result.Error);
        Assert.Contains($"Embeddings plugin 0.6.0 ({Rid}) installed at", result.Error);
        Assert.Equal("0.6.0", WorkspaceConfig.GetValue(fixture.Workspace, "plugins:embeddings:version"));
        var resolution = EmbeddingsHost.ResolvePluginDirectory("scri-plugin-embeddings", "0.6.0");
        Assert.Equal(Path.Combine(fixture.Home, "plugins", "scri-plugin-embeddings", "0.6.0", Rid), resolution.PluginDir);
        Assert.False(resolution.Degraded);
        Assert.NotNull(EmbeddingsHost.ResolveEmbeddingsPluginExe());
    }

    [Theory]
    [InlineData("ubuntu.24.04-x64", "linux", (int)Architecture.X64, true, "linux-x64")]
    [InlineData("ubuntu.24.04-x64", "linux", (int)Architecture.X64, false, null)]
    [InlineData("ubuntu.26.04-x64", "linux", (int)Architecture.X64, true, null)]
    [InlineData("unknown-x64", "linux", (int)Architecture.X64, true, null)]
    [InlineData("linux-musl-x64", "linux", (int)Architecture.X64, true, null)]
    [InlineData("linux-bionic-x64", "linux", (int)Architecture.X64, true, null)]
    [InlineData("ubuntu.24.04-arm64", "linux", (int)Architecture.Arm64, true, null)]
    [InlineData("ubuntu.24.04-x64", "linux", (int)Architecture.Arm64, true, null)]
    [InlineData("linux-x64", "linux", (int)Architecture.X64, false, "linux-x64")]
    [InlineData("win-x64", "windows", (int)Architecture.X64, false, "win-x64")]
    [InlineData("osx-arm64", "osx", (int)Architecture.Arm64, false, "osx-arm64")]
    [InlineData("freebsd-x64", "freebsd", (int)Architecture.X64, true, null)]
    public void RuntimeIdentityRequiresMatchingProcessAndKnownAbi(string runtime, string platform, int architecture, bool glibc, string? expected)
    {
        Assert.Equal(expected, EmbeddingsRuntimeIdentity.ResolveSupportedRid(runtime,
            OSPlatform.Create(platform.ToUpperInvariant()), (Architecture)architecture, glibc));
    }

    [Fact]
    public async Task ProviderWriteRefusalAfterModelPreparationIsConfigurationFailure()
    {
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        using var envLock = ProcessEnvironmentLock.Acquire();
        using var home = new EnvironmentVariableScope("SCRINIA_HOME", fixture.Home);
        var result = await CaptureError(() => Run(fixture, fixture.Root, prepareModels: async ct =>
        {
            await PrepareTinyModel(fixture.Home, ct);
            File.WriteAllText(Path.Combine(fixture.Workspace, ".scrinia", ".tracking-recovery.json"), "retained late journal");
        }));
        Assert.Equal(1, result.Exit);
        Assert.Contains("InstalledConfigurationFailed", result.Error);
        Assert.DoesNotContain("InstalledModelPreparationFailed", result.Error);
    }

    [Theory]
    [InlineData((int)EmbeddingsInstallStatus.InstallationFailed)]
    [InlineData((int)EmbeddingsInstallStatus.ExistingContentConflict)]
    public async Task UnprovenDestinationIsNotDescribedAsInstalled(int status)
    {
        var result = await CaptureError(() => Task.FromResult(EmbeddingsBundleSetup.ReportFailure(
            new((EmbeddingsInstallStatus)status, "destination", "publication is not confirmed"), "source", "workspace")));
        Assert.Contains("Installation destination: destination", result.Error);
        Assert.DoesNotContain("Installed payload retained", result.Error);
        Assert.DoesNotContain("Installed embeddings payload retained", result.Error);
    }

    [Fact]
    public async Task RetryGuidanceDistinguishesUnsafeSourceAndWorkspace()
    {
        var result = await CaptureError(() => Task.FromResult(EmbeddingsBundleSetup.ReportFailure(
            new(EmbeddingsInstallStatus.InvalidBundle, null, "invalid"), @"C:\source\%BUNDLE%", @"C:\workspace\%ROOT%")));
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("--embeddings-source <embeddings-source> --workspace-root <workspace-root>", result.Error);
            Assert.Contains("Replace <embeddings-source>", result.Error);
            Assert.Contains("<workspace-root> with", result.Error);
        }
        else Assert.Contains("--embeddings-source 'C:\\source\\%BUNDLE%'", result.Error);
    }

    [Fact]
    public async Task ServeProcessNeverInstallsAdjacentBundle()
    {
        using var fixture = EmbeddingsBundleTestFixture.Create("0.6.0", Rid);
        string hostDirectory = Path.GetDirectoryName(typeof(ScriniaCommands).Assembly.Location)!;
        foreach (string file in Directory.EnumerateFiles(hostDirectory))
            File.Copy(file, Path.Combine(fixture.Root, Path.GetFileName(file)), overwrite: true);
        Dictionary<string, string> before = Snapshot(fixture.Root);
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(fixture.Root, "scri.dll"));
        foreach (string arg in new[] { "serve", "--no-auto-setup", "--workspace-root", fixture.Workspace }) start.ArgumentList.Add(arg);
        start.Environment["SCRINIA_HOME"] = fixture.Home;
        using var process = System.Diagnostics.Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task<string> error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-03-26\",\"capabilities\":{},\"clientInfo\":{\"name\":\"setup-test\",\"version\":\"1.0\"}}}");
            string? response = await process.StandardOutput.ReadLineAsync(timeout.Token);
            Assert.Contains("serverInfo", response);
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
            process.StandardInput.Close();
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            Assert.DoesNotContain("installed at", await error);
            Assert.False(Directory.Exists(Path.Combine(fixture.Home, "plugins")));
            Assert.Equal(before.OrderBy(p => p.Key), Snapshot(fixture.Root).OrderBy(p => p.Key));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await error;
        }
    }

    private static async Task<(int Exit, string Error)> CaptureError(Func<Task<int>> action)
    {
        TextWriter previous = Console.Error;
        using var output = new StringWriter();
        Console.SetError(output);
        try { return (await action(), output.ToString()); }
        finally { Console.SetError(previous); }
    }

    private static Task<int> Run(EmbeddingsBundleTestFixture fixture, string? source,
        string? executableDirectory = null, Func<CancellationToken, Task>? prepareModels = null) =>
        new ScriniaCommands().SetupCoreAsync(fixture.Workspace, true, false, false, false, source,
            executableDirectory ?? fixture.Root, "0.6.0+test", Rid,
            prepareModels: prepareModels ?? (ct => PrepareTinyModel(fixture.Home, ct)));

    private static Task PrepareTinyModel(string home, CancellationToken ct, bool fail = false)
    {
        byte[] bytes = "tiny verified setup asset"u8.ToArray();
        var asset = new DownloadableAsset("test-model", "https://example.invalid/model", "tiny.bin", Convert.ToHexStringLower(SHA256.HashData(bytes)));
        return ScriniaCommands.EnsureVerifiedModelAssetsAsync(Path.Combine(home, "models", "test-model"),
            [asset], () => new HttpClient(new TinyAssetHandler(bytes, fail)), NullLogger.Instance, ct);
    }

    private sealed class TinyAssetHandler(byte[] bytes, bool fail) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(fail ? HttpStatusCode.BadGateway : HttpStatusCode.OK)
            { Content = new ByteArrayContent(bytes) });
    }

    private static Dictionary<string, string> Snapshot(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(root, path), path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
}
