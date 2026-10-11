using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Scrinia.Agent.Benchmarks;

public sealed class RunJournal : IAsyncDisposable
{
    private readonly string _root;
    private readonly Dictionary<string, EventSink> _sinks = new(StringComparer.Ordinal);
    private readonly AtomicJsonFile _files = new();
    private bool _disposed;
    private static readonly RecordJsonContext Strict = new(new JsonSerializerOptions(RecordJsonContext.Default.Options)
    {
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true
    });
    public RunManifest Manifest { get; }

    private RunJournal(string root, RunManifest manifest) { _root = root; Manifest = manifest; }

    public static FileStream AcquireOwnership(string root)
    {
        PathSafety.RequireNoLinks(root);
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, ".run.lock");
        PathSafety.RequireNoLinks(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public static void ValidateLocalResumeFingerprint(string root, string localFingerprint)
    {
        var manifest = ReadManifest(root);
        if (!manifest.ContentHashes.TryGetValue("localInputs", out var stored) || stored != localFingerprint)
            throw new InvalidDataException("Local run inputs changed; use a new run directory.");
    }

    public static IRunEvents CreatePreflightEvents(string root)
    {
        PathSafety.RequireNoLinks(root);
        var parent = Directory.CreateDirectory(Path.Combine(root, "preflight")).FullName;
        int ordinal = Directory.EnumerateDirectories(parent).Select(p => ParseOrdinal(Path.GetFileName(p))).DefaultIfEmpty(0).Max() + 1;
        var directory = Path.Combine(parent, ordinal.ToString("D6", CultureInfo.InvariantCulture));
        if (Directory.Exists(directory)) throw new IOException("Preflight evidence already exists.");
        Directory.CreateDirectory(directory);
        return new EventSink(directory);
    }

    public static async Task<RunJournal> OpenAsync(string root, RunManifest manifest, bool resume, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        root = Path.GetFullPath(root);
        PathSafety.RequireNoLinks(root);
        ValidateManifest(manifest);
        var destination = Path.Combine(root, "manifest.json");
        if (resume)
        {
            var existing = ReadManifest(root);
            if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(existing, Strict.RunManifest),
                JsonSerializer.SerializeToElement(manifest, Strict.RunManifest)))
                throw new InvalidDataException("Run manifest does not match current inputs and discovery.");
            return new(root, existing);
        }
        Directory.CreateDirectory(root);
        if (File.Exists(destination)) throw new IOException("Run manifest already exists; use resume or a new run directory.");
        await new AtomicJsonFile().WriteAsync(destination, JsonSerializer.SerializeToUtf8Bytes(manifest, Strict.RunManifest), ct);
        return new(root, manifest);
    }

    public IRunEvents CreateEvents(AttemptPaths paths)
    {
        ValidatePaths(paths);
        if (!_sinks.TryGetValue(paths.Root, out var sink)) _sinks.Add(paths.Root, sink = new EventSink(paths.Evidence));
        return sink;
    }

    public async Task SaveAnswerAsync(AttemptPaths paths, AnswerRecord answer, CancellationToken ct)
    {
        var (ordinal, attempt) = ValidatePaths(paths);
        AtomicJsonFile.ValidateAnswerHash(answer.Text, answer.Sha256);
        if (string.IsNullOrWhiteSpace(answer.Text)) throw new InvalidDataException("An empty final answer is an execution failure.");
        var destination = Path.Combine(paths.Evidence, "answer.json");
        if (File.Exists(destination)) throw new IOException("Answer is immutable; use a new attempt.");
        var sink = (EventSink)CreateEvents(paths);
        await sink.WriteAsync("answer", "saved", JsonSerializer.SerializeToElement(answer.Sha256, ConfigurationJsonContext.Default.String), ct);
        await _files.WriteAsync(destination, JsonSerializer.SerializeToUtf8Bytes(answer, Strict.AnswerRecord), ct);
        var evidence = await sink.SnapshotAsync(seal: false, ct);
        var receipt = new OutcomeEnvelope(1, new(Manifest.SelectedIds[ordinal - 1], attempt, CaseState.Answered,
            answer.Sha256, null, null), evidence.Count, evidence.Hash);
        await _files.WriteAsync(Path.Combine(paths.Evidence, "answer.saved.json"), JsonSerializer.SerializeToUtf8Bytes(receipt, Strict.OutcomeEnvelope), ct);
    }

    public async Task CommitOutcomeAsync(AttemptPaths paths, CaseOutcome outcome, CancellationToken ct)
    {
        var (ordinal, attempt) = ValidatePaths(paths);
        ValidateOutcome(outcome, ordinal, attempt);
        if (RequiresAnswer(outcome.State) || outcome.AnswerSha256 is not null) _ = ReadAnswer(paths, outcome.AnswerSha256);
        var evidence = await ((EventSink)CreateEvents(paths)).SnapshotAsync(seal: true, ct);
        if (evidence.Count == 0) throw new InvalidDataException("An outcome requires durable event evidence.");
        var envelope = new OutcomeEnvelope(1, outcome, evidence.Count, evidence.Hash);
        var destination = Path.Combine(paths.Evidence, "outcome.json");
        if (File.Exists(destination)) throw new IOException("Outcome is immutable; use a new attempt.");
        await _files.WriteAsync(destination, JsonSerializer.SerializeToUtf8Bytes(envelope, Strict.OutcomeEnvelope), ct);
    }

    public Task<IReadOnlyList<CaseOutcome>> ReadOutcomesAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var results = new Dictionary<string, CaseOutcome>(StringComparer.Ordinal);
        var cases = Path.Combine(_root, "cases");
        if (!Directory.Exists(cases)) return Task.FromResult<IReadOnlyList<CaseOutcome>>([]);
        PathSafety.RequireNoLinks(cases);
        foreach (var caseDirectory in Directory.EnumerateDirectories(cases))
        {
            int ordinal = ParseOrdinal(Path.GetFileName(caseDirectory));
            if (ordinal > Manifest.SelectedIds.Length) throw new InvalidDataException("Unexpected case directory.");
            var attempts = Path.Combine(caseDirectory, "attempts");
            PathSafety.RequireNoLinks(attempts);
            if (!Directory.Exists(attempts)) continue;
            var numbers = Directory.EnumerateDirectories(attempts).Select(p => ParseOrdinal(Path.GetFileName(p)))
                .Concat(Directory.EnumerateFiles(attempts, "*.reserved").Select(p => ParseOrdinal(Path.GetFileNameWithoutExtension(p))))
                .Distinct().Order();
            foreach (int attempt in numbers)
            {
                ct.ThrowIfCancellationRequested();
                var paths = PathsFor(ordinal, attempt);
                ValidatePaths(paths);
                var final = Path.Combine(paths.Evidence, "outcome.json");
                var receipt = Path.Combine(paths.Evidence, "answer.saved.json");
                CaseOutcome outcome;
                if (File.Exists(final) || File.Exists(receipt))
                {
                    bool completed = File.Exists(final);
                    var envelope = ReadJson(completed ? final : receipt, Strict.OutcomeEnvelope);
                    if (envelope.SchemaVersion != 1 || envelope.EventCount < 1 || (!completed && envelope.Outcome.State != CaseState.Answered))
                        throw new InvalidDataException("Invalid outcome envelope.");
                    outcome = envelope.Outcome;
                    ValidateOutcome(outcome, ordinal, attempt);
                    ValidateEvidence(paths.Evidence, envelope.EventCount, envelope.EventSha256, allowTail: !completed);
                    if (RequiresAnswer(outcome.State) || outcome.AnswerSha256 is not null) _ = ReadAnswer(paths, outcome.AnswerSha256);
                }
                else outcome = new(Manifest.SelectedIds[ordinal - 1], attempt, CaseState.Running, null, null, null);
                // Validate all retained outcomes; the latest attempt wins even when interrupted.
                results[outcome.QuestionId] = outcome;
            }
        }
        return Task.FromResult<IReadOnlyList<CaseOutcome>>(Manifest.SelectedIds.Where(results.ContainsKey).Select(id => results[id]).ToArray());
    }

    internal AttemptPaths PathsFor(int ordinal, int attempt)
    {
        var root = Path.Combine(_root, "cases", ordinal.ToString("D6", CultureInfo.InvariantCulture), "attempts", attempt.ToString("D6", CultureInfo.InvariantCulture));
        return new(root, Path.Combine(root, "workspace"), Path.Combine(root, "home"), Path.Combine(root, "evidence"));
    }

    internal AnswerRecord ReadAnswer(AttemptPaths paths, string? expectedHash)
    {
        var answer = ReadJson(Path.Combine(paths.Evidence, "answer.json"), Strict.AnswerRecord);
        if (string.IsNullOrWhiteSpace(answer.Text) || answer.Sha256 != expectedHash)
            throw new InvalidDataException("Outcome does not reference its saved answer.");
        AtomicJsonFile.ValidateAnswerHash(answer.Text, answer.Sha256);
        return answer;
    }

    private (int Ordinal, int Attempt) ValidatePaths(AttemptPaths paths)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        PathSafety.RequireNoLinks(paths.Root);
        var parts = Path.GetRelativePath(_root, paths.Root).Split(Path.DirectorySeparatorChar);
        if (parts.Length != 4 || parts[0] != "cases" || parts[2] != "attempts") throw new InvalidDataException("Attempt is outside this run.");
        var ordinal = ParseOrdinal(parts[1]);
        var attempt = ParseOrdinal(parts[3]);
        if (ordinal > Manifest.SelectedIds.Length || paths != PathsFor(ordinal, attempt)) throw new InvalidDataException("Attempt path identity mismatch.");
        PathSafety.RequireNoLinks(paths.Evidence);
        return (ordinal, attempt);
    }

    private void ValidateOutcome(CaseOutcome outcome, int ordinal, int attempt)
    {
        if (outcome is null || outcome.QuestionId != Manifest.SelectedIds[ordinal - 1] || outcome.Attempt != attempt ||
            !Enum.IsDefined(outcome.State) || outcome.State == CaseState.Pending ||
            (RequiresAnswer(outcome.State) && !IsDigest(outcome.AnswerSha256)))
            throw new InvalidDataException("Outcome identity or state is invalid.");
    }

    private static bool RequiresAnswer(CaseState state) => state is CaseState.Answered or CaseState.Correct or CaseState.Incorrect or CaseState.GradingFailed;
    private static bool IsDigest(string? text) => text is { Length: 64 } && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static int ParseOrdinal(string text) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value > 0 && text == value.ToString("D6", CultureInfo.InvariantCulture)
        ? value : throw new InvalidDataException("Invalid case or attempt ordinal.");

    private static RunManifest ReadManifest(string root)
    {
        var manifest = ReadJson(Path.Combine(root, "manifest.json"), Strict.RunManifest);
        ValidateManifest(manifest);
        return manifest;
    }

    private static void ValidateManifest(RunManifest manifest)
    {
        if (manifest.SchemaVersion != 1 || !IsDigest(manifest.Fingerprint) || manifest.SelectedIds is null ||
            manifest.SelectedIds.Length == 0 || manifest.SelectedIds.Any(string.IsNullOrWhiteSpace) ||
            manifest.SelectedIds.Distinct(StringComparer.Ordinal).Count() != manifest.SelectedIds.Length ||
            manifest.ResolvedConfiguration.ValueKind != JsonValueKind.Object || manifest.ContentHashes is null ||
            !manifest.ContentHashes.ContainsKey("localInputs") || !manifest.ContentHashes.ContainsKey("mcpCatalog"))
            throw new InvalidDataException("Invalid run manifest.");
    }

    private static T ReadJson<T>(string path, JsonTypeInfo<T> type) where T : class
    {
        PathSafety.RequireNoLinks(path);
        try { return JsonSerializer.Deserialize(File.ReadAllBytes(path), type) ?? throw new InvalidDataException("Null evidence record."); }
        catch (Exception e) when (e is JsonException or FileNotFoundException or DirectoryNotFoundException)
        { throw new InvalidDataException("Required evidence is missing or corrupt.", e); }
    }

    private static void ValidateEvidence(string directory, int count, string hash, bool allowTail)
    {
        var path = Path.Combine(directory, "events.ndjson");
        PathSafety.RequireNoLinks(path);
        if (!File.Exists(path)) throw new InvalidDataException("Missing event evidence.");
        var bytes = File.ReadAllBytes(path);
        int start = 0;
        for (int i = 1; i <= count; i++)
        {
            int end = Array.IndexOf(bytes, (byte)'\n', start);
            if (end < 0) throw new InvalidDataException("Incomplete event evidence.");
            RunEvent item;
            try { item = JsonSerializer.Deserialize(bytes.AsSpan(start, end - start), Strict.RunEvent) ?? throw new InvalidDataException("Null event."); }
            catch (JsonException e) { throw new InvalidDataException("Corrupt event evidence.", e); }
            if (item.Sequence != i || string.IsNullOrWhiteSpace(item.Phase) || string.IsNullOrWhiteSpace(item.Kind) || item.AtUtc == default)
                throw new InvalidDataException("Invalid event sequence or metadata.");
            start = end + 1;
        }
        if ((!allowTail && start != bytes.Length) || Convert.ToHexStringLower(SHA256.HashData(bytes.AsSpan(0, start))) != hash)
            throw new InvalidDataException("Event evidence digest does not match the outcome.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var sink in _sinks.Values) await sink.DisposeAsync();
    }

    private sealed class EventSink(string directory) : IRunEvents
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private int _sequence;
        private bool _failed;
        private bool _sealed;
        private bool _disposed;
        private string EventPath => Path.Combine(directory, "events.ndjson");

        public async ValueTask WriteAsync(string phase, string kind, JsonElement data, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                RequireHealthy();
                if (string.IsNullOrWhiteSpace(phase) || string.IsNullOrWhiteSpace(kind) || data.ValueKind == JsonValueKind.Undefined)
                    throw new InvalidDataException("Event metadata is required.");
                PathSafety.RequireNoLinks(EventPath);
                if (_sequence == 0 && File.Exists(EventPath)) throw new InvalidDataException("Existing event evidence cannot be reopened for appending.");
                var item = new RunEvent(_sequence + 1, DateTimeOffset.UtcNow, phase, kind, data);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(item, Strict.RunEvent);
                await using var stream = new FileStream(EventPath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
                await stream.WriteAsync(bytes, ct);
                await stream.WriteAsync("\n"u8.ToArray(), ct);
                stream.Flush(flushToDisk: true);
                _sequence++;
            }
            catch { _failed = true; throw; }
            finally { _gate.Release(); }
        }

        public async Task<(int Count, string Hash)> SnapshotAsync(bool seal, CancellationToken ct)
        {
            await _gate.WaitAsync(ct);
            try
            {
                RequireHealthy();
                var bytes = File.Exists(EventPath) ? await File.ReadAllBytesAsync(EventPath, ct) : [];
                var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
                if (_sequence > 0) ValidateEvidence(directory, _sequence, hash, allowTail: false);
                if (seal) _sealed = true;
                return (_sequence, hash);
            }
            finally { _gate.Release(); }
        }

        private void RequireHealthy()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_failed || _sealed) throw new InvalidDataException("Event sink failed or has already published its outcome.");
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await _gate.WaitAsync();
            _gate.Release();
            _gate.Dispose();
        }
    }
}
