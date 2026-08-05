using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text.Json;
using ConsoleAppFramework;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Scrinia.Core;
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
    /// <param name="multiUser">Configure git merge drivers for multi-user collaboration.</param>
    /// <param name="resolver">Conflict resolver when --multi-user is set (none, claude, copilot).</param>
    /// <param name="noOllama">Skip Ollama auto-detection even if it is running. Use to force local-only setup.</param>
    /// <param name="hooks">Install SessionStart/UserPromptSubmit hooks into detected agent CLIs (Claude Code, etc.). Opt-in.</param>
    /// <param name="uninstallHooks">Remove scrinia-managed hooks from agent CLIs and exit.</param>
    /// <param name="project">With --hooks / --uninstall-hooks, target workspace-local config (.claude/...) instead of user-global (~/.claude/...).</param>
    public async Task<int> Setup(
        string? workspaceRoot = null,
        bool multiUser = false,
        string? resolver = null,
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

        if (multiUser)
        {
            ConfigureMultiUser(resolver);
        }

        string exeDir = AppContext.BaseDirectory;

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

        string modelDir = Path.Combine(exeDir, "models", "m2v-MiniLM-L6-v2");
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
        string pluginsDir = Path.Combine(exeDir, "plugins");
        string pluginName = WorkspaceSetup.GetPluginName("plugins:embeddings", "scri-plugin-embeddings");
        string ext = OperatingSystem.IsWindows() ? ".exe" : "";

        // Check both subdirectory layout (multi-file publish) and flat layout (single-file)
        string pluginExe = Path.Combine(pluginsDir, pluginName, $"{pluginName}{ext}");
        if (!File.Exists(pluginExe))
            pluginExe = Path.Combine(pluginsDir, $"{pluginName}{ext}");

        if (File.Exists(pluginExe))
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine("[bold]Vulkan plugin (GPU acceleration)[/]");

            string vulkanModelsDir = Path.Combine(pluginsDir, pluginName);
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

        return 0;
    }

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

    private static void ConfigureMultiUser(string? resolver)
    {
        string root = WorkspaceSetup.Root;
        string scriniaDir = Path.Combine(root, ".scrinia");

        // 1. Configure git merge drivers
        string driver = ResolveMergeDriverPath();
        RunGit(root, "config", "merge.scrinia-meta.driver",
            $"\"{driver}\" meta %O %A %B %P");
        RunGit(root, "config", "merge.scrinia-nmp2.driver",
            $"\"{driver}\" nmp2 %O %A %B %P");
        AnsiConsole.MarkupLine("[green]  Git merge drivers configured.[/]");

        // 2. Create/update .scrinia/.gitattributes
        string gitattributesPath = Path.Combine(scriniaDir, ".gitattributes");
        Directory.CreateDirectory(scriniaDir);
        File.WriteAllText(gitattributesPath,
            "*.meta.json merge=scrinia-meta\n*.nmp2 merge=scrinia-nmp2\n");
        AnsiConsole.MarkupLine($"[green]  Created:[/] {Markup.Escape(gitattributesPath)}");

        // 3. Create .scrinia/merge.config
        string resolverValue = resolver ?? "none";
        string mergeConfigPath = Path.Combine(scriniaDir, "merge.config");
        string mergeConfigJson = JsonSerializer.Serialize(
            new MergeConfig(JaccardThreshold: 0.7, Resolver: resolverValue, ConflictDir: "conflict"),
            CliJsonContext.Default.MergeConfig);
        File.WriteAllText(mergeConfigPath, mergeConfigJson + "\n");
        AnsiConsole.MarkupLine($"[green]  Created:[/] {Markup.Escape(mergeConfigPath)}");

        // 4. Add .scrinia/hooks/scri-merge* to .gitignore if not already there
        string gitignorePath = Path.Combine(root, ".gitignore");
        const string hookEntry = ".scrinia/hooks/scri-merge*";
        bool needsEntry = true;
        if (File.Exists(gitignorePath))
        {
            string content = File.ReadAllText(gitignorePath);
            if (content.Contains(hookEntry, StringComparison.Ordinal))
                needsEntry = false;
        }
        if (needsEntry)
        {
            using var writer = File.AppendText(gitignorePath);
            writer.WriteLine();
            writer.WriteLine("# Scrinia merge driver binary (platform-specific)");
            writer.WriteLine(hookEntry);
            AnsiConsole.MarkupLine($"[green]  Updated:[/] {Markup.Escape(gitignorePath)}");
        }

        // 5. Print instructions
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine(
            "[bold]Multi-user merge driver configured.[/] " +
            "Copy scri-merge binary to [blue].scrinia/hooks/[/] for your platform.");
    }

    /// <summary>
    /// Path git should invoke for the merge driver. scri-merge is published beside scri, so
    /// prefer the sibling of the running executable; fall back to the bare name for installs
    /// that expose it on PATH only.
    /// </summary>
    private static string ResolveMergeDriverPath()
    {
        string exeName = OperatingSystem.IsWindows() ? "scri-merge.exe" : "scri-merge";
        string? installDir = Path.GetDirectoryName(Environment.ProcessPath ?? "");
        if (!string.IsNullOrEmpty(installDir))
        {
            string sibling = Path.Combine(installDir, exeName);
            if (File.Exists(sibling))
                return sibling.Replace('\\', '/');   // git runs driver commands through sh
        }
        return "scri-merge";
    }

    private static void RunGit(string workingDir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process.");
        process.WaitForExit(10_000);
        if (process.ExitCode != 0)
        {
            string error = process.StandardError.ReadToEnd();
            throw new InvalidOperationException(
                $"git {string.Join(' ', args)} failed (exit {process.ExitCode}): {error}");
        }
    }

    /// <summary>
    /// First-run helper used by <see cref="Serve"/>. If the built-in embedding model is missing,
    /// quietly downloads it before the MCP host opens its stdio channel. All output goes to stderr
    /// to keep the JSON-RPC stdout pristine. Any failure is logged but not fatal — the server still
    /// starts; semantic search degrades to BM25-only via NullEmbeddingProvider.
    /// </summary>
    private static async Task EnsureEmbeddingModelAsync(CancellationToken ct)
    {
        string exeDir = AppContext.BaseDirectory;
        string modelDir = Path.Combine(exeDir, "models", "m2v-MiniLM-L6-v2");
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
            bool wasSet = WorkspaceConfig.UnsetValue(root, key);
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
        WorkspaceConfig.SetValue(root, key, value);
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
            WriteJson(new CliReindexOutput(result.Embedded, summary), CliJsonContext.Default.CliReindexOutput);
        else
            AnsiConsole.MarkupLine($"[green]Reindex complete.[/] {Markup.Escape(summary)}");
        return result.Failed == 0 ? 0 : 1;
    }
}
