using FluentAssertions;
using Microsoft.Extensions.Logging;
using System.Text;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Formats;
using System.Text.Json;
using Scrinia.Mcp;

namespace Scrinia.Tests;

[Collection(GlobalWorkspaceConfigCollection.Name)]
public sealed class ReservedAppendIntegrityTests
{
    [Theory]
    [InlineData("skill", "\n\n")]
    [InlineData("agent", "\n")]
    public async Task ReservedAppendPreservesCompleteBody(string kind, string separator)
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        string address = $"/{kind}/long-body";
        await tools.MemoryRemember(address, [new string('x', 9000) + "TAIL"]);
        string path = Path.Combine(PathRouter.ToFileEntityDir(kind, scope.WorkspaceDir), "long-body.md");
        string before = File.ReadAllText(path);
        var result = ResponseParser.Parse(await tools.MemoryAppend(address, "APPENDED"));
        result.Status.Should().Be("success");
        File.ReadAllText(path).Should().Be(before + separator + "APPENDED");
        Directory.GetFiles(Path.Combine(Path.GetDirectoryName(path)!, "versions"), "*.md")
            .Select(File.ReadAllText).Should().Contain(before);
    }
    [Fact]
    public async Task BuiltInAppendCreatesOneExactOverrideWithoutAddingScaffold()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        string original = EmbeddedPrompts.LoadAllSkills()["qa"];
        var result = ResponseParser.Parse(await tools.MemoryAppend("/skill/qa", "FIRST"));
        result.Status.Should().Be("success");
        string path = Path.Combine(PathRouter.ToFileEntityDir("skill", scope.WorkspaceDir), "qa.md");
        File.ReadAllText(path).Should().Be(original + "\n\nFIRST");
        File.Exists(Path.Combine(scope.WorkspaceDir, ".scrinia", ".gitignore")).Should().BeTrue();
        await tools.MemoryAppend("/skill/qa", "SECOND");
        File.ReadAllText(path).Should().Be(original + "\n\nFIRST\n\nSECOND");
        Directory.GetFiles(Path.Combine(Path.GetDirectoryName(path)!, "versions"), "*.md")
            .Select(File.ReadAllText).Should().ContainSingle().Which.Should().Be(original + "\n\nFIRST");
    }

    [Fact]
    public async Task MissingSkillAppendRefusesWithoutCreatingAnOverride()
    {
        using var scope = new TestHelpers.StoreScope();
        var result = ResponseParser.Parse(await new ScriniaMcpTools().MemoryAppend("/skill/missing", "suffix"));
        result.Status.Should().Be("error");
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
        File.Exists(Path.Combine(PathRouter.ToFileEntityDir("skill", scope.WorkspaceDir), "missing.md"))
            .Should().BeFalse();
    }

    [Fact]
    public async Task MissingAgentAppendRefusesWithoutCreatingAFile()
    {
        using var scope = new TestHelpers.StoreScope();
        var result = ResponseParser.Parse(await new ScriniaMcpTools().MemoryAppend("/agent/missing", "suffix"));
        result.Status.Should().Be("error");
        result.ErrorCode.Should().Be(ErrorCodes.NotFound);
        File.Exists(Path.Combine(PathRouter.ToFileEntityDir("agent", scope.WorkspaceDir), "missing.md"))
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("skill")]
    [InlineData("agent")]
    public async Task AppendPreservesCuratedAndUnknownMetadata(string kind)
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember($"/{kind}/metadata", ["body"]);
        string path = Path.Combine(PathRouter.ToFileEntityDir(kind, scope.WorkspaceDir), "metadata.md");
        string metaPath = Path.ChangeExtension(path, ".meta.json");
        string prior = """{"createdAt":"2026-01-01","updatedAt":"old","v":1,"basedOn":"curated-hash","role":"curated-role","capabilities":["curated-tool"],"scaffold":"reviewer","future":{"nested":42}}""";
        File.WriteAllText(metaPath, prior);
        var result = ResponseParser.Parse(await tools.MemoryAppend($"/{kind}/metadata", "suffix"));
        result.Status.Should().Be("success");
        using var meta = JsonDocument.Parse(File.ReadAllText(metaPath));
        using var original = JsonDocument.Parse(prior);
        foreach (var property in original.RootElement.EnumerateObject().Where(p => p.Name != "updatedAt"))
            JsonElement.DeepEquals(meta.RootElement.GetProperty(property.Name), property.Value).Should().BeTrue(property.Name);
        meta.RootElement.GetProperty("updatedAt").GetString().Should().NotBe("old");
        Directory.GetFiles(Path.Combine(Path.GetDirectoryName(path)!, "versions"), "*.meta.json")
            .Select(File.ReadAllText).Should().Contain(prior);
    }

    [Theory]
    [InlineData("skill")]
    [InlineData("agent")]
    public async Task AppendRefusesForwardMetadataBeforePublishingOrArchiving(string kind)
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember($"/{kind}/future", ["body"]);
        string path = Path.Combine(PathRouter.ToFileEntityDir(kind, scope.WorkspaceDir), "future.md");
        string before = File.ReadAllText(path);
        string metaPath = Path.ChangeExtension(path, ".meta.json");
        File.WriteAllText(metaPath, """{"v":999}""");
        Func<Task> append = () => tools.MemoryAppend($"/{kind}/future", "suffix");
        await append.Should().ThrowAsync<ScopeReadOnlyException>();
        File.ReadAllText(path).Should().Be(before);
        File.ReadAllText(metaPath).Should().Be("""{"v":999}""");
        Directory.Exists(Path.Combine(Path.GetDirectoryName(path)!, "versions")).Should().BeFalse();
    }

    [Fact]
    public async Task AppendPromotesCompleteLegacyAgentBody()
    {
        using var scope = new TestHelpers.StoreScope();
        string original = new string('x', 9000) + "LEGACY-TAIL";
        await TestHelpers.Store.WriteArtifactAsync("legacy", "local:agent", Nmp2ChunkedEncoder.Encode(original));
        var result = ResponseParser.Parse(await new ScriniaMcpTools().MemoryAppend("/agent/legacy", "suffix"));
        result.Status.Should().Be("success");
        File.ReadAllText(Path.Combine(PathRouter.ToFileEntityDir("agent", scope.WorkspaceDir), "legacy.md"))
            .Should().Be(original + "\nsuffix");
        File.Exists(TestHelpers.Store.FindArtifactPath("legacy", "local:agent")).Should().BeFalse();
    }
    [Theory]
    [InlineData("skill", "\n\n")]
    [InlineData("agent", "\n")]
    public async Task ConcurrentPublicAppendsKeepFullSerialPredecessors(string kind, string separator)
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        string address = $"/{kind}/concurrent";
        await tools.MemoryRemember(address, [new string('x', 9000) + "ORIGINAL-TAIL"]);
        string path = Path.Combine(PathRouter.ToFileEntityDir(kind, scope.WorkspaceDir), "concurrent.md");
        string original = File.ReadAllText(path);
        var firstPublishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondContended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int publicationCount = 0;
        ILogger previousLogger = FileLock.DefaultLogger;
        Func<DurableWritePhase, string, Exception?>? previousInjector = DurableFile.FailureInjector;
        FileLock.DefaultLogger = new ContentionLogger(Path.Combine(Path.GetDirectoryName(path)!, ".lock"), secondContended);
        DurableFile.FailureInjector = (phase, destination) =>
        {
            if (phase == DurableWritePhase.BeforeMove && destination == path &&
                Interlocked.Increment(ref publicationCount) == 1)
            {
                firstPublishing.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(15)))
                    return new TimeoutException("The test did not release the first publication.");
            }
            return null;
        };
        Task<string>? first = null;
        Task<string>? second = null;
        try
        {
            first = Task.Run(() => tools.MemoryAppend(address, "FIRST"));
            await firstPublishing.Task.WaitAsync(TimeSpan.FromSeconds(15));
            second = Task.Run(() => tools.MemoryAppend(address, "SECOND"));
            // Contention proves the second append reached the entity lock before publication
            // resumes. An implementation that read the body first has already captured stale data.
            await secondContended.Task.WaitAsync(TimeSpan.FromSeconds(15));
            first.IsCompleted.Should().BeFalse();
            second.IsCompleted.Should().BeFalse();
            release.Set();
            ResponseParser.Parse(await first).Status.Should().Be("success");
            ResponseParser.Parse(await second).Status.Should().Be("success");
            string predecessor = original + separator + "FIRST";
            File.ReadAllText(path).Should().Be(predecessor + separator + "SECOND");
            Directory.GetFiles(Path.Combine(Path.GetDirectoryName(path)!, "versions"), "*.md")
                .Select(File.ReadAllText).Should().BeEquivalentTo([original, predecessor]);
        }
        finally
        {
            release.Set();
            try
            {
                if (first is not null) await first;
                if (second is not null) await second;
            }
            finally
            {
                DurableFile.FailureInjector = previousInjector;
                FileLock.DefaultLogger = previousLogger;
            }
        }
    }

    [Fact]
    public async Task TransformPreparationRetainsEntityLockAndCapturesTheCommittedPredecessor()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        await tools.MemoryRemember("/agent/preparation", ["ORIGINAL-TAIL"]);
        string body = Path.Combine(PathRouter.ToFileEntityDir("agent", scope.WorkspaceDir), "preparation.md");
        var target = new FileEntityTarget("agent", "preparation", body,
            Path.ChangeExtension(body, ".meta.json"), Path.Combine(Path.GetDirectoryName(body)!, "versions"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var contended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ILogger previousLogger = FileLock.DefaultLogger;
        FileLock.DefaultLogger = new ContentionLogger(Path.Combine(Path.GetDirectoryName(body)!, ".lock"), contended);
        int secondPrepared = 0;
        string? secondBody = null;
        Task<FileEntityMutationResult>? first = null;
        Task<FileEntityMutationResult>? second = null;
        try
        {
            first = Task.Run(() => new FileEntityTransactionCoordinator(scope.WorkspaceDir).TransformAsync(target,
                async (current, ct) =>
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(ct);
                    return AppendedMutation(target, current, "FIRST");
                }));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            second = Task.Run(() => new FileEntityTransactionCoordinator(scope.WorkspaceDir).TransformAsync(target,
                (current, _) =>
                {
                    Interlocked.Increment(ref secondPrepared);
                    secondBody = Encoding.UTF8.GetString(current.Body.Bytes);
                    return ValueTask.FromResult(AppendedMutation(target, current, "SECOND"));
                }));
            await contended.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Volatile.Read(ref secondPrepared).Should().Be(0);
            release.TrySetResult();
            (await first).Outcome.Should().Be(WholeMemoryOutcome.Replaced);
            (await second).Outcome.Should().Be(WholeMemoryOutcome.Replaced);
            secondBody.Should().Be("ORIGINAL-TAIL\nFIRST");
            File.ReadAllText(body).Should().Be("ORIGINAL-TAIL\nFIRST\nSECOND");
        }
        finally
        {
            release.TrySetResult();
            try
            {
                if (first is not null) await first;
                if (second is not null) await second;
            }
            finally { FileLock.DefaultLogger = previousLogger; }
        }
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("name")]
    [InlineData("body")]
    [InlineData("meta")]
    [InlineData("versions")]
    public async Task TransformRefusesRetargetingBeforePublication(string member)
    {
        using var scope = new TestHelpers.StoreScope();
        await new ScriniaMcpTools().MemoryRemember("/agent/target", ["original"]);
        string body = Path.Combine(PathRouter.ToFileEntityDir("agent", scope.WorkspaceDir), "target.md");
        var target = new FileEntityTarget("agent", "target", body,
            Path.ChangeExtension(body, ".meta.json"), Path.Combine(Path.GetDirectoryName(body)!, "versions"));
        var changed = member switch
        {
            "kind" => target with { EntityKind = "skill" },
            "name" => target with { Name = "other" },
            "body" => target with { BodyPath = body + ".other" },
            "meta" => target with { MetaPath = target.MetaPath + ".other" },
            "versions" => target with { VersionsDirectory = target.VersionsDirectory + ".other" },
            _ => throw new InvalidOperationException()
        };
        Func<Task> transform = () => new FileEntityTransactionCoordinator(scope.WorkspaceDir)
            .TransformAsync(target, (current, _) => ValueTask.FromResult(AppendedMutation(changed, current, "suffix")));
        await transform.Should().ThrowAsync<ArgumentException>();
        File.ReadAllText(body).Should().Be("original");
        Directory.Exists(target.VersionsDirectory).Should().BeFalse();
    }

    [Theory]
    [InlineData("warning")]
    [InlineData("success_with_warning")]
    [InlineData("error")]
    public void PersistedIdentityProjectionRetainsStatusWarningsAndRecovery(string status)
    {
        var original = new McpResponse
        {
            Status = status, Info = ["warning"], OperationId = "operation",
            Scope = "skill/qa", JournalPath = "evidence", Reason = "recovery", ErrorCode = "CODE"
        };
        McpResponse projected = original.WithPersistedIdentity("/skill/qa");
        projected.Should().Be(original with { Path = "/skill/qa", PersistedPath = "/skill/qa" });
    }

    [Theory]
    [InlineData("skill")]
    [InlineData("agent")]
    public async Task AppendRetainsCommittedWarningAndRecoveryIdentity(string kind)
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        string address = $"/{kind}/warning";
        await tools.MemoryRemember(address, ["original"]);
        B0FaultSeam.FailOnceAt(B0FaultSeam.FaultPhase.FileEntityEvidenceCleanup, FileEntityFaultPoint.EvidenceCleanup.ToString());
        try
        {
            McpResponse response = kind == "skill"
                ? await ScriniaMcpTools.AppendSkillAsync("warning", ["suffix"], CancellationToken.None)
                : await tools.AppendAgentAsync("warning", ["suffix"], CancellationToken.None);
            response.Status.Should().Be("success_with_warning");
            response.Path.Should().Be(address);
            response.PersistedPath.Should().Be(address);
            response.OperationId.Should().NotBeNullOrEmpty();
            response.Info.Should().ContainSingle().Which.Should().Contain("cleanup");
            response.JournalPath.Should().NotBeNullOrEmpty();
        }
        finally { B0FaultSeam.ClearFaults(); }
    }

    [Theory]
    [InlineData("skill")]
    [InlineData("agent")]
    public async Task FirstReservedPublicationRetainsCleanupWarningAndRecoveryIdentity(string kind)
    {
        using var scope = new TestHelpers.StoreScope();
        B0FaultSeam.FailOnceAt(B0FaultSeam.FaultPhase.FileEntityEvidenceCleanup, FileEntityFaultPoint.EvidenceCleanup.ToString());
        try
        {
            McpResponse response = kind == "skill"
                ? await ScriniaMcpTools.AppendSkillAsync("qa", ["suffix"], CancellationToken.None)
                : await new ScriniaMcpTools().WriteAgentAsync("qa", ["body"], CancellationToken.None);
            response.Status.Should().Be("success_with_warning");
            response.Path.Should().Be($"/{kind}/qa");
            response.PersistedPath.Should().Be($"/{kind}/qa");
            response.OperationId.Should().NotBeNullOrEmpty();
            response.Info.Should().ContainSingle().Which.Should().Contain("cleanup");
            response.JournalPath.Should().NotBeNullOrEmpty();
        }
        finally { B0FaultSeam.ClearFaults(); }
    }

    [Theory]
    [InlineData("skill", "\n\n")]
    [InlineData("agent", "\n")]
    public async Task AppendArchivesExactHandAuthoredUtf8Bytes(string kind, string separator)
    {
        using var scope = new TestHelpers.StoreScope();
        string path = Path.Combine(PathRouter.ToFileEntityDir(kind, scope.WorkspaceDir), "unicode.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string original = "\u00e9" + new string('x', 9000) + "TAIL";
        byte[] bytes = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(original)];
        File.WriteAllBytes(path, bytes);
        var result = ResponseParser.Parse(await new ScriniaMcpTools().MemoryAppend($"/{kind}/unicode", "\u8ffd\u52a0"));
        result.Status.Should().Be("success");
        File.ReadAllText(path).Should().Be(original + separator + "\u8ffd\u52a0");
        string archive = Directory.GetFiles(Path.Combine(Path.GetDirectoryName(path)!, "versions"), "*.md")
            .Should().ContainSingle().Subject;
        File.ReadAllBytes(archive).Should().Equal(bytes);
    }

    private static FileEntityMutation AppendedMutation(
        FileEntityTarget target, FileEntityCurrentState current, string suffix) =>
        new(target.EntityKind, target.Name, target.BodyPath, target.MetaPath, target.VersionsDirectory,
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(current.Body.Bytes) + "\n" + suffix),
            current.Meta.Bytes, false, false);

    private sealed class ContentionLogger(string lockPath, TaskCompletionSource contended) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Name == "LockContended" &&
                state is IEnumerable<KeyValuePair<string, object?>> values &&
                values.Any(value => value.Key == "LockPath" && value.Value?.ToString() == lockPath))
                contended.TrySetResult();
        }
    }
}
