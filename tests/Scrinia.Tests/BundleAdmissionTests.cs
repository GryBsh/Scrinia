using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Models;
using Scrinia.TestDoubles;
using Scrinia.Mcp;

namespace Scrinia.Tests;

public sealed class BundleAdmissionTests
{
    [Fact]
    public async Task EphemeralOnlyListingRemainsAvailableWithBlockedPersistentEvidence()
    {
        using var scope = new TestHelpers.StoreScope();
        using var store = new FileMemoryStore(scope.WorkspaceDir);
        await store.StoreAsync(new MemoryWrite(MemoryAddress.Parse("~scratch"), ["ephemeral"u8.ToArray()],
            new Dictionary<string, IReadOnlyList<string>>(), []));
        File.WriteAllText(Path.Combine(scope.WorkspaceDir, ".scrinia", ".bundle-restore-journal.json"), "invalid");
        store.ListScoped(["ephemeral"]).Should().ContainSingle();
        store.AnyScoped(["ephemeral"], _ => true).Should().BeTrue();
    }

    [Theory]
    [InlineData("store")]
    [InlineData("append-existing")]
    [InlineData("append-create")]
    [InlineData("delete")]
    [InlineData("read")]
    [InlineData("skill")]
    [InlineData("agent")]
    public async Task TaskRunChildCannotInheritRestoreOwnership(string operation)
    {
        using var scope = new TestHelpers.StoreScope();
        using var importer = new FileMemoryStore(scope.WorkspaceDir);
        using var writer = new FileMemoryStore(scope.WorkspaceDir);
        MemoryAddress address = MemoryAddress.Parse("/concurrent");
        await writer.StoreAsync(new MemoryWrite(address, ["before"u8.ToArray()], new Dictionary<string, IReadOnlyList<string>>(), []));
        bool excluded = false;
        FileMemoryStore.RestoreJournalFailureInjector.Value = (transition, _) =>
        {
            if (transition != RestoreJournalTransition.AfterPreparedWrite) return null;
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            Task child = Task.Run(async () =>
            {
                var write = new MemoryWrite(operation == "append-create" ? MemoryAddress.Parse("/created") : address,
                    ["after"u8.ToArray()], new Dictionary<string, IReadOnlyList<string>>(), []);
                switch (operation)
                {
                    case "store": await writer.StoreAsync(write, deadline.Token); break;
                    case "append-existing": case "append-create": await writer.AppendAsync(write, deadline.Token); break;
                    case "delete": await writer.DeleteAsync(address, deadline.Token); break;
                    case "read": await writer.ReadArtifactAsync(address, deadline.Token); break;
                    case "skill": case "agent":
                        await new ScriniaMcpTools().MemoryRemember("/" + operation + "/concurrent", ["new"], cancellationToken: deadline.Token);
                        break;
                }
            });
            try { child.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { excluded = true; }
            return new IOException("one-shot import failure");
        };
        try
        {
            Func<Task> restore = () => importer.RestoreBundleAsync(new([], [new("skill", "owned.md", "bundle"u8.ToArray(), null)], true));
            await restore.Should().ThrowAsync<BundleImportException>().Where(e => e.Failure == BundleImportFailure.RolledBack);
        }
        finally { FileMemoryStore.RestoreJournalFailureInjector.Value = null; }
        excluded.Should().BeTrue("a child flow must acquire its own workspace admission");
        await writer.StoreAsync(new MemoryWrite(address, ["survives"u8.ToArray()], new Dictionary<string, IReadOnlyList<string>>(), []));
        (await writer.ReadAsync(address))!.ContentPreview.Should().Contain("survives");
    }

    [Fact]
    public async Task WarmReaderSeesOtherStoresUpdateDeleteAndNewTopic()
    {
        using var scope = new TestHelpers.StoreScope();
        using var reader = new FileMemoryStore(scope.WorkspaceDir);
        using var writer = new FileMemoryStore(scope.WorkspaceDir);
        MemoryAddress address = MemoryAddress.Parse("/existing/note");
        await writer.StoreAsync(new MemoryWrite(address, ["before"u8.ToArray()], new Dictionary<string, IReadOnlyList<string>>(), []));
        reader.ListScoped().Should().ContainSingle();
        reader.DiscoverTopics().Should().Contain("local:existing");
        await writer.StoreAsync(new MemoryWrite(address, ["change"u8.ToArray()], new Dictionary<string, IReadOnlyList<string>>(), []));
        (await reader.ReadAsync(address))!.ContentPreview.Should().Contain("change");
        await writer.DeleteAsync(address);
        reader.ListScoped().Should().BeEmpty();
        reader.DiscoverTopics().Should().NotContain("local:existing");
        await writer.StoreAsync(new MemoryWrite(MemoryAddress.Parse("/newtopic/note"), ["new"u8.ToArray()], new Dictionary<string, IReadOnlyList<string>>(), []));
        reader.DiscoverTopics().Should().Contain("local:newtopic");
        reader.ListScoped().Should().ContainSingle().Which.Scope.Should().Be("local:newtopic");
    }

    [Theory]
    [InlineData("store")]
    [InlineData("read")]
    [InlineData("append")]
    public async Task SeparateProcessPublicOperationWaitsForRestore(string operation)
    {
        using var scope = new TestHelpers.StoreScope();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var held = new WorkspaceAdmission(scope.WorkspaceDir, null)
            .EnterRestore(TimeSpan.FromSeconds(1), default);
        await using var child = await TestProcessRunner.StartAsync(
            ["admission", operation, scope.WorkspaceDir], deadline.Token);
        await child.ReadEventAsync("ready", deadline.Token);
        await child.SendAsync("continue", deadline.Token);
        await child.ReadEventAsync("entered", deadline.Token);
        await child.ReadEventAsync("blocked", deadline.Token);
        (await child.WaitForExitAsync(deadline.Token)).Should().Be(0);
        File.Exists(Path.Combine(scope.WorkspaceDir, ".scrinia", "memories", "concurrent.nmp2")).Should().BeFalse();
    }

    [Theory]
    [InlineData("read")]
    [InlineData("artifact")]
    [InlineData("list")]
    [InlineData("index")]
    [InlineData("topics")]
    [InlineData("scopes")]
    [InlineData("facts")]
    [InlineData("store")]
    [InlineData("append")]
    [InlineData("delete")]
    [InlineData("metadata")]
    public async Task AlreadyOpenStoreRefusesUnknownBundleEvidence(string operation)
    {
        using var scope = new TestHelpers.StoreScope();
        using var store = new FileMemoryStore(scope.WorkspaceDir);
        MemoryAddress address = MemoryAddress.Parse("/note");
        await store.StoreAsync(new MemoryWrite(address, ["before"u8.ToArray()], new Dictionary<string, IReadOnlyList<string>>(), []));
        string journal = Path.Combine(scope.WorkspaceDir, ".scrinia", ".bundle-restore-journal.json");
        File.WriteAllText(journal, "unknown evidence");
        Func<Task> action = async () =>
        {
            switch (operation)
            {
                case "read": await store.ReadAsync(address); break;
                case "artifact": await store.ReadArtifactAsync(address); break;
                case "list": store.ListScoped(); break;
                case "index": store.LoadIndex(); break;
                case "topics": store.DiscoverTopics(); break;
                case "scopes": store.ResolveReadScopes(); break;
                case "facts": store.GetEntityFactStore(); break;
                case "store": await store.StoreAsync(new MemoryWrite(address, ["after"u8.ToArray()], new Dictionary<string, IReadOnlyList<string>>(), [])); break;
                case "append": await store.AppendAsync(new MemoryWrite(address, ["after"u8.ToArray()], new Dictionary<string, IReadOnlyList<string>>(), [])); break;
                case "delete": await store.DeleteAsync(address); break;
                case "metadata": store.TryUpdateEntry(address, entry => entry); break;
            }
        };
        await action.Should().ThrowAsync<BundleImportException>()
            .Where(e => e.Failure == BundleImportFailure.RecoveryRequired);
        File.ReadAllText(journal).Should().Be("unknown evidence");
    }

    [Fact]
    public async Task LegacyJournalCannotDeleteUnrelatedData()
    {
        using var scope = new TestHelpers.StoreScope();
        string root = Path.Combine(scope.WorkspaceDir, ".scrinia");
        Directory.CreateDirectory(root);
        string sentinel = Path.Combine(root, "unrelated.txt");
        string journal = Path.Combine(root, ".bundle-restore-journal.json");
        File.WriteAllText(sentinel, "keep");
        File.WriteAllText(journal,
            """{"format":"scrinia.bundle-restore","state":"prepared","files":[{"relativePath":"skills/broken.md","existed":true,"contentBase64":"!!!"}]}""");
        using var store = new FileMemoryStore(scope.WorkspaceDir);
        File.ReadAllText(sentinel).Should().Be("keep");
        File.Exists(journal).Should().BeTrue();
        Func<Task> restore = () => store.RestoreBundleAsync(new([], [], true));
        await restore.Should().ThrowAsync<BundleImportException>()
            .Where(e => e.Failure == BundleImportFailure.RecoveryRequired);
    }

    [Fact]
    public async Task SidecarOnlyDestinationReservesThePair()
    {
        using var scope = new TestHelpers.StoreScope();
        using var store = new FileMemoryStore(scope.WorkspaceDir);
        string dir = PathRouter.ToFileEntityDir("skill", scope.WorkspaceDir);
        Directory.CreateDirectory(dir);
        string meta = Path.Combine(dir, "planner.meta.json");
        File.WriteAllText(meta, "{}");
        var result = await store.RestoreBundleAsync(new(
            [], [new("skill", "planner.md", "new prompt"u8.ToArray(), null)], false));
        result.FileEntityCount.Should().Be(0);
        File.Exists(Path.Combine(dir, "planner.md")).Should().BeFalse();
        File.ReadAllText(meta).Should().Be("{}");
    }
}
