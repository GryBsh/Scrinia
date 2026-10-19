using Microsoft.Extensions.AI;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Scrinia.Agent.Benchmarks;

public sealed class BenchmarkApplication(Func<ModelSettings, IChatClient>? createClient = null,
    TextWriter? output = null, TextWriter? error = null)
{
    public async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var stdout = output ?? Console.Out;
        var stderr = error ?? Console.Error;
        bool running = false;
        bool preflighting = false;
        try
        {
            if (args is ["--help"])
            {
                await stdout.WriteLineAsync("Usage: scrinia-agent-bench run --config FILE [--resume DIRECTORY]\nRun LongMemEval through isolated Scrinia MCP sessions and a separate judge.");
                return 0;
            }
            var (file, resume) = Parse(args);
            var config = new ConfigurationReader().Read(file);
            string root = resume is null ? config.OutputDirectory : Path.GetFullPath(resume);
            if (resume is not null)
                config = config with { OutputDirectory = root };
            foreach (var input in new[] { config.Dataset, config.Scrinia.PayloadDirectory, config.Scrinia.HomeSeedDirectory, config.Scrinia.Executable, Path.GetFullPath(file) }.OfType<string>())
                PathSafety.RequireSeparate(root, input);
            var dataset = await new LongMemEvalReader().LoadAsync(config.Dataset, config.CaseIds, ct);
            var hashes = Inventory(config);
            string local = RunFingerprint.Compute(config, dataset, hashes);
            hashes["localInputs"] = local;
            using var ownership = RunJournal.AcquireOwnership(root);
            if (resume is not null) RunJournal.ValidateLocalResumeFingerprint(root, local);
            else if (File.Exists(Path.Combine(root, "manifest.json"))) throw InputDiagnostics.Invalid("Run already exists; use --resume.");
            var previousManifest = resume is null ? null : RunJournal.ReadManifest(root);
            var factory = createClient ?? ModelClientFactory.Create;
            preflighting = true;
            await using (var rawEvents = RunJournal.CreatePreflightEvents(root))
            {
                var events = new PreflightEvents(new RequiredEvents(rawEvents));
                var preflight = Directory.EnumerateDirectories(Path.Combine(root, "preflight")).Order(StringComparer.Ordinal).Last();
                var paths = new AttemptWorkspace().Create(Path.Combine(preflight, "probe"), 1, 1, config.Scrinia);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(config.Limits.CaseSeconds));
                await events.WriteAsync("preflight", "start", JsonSerializer.SerializeToElement(config, ConfigurationJsonContext.Default.RunConfiguration), ct);
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    await using (var session = await ScriniaMcpSession.OpenAsync(config.Scrinia, paths, events, deadline.Token))
                    {
                        hashes["mcpCatalog"] = session.CatalogHash;
                        hashes["serverVersion"] = session.ServerVersion ?? "unavailable";
                        if (previousManifest is not null && (hashes["mcpCatalog"] != previousManifest.ContentHashes["mcpCatalog"] || hashes["serverVersion"] != previousManifest.ContentHashes["serverVersion"]))
                            throw new BenchmarkFailureException(BenchmarkFailureCode.CATALOG_DRIFT, "MCP catalog or server identity changed during resume preflight.");
                        var capability = await new AgentEpisode(factory, previousManifest?.ContentHashes["agentModel"]).RunAsync(new(EpisodePhase.Learn,
                            "Capability check only. Call the guide tool once, then reply ready. Do not store memories."),
                            session, config.Agent, config.Limits, events, deadline.Token);
                        if (!events.GuideSucceeded) throw new BenchmarkFailureException(BenchmarkFailureCode.PREFLIGHT_FAILED, "Agent capability check did not successfully invoke guide.");
                        hashes["agentModel"] = capability.ReportedModelId ?? "unavailable";
                    }
                    using var judgeDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    judgeDeadline.CancelAfter(TimeSpan.FromSeconds(config.Limits.EpisodeSeconds));
                    using var judge = new ObservedChatClient(factory(config.Judge), config.Judge, config.Limits, events, "judge-preflight", previousManifest?.ContentHashes["judgeModel"]);
                    var response = await judge.GetResponseAsync([new(ChatRole.User, "Reply yes.")], new ChatOptions(), judgeDeadline.Token);
                    if (response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().Any() || LongMemEvalRubric.ParseVerdict(response.Text) != true)
                        throw new BenchmarkFailureException(BenchmarkFailureCode.PREFLIGHT_FAILED, "Judge capability check did not return yes without tools.");
                    hashes["judgeModel"] = judge.ReportedModelId ?? "unavailable";
                    await events.WriteAsync("preflight", "complete", ChatEvidence.Build(w => w.WriteString("reproducibilityLimit",
                        "Reported IDs and stable aliases do not prove unchanged provider weights; unavailable identity is explicitly recorded.")), ct);
                }
                catch (Exception failure)
                {
                    await events.WriteAsync("preflight", "failure", ChatEvidence.Build(w => w.WriteString("errorType", failure.GetType().Name)), CancellationToken.None);
                    throw;
                }
                finally
                {
                    await events.WriteAsync("preflight", "phase-end", ChatEvidence.Build(w => w.WriteNumber("durationMs", System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds)), CancellationToken.None);
                }
            }
            if (RunFingerprint.Compute(config, dataset, Inventory(config)) != local)
                throw new BenchmarkFailureException(BenchmarkFailureCode.INPUT_INVALID, "Local inputs changed during preflight.");
            var resolved = resume is null ? JsonSerializer.SerializeToElement(config, ConfigurationJsonContext.Default.RunConfiguration)
                : RunJournal.ReadManifest(root).ResolvedConfiguration;
            var manifest = new RunManifest(1, RunFingerprint.Compute(config, dataset, hashes), dataset.Cases.Select(c => c.Evaluation.QuestionId).ToArray(), resolved, hashes);
            await using var journal = await RunJournal.OpenAsync(root, manifest, resume is not null, ct);
            running = true;
            var runner = new CaseRunner(new AgentEpisode(factory, hashes["agentModel"]), new AnswerGrader(factory, hashes["judgeModel"]));
            Exception? runFailure = null;
            try
            {
                var previous = (await journal.ReadOutcomesAsync(ct)).ToDictionary(o => o.QuestionId, StringComparer.Ordinal);
                for (int i = 0; i < dataset.Cases.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var item = dataset.Cases[i];
                    previous.TryGetValue(item.Evaluation.QuestionId, out var old);
                    if (old?.State is CaseState.Correct or CaseState.Incorrect) continue;
                    var outcome = await runner.RunAsync(item, i + 1, (old?.Attempt ?? 0) + 1, config, journal, root, ct);
                    await stdout.WriteLineAsync($"Case {i + 1}/{dataset.Cases.Count}: {outcome.State}");
                    if (outcome.State == CaseState.Cancelled) break;
                    if (outcome.FailureCode == nameof(BenchmarkFailureCode.PROVIDER_UNAVAILABLE))
                    {
                        await stderr.WriteLineAsync("PROVIDER_UNAVAILABLE: endpoint recovery reached a retry or time limit; run stopped. Restore endpoint access, then rerun with --resume DIRECTORY using the output directory for this run.");
                        break;
                    }
                }
            }
            catch (Exception failure) { runFailure = failure; throw; }
            finally
            {
                try { await SummaryWriter.WriteAsync(root, dataset, journal, stdout, CancellationToken.None); }
                catch when (runFailure is not null) { /* Retain the primary failure; incomplete evidence remains on disk. */ }
            }
            if (ct.IsCancellationRequested) return 130;
            return RunSummary.Compute(manifest.SelectedIds, await journal.ReadOutcomesAsync(ct)).Provisional ? 1 : 0;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { await stderr.WriteLineAsync("CANCELLED: run interrupted; durable evidence is retained."); return 130; }
        catch (Exception failure)
        {
            string code = failure is BenchmarkFailureException typed ? typed.Code.ToString() : running ? "RUN_FAILED" : preflighting ? "PREFLIGHT_FAILED" : "INPUT_INVALID";
            // Never echo provider exception messages or raw configuration values.
            await stderr.WriteLineAsync($"{code}: {failure.GetType().Name}. Check configuration and retained evidence.");
            if (failure is BenchmarkFailureException known) await stderr.WriteLineAsync(known.Message);
            else if (InputDiagnostics.Get(failure) is { } diagnostic) await stderr.WriteLineAsync(diagnostic);
            return running ? 1 : 2;
        }
    }

    private static (string Config, string? Resume) Parse(string[] args)
    {
        if (args.Length < 3 || args[0] != "run" || args.Length % 2 != 1) throw new InvalidDataException("Expected run --config FILE [--resume DIRECTORY].");
        var flags = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i < args.Length; i += 2)
            if (args[i] is not ("--config" or "--resume") || string.IsNullOrWhiteSpace(args[i + 1]) || args[i + 1].StartsWith("--", StringComparison.Ordinal) || !flags.TryAdd(args[i], args[i + 1]))
                throw new InvalidDataException("Unknown, duplicate, or incomplete option.");
        if (!flags.TryGetValue("--config", out var config)) throw new InvalidDataException("--config is required.");
        return (config, flags.GetValueOrDefault("--resume"));
    }

    private static Dictionary<string, string> Inventory(RunConfiguration config)
    {
        static string FileHash(string file) { using var stream = File.OpenRead(file); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["payload"] = RunFingerprint.HashTree(config.Scrinia.PayloadDirectory),
            ["seed"] = config.Scrinia.HomeSeedDirectory is { } seed ? RunFingerprint.HashTree(seed) : "none",
            ["host"] = FileHash(config.Scrinia.Executable),
            ["agentInstructions"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(AgentInstructions.ForPhase(EpisodePhase.Learn) + "\n\n" + AgentInstructions.ForPhase(EpisodePhase.Answer)))),
            ["rubric"] = LongMemEvalRubric.ContentHash
        };
        // Fingerprint deployed assemblies, including the actual SDK implementation and prompt builders.
        foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll").Order(StringComparer.Ordinal))
        {
            hashes["runtime/" + Path.GetFileName(file)] = FileHash(file);
            hashes["runtimeVersion/" + Path.GetFileName(file)] = System.Diagnostics.FileVersionInfo.GetVersionInfo(file).ProductVersion ?? "unavailable";
        }
        string product = Path.Combine(config.Scrinia.PayloadDirectory, "scri.dll");
        hashes["productVersion"] = System.Diagnostics.FileVersionInfo.GetVersionInfo(File.Exists(product) ? product : config.Scrinia.Executable).ProductVersion ?? "unavailable";
        return hashes;
    }

    private sealed class PreflightEvents(IRunEvents inner) : IRunEvents
    {
        public bool GuideSucceeded { get; private set; }
        public async ValueTask WriteAsync(string phase, string kind, JsonElement data, CancellationToken ct)
        {
            await inner.WriteAsync(phase, kind, data, ct);
            if (kind == "tool-result" && data.GetProperty("name").GetString() == "guide" && !data.GetProperty("error").GetBoolean()) GuideSucceeded = true;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
