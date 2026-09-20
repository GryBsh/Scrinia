using System.Runtime.InteropServices;
using FluentAssertions;
using Scrinia.Commands;
using Scrinia.Core.Configuration;
using Scrinia.Core.Embeddings;
using Scrinia.TestDoubles;

namespace Scrinia.Tests;

/// <summary>
/// Phase 5 Task 1 — Model2Vec relocation under <c>SCRINIA_HOME</c>.
///
/// <para>The built-in embedding model must resolve through Phase 2's <see cref="UserHome.Resolve"/>
/// resolver to <c>SCRINIA_HOME/models/m2v-MiniLM-L6-v2</c>, never to the executable-relative
/// <c>AppContext.BaseDirectory/models</c> path the pre-fix setup used. These tests drive the real
/// <see cref="ScriniaCommands.Setup"/> entry point against an isolated <c>SCRINIA_HOME</c> and an
/// isolated workspace root.</para>
///
/// <para>Setup is invoked with <c>noOllama: true</c> and the resolved model directory is pre-seeded
/// with both model files, so the fixed path performs no network download. The pre-fix
/// executable-relative path (<c>AppContext.BaseDirectory/models/m2v-MiniLM-L6-v2</c>) is trapped with
/// a FILE so the old <c>Directory.CreateDirectory</c> fails fast — deterministically red, with no
/// network dependency and no model download — while the fixed path never touches it.</para>
///
/// <para><c>Setup</c> mutates <see cref="WorkspaceSetup"/>'s process-wide static workspace root via
/// <c>Configure</c>, so this class lives in the global workspace-config collection that serializes
/// such mutations.</para>
/// </summary>
[Collection(GlobalWorkspaceConfigCollection.Name)]
public sealed class ModelRelocationTests : IDisposable
{
    private const string ScriniaHomeVar = "SCRINIA_HOME";

    private string _home = null!;
    private string _homeModelsDir = null!;
    private string _workspace = null!;
    private string _appContextModelDir = null!;
    private string _legacyRoot = null!;
    private string _altHome = null!;

    private bool _trapCreated;
    private readonly ProcessEnvironmentLock? _environmentLock;
    private EnvironmentVariableScope? _homeScope;

    public ModelRelocationTests() : this(false)
    {
    }

