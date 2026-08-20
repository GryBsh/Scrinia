using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Core.Embeddings;

namespace Scrinia.Tests.Embeddings;

/// <summary>
/// <see cref="Model2VecProvider.Load"/> must not read the embedding matrix or build the tokenizer
/// until something actually embeds.
///
/// <para><b>Why.</b> Every <c>scri</c> verb is a fresh process, and several of them construct the
/// provider through <c>WorkspaceSetup.LoadPluginsAsync</c> without ever embedding anything.
/// <c>restore</c> is the worst case and the one that matters: it is wired as the SessionStart hook,
/// and it only ever calls <c>LoadIndex</c> and walks directories — it never searches and never
/// writes, so it has no use for either the reranker or the event sinks. Measured against a
/// local-disk copy of this repository's workspace, median of 15 runs, by renaming the model
/// directory away and back: <b>restore paid 67ms</b> for a model it does not use. <c>search</c>
/// paid 70ms and earns it, because it embeds the query.</para>
///
/// <para><b>Why laziness rather than dropping the call from restore.</b> Removing
/// <c>LoadPluginsAsync</c> from the verbs that do not need it fixes those verbs and leaves the trap
/// armed for the next one — including for <c>restore</c> itself, if it ever grows a write. Making
/// the cost lazy puts the rule where the cost is: you pay when you embed.</para>
///
/// <para><b>What stays eager</b>, so that a broken model still fails at startup where
/// <c>LoadBuiltInEmbeddings</c> catches it and degrades to BM25, rather than throwing from inside a
/// search: both files must exist, the vocab's first lines must be readable (that is how the
/// tokenizer kind is detected), and the SafeTensors header must parse and name a tensor. Only the
/// bulk reads move.</para>
/// </summary>
public class Model2VecLazyLoadTests
{
    private static readonly string MiniLmDir = Path.Combine(AppContext.BaseDirectory, "models", "m2v-MiniLM-L6-v2");

    private static bool IsAvailable => File.Exists(Path.Combine(MiniLmDir, "model.safetensors"))
                                    && File.Exists(Path.Combine(MiniLmDir, "vocab.txt"));

    /// <summary>
    /// Allocation, not wall-clock: the matrix is a <c>float[]</c> of vocabSize × dims, which for
    /// this model is over 20MB in one array. A timing assertion would be flaky on a loaded machine
    /// and would not say WHAT was avoided; an allocation budget names the thing exactly and cannot
    /// pass by accident.
    /// </summary>
    [SkippableFact]
    public void Load_DoesNotAllocateTheEmbeddingMatrix()
    {
        Skip.If(!IsAvailable, "MiniLM distillation not available");

        // Touch the type first so its static init and the JIT for Load are not counted.
        _ = Model2VecProvider.Load(MiniLmDir, NullLogger.Instance);

        long before = GC.GetAllocatedBytesForCurrentThread();
        var provider = Model2VecProvider.Load(MiniLmDir, NullLogger.Instance);
        long loadCost = GC.GetAllocatedBytesForCurrentThread() - before;

        long matrixBytes = (long)provider.Dimensions * 29524 * sizeof(float);
        matrixBytes.Should().BeGreaterThan(20_000_000,
            because: "this test is only meaningful if the matrix is big enough to be unmistakable");

        loadCost.Should().BeLessThan(1_000_000,
            because: $"Load must read the SafeTensors header and the vocab's first lines, nothing " +
                     $"more — it allocated {loadCost:N0} bytes against a matrix of {matrixBytes:N0}");
    }

    /// <summary>
    /// The other half, and the one that would catch a "lazy" field that is never populated: the
    /// first embed must pay for the matrix, and the second must not pay again.
    /// </summary>
    [SkippableFact]
    public async Task FirstEmbed_MaterializesTheMatrix_AndTheSecondDoesNot()
    {
        Skip.If(!IsAvailable, "MiniLM distillation not available");

        var provider = Model2VecProvider.Load(MiniLmDir, NullLogger.Instance);

        long before = GC.GetAllocatedBytesForCurrentThread();
        var first = await provider.EmbedAsync("the merge driver resolves sidecar conflicts");
        long firstCost = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        var second = await provider.EmbedAsync("a second, different sentence entirely");
        long secondCost = GC.GetAllocatedBytesForCurrentThread() - before;

        first.Should().NotBeNull();
        second.Should().NotBeNull();

        firstCost.Should().BeGreaterThan(20_000_000,
            because: "the first embed is where the matrix is read");
        secondCost.Should().BeLessThan(1_000_000,
            because: $"the matrix is cached after the first embed — the second allocated " +
                     $"{secondCost:N0} bytes, which means it re-read something");
    }

    /// <summary>
    /// Deferring the read must not change the answer. Same input, same vector, whether the
    /// provider was warmed by an earlier call or not.
    /// </summary>
    [SkippableFact]
    public async Task DeferredLoad_ProducesTheSameVectorAsAWarmedProvider()
    {
        Skip.If(!IsAvailable, "MiniLM distillation not available");

        const string text = "persistent memory for coding agents";

        var cold = Model2VecProvider.Load(MiniLmDir, NullLogger.Instance);
        var fromCold = await cold.EmbedAsync(text);

        var warm = Model2VecProvider.Load(MiniLmDir, NullLogger.Instance);
        await warm.EmbedAsync("something else first");
        var fromWarm = await warm.EmbedAsync(text);

        fromCold.Should().NotBeNull();
        fromCold.Should().Equal(fromWarm);
    }

    /// <summary>
    /// <c>Dimensions</c> and <c>Signature</c> are read at startup by
    /// <c>WorkspaceSetup.LoadBuiltInEmbeddings</c> — the dimension goes into the vector store's
    /// signature, which decides whether the on-disk vectors are stale. Both must be right without
    /// materializing anything, or the laziness is defeated on the very first use.
    /// </summary>
    [SkippableFact]
    public void DimensionsAndSignature_AreAvailableWithoutMaterializing()
    {
        Skip.If(!IsAvailable, "MiniLM distillation not available");

        _ = Model2VecProvider.Load(MiniLmDir, NullLogger.Instance);

        long before = GC.GetAllocatedBytesForCurrentThread();
        var provider = Model2VecProvider.Load(MiniLmDir, NullLogger.Instance);
        int dims = provider.Dimensions;
        string signature = provider.Signature;
        bool available = provider.IsAvailable;
        long cost = GC.GetAllocatedBytesForCurrentThread() - before;

        dims.Should().Be(384);
        signature.Should().Be("model2vec:m2v-MiniLM-L6-v2");
        available.Should().BeTrue();
        cost.Should().BeLessThan(1_000_000,
            because: "reading the three properties the startup path needs must not load the model");
    }

    /// <summary>
    /// A missing file still fails at construction, not at the first search. This is the property
    /// the eager half exists to preserve: <c>LoadBuiltInEmbeddings</c> wraps <c>Load</c> in a
    /// try/catch and degrades to BM25-only on failure, and moving that failure into
    /// <c>EmbedAsync</c> would surface it as a broken search instead of a logged downgrade.
    /// </summary>
    [Fact]
    public void MissingModelFile_ThrowsFromLoad_NotFromEmbed()
    {
        string dir = Path.Combine(Path.GetTempPath(), "m2v-missing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var act = () => Model2VecProvider.Load(dir, NullLogger.Instance);
            act.Should().Throw<FileNotFoundException>();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
