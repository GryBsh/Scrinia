using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using ConsoleAppFramework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Scrinia.Core;
using Scrinia.Core.Configuration;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Commands.Hooks;
using Spectre.Console;

namespace Scrinia.Commands;

public partial class ScriniaCommands
{
    /// <summary>Download embedding models for built-in providers and installed plugins.</summary>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="noOllama">Skip Ollama auto-detection even if it is running. Use to force local-only setup.</param>
    /// <param name="hooks">Install SessionStart/UserPromptSubmit hooks into detected agent CLIs (Claude Code, etc.). Opt-in.</param>
    /// <param name="uninstallHooks">Remove scrinia-managed hooks from agent CLIs and exit.</param>
    /// <param name="project">With --hooks / --uninstall-hooks, target workspace-local config (.claude/...) instead of user-global (~/.claude/...).</param>
    public async Task<int> Setup(
        string? workspaceRoot = null,
        bool noOllama = false,
        bool hooks = false,
        bool uninstallHooks = false,
        bool project = false,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspaceRoot);
        WorkspaceSetup.WarnOnOrphanStores();

        // Hook-management flags short-circuit the model-download flow — they're standalone
        // operations users typically run after the regular setup is complete.
        if (uninstallHooks)
        {
            return await UninstallHooksFlowAsync(project);
        }
        if (hooks)
        {
            return await InstallHooksFlowAsync(project);
        }

        string exeDir = AppContext.BaseDirectory;

        // One-time, best-effort migration of the executable-relative legacy models/plugins trees
        // into SCRINIA_HOME. The immutable global-tool package directory (AppContext.BaseDirectory)
        // is the legacy source and is never written at runtime; all destinations live under the
        // Phase 2-resolved home. Runs before the model downloads below so freshly migrated assets
        // are the ones setup and startup then consume.
        MigrateLegacyAssets(exeDir, UserHome.Resolve());

        // ── Step 0: Ollama auto-detect (offers to wire embeddings + completion through Ollama
        //           if it's already running, skipping the local-model downloads below). ──
        bool ollamaConfigured = !noOllama && await TryConfigureOllamaAsync(cancellationToken);
        if (ollamaConfigured)
        {
            // Reindex any existing vectors against the new Ollama provider signature.
            await WorkspaceSetup.LoadPluginsAsync(cancellationToken);

            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine(
                "[green]Setup complete via Ollama.[/] Skipping local-model downloads. " +
                "Run [cyan]scri config[/] to inspect or adjust settings.");
            return 0;
        }

        // Ollama path was declined / unreachable / explicitly skipped — clear any stale
        // Ollama-derived keys from a prior run so the user doesn't end up with a config
        // pointing at a service they're no longer using. Without this, switching off
        // Ollama leaves Provider=ollama in place and startup tries it every time before
        // falling back, which surfaces as confusing "Ollama unreachable" warnings.
        ClearStaleOllamaConfig(WorkspaceSetup.Root);

        // ── Step 1: Built-in Model2Vec model (always) ──
        AnsiConsole.MarkupLine("[bold]Built-in embeddings (Model2Vec / MiniLM-L6-v2)[/]");

        string modelDir = Path.Combine(UserHome.Resolve(), "models", "m2v-MiniLM-L6-v2");
        Directory.CreateDirectory(modelDir);

        string[] files = ["model.safetensors", "vocab.txt"];
        string baseUrl = Model2VecModelManager.ModelBaseUrl;