    internal ModelRelocationTests(bool injectSetupFailure, bool injectFailureAfterTrapCreation = false)
    {
        try
        {
            _environmentLock = ProcessEnvironmentLock.Acquire();
        _home = Path.Combine(Path.GetTempPath(), $"scrinia_reloc_home_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_home);

        // Pre-seed the SCRINIA_HOME model location so the fixed setup finds it and downloads nothing.
        _homeModelsDir = Path.Combine(_home, "models", "m2v-MiniLM-L6-v2");
        Directory.CreateDirectory(_homeModelsDir);
        File.WriteAllText(Path.Combine(_homeModelsDir, "model.safetensors"), "model2vec model.safetensors");
        File.WriteAllText(Path.Combine(_homeModelsDir, "vocab.txt"), "model2vec vocab");

        _workspace = Path.Combine(Path.GetTempPath(), $"scrinia_reloc_ws_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspace);

        // A package-style executable-relative legacy tree the one-time migration must move OUT of
        // (never into) — mirrors the immutable AppContext.BaseDirectory that production migrates from.
        _legacyRoot = Path.Combine(Path.GetTempPath(), $"scrinia_legacy_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_legacyRoot);

        // An isolated alt home for failure-path tests that must not disturb the shared _home.
        _altHome = Path.Combine(Path.GetTempPath(), $"scrinia_alt_home_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_altHome);

        // Phase 2 resolver honors an isolated, non-empty absolute SCRINIA_HOME exactly.
        _homeScope = new EnvironmentVariableScope(ScriniaHomeVar, _home);

        if (injectSetupFailure)
            throw new InvalidOperationException("intentional constructor setup failure");

        // Trap the OLD executable-relative model path with a FILE so the pre-fix code's
        // Directory.CreateDirectory(AppContext/models/m2v-MiniLM-L6-v2) throws immediately —
        // failing fast with no network download — while the fix resolves to SCRINIA_HOME and
        // never touches that path.
        _appContextModelDir = Path.Combine(AppContext.BaseDirectory, "models", "m2v-MiniLM-L6-v2");
        string appContextModelsParent = Path.GetDirectoryName(_appContextModelDir)!;
        if (File.Exists(appContextModelsParent))
            File.Delete(appContextModelsParent);
        Directory.CreateDirectory(appContextModelsParent);
        if (Directory.Exists(_appContextModelDir))
            Directory.Delete(_appContextModelDir, recursive: true);
        if (!File.Exists(_appContextModelDir))
        {
            File.WriteAllText(_appContextModelDir, "model-relocation-trap");
            _trapCreated = true;
        }

        if (injectFailureAfterTrapCreation)
            throw new InvalidOperationException("intentional constructor setup failure after trap creation");
        }
        catch (Exception setupException)
        {
            var cleanupFailures = new List<Exception>();
            AttemptCleanup("SCRINIA_HOME restoration", () => _homeScope?.Dispose());
            AttemptCleanup("constructor executable-relative model trap", () =>
            {
                if (!string.IsNullOrEmpty(_appContextModelDir))
                    DeletePathAndVerify(_appContextModelDir);
            });
            AttemptCleanup("constructor home", () => DeletePathAndVerify(_home));
            AttemptCleanup("constructor workspace", () => DeletePathAndVerify(_workspace));
            AttemptCleanup("constructor legacy root", () => DeletePathAndVerify(_legacyRoot));
            AttemptCleanup("constructor alternate home", () => DeletePathAndVerify(_altHome));
            AttemptCleanup("constructor environment lock", () => _environmentLock?.Dispose());

            if (cleanupFailures.Count > 0)
                throw new AggregateException("Model relocation constructor compensation failed.", [setupException, .. cleanupFailures]);

            throw;

            void AttemptCleanup(string description, Action cleanup)
            {
                try
                {
                    cleanup();
                }
                catch (Exception ex)
                {
                    cleanupFailures.Add(new IOException($"Could not clean up {description} after constructor failure.", ex));
                }
            }
        }
    }

    public void Dispose()
    {
        var cleanupFailures = new List<Exception>();

        AttemptCleanup("SCRINIA_HOME restoration", () => _homeScope?.Dispose());

        // Remove the trap file/dir and any executable-relative model the pre-fix code may have
        // downloaded there, leaving the test bin folder clean. Every cleanup attempt runs and any
        // failure is reported to xUnit instead of being hidden from the next isolated repetition.
        AttemptCleanup("executable-relative model trap", () => DeletePathAndVerify(_appContextModelDir));
        AttemptCleanup("isolated home", () => DeletePathAndVerify(_home));
        AttemptCleanup("process environment lock", () => _environmentLock?.Dispose());
        AttemptCleanup("isolated workspace", () => DeletePathAndVerify(_workspace));
        AttemptCleanup("legacy migration root", () => DeletePathAndVerify(_legacyRoot));
        AttemptCleanup("alternate home", () => DeletePathAndVerify(_altHome));

        if (cleanupFailures.Count > 0)
            throw new AggregateException("Model relocation test cleanup failed; isolation is not trustworthy.", cleanupFailures);

        void AttemptCleanup(string description, Action cleanup)
        {
            try
            {
                cleanup();
            }
            catch (Exception ex)
            {
                cleanupFailures.Add(new IOException($"Could not clean up {description}.", ex));
            }
        }

    }

    private static void DeletePathAndVerify(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }

        if (Directory.Exists(path) || File.Exists(path))
            throw new IOException($"Cleanup did not remove '{path}'.");
    }

    [Fact]
    public void UserHome_ResolvesIsolatedHome_ExactAbsoluteOverride()
    {
        // The isolated, non-empty absolute SCRINIA_HOME override is honored exactly.
        UserHome.Resolve().Should().Be(_home);

        // And the model path is built entirely from that resolved home.
        Path.Combine(UserHome.Resolve(), "models", "m2v-MiniLM-L6-v2")
            .Should().Be(_homeModelsDir);
    }

    [Fact]
    public async Task Setup_ResolvesModel2VecUnderScriniaHome_NeverAppBaseDirectory()
    {
        int exit = await new ScriniaCommands().Setup(workspaceRoot: _workspace, noOllama: true);

        exit.Should().Be(0, "scri setup with the built-in model already present completes cleanly");

        // The model resolves through the Phase 2 resolver to SCRINIA_HOME — never the executable
        // directory — and the pre-seeded built-in files are the ones setup confirmed present.
        Path.Combine(UserHome.Resolve(), "models", "m2v-MiniLM-L6-v2")
            .Should().Be(_homeModelsDir);
        Model2VecModelManager.IsModelAvailable(_homeModelsDir).Should().BeTrue(
            "setup must repair the pre-seeded files and publish verified assets");

        // The pre-fix code resolved under AppContext.BaseDirectory/models; the fix must never create
        // or populate that executable-relative path.
        Directory.Exists(_appContextModelDir).Should().BeFalse(
            "Model2Vec must resolve under SCRINIA_HOME, never AppContext.BaseDirectory/models");
    }

    [Fact]
    public async Task Setup_ResolvesModel2VecAcrossTenIsolatedHomes()
    {
        for (int repetition = 0; repetition < 10; repetition++)
        {
            string home = Path.Combine(Path.GetTempPath(), $"scrinia_reloc_repeat_home_{Guid.NewGuid():N}");
            string workspace = Path.Combine(Path.GetTempPath(), $"scrinia_reloc_repeat_ws_{Guid.NewGuid():N}");
            string modelDir = Path.Combine(home, "models", "m2v-MiniLM-L6-v2");
            Directory.CreateDirectory(modelDir);
            Directory.CreateDirectory(workspace);
            File.WriteAllText(Path.Combine(modelDir, "model.safetensors"), $"isolated-model-{repetition}");
            File.WriteAllText(Path.Combine(modelDir, "vocab.txt"), $"isolated-vocab-{repetition}");

            try
            {
                using var homeScope = new EnvironmentVariableScope(ScriniaHomeVar, home);
                int exit = await new ScriniaCommands().Setup(workspaceRoot: workspace, noOllama: true);

                exit.Should().Be(0, $"isolated repetition {repetition + 1} should complete cleanly");
                Model2VecModelManager.IsModelAvailable(modelDir).Should().BeTrue(
                    "setup must publish verified assets in the isolated home");
                Directory.Exists(Path.Combine(home, "plugins")).Should().BeFalse(
                    "a model-only setup must not create a plugin hierarchy");
            }
            finally
            {
                var cleanupFailures = new List<Exception>();
                AttemptCleanup("repeated isolated home", () => DeletePathAndVerify(home));
                AttemptCleanup("repeated isolated workspace", () => DeletePathAndVerify(workspace));

                if (cleanupFailures.Count > 0)
                    throw new AggregateException(
                        $"Repetition {repetition + 1} cleanup failed; isolation is not trustworthy.",
                        cleanupFailures);

                void AttemptCleanup(string description, Action cleanup)
                {
                    try
                    {
                        cleanup();
                    }
                    catch (Exception ex)
                    {
                        cleanupFailures.Add(new IOException($"Could not clean up {description}.", ex));
                    }
                }
            }
        }
    }

    /// <summary>
    /// Phase 5 Task 2 — the embeddings plugin exe resolves through the mandatory
    /// <c>SCRINIA_HOME/plugins/&lt;plugin&gt;/&lt;version&gt;/&lt;rid&gt;/</c> hierarchy using the
    /// workspace-configured plugin name and version, never the executable-relative
    /// <c>AppContext.BaseDirectory/plugins</c> tree the pre-fix discovery scanned.
    /// </summary>
    [Fact]
    public void ResolveEmbeddingsPluginExe_UsesMandatoryHierarchyUnderScriniaHome()
    {
        const string PluginName = "scri-plugin-embeddings";

        // Configure the workspace root so GetConfigValue reads the plugin identity from workspace config.
        WorkspaceSetup.Configure(_workspace);

        string? previousPluginEnv = Environment.GetEnvironmentVariable("PLUGINS_EMBEDDINGS");
        string? previousPluginDblEnv = Environment.GetEnvironmentVariable("PLUGINS__EMBEDDINGS");
        string? previousVersionEnv = Environment.GetEnvironmentVariable("PLUGINS_EMBEDDINGS_VERSION");
        string? previousVersionDblEnv = Environment.GetEnvironmentVariable("PLUGINS__EMBEDDINGS__VERSION");
        bool appTrapCreated = false;
        string appFlatExe = Path.Combine(AppContext.BaseDirectory, "plugins", PluginName, $"{PluginName}.exe");
        try
        {
            // Eliminate any ambient environment override so the workspace config is authoritative.
            Environment.SetEnvironmentVariable("PLUGINS_EMBEDDINGS", null);
            Environment.SetEnvironmentVariable("PLUGINS__EMBEDDINGS", null);
            Environment.SetEnvironmentVariable("PLUGINS_EMBEDDINGS_VERSION", null);
            Environment.SetEnvironmentVariable("PLUGINS__EMBEDDINGS__VERSION", null);

            Scrinia.Commands.WorkspaceConfig.SetValue(_workspace, "plugins:embeddings", PluginName);
            Scrinia.Commands.WorkspaceConfig.SetValue(_workspace, "plugins:embeddings:version", "1.2.3");

            // Install the plugin at the mandatory version/RID location under SCRINIA_HOME.
            string rid = RuntimeInformation.RuntimeIdentifier;
            string pluginDir = Path.Combine(_home, "plugins", PluginName, "1.2.3", rid);
            Directory.CreateDirectory(pluginDir);
            string installedExe = Path.Combine(pluginDir, $"{PluginName}.exe");
            File.WriteAllText(installedExe, "plugin-exe");

            // Trap the OLD executable-relative flat plugin path with a FILE: the pre-fix code scanned
            // AppContext.BaseDirectory/plugins and would have found a there. The fix must never read it.
            string appFlatParent = Path.GetDirectoryName(appFlatExe)!;
            if (!Directory.Exists(appFlatParent)) Directory.CreateDirectory(appFlatParent);
            appTrapCreated = !File.Exists(appFlatExe);
            if (appTrapCreated) File.WriteAllText(appFlatExe, "app-relative-exe");

            string? exe = EmbeddingsHost.ResolveEmbeddingsPluginExe();

            exe.Should().Be(installedExe, "the exe resolves at SCRINIA_HOME/plugins/<plugin>/<version>/<rid>/<plugin>.exe");
            exe!.StartsWith(_home, StringComparison.Ordinal).Should().BeTrue(
                "plugin discovery is rooted at SCRINIA_HOME");
            exe.StartsWith(AppContext.BaseDirectory, StringComparison.Ordinal).Should().BeFalse(
                "plugin discovery must never scan AppContext.BaseDirectory/plugins");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PLUGINS_EMBEDDINGS", previousPluginEnv);
            Environment.SetEnvironmentVariable("PLUGINS__EMBEDDINGS", previousPluginDblEnv);
            Environment.SetEnvironmentVariable("PLUGINS_EMBEDDINGS_VERSION", previousVersionEnv);
            Environment.SetEnvironmentVariable("PLUGINS__EMBEDDINGS__VERSION", previousVersionDblEnv);

            try { if (appTrapCreated && File.Exists(appFlatExe)) File.Delete(appFlatExe); } catch { }
        }
    }

    // ── Phase 5 Task 3 — one-time migration of both executable-relative legacy asset trees ──────

    /// <summary>
    /// Phase 5 Task 3 — the migration boundary moves the executable-relative <c>models</c> tree from
    /// <paramref name="legacyRoot"/> into <c>SCRINIA_HOME/models</c>, records completion under
    /// <c>SCRINIA_HOME</c>, and never writes back under the package directory.
    /// </summary>
    [Fact]
    public void MigrateLegacyAssets_MovesExecutableRelativeModelsIntoHome()
    {
        string legacyModels = Path.Combine(_legacyRoot, "models");
        Directory.CreateDirectory(legacyModels);
        File.WriteAllText(Path.Combine(legacyModels, "model.safetensors"), "legacy-model");
        File.WriteAllText(Path.Combine(legacyModels, "vocab.txt"), "legacy-vocab");

        ScriniaCommands.MigrateLegacyAssets(_legacyRoot, _home);

        File.ReadAllText(Path.Combine(_home, "models", "model.safetensors"))
            .Should().Be("legacy-model", "legacy model content lands under SCRINIA_HOME/models");
        File.ReadAllText(Path.Combine(_home, "models", "vocab.txt"))
            .Should().Be("legacy-vocab");
        File.Exists(Path.Combine(legacyModels, "model.safetensors"))
            .Should().BeFalse("legacy executable-relative models are migrated out of the package directory");

        // Completion is recorded in SCRINIA_HOME state, not merely implied by an emptied legacy tree.
        string statePath = Path.Combine(_home, "migration-state.json");
        File.Exists(statePath).Should().BeTrue("migration completion is recorded in SCRINIA_HOME state");
    }

    [Fact]
    public async Task MigrateLegacyAssets_MovesExecutableRelativePluginsIntoVersionRidHierarchy()
    {
        WorkspaceSetup.Configure(_workspace);
        string? prevName = Environment.GetEnvironmentVariable("PLUGINS_EMBEDDINGS");
        string? prevVer = Environment.GetEnvironmentVariable("PLUGINS_EMBEDDINGS_VERSION");
        string? prevVerDbl = Environment.GetEnvironmentVariable("PLUGINS__EMBEDDINGS__VERSION");
        try
        {
            Environment.SetEnvironmentVariable("PLUGINS_EMBEDDINGS", null);
            Environment.SetEnvironmentVariable("PLUGINS_EMBEDDINGS_VERSION", null);
            Environment.SetEnvironmentVariable("PLUGINS__EMBEDDINGS__VERSION", null);
            Scrinia.Commands.WorkspaceConfig.SetValue(_workspace, "plugins:embeddings", "scri-plugin-embeddings");
            Scrinia.Commands.WorkspaceConfig.SetValue(_workspace, "plugins:embeddings:version", "1.2.3");

            string legacyPlugin = Path.Combine(_legacyRoot, "plugins", "scri-plugin-embeddings");
            Directory.CreateDirectory(legacyPlugin);
            File.WriteAllText(Path.Combine(legacyPlugin, "scri-plugin-embeddings.dll"), "legacy-plugin");

            ScriniaCommands.MigrateLegacyAssets(_legacyRoot, _home);

            string rid = RuntimeInformation.RuntimeIdentifier;
            string target = Path.Combine(_home, "plugins", "scri-plugin-embeddings", "1.2.3", rid, "scri-plugin-embeddings.dll");
            target.StartsWith(_home, StringComparison.Ordinal).Should().BeTrue(
                "legacy plugins migrate into the mandatory SCRINIA_HOME/plugins/<plugin>/<version>/<rid>/ hierarchy");
            File.Exists(target).Should().BeTrue("legacy plugin content is relocated to the selected version/RID directory");
            File.Exists(Path.Combine(legacyPlugin, "scri-plugin-embeddings.dll"))
                .Should().BeFalse("legacy executable-relative plugin is migrated out of the package directory");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PLUGINS_EMBEDDINGS", prevName);
            Environment.SetEnvironmentVariable("PLUGINS_EMBEDDINGS_VERSION", prevVer);
            Environment.SetEnvironmentVariable("PLUGINS__EMBEDDINGS__VERSION", prevVerDbl);
        }
    }

    [Fact]
    public void MigrateLegacyAssets_SecondRun_IsIdempotentNoOp()
    {
        string legacyModels = Path.Combine(_legacyRoot, "models");
        Directory.CreateDirectory(legacyModels);
        File.WriteAllText(Path.Combine(legacyModels, "model.safetensors"), "legacy-model");

        ScriniaCommands.MigrateLegacyAssets(_legacyRoot, _home);

        // Re-seed the legacy tree; the recorded state must make the second run a no-op.
        File.WriteAllText(Path.Combine(legacyModels, "model.safetensors"), "legacy-model-2");

        var ex = Record.Exception(() => ScriniaCommands.MigrateLegacyAssets(_legacyRoot, _home));
        ex.Should().BeNull("a second startup after a completed migration is an idempotent no-op");

        File.ReadAllText(Path.Combine(_home, "models", "model.safetensors"))
            .Should().Be("legacy-model", "the already-migrated home model is not overwritten by a stale reseed");
        File.Exists(Path.Combine(legacyModels, "model.safetensors"))
            .Should().BeTrue("idempotent second run leaves the reseeded legacy file untouched (no re-migration)");
    }

    [Fact]
    public void MigrateLegacyAssets_PreservesExistingDestinationFiles()
    {
        string homeModels = Path.Combine(_home, "models");
        Directory.CreateDirectory(homeModels);
        File.WriteAllText(Path.Combine(homeModels, "model.safetensors"), "HOME-USER-MODEL");

        string legacyModels = Path.Combine(_legacyRoot, "models");
        Directory.CreateDirectory(legacyModels);
        File.WriteAllText(Path.Combine(legacyModels, "model.safetensors"), "LEGACY-MODEL");
        File.WriteAllText(Path.Combine(legacyModels, "extra.txt"), "legacy-extra");

        ScriniaCommands.MigrateLegacyAssets(_legacyRoot, _home);

        File.ReadAllText(Path.Combine(homeModels, "model.safetensors"))
            .Should().Be("HOME-USER-MODEL", "an existing home destination file is preserved, not clobbered by the legacy copy");
        File.ReadAllText(Path.Combine(homeModels, "extra.txt"))
            .Should().Be("legacy-extra", "a legacy file with no home counterpart is migrated");
    }

    [Fact]
    public void MigrateLegacyAssets_RejectsTraversalInPluginVersion()
    {
        WorkspaceSetup.Configure(_workspace);
        string? prevVer = Environment.GetEnvironmentVariable("PLUGINS_EMBEDDINGS_VERSION");
        string? prevVerDbl = Environment.GetEnvironmentVariable("PLUGINS__EMBEDDINGS__VERSION");
        try
        {
            Environment.SetEnvironmentVariable("PLUGINS_EMBEDDINGS_VERSION", "../../escape-model");
            Environment.SetEnvironmentVariable("PLUGINS__EMBEDDINGS__VERSION", "../../escape-model");

            string legacyPlugin = Path.Combine(_legacyRoot, "plugins", "scri-plugin-embeddings");
            Directory.CreateDirectory(legacyPlugin);
            File.WriteAllText(Path.Combine(legacyPlugin, "scri-plugin-embeddings.dll"), "legacy-plugin");

            var ex = Record.Exception(() => ScriniaCommands.MigrateLegacyAssets(_legacyRoot, _home));
            ex.Should().BeNull("an invalid plugin version must not throw out of a best-effort migration boundary");

            File.Exists(Path.Combine(legacyPlugin, "scri-plugin-embeddings.dll"))
                .Should().BeTrue("a traversal-bearing version is not migrated");
            File.Exists(Path.Combine(_home, "escape-model", "scri-plugin-embeddings.dll"))
                .Should().BeFalse("traversal must never escape SCRINIA_HOME");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PLUGINS_EMBEDDINGS_VERSION", prevVer);
            Environment.SetEnvironmentVariable("PLUGINS__EMBEDDINGS__VERSION", prevVerDbl);
        }
    }

    [Fact]
    public void MigrateLegacyAssets_MoveFailure_RemainsNonFatal()
    {
        // A pre-existing FILE at the models destination makes Directory.CreateDirectory(move target)
        // fail deterministically; the boundary must swallow it and preserve startup.
        string malformedModels = Path.Combine(_altHome, "models");
        File.WriteAllText(malformedModels, "a-file-not-a-directory");

        string legacyModels = Path.Combine(_legacyRoot, "models");
        Directory.CreateDirectory(legacyModels);
        File.WriteAllText(Path.Combine(legacyModels, "model.safetensors"), "legacy");

        var ex = Record.Exception(() => ScriniaCommands.MigrateLegacyAssets(_legacyRoot, _altHome));
        ex.Should().BeNull("an expected migration I/O failure must not propagate past the boundary");

        File.Exists(Path.Combine(legacyModels, "model.safetensors"))
            .Should().BeTrue("a failed move leaves the legacy file in place for a later retry");
    }

    [Fact]
    public void MigrateLegacyAssets_NeverWritesLegacyPackageDirectory()
    {
        string legacyModels = Path.Combine(_legacyRoot, "models");
        Directory.CreateDirectory(legacyModels);
        File.WriteAllText(Path.Combine(legacyModels, "model.safetensors"), "legacy");

        WorkspaceSetup.Configure(_workspace);
        string? prevName = Environment.GetEnvironmentVariable("PLUGINS_EMBEDDINGS");
        string? prevVer = Environment.GetEnvironmentVariable("PLUGINS_EMBEDDINGS_VERSION");
        string? prevVerDbl = Environment.GetEnvironmentVariable("PLUGINS__EMBEDDINGS__VERSION");
        try
        {
            Environment.SetEnvironmentVariable("PLUGINS_EMBEDDINGS", null);
            Environment.SetEnvironmentVariable("PLUGINS_EMBEDDINGS_VERSION", null);
            Environment.SetEnvironmentVariable("PLUGINS__EMBEDDINGS__VERSION", null);
            Scrinia.Commands.WorkspaceConfig.SetValue(_workspace, "plugins:embeddings", "scri-plugin-embeddings");
            Scrinia.Commands.WorkspaceConfig.SetValue(_workspace, "plugins:embeddings:version", "1.2.3");

            string legacyPlugin = Path.Combine(_legacyRoot, "plugins", "scri-plugin-embeddings");
            Directory.CreateDirectory(legacyPlugin);
            File.WriteAllText(Path.Combine(legacyPlugin, "scri-plugin-embeddings.dll"), "legacy");

            HashSet<string> before = Directory
                .EnumerateFiles(_legacyRoot, "*", SearchOption.AllDirectories)
                .ToHashSet();

            ScriniaCommands.MigrateLegacyAssets(_legacyRoot, _home);

            HashSet<string> after = Directory
                .EnumerateFiles(_legacyRoot, "*", SearchOption.AllDirectories)
                .ToHashSet();

            after.Should().NotContain(f => !before.Contains(f),
                "the migration must never create a file under the executable-relative package directory");
            File.Exists(Path.Combine(_legacyRoot, "migration-state.json"))
                .Should().BeFalse("migration state is recorded under SCRINIA_HOME, never the package directory");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PLUGINS_EMBEDDINGS", prevName);
            Environment.SetEnvironmentVariable("PLUGINS_EMBEDDINGS_VERSION", prevVer);
            Environment.SetEnvironmentVariable("PLUGINS__EMBEDDINGS__VERSION", prevVerDbl);
        }
    }

}

[Collection(GlobalWorkspaceConfigCollection.Name)]
public sealed class ModelRelocationConstructorIsolationTests
{
    [Fact]
    public void FailedConstructor_RestoresEnvironmentAndReleasesLock()
    {
        string? previousHome = Environment.GetEnvironmentVariable("SCRINIA_HOME");
        var failure = Record.Exception(() => new ModelRelocationTests(injectSetupFailure: true));

        failure.Should().BeOfType<InvalidOperationException>();
        Environment.GetEnvironmentVariable("SCRINIA_HOME").Should().Be(previousHome);

        Task<ProcessEnvironmentLock> lockTask = Task.Run(ProcessEnvironmentLock.Acquire);
        lockTask.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue(
            "a failed fixture constructor must release the process-wide lock");
        lockTask.Result.Dispose();
    }

    [Fact]
    public void FailedConstructorAfterTrapCreation_RestoresEnvironmentReleasesLockAndCleansFilesystem()
    {
        string? previousHome = Environment.GetEnvironmentVariable("SCRINIA_HOME");
        string trapPath = Path.Combine(AppContext.BaseDirectory, "models", "m2v-MiniLM-L6-v2");
        HashSet<string> temporaryPathsBefore = CaptureRelocationTemporaryPaths();

        var failure = Record.Exception(() => new ModelRelocationTests(
            injectSetupFailure: false,
            injectFailureAfterTrapCreation: true));

        failure.Should().BeOfType<InvalidOperationException>();
        Environment.GetEnvironmentVariable("SCRINIA_HOME").Should().Be(previousHome);
        File.Exists(trapPath).Should().BeFalse("constructor compensation must remove the trap file");
        Directory.Exists(trapPath).Should().BeFalse("constructor compensation must remove the trap directory");
        CaptureRelocationTemporaryPaths().Should().BeEquivalentTo(temporaryPathsBefore,
            "constructor compensation must remove every temporary path it created");

        Task<ProcessEnvironmentLock> lockTask = Task.Run(ProcessEnvironmentLock.Acquire);
        lockTask.Wait(TimeSpan.FromSeconds(2)).Should().BeTrue(
            "a failed fixture constructor must release the process-wide lock");
        lockTask.Result.Dispose();
    }

    private static HashSet<string> CaptureRelocationTemporaryPaths()
    {
        string temporaryRoot = Path.GetTempPath();
        return Directory.EnumerateDirectories(temporaryRoot, "scrinia_*", SearchOption.TopDirectoryOnly)
            .Where(path => path.Contains("reloc_", StringComparison.Ordinal)
                || path.Contains("legacy_", StringComparison.Ordinal)
                || path.Contains("alt_home_", StringComparison.Ordinal))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
