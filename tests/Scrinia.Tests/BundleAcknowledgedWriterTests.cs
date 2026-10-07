using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

public sealed class BundleAcknowledgedWriterTests
{
    [Fact]
    public async Task AcknowledgedUnrelatedWriterSurvivesFailedBundleRollback()
    {
        using var scope = new TestHelpers.StoreScope();
        using var importer = new FileMemoryStore(scope.WorkspaceDir);
        using var writer = new FileMemoryStore(scope.WorkspaceDir);
        MemoryAddress address = MemoryAddress.Parse("/unrelated/note");
        var write = new MemoryWrite(address, ["acknowledged after"u8.ToArray()],
            new Dictionary<string, IReadOnlyList<string>>(), []);
        bool acknowledged = false;
        bool excluded = false;
        FileMemoryStore.RestoreJournalFailureInjector.Value = (transition, _) =>
        {
            if (transition != RestoreJournalTransition.AfterPreparedWrite) return null;
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            Task child = Task.Run(async () =>
            {
                await writer.StoreAsync(write, deadline.Token);
                acknowledged = true;
            });
            try { child.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { excluded = true; }
            return new IOException("one-shot bundle failure");
        };
        try
        {
            Func<Task> restore = () => importer.RestoreBundleAsync(new([], [new("skill", "owned.md", "bundle"u8.ToArray(), null)], true));
            await restore.Should().ThrowAsync<BundleImportException>()
                .Where(exception => exception.Failure == BundleImportFailure.RolledBack);
        }
        finally { FileMemoryStore.RestoreJournalFailureInjector.Value = null; }

        // Check acknowledged content before the exclusion assertion: the pre-fix failure must
        // identify actual lost data, rather than merely prove that admission was bypassed.
        if (acknowledged)
        {
            using var cold = new FileMemoryStore(scope.WorkspaceDir);
            var persisted = await cold.ReadAsync(address);
            persisted.Should().NotBeNull("a successful unrelated write must survive bundle rollback");
            persisted!.ContentPreview.Should().Contain("acknowledged after");
        }
        excluded.Should().BeTrue("a child operation must acquire its own workspace admission");
        await writer.StoreAsync(write);
        using var completed = new FileMemoryStore(scope.WorkspaceDir);
        (await completed.ReadAsync(address))!.ContentPreview.Should().Contain("acknowledged after");
    }
}
