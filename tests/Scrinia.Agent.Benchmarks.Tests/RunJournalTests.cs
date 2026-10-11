using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Scrinia.Agent.Benchmarks.Tests;

public sealed class RunJournalTests
{
    [Fact]
    public async Task Disposing_journal_closes_its_attempt_event_sinks()
    {
        using var temp = new ScratchDirectory();
        var config = CaseSamples.Configuration(temp.Root);
        var journal = await RunJournal.OpenAsync(config.OutputDirectory, Manifest(), false, CancellationToken.None);
        var paths = new AttemptWorkspace().Create(config.OutputDirectory, 1, 1, config.Scrinia);
        var events = journal.CreateEvents(paths);
        await journal.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await events.WriteAsync("answer", "late", Data(), CancellationToken.None));
    }

    [Fact]
    public async Task Changed_answer_is_rejected_on_resume_even_with_completed_outcome()
    {
        using var temp = new ScratchDirectory();
        var config = CaseSamples.Configuration(temp.Root);
        await using var journal = await RunJournal.OpenAsync(config.OutputDirectory, Manifest(), false, CancellationToken.None);
        var paths = new AttemptWorkspace().Create(config.OutputDirectory, 1, 1, config.Scrinia);
        await journal.SaveAnswerAsync(paths, Answer(), CancellationToken.None);
        await journal.CommitOutcomeAsync(paths, new("sample_abs", 1, CaseState.Correct, Answer().Sha256, null, null), CancellationToken.None);
        File.WriteAllText(Path.Combine(paths.Evidence, "answer.json"), JsonSerializer.Serialize(Answer("tampered"), RecordJsonContext.Default.AnswerRecord));
        await Assert.ThrowsAsync<InvalidDataException>(() => journal.ReadOutcomesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Durable_answer_is_recoverable_before_a_judgment_exists()
    {
        using var temp = new ScratchDirectory();
        var config = CaseSamples.Configuration(temp.Root);
        await using var journal = await RunJournal.OpenAsync(config.OutputDirectory, Manifest(), false, CancellationToken.None);
        var paths = new AttemptWorkspace().Create(config.OutputDirectory, 1, 1, config.Scrinia);
        await journal.CreateEvents(paths).WriteAsync("answer", "completed", Data(), CancellationToken.None);
        await journal.SaveAnswerAsync(paths, Answer(), CancellationToken.None);
        var saved = Assert.Single(await journal.ReadOutcomesAsync(CancellationToken.None));
        Assert.Equal(CaseState.Answered, saved.State);
        Assert.Equal(Answer().Sha256, saved.AnswerSha256);
    }

    internal static RunManifest Manifest() => new(1, new string('f', 64), ["sample_abs"], JsonDocument.Parse("{}").RootElement.Clone(),
        new() { ["localInputs"] = "local", ["mcpCatalog"] = "catalog" });
    internal static AnswerRecord Answer(string text = "candidate") => new(text, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
    internal static JsonElement Data() => JsonDocument.Parse("{}").RootElement.Clone();

    [Fact]
    public void Concurrent_owners_are_rejected_and_lock_can_be_reacquired()
    {
        using var temp = new ScratchDirectory();
        using (var first = RunJournal.AcquireOwnership(temp.Root))
            Assert.Throws<IOException>(() => RunJournal.AcquireOwnership(temp.Root));
        using var later = RunJournal.AcquireOwnership(temp.Root);
    }

    [Fact]
    public async Task Immutable_manifest_and_local_fingerprint_refuse_mismatch()
    {
        using var temp = new ScratchDirectory();
        await using var journal = await RunJournal.OpenAsync(temp.Root, Manifest(), false, CancellationToken.None);
        RunJournal.ValidateLocalResumeFingerprint(temp.Root, "local");
        Assert.Throws<InvalidDataException>(() => RunJournal.ValidateLocalResumeFingerprint(temp.Root, "changed"));
        await Assert.ThrowsAsync<InvalidDataException>(() => RunJournal.OpenAsync(temp.Root, Manifest() with { Fingerprint = new string('e', 64) }, true, CancellationToken.None));
        await Assert.ThrowsAsync<IOException>(() => RunJournal.OpenAsync(temp.Root, Manifest(), false, CancellationToken.None));
        await using var resumed = await RunJournal.OpenAsync(temp.Root, Manifest(), true, CancellationToken.None);
        Assert.Equal(Manifest().Fingerprint, resumed.Manifest.Fingerprint);
    }

    [Fact]
    public async Task Preflight_without_manifest_is_preserved_but_not_resumable()
    {
        using var temp = new ScratchDirectory();
        var events = RunJournal.CreatePreflightEvents(temp.Root);
        await events.WriteAsync("preflight", "started", Data(), CancellationToken.None);
        Assert.Throws<InvalidDataException>(() => RunJournal.ValidateLocalResumeFingerprint(temp.Root, "local"));
        await Assert.ThrowsAsync<InvalidDataException>(() => RunJournal.OpenAsync(temp.Root, Manifest(), true, CancellationToken.None));
        Assert.NotEmpty(Directory.EnumerateFiles(temp.Root, "*.ndjson", SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Outcomes_refuse_missing_or_modified_answers(bool modified)
    {
        using var temp = new ScratchDirectory();
        var config = CaseSamples.Configuration(temp.Root);
        await using var journal = await RunJournal.OpenAsync(config.OutputDirectory, Manifest(), false, CancellationToken.None);
        var paths = new AttemptWorkspace().Create(config.OutputDirectory, 1, 1, config.Scrinia);
        await journal.CreateEvents(paths).WriteAsync("answer", "started", Data(), CancellationToken.None);
        var answer = Answer();
        if (modified)
        {
            await journal.SaveAnswerAsync(paths, answer, CancellationToken.None);
            File.WriteAllText(Path.Combine(paths.Evidence, "answer.json"), JsonSerializer.Serialize(Answer("changed"), RecordJsonContext.Default.AnswerRecord));
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => journal.CommitOutcomeAsync(paths,
            new("sample_abs", 1, CaseState.Correct, answer.Sha256, null, null), CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(paths.Evidence, "outcome.json")));
    }

    [Fact]
    public async Task Events_are_flushed_and_monotonic_and_completed_evidence_is_validated()
    {
        using var temp = new ScratchDirectory();
        var config = CaseSamples.Configuration(temp.Root);
        await using var journal = await RunJournal.OpenAsync(config.OutputDirectory, Manifest(), false, CancellationToken.None);
        var paths = new AttemptWorkspace().Create(config.OutputDirectory, 1, 1, config.Scrinia);
        var events = journal.CreateEvents(paths);
        await events.WriteAsync("answer", "started", Data(), CancellationToken.None);
        await events.WriteAsync("answer", "completed", Data(), CancellationToken.None);
        var eventFile = Path.Combine(paths.Evidence, "events.ndjson");
        var lines = File.ReadAllLines(eventFile);
        Assert.Equal(2, lines.Length);
        Assert.Equal(1, JsonSerializer.Deserialize(lines[0], RecordJsonContext.Default.RunEvent)!.Sequence);
        Assert.Equal(2, JsonSerializer.Deserialize(lines[1], RecordJsonContext.Default.RunEvent)!.Sequence);
        await journal.SaveAnswerAsync(paths, Answer(), CancellationToken.None);
        await journal.CommitOutcomeAsync(paths, new("sample_abs", 1, CaseState.Correct, Answer().Sha256, null, null), CancellationToken.None);
        Assert.Equal(CaseState.Correct, Assert.Single(await journal.ReadOutcomesAsync(CancellationToken.None)).State);
        File.AppendAllText(eventFile, "{unfinished");
        await Assert.ThrowsAsync<InvalidDataException>(() => journal.ReadOutcomesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Failed_event_append_prevents_outcome_publication()
    {
        using var temp = new ScratchDirectory();
        var config = CaseSamples.Configuration(temp.Root);
        await using var journal = await RunJournal.OpenAsync(config.OutputDirectory, Manifest(), false, CancellationToken.None);
        var paths = new AttemptWorkspace().Create(config.OutputDirectory, 1, 1, config.Scrinia);
        var events = journal.CreateEvents(paths);
        Directory.CreateDirectory(Path.Combine(paths.Evidence, "events.ndjson"));
        await Assert.ThrowsAnyAsync<Exception>(async () => await events.WriteAsync("learn", "started", Data(), CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() => journal.CommitOutcomeAsync(paths,
            new("sample_abs", 1, CaseState.ExecutionFailed, null, "learn", "io"), CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(paths.Evidence, "outcome.json")));
    }

    [Fact]
    public async Task Read_returns_latest_attempt_not_highest_score_and_preserves_old_evidence()
    {
        using var temp = new ScratchDirectory();
        var config = CaseSamples.Configuration(temp.Root);
        await using var journal = await RunJournal.OpenAsync(config.OutputDirectory, Manifest(), false, CancellationToken.None);
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            var paths = new AttemptWorkspace().Create(config.OutputDirectory, 1, attempt, config.Scrinia);
            await journal.CreateEvents(paths).WriteAsync("answer", "completed", Data(), CancellationToken.None);
            await journal.SaveAnswerAsync(paths, Answer(), CancellationToken.None);
            await journal.CommitOutcomeAsync(paths, new("sample_abs", attempt,
                attempt == 1 ? CaseState.Correct : CaseState.Incorrect, Answer().Sha256, null, null), CancellationToken.None);
        }
        Assert.Equal(CaseState.Incorrect, Assert.Single(await journal.ReadOutcomesAsync(CancellationToken.None)).State);
        Assert.Equal(2, Directory.EnumerateFiles(config.OutputDirectory, "outcome.json", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public async Task Interrupted_new_attempt_is_not_hidden_by_an_older_success()
    {
        using var temp = new ScratchDirectory();
        var config = CaseSamples.Configuration(temp.Root);
        await using var journal = await RunJournal.OpenAsync(config.OutputDirectory, Manifest(), false, CancellationToken.None);
        var paths = new AttemptWorkspace().Create(config.OutputDirectory, 1, 1, config.Scrinia);
        await journal.CreateEvents(paths).WriteAsync("answer", "completed", Data(), CancellationToken.None);
        await journal.SaveAnswerAsync(paths, Answer(), CancellationToken.None);
        await journal.CommitOutcomeAsync(paths, new("sample_abs", 1, CaseState.Correct, Answer().Sha256, null, null), CancellationToken.None);
        _ = new AttemptWorkspace().Create(config.OutputDirectory, 1, 2, config.Scrinia);
        var latest = Assert.Single(await journal.ReadOutcomesAsync(CancellationToken.None));
        Assert.Equal(2, latest.Attempt);
        Assert.Equal(CaseState.Running, latest.State);
    }
}