        bool allExist = files.All(f => File.Exists(Path.Combine(modelDir, f)));
        if (allExist)
        {
            AnsiConsole.MarkupLine("[green]  Model already downloaded.[/]");
            AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(modelDir)}[/]");
        }
        else
        {
            await DownloadFilesAsync(baseUrl, files, modelDir, cancellationToken);
            AnsiConsole.MarkupLine($"[green]  Model ready at:[/] {Markup.Escape(modelDir)}");
        }

        // ── Step 2: Vulkan plugin GGUF model (if plugin is installed) ──
        // The embeddings plugin is discovered only through the mandatory
        // SCRINIA_HOME/plugins/<plugin>/<version>/<rid>/ hierarchy — never the executable-relative
        // plugins tree a pre-SCRINIA_HOME package scanned, and never a flat fallback.
        string? pluginExe = WorkspaceSetup.ResolveEmbeddingsPluginExe();

        if (pluginExe is not null)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Vulkan plugin (GPU acceleration)[/]");

            string vulkanModelsDir = Path.Combine(UserHome.Resolve(), "models");
            Directory.CreateDirectory(vulkanModelsDir);

            const string ggufFile = "all-MiniLM-L6-v2-Q8_0.gguf";
            const string ggufUrl = "https://huggingface.co/second-state/All-MiniLM-L6-v2-Embedding-GGUF/resolve/main/all-MiniLM-L6-v2-Q8_0.gguf";

            if (File.Exists(Path.Combine(vulkanModelsDir, ggufFile)))
            {
                AnsiConsole.MarkupLine("[green]  GGUF model already downloaded.[/]");
                AnsiConsole.MarkupLine($"  [dim]{Markup.Escape(vulkanModelsDir)}[/]");
            }
            else
            {
                await DownloadFilesAsync(ggufUrl.Replace($"/{ggufFile}", ""), [ggufFile], vulkanModelsDir, cancellationToken);
                AnsiConsole.MarkupLine($"[green]  GGUF model ready at:[/] {Markup.Escape(vulkanModelsDir)}");
            }
        }
        else
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[dim]Vulkan plugin not installed — skipping GPU model download.[/]");
        }

        // Explicitly record the embeddings provider that setup just configured. The
        // runtime defaults to model2vec when no value is set, but writing it explicitly
        // means re-running setup after declining a previously-configured Ollama doesn't
        // leave the user wondering which provider is active. Plugin presence still
        // takes precedence at runtime (LoadPluginsAsync handles that branch).
        WorkspaceConfig.SetValue(
            WorkspaceSetup.Root,
            "Scrinia:Embeddings:Provider",
            "model2vec");

        // Merge drivers are retired (see the merge-retirement plan). Print the manual cleanup and
        // legacy driver-unset guidance without ever launching Git — the agent runs these commands.
        AnsiConsole.WriteLine();
        AnsiConsole.WriteLine(PrintMergeRemovedHint(WorkspaceSetup.Root));

        return 0;
    }

    /// <summary>
    /// The single idempotent migration boundary for Phase 5. Moves the executable-relative legacy
    /// <c>models</c> and <c>plugins</c> trees that a previous (pre-SCRINIA_HOME) installation left under
    /// <paramref name="legacyRoot"/> — the immutable <c>AppContext.BaseDirectory</c> — into the
    /// Phase 2-resolved home <paramref name="homeRoot"/>. Exactly one call site exists (see
    /// <see cref="Setup"/>), and both asset classes are migrated here so the one-time move is a single,
    /// auditable boundary rather than two scattered copies.
    ///
    /// <para>The boundary is:</para>
    /// <list type="bullet">
    ///   <item>idempotent — completion is recorded in <c>SCRINIA_HOME</c> state and a later run is a
    ///   no-op;</item>
    ///   <item>containment-faithful — every destination is canonicalized and proven inside
    ///   <paramref name="homeRoot"/> with <see cref="PathContainment"/>; a plugin identity carrying a
    ///   traversal segment is refused rather than followed;</item>
    ///   <item>non-destructive — an already-present home destination is preserved, never clobbered;</item>
    ///   <item>fail-safe — only expected migration I/O failures are swallowed (after the move is done so
    ///   startup is preserved), and nothing is ever written back under <paramref name="legacyRoot"/>.</item>
    /// </list>
    /// </summary>
    /// <param name="legacyRoot">The executable-relative asset source (the immutable tool package dir).</param>
    /// <param name="homeRoot">The resolved <c>SCRINIA_HOME</c> that owns all mutable runtime state.</param>
    internal static void MigrateLegacyAssets(string legacyRoot, string homeRoot)
    {
        // Canonicalize both ends up front: a non-absolute or unresolvable home can never be a
        // migration target, and canonicalizing the legacy source keeps the containment math honest.
        string canonicalHome;
        string canonicalLegacy;
        try
        {
            canonicalHome = PathContainment.ResolveCanonical(homeRoot);
            canonicalLegacy = PathContainment.ResolveCanonical(legacyRoot);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Unresolvable root — nothing safe to migrate and startup is already preserved.
            LogMigrationWarning($"cannot canonicalize home/legacy root — {ex.Message}");
            return;
        }

        LegacyAssetMigrationState state = ReadMigrationState(canonicalHome) ?? new LegacyAssetMigrationState(false, false, null);

        // Fully migrated on a prior run — a true no-op: no moves, no state rewrite.
        if (state.Models && state.Plugins)
        {
            return;
        }

        if (!state.Models)
        {
            try
            {
                MigrateModelsTree(canonicalLegacy, canonicalHome);
                state = state with { Models = true };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Expected migration I/O failure: leave the legacy tree in place for a later retry
                // and preserve startup rather than surfacing it.
                LogMigrationWarning($"models migration failed, left in place for retry — {ex.Message}");
            }
        }

        if (!state.Plugins)
        {
            try
            {
                if (MigratePluginsTree(canonicalLegacy, canonicalHome))
                    state = state with { Plugins = true };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogMigrationWarning($"plugins migration failed, left in place for retry — {ex.Message}");
            }
        }

        // Record completion. A state-write failure is non-fatal: the moves above already relocated the
        // legacy content (an emptied legacy tree is itself a no-op marker on the next run), so startup
        // is preserved either way.
        try
        {
            WriteMigrationState(canonicalHome, state with { MigratedAt = DateTimeOffset.UtcNow.ToString("o") });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogMigrationWarning($"could not record migration state — {ex.Message}");
        }
    }

    /// <summary>Migrates the executable-relative <c>models</c> tree into <c>SCRINIA_HOME/models</c>.</summary>
    private static void MigrateModelsTree(string legacyRoot, string homeRoot)
    {
        string legacyModels = Path.Combine(legacyRoot, "models");
        if (!Directory.Exists(legacyModels)) return;

        string destination = Path.Combine(homeRoot, "models");
        if (!PathContainment.IsWithin(homeRoot, destination))
            throw new IOException($"models destination '{destination}' is not contained in SCRINIA_HOME");

        MoveTreePreservingExisting(legacyModels, destination);
    }

    /// <summary>
    /// Migrates the executable-relative <c>plugins</c> tree into the mandatory
    /// <c>SCRINIA_HOME/plugins/&lt;plugin&gt;/&lt;version&gt;/&lt;rid&gt;/</c> hierarchy selected from the
    /// workspace plugin identity. Returns <c>true</c> only when legacy plugin content was actually
    /// relocated; a missing legacy dir, an unset version, or a traversal-bearing identity returns
    /// <c>false</c> without throwing.
    /// </summary>
    private static bool MigratePluginsTree(string legacyRoot, string homeRoot)
    {
        string legacyPlugins = Path.Combine(legacyRoot, "plugins");
        if (!Directory.Exists(legacyPlugins)) return false;

        string pluginName = WorkspaceSetup.GetPluginName("plugins:embeddings", "scri-plugin-embeddings");
        string? version = WorkspaceSetup.GetConfigValue("plugins:embeddings:version");
        if (string.IsNullOrEmpty(version) || !IsSafePathSegment(pluginName) || !IsSafePathSegment(version))
        {
            // No configured version means no mandatory hierarchy to migrate into, and a traversal
            // segment in either identity is refused rather than followed out of SCRINIA_HOME.
            return false;
        }

        string rid = RuntimeInformation.RuntimeIdentifier;
        string destination = Path.Combine(homeRoot, "plugins", pluginName, version, rid);
        if (!PathContainment.IsWithin(homeRoot, destination))
            return false;

        string legacyPlugin = Path.Combine(legacyPlugins, pluginName);
        if (!Directory.Exists(legacyPlugin)) return false;

        MoveTreePreservingExisting(legacyPlugin, destination);
        return true;
    }

    /// <summary>
    /// Recursively moves every file and subdirectory from <paramref name="source"/> into
    /// <paramref name="destination"/>, creating the destination as needed. An already-present
    /// destination file is preserved (never clobbered); a per-entry I/O failure is logged and skipped
    /// so one unreadable entry cannot abort the whole tree.
    /// </summary>
    private static void MoveTreePreservingExisting(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (string file in Directory.GetFiles(source))
        {
            string destFile = Path.Combine(destination, Path.GetFileName(file));
            if (File.Exists(destFile)) continue; // preserve the existing home destination
            try { File.Move(file, destFile); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogMigrationWarning($"skipped '{file}' — {ex.Message}");
            }
        }

        foreach (string dir in Directory.GetDirectories(source))
        {
            string subDestination = Path.Combine(destination, Path.GetFileName(dir));
            try { MoveTreePreservingExisting(dir, subDestination); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogMigrationWarning($"skipped subdirectory '{dir}' — {ex.Message}");
            }
        }
    }

    private static bool IsSafePathSegment(string segment) =>
        !string.IsNullOrEmpty(segment)
        && !segment.Contains(Path.DirectorySeparatorChar)
        && !segment.Contains(Path.AltDirectorySeparatorChar)
        && segment != "..";

    private static LegacyAssetMigrationState? ReadMigrationState(string homeRoot)
    {
        string statePath = Path.Combine(homeRoot, "migration-state.json");
        if (!File.Exists(statePath)) return null;

        try
        {
            string? json = File.ReadAllText(statePath);
            if (json is null) return null;
            return JsonSerializer.Deserialize(json, MigrationStateJsonContext.Default.LegacyAssetMigrationState);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable state is treated as "not yet migrated" so a retry is always safe.
            return null;
        }
    }

    private static void WriteMigrationState(string homeRoot, LegacyAssetMigrationState state)
    {
        string statePath = Path.Combine(homeRoot, "migration-state.json");
        if (!PathContainment.IsWithin(homeRoot, statePath))
            throw new IOException("migration state path is not contained in SCRINIA_HOME");

        Directory.CreateDirectory(homeRoot);
        string json = JsonSerializer.Serialize(state, MigrationStateJsonContext.Default.LegacyAssetMigrationState);
        DurableFile.AtomicWriteAllText(statePath, json);
    }

    private static void LogMigrationWarning(string message) =>
        Console.Error.WriteLine($"[scrinia:warn] legacy migration: {message}");

    /// <summary>
    /// Probes for a running Ollama at the default URL and, if detected, walks the user through
    /// picking an embedding model and pulling it if missing. On success writes
    /// <c>Scrinia:Embeddings:*</c> config and returns true so the caller can skip the
    /// local-model download steps.
    ///
    /// Returns false (continue with local setup) when Ollama isn't running, the user declines,
    /// or any required pull fails. All prompts default to safe values so an accidental Enter
    /// doesn't trigger a multi-GB download.
    /// </summary>
    private static async Task<bool> TryConfigureOllamaAsync(CancellationToken ct)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[bold]Ollama auto-detect[/]");

        // Single probe-and-list call. Setup is interactive so we can afford a generous timeout
        // — better to wait 5s and detect than to silently miss because IPv6/firewall added a
        // round-trip. The probe also surfaces the actual error string when it fails so the
        // user can diagnose ("ConnectionRefused" vs "TimeoutException" vs "HTTP 503") instead
        // of just seeing "no Ollama detected".
        var probe = await OllamaSetup.ProbeAsync(OllamaSetup.DefaultBaseUrl, timeoutSeconds: 5, ct);
        if (!probe.Reachable)
        {
            AnsiConsole.MarkupLine(
                $"[dim]  No Ollama detected at {OllamaSetup.DefaultBaseUrl}: {Markup.Escape(probe.Error ?? "unknown")}. " +
                "Continuing with local setup.[/]");
            return false;
        }

        AnsiConsole.MarkupLine(
            $"[green]  Ollama detected at {OllamaSetup.DefaultBaseUrl}.[/] " +
            $"[dim]({probe.Models.Count} model(s) installed)[/]");
        if (!AnsiConsole.Confirm("  Use Ollama for Scrinia embeddings + completion?", defaultValue: true))
            return false;

        var installed = probe.Models;
        var pulledNames = new HashSet<string>(installed.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        var pulledEmbedding = installed.Where(m => OllamaSetup.LooksLikeEmbeddingModel(m.Name)).ToList();

        string root = WorkspaceSetup.Root;

        // -- Embedding model selection --
        string? embeddingModel = await PromptForOllamaModelAsync(
            label: "embedding model",
            defaultModel: OllamaSetup.DefaultEmbeddingModel,
            installedRelevant: pulledEmbedding,
            installedAll: pulledNames,
            ct: ct);
        if (embeddingModel is null) return false;

        // -- Write config --
        WorkspaceConfig.SetValue(root, "Scrinia:Embeddings:Provider", "ollama");
        WorkspaceConfig.SetValue(root, "Scrinia:Embeddings:OllamaBaseUrl", OllamaSetup.DefaultBaseUrl);
        WorkspaceConfig.SetValue(root, "Scrinia:Embeddings:OllamaModel", embeddingModel);

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[green]  Config written:[/]");
        AnsiConsole.MarkupLine($"    [dim]Scrinia:Embeddings:Provider = ollama[/]");
        AnsiConsole.MarkupLine($"    [dim]Scrinia:Embeddings:OllamaModel = {Markup.Escape(embeddingModel)}[/]");
        return true;
    }

    /// <summary>
    /// Interactive picker for an Ollama model. Shows pulled candidates as a selection list with
    /// "Pull {default}" and "Type a name" options. When the chosen name isn't already pulled,
    /// confirms and runs <see cref="OllamaSetup.PullModelAsync"/>. Returns the final model name
    /// when ready, or <c>null</c> when the user aborts or all pull attempts fail.
    /// </summary>
    private static async Task<string?> PromptForOllamaModelAsync(
        string label,
        string defaultModel,
        List<OllamaSetup.OllamaModelInfo> installedRelevant,
        HashSet<string> installedAll,
        CancellationToken ct,
        string? fallbackOnPullFailure = null)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"[bold]Select {label}:[/]");

        const string pullDefaultOption = "__pull_default__";
        const string typeNameOption = "__type__";

        var prompt = new SelectionPrompt<string>()
            .Title($"  Choose an Ollama {label}")
            .PageSize(10);

        foreach (var m in installedRelevant)
            prompt.AddChoice(m.Name);
        if (!installedAll.Contains(defaultModel))
            prompt.AddChoice(pullDefaultOption);
        prompt.AddChoice(typeNameOption);

        prompt.UseConverter(s => s switch
        {
            pullDefaultOption => $"Pull {defaultModel}",
            typeNameOption => "Type a name…",
            _ => $"{s} (already pulled)",
        });

        string choice = AnsiConsole.Prompt(prompt);
        string targetModel;
        if (choice == pullDefaultOption)
        {
            targetModel = defaultModel;
        }
        else if (choice == typeNameOption)
        {
            targetModel = AnsiConsole.Prompt(
                new TextPrompt<string>($"  Enter Ollama {label} tag:")
                    .DefaultValue(defaultModel)
                    .ValidationErrorMessage("[red]Model name cannot be empty.[/]")
                    .Validate(s => !string.IsNullOrWhiteSpace(s)));
        }
        else
        {
            targetModel = choice;
        }

        // Pull if not already on disk. Default-No so a typo isn't a multi-GB download.
        if (!installedAll.Contains(targetModel))
        {
            bool wantsPull = AnsiConsole.Confirm(
                $"  [cyan]{Markup.Escape(targetModel)}[/] is not yet pulled. Pull it now?",
                defaultValue: false);
            if (!wantsPull) return null;

            bool ok = await OllamaSetup.PullModelAsync(OllamaSetup.DefaultBaseUrl, targetModel, ct);
            if (!ok)
            {
                if (fallbackOnPullFailure is not null
                    && !string.Equals(fallbackOnPullFailure, targetModel, StringComparison.OrdinalIgnoreCase))
                {
                    AnsiConsole.MarkupLine(
                        $"[yellow]  Pull failed. Trying fallback [cyan]{Markup.Escape(fallbackOnPullFailure)}[/]…[/]");
                    if (await OllamaSetup.PullModelAsync(OllamaSetup.DefaultBaseUrl, fallbackOnPullFailure, ct))
                    {
                        return fallbackOnPullFailure;
                    }
                }
                AnsiConsole.MarkupLine(
                    $"[red]  Pull failed. Run [cyan]ollama pull {Markup.Escape(targetModel)}[/] manually, then re-run setup.[/]");
                return null;
            }
        }

        return targetModel;
    }

    /// <summary>
    /// Remove Ollama-derived config keys from a prior <c>scri setup</c> run. Called on the
    /// non-Ollama branch of setup so a user switching off Ollama doesn't carry stale
    /// <c>Scrinia:Embeddings:Provider=ollama</c> + URL + model into the next startup. Only
    /// touches keys that were demonstrably Ollama-installed — anything pointing elsewhere
    /// (custom OpenAI base URL, user-set Anthropic key) is preserved.
    /// </summary>
    internal static void ClearStaleOllamaConfig(string root)
    {
        bool wasOllamaEmbeddings = string.Equals(
            WorkspaceConfig.GetValue(root, "Scrinia:Embeddings:Provider"),
            "ollama",
            StringComparison.OrdinalIgnoreCase);

        if (wasOllamaEmbeddings)
        {
            WorkspaceConfig.UnsetValue(root, "Scrinia:Embeddings:Provider");
            WorkspaceConfig.UnsetValue(root, "Scrinia:Embeddings:OllamaBaseUrl");
            WorkspaceConfig.UnsetValue(root, "Scrinia:Embeddings:OllamaModel");

            AnsiConsole.MarkupLine(
                "[dim]  Cleared stale Ollama-derived config keys from a prior setup run.[/]");
        }
    }

    /// <summary>
    /// Print-only guidance that replaces the retired <c>scri setup --multi-user</c> behavior.
    /// Scrinia never launches Git, so a user who previously configured merge drivers must unset the
    /// legacy driver keys and resolve conflicts by hand. This returns the exact §7.2 conflict
    /// guidance and §8.3 driver-unset argv with the workspace root substituted for the
    /// <c>&lt;workspace-root&gt;</c> placeholder — it executes nothing and resolves no driver path.
    /// The root is rendered with the platform's standard reversible quoting (§8.3) so a copied
    /// command re-parses to exactly this root; Windows paths containing characters that cmd.exe
    /// would still interpret (for example <c>%</c>, <c>^</c>, operators, or quotes) fall back to the
    /// exact <c>&lt;workspace-root&gt;</c> argv placeholder instead of emitting unsafe runnable text.
    /// </summary>
    /// <param name="workspaceRoot">The absolute workspace root to place inside the unset commands.</param>
    /// <returns>The multi-line, human guidance text; safe to print with <c>AnsiConsole.WriteLine</c>.</returns>
    internal static string PrintMergeRemovedHint(string workspaceRoot)
    {
        string root = PlatformQuoting.Current(workspaceRoot);
        string[] lines =
        [
            "Merge drivers retired: Scrinia no longer installs merge drivers and never launches Git.",
            "If a legacy managed file from a prior setup still exists, unset the driver keys by running",
            "these commands in your agent or shell:",
            "",
            $"git -C {root} config --local --unset-all merge.scrinia-meta.driver",
            $"git -C {root} config --local --unset-all merge.scrinia-nmp2.driver",
            "",
            "# Resolve the listed files manually, then stage and commit them with Git.",
            "# When no shared mutation is running, call shared_refresh for this base.",
        ];

        return string.Join("\n", lines);
    }

    /// <summary>
    /// First-run helper used by <see cref="Serve"/>. If the built-in embedding model is missing,
    /// quietly downloads it before the MCP host opens its stdio channel. All output goes to stderr
    /// to keep the JSON-RPC stdout pristine. Any failure is logged but not fatal — the server still
    /// starts; semantic search degrades to BM25-only via NullEmbeddingProvider.
    /// </summary>
    private static async Task EnsureEmbeddingModelAsync(CancellationToken ct)
    {
        string modelDir = Path.Combine(UserHome.Resolve(), "models", "m2v-MiniLM-L6-v2");
        string safetensors = Path.Combine(modelDir, "model.safetensors");
        string vocab = Path.Combine(modelDir, "vocab.txt");

        if (File.Exists(safetensors) && File.Exists(vocab))
            return;

        try { Directory.CreateDirectory(modelDir); }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"[scrinia] auto-setup: cannot create {modelDir}: {ex.Message} — semantic search disabled");
            return;
        }

        await Console.Error.WriteLineAsync("[scrinia] first-run: downloading embedding model (~50MB)…");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        string baseUrl = Model2VecModelManager.ModelBaseUrl;
        foreach (string file in new[] { "model.safetensors", "vocab.txt" })
        {
            string target = Path.Combine(modelDir, file);
            if (File.Exists(target)) continue;

            string tmp = target + ".tmp";
            try
            {
                using var resp = await http.GetAsync($"{baseUrl}/{file}", HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                await using (var fs = File.Create(tmp))
                    await resp.Content.CopyToAsync(fs, ct);
                File.Move(tmp, target, overwrite: true);
                await Console.Error.WriteLineAsync($"[scrinia] downloaded {file}");
            }
            catch (Exception ex)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                await Console.Error.WriteLineAsync($"[scrinia] auto-setup failed ({file}): {ex.Message} — semantic search disabled, run `scri setup` to retry");
                return;
            }
        }
        await Console.Error.WriteLineAsync("[scrinia] embedding model ready");
    }

    private static async Task DownloadFilesAsync(string baseUrl, string[] files, string targetDir, CancellationToken ct)
    {
        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(10);

        foreach (string file in files)
        {
            string filePath = Path.Combine(targetDir, file);
            if (File.Exists(filePath))
            {
                AnsiConsole.MarkupLine($"  [dim]{file} already exists, skipping.[/]");
                continue;
            }

            string url = $"{baseUrl}/{file}";
            AnsiConsole.MarkupLine($"  Downloading [blue]{file}[/]...");

            string tmpPath = filePath + ".tmp";
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long? totalBytes = response.Content.Headers.ContentLength;
            await using var stream = await response.Content.ReadAsStreamAsync(ct);

            await AnsiConsole.Progress()
                .AutoClear(true)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new TransferSpeedColumn(),
                    new RemainingTimeColumn())
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask(file, maxValue: totalBytes ?? 0);
                    if (totalBytes is null) task.IsIndeterminate = true;

                    await using var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    byte[] buffer = new byte[81920];
                    int bytesRead;
                    while ((bytesRead = await stream.ReadAsync(buffer, ct)) > 0)
                    {
                        await fs.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                        task.Increment(bytesRead);
                    }
                });

            File.Move(tmpPath, filePath, overwrite: true);

            long size = new FileInfo(filePath).Length;
            string sizeStr = size switch
            {
                < 1024 => $"{size} B",
                < 1024 * 1024 => $"{size / 1024.0:F1} KB",
                _ => $"{size / (1024.0 * 1024):F1} MB",
            };
            AnsiConsole.MarkupLine($"  [green]Downloaded {file} ({sizeStr})[/]");
        }
    }

    /// <summary>Get or set workspace configuration.</summary>
    /// <param name="key">Config key (e.g. plugins:embeddings). Omit to list all.</param>
    /// <param name="value">Value to set. Omit to read current value.</param>
    /// <param name="unset">Remove the setting.</param>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="json">Output as JSON instead of formatted text.</param>
    public async Task<int> Config(
        [Argument] string? key = null,
        [Argument] string? value = null,
        bool unset = false,
        string? workspaceRoot = null,
        bool json = false,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspaceRoot);
        string root = WorkspaceSetup.Root;

        if (key is null)
        {
            // List all settings
            var config = WorkspaceConfig.Load(root);
            if (json)
            {
                WriteJson(new CliConfigOutput(new Dictionary<string, string>(config, StringComparer.OrdinalIgnoreCase), null, null),
                    CliJsonContext.Default.CliConfigOutput);
                return 0;
            }

            if (config.Count == 0)
            {
                AnsiConsole.MarkupLine("[dim]No configuration set.[/]");
                return 0;
            }

            var table = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Key")
                .AddColumn("Value");

            foreach (var (k, v) in config.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
                table.AddRow(Markup.Escape(k), Markup.Escape(v));

            AnsiConsole.Write(table);
            return 0;
        }

        if (unset)
        {
            bool wasSet;
            try
            {
                wasSet = WorkspaceConfig.UnsetValue(root, key);
            }
            catch (ScriniaConfigurationException exception) when (exception.InnerException is DurableWriteException)
            {
                return EmitConfigWriteFailure(exception, json);
            }
            if (json)
                WriteJson(new CliConfigOutput(null, key, null), CliJsonContext.Default.CliConfigOutput);
            else if (wasSet)
                AnsiConsole.MarkupLine($"[green]Unset '{Markup.Escape(key)}'.[/]");
            else
                AnsiConsole.MarkupLine($"[dim]'{Markup.Escape(key)}' was not set.[/]");
            return 0;
        }

        if (value is null)
        {
            // Get a single value
            string? current = WorkspaceConfig.GetValue(root, key);
            if (json)
            {
                WriteJson(new CliConfigOutput(null, key, current), CliJsonContext.Default.CliConfigOutput);
                return 0;
            }
            if (current is not null)
                AnsiConsole.WriteLine(current);
            else
                AnsiConsole.MarkupLine("[dim]not set[/]");
            return 0;
        }

        // Set a value
        try
        {
            WorkspaceConfig.SetValue(root, key, value);
        }
        catch (ScriniaConfigurationException exception) when (exception.InnerException is DurableWriteException)
        {
            return EmitConfigWriteFailure(exception, json);
        }
        if (json)
            WriteJson(new CliConfigOutput(null, key, value), CliJsonContext.Default.CliConfigOutput);
        else
            AnsiConsole.MarkupLine($"[green]Set '{Markup.Escape(key)}' = '{Markup.Escape(value)}'.[/]");

        // Embedding settings affect vector identity. After a Scrinia:Embeddings:* write we
        // load the active provider against the new config. For a Provider switch we
        // unconditionally rebuild — the plugin path doesn't participate in the in-process
        // signature-mismatch quarantine that drives MaybeReindexAfterModelSwitch, so a
        // gating-on-signature approach would silently skip ollama→vulkan / vulkan→model2vec
        // transitions. For other Embeddings:* keys we still rely on the signature gate.
        if (key.StartsWith("Scrinia:Embeddings:", StringComparison.OrdinalIgnoreCase))
        {
            bool providerSwitch = key.Equals("Scrinia:Embeddings:Provider", StringComparison.OrdinalIgnoreCase);
            await TryReindexAfterConfigChangeAsync(providerSwitch, cancellationToken);
        }

        return 0;
    }

    private static int EmitConfigWriteFailure(ScriniaConfigurationException exception, bool json)
    {
        ConfigurationResult result = ConfigResults.FromException(exception);
        if (json)
        {
            WriteJson(new CliErrorOutput(
                exception.Message,
                "Configuration",
                "CONFIG_WRITE_FAILED",
                ExitClass: (int)result.ExitClass),
                CliJsonContext.Default.CliErrorOutput);
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(exception.Message)} (CONFIG_WRITE_FAILED, exit {((int)result.ExitClass)})");
        }

        return (int)result.ExitClass;
    }

    /// <summary>
    /// Reload the active embedding pipeline against the just-written config and trigger a
    /// reindex. If <paramref name="forceReindex"/> is true (provider key changed), runs an
    /// unconditional rebuild via <see cref="WorkspaceSetup.ForceReindexAsync"/> so the new
    /// provider — including plugin-owned ones — gets a clean vector set. Otherwise relies on
    /// the in-process signature-mismatch path inside <see cref="WorkspaceSetup.LoadPluginsAsync"/>.
    /// Errors are surfaced as warnings so a reindex failure doesn't fail the config command itself.
    /// </summary>
    private static async Task TryReindexAfterConfigChangeAsync(bool forceReindex, CancellationToken ct)
    {
        try
        {
            // Configure must have already run by the caller — workspace root and
            // MemoryStoreContext.Current are populated.
            await WorkspaceSetup.LoadPluginsAsync(ct);

            if (forceReindex)
                await WorkspaceSetup.ForceReindexAsync(ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"[scrinia:warn] Reindex after config change failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Rebuild vector embeddings for every persistent memory in the workspace.
    /// Use after switching embedding model or recovering from a corrupted vector file.</summary>
    /// <param name="workspaceRoot">Workspace root for .scrinia store. Defaults to cwd.</param>
    /// <param name="json">Emit result as JSON instead of human-readable progress.</param>
    public async Task<int> Reindex(
        string? workspaceRoot = null,
        bool json = false,
        CancellationToken cancellationToken = default)
    {
        WorkspaceSetup.Configure(workspaceRoot);

        // Wire the active embedding provider before forcing the rebuild. LoadPluginsAsync also
        // runs the signature-mismatch auto-reindex (a no-op when signatures match), then we
        // override with an unconditional pass so the command does what its name says even on
        // a "vectors are stale, I just want them rebuilt" recovery flow.
        await WorkspaceSetup.LoadPluginsAsync(cancellationToken);

        var result = await WorkspaceSetup.ForceReindexAsync(cancellationToken);

        if (result is null)
        {
            if (json)
                WriteJsonError("Reindex skipped — no embedding provider is available. Run `scri setup` first.");
            else
                AnsiConsole.MarkupLine(
                    "[yellow]Reindex skipped[/] — no embedding provider is available. " +
                    "Run [italic]scri setup[/] first.");
            return 1;
        }

        string summary =
            $"Embedded {result.Embedded}/{result.Total}, " +
            $"{result.Skipped} skipped, {result.Failed} failed.";
        if (json)
            WriteJson(new CliReindexOutput(result.Embedded, result.Total, result.Skipped, result.Failed, summary), CliJsonContext.Default.CliReindexOutput);
        else
            AnsiConsole.MarkupLine($"[green]Reindex complete.[/] {Markup.Escape(summary)}");
        return result.Failed == 0 ? 0 : 1;
    }
}

/// <summary>
/// One-time Phase 5 migration bookkeeping persisted as <c>migration-state.json</c> under
/// <c>SCRINIA_HOME</c>. <see cref="Models"/> and <see cref="Plugins"/> record which legacy trees have
/// already been relocated so the boundary is idempotent; <see cref="MigratedAt"/> is the ISO-8601
/// timestamp of the most recent successful completion.
/// </summary>
internal sealed record LegacyAssetMigrationState(bool Models, bool Plugins, string? MigratedAt);

/// <summary>
/// Source-generated serialization context for <see cref="LegacyAssetMigrationState"/> so the SCRINIA_HOME
/// migration-state marker stays trim-safe (reflection-based <c>JsonSerializer</c> is incompatible).
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(LegacyAssetMigrationState))]
internal partial class MigrationStateJsonContext : JsonSerializerContext;
