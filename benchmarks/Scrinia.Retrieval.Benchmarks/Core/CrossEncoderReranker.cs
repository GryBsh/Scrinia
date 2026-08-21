using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using Scrinia.Core.Search;

namespace Scrinia.Retrieval.Benchmarks.Core;

/// <summary>
/// Cross-encoder reranker (bench-only). Re-scores the top-N first-stage candidates
/// by running a BERT-class sequence-classification model over each
/// <c>(query, document)</c> pair and sorting by the model's relevance logit.
///
/// <para><b>Why bench-only.</b> This exists to answer one question:
/// does a real cross-encoder beat the cheap sentence-MaxSim reranker this harness defaults to
/// (sentence-MaxSim is bench-only — the shipped product re-ranks by whole-vector cosine over
/// the BM25 + attribute candidate set, in <c>Scrinia.Core.Embeddings.HybridReranker</c>)
/// — particularly once agent-supplied anchors are in play (findings #4/#5 in
/// <c>docs/findings/benchmark-methodology-findings.md</c> show anchors and rerankers
/// are substitutes). If the cross-encoder doesn't beat sentence-MaxSim by a clear
/// margin with anchors on, it doesn't justify importing ONNX Runtime + a BERT
/// tokenizer into <c>Scrinia.Core</c>. We measure before we adopt.</para>
///
/// <para><b>Bounded depth.</b> First-stage retrieval (chunked mode) can hand us
/// hundreds of candidates. A cross-encoder is a full BERT forward pass per pair, so
/// reranking all of them is prohibitive (MemBench: ~12k queries × hundreds of pairs).
/// We rerank only the top <see cref="_depth"/> first-stage candidates and leave the
/// tail in first-stage order beneath them. Depth must exceed the largest R@K we
/// score (50) by a comfortable margin so a relevant doc deep in the first-stage list
/// can still be promoted into the top 50.</para>
///
/// <para><b>Scoring.</b> The model's logit replaces the candidate score for reranked
/// items (higher = more relevant). Tail items (beyond depth, or with no recoverable
/// text) get synthetic descending scores strictly below the minimum reranked logit,
/// so the final order is "reranked window by logit, then untouched tail" and any
/// downstream re-sort (MMR) preserves it.</para>
/// </summary>
public sealed class CrossEncoderReranker : ISearchReranker, IDisposable
{
    private readonly InferenceSession _session;
    private readonly BertTokenizer _tokenizer;
    private readonly int _clsId;
    private readonly int _sepId;
    private readonly int _padId;
    private readonly int _depth;
    private readonly int _maxSeqLen;
    private readonly int _maxQueryTokens;
    private readonly string _inputIdsName;
    private readonly string _attentionMaskName;
    private readonly string? _tokenTypeIdsName;

    /// <summary>Default rerank depth — comfortably above R@50 so docs deep in the
    /// first-stage list can still reach the top 50 after reranking.</summary>
    public const int DefaultDepth = 100;

    /// <summary>Default max sequence length. 256 covers the vast majority of
    /// query+turn pairs in the memory benchmarks and is ~4× cheaper than 512 (attention
    /// is O(n²)). Override with a larger value only if your docs are long.</summary>
    public const int DefaultMaxSeqLen = 256;

    private const int DefaultMaxQueryTokens = 64;

    private CrossEncoderReranker(
        InferenceSession session,
        BertTokenizer tokenizer,
        int clsId, int sepId, int padId,
        int depth, int maxSeqLen,
        string inputIdsName, string attentionMaskName, string? tokenTypeIdsName)
    {
        _session = session;
        _tokenizer = tokenizer;
        _clsId = clsId;
        _sepId = sepId;
        _padId = padId;
        _depth = depth;
        _maxSeqLen = maxSeqLen;
        _maxQueryTokens = Math.Min(DefaultMaxQueryTokens, maxSeqLen / 2);
        _inputIdsName = inputIdsName;
        _attentionMaskName = attentionMaskName;
        _tokenTypeIdsName = tokenTypeIdsName;
    }

    /// <summary>
    /// Loads a cross-encoder from an ONNX model file and a BERT <c>vocab.txt</c>.
    /// Reads CLS/SEP/PAD ids straight from the vocab (no dependency on tokenizer
    /// property names) and discovers the model's input tensor names from its metadata
    /// so exports that omit <c>token_type_ids</c> still work.
    /// </summary>
    public static CrossEncoderReranker Load(
        string modelPath, string vocabPath, int depth = DefaultDepth,
        int maxSeqLen = DefaultMaxSeqLen, int intraOpThreads = 4)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(vocabPath);
        if (!File.Exists(modelPath)) throw new FileNotFoundException("ONNX model not found", modelPath);
        if (!File.Exists(vocabPath)) throw new FileNotFoundException("vocab.txt not found", vocabPath);

        var (clsId, sepId, padId) = ReadSpecialTokenIds(vocabPath);

        var so = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        if (intraOpThreads > 0) so.IntraOpNumThreads = intraOpThreads;

        var session = new InferenceSession(modelPath, so);

        // Discover input names — ms-marco MiniLM ONNX uses input_ids / attention_mask /
        // token_type_ids, but match defensively against the actual graph so a slightly
        // different export doesn't silently feed the wrong tensors.
        var inputNames = session.InputMetadata.Keys.ToList();
        string inputIds = MatchInput(inputNames, "input_ids") ?? inputNames.FirstOrDefault()
            ?? throw new InvalidOperationException("ONNX model exposes no inputs");
        string attentionMask = MatchInput(inputNames, "attention_mask")
            ?? throw new InvalidOperationException("ONNX model has no attention_mask input");
        string? tokenTypeIds = MatchInput(inputNames, "token_type_ids");

        using var vocabStream = File.OpenRead(vocabPath);
        var tokenizer = BertTokenizer.Create(vocabStream);

        return new CrossEncoderReranker(
            session, tokenizer, clsId, sepId, padId, depth, maxSeqLen,
            inputIds, attentionMask, tokenTypeIds);
    }

    public Task<IReadOnlyList<SearchResult>> RerankAsync(
        string query,
        IReadOnlyList<SearchResult> candidates,
        CancellationToken ct)
    {
        if (candidates.Count == 0) return Task.FromResult(candidates);

        int n = Math.Min(_depth, candidates.Count);

        // Tokenize the query once; reused for every pair.
        IReadOnlyList<int> qIds = Truncate(EncodeWithoutSpecials(query), _maxQueryTokens);

        // Build the window: tokenized pairs + an index back to the candidate. Items with
        // no recoverable text drop to the tail (scored below the window).
        var windowRows = new List<(int CandidateIdx, int[] Ids, int[] TypeIds)>(n);
        var noTextIdx = new List<int>();
        for (int i = 0; i < n; i++)
        {
            string? text = ExtractText(candidates[i]);
            if (text is null)
            {
                noTextIdx.Add(i);
                continue;
            }
            int budget = _maxSeqLen - qIds.Count - 3; // [CLS] q [SEP] d [SEP]
            IReadOnlyList<int> dIds = Truncate(EncodeWithoutSpecials(text), Math.Max(1, budget));
            var (ids, typeIds) = AssemblePair(qIds, dIds);
            windowRows.Add((i, ids, typeIds));
        }

        float[] logits = windowRows.Count > 0 ? RunBatch(windowRows, ct) : [];

        // Reranked window, sorted by logit desc.
        var rescored = new List<(SearchResult Result, double Score)>(candidates.Count);
        double minLogit = double.MaxValue;
        for (int r = 0; r < windowRows.Count; r++)
        {
            double logit = logits[r];
            if (logit < minLogit) minLogit = logit;
            rescored.Add((Rescored(candidates[windowRows[r].CandidateIdx], logit), logit));
        }
        rescored.Sort((a, b) => b.Score.CompareTo(a.Score));

        // Tail: window candidates with no text, then everything past depth. Scored on a
        // strictly-descending sequence below the window so global order is well-defined.
        double tail = (minLogit == double.MaxValue ? 0.0 : minLogit) - 1.0;
        var ordered = new List<SearchResult>(candidates.Count);
        ordered.AddRange(rescored.Select(t => t.Result));
        foreach (int idx in noTextIdx)
            ordered.Add(Rescored(candidates[idx], tail -= 1.0));
        for (int i = n; i < candidates.Count; i++)
            ordered.Add(Rescored(candidates[i], tail -= 1.0));

        return Task.FromResult<IReadOnlyList<SearchResult>>(ordered);
    }

    private float[] RunBatch(List<(int CandidateIdx, int[] Ids, int[] TypeIds)> rows, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        int batch = rows.Count;
        int seqLen = 0;
        foreach (var row in rows) if (row.Ids.Length > seqLen) seqLen = row.Ids.Length;

        var inputIds = new DenseTensor<long>(new[] { batch, seqLen });
        var attentionMask = new DenseTensor<long>(new[] { batch, seqLen });
        var tokenTypeIds = _tokenTypeIdsName is not null ? new DenseTensor<long>(new[] { batch, seqLen }) : null;

        for (int b = 0; b < batch; b++)
        {
            int[] ids = rows[b].Ids;
            int[] types = rows[b].TypeIds;
            for (int t = 0; t < seqLen; t++)
            {
                bool real = t < ids.Length;
                inputIds[b, t] = real ? ids[t] : _padId;
                attentionMask[b, t] = real ? 1 : 0;
                if (tokenTypeIds is not null) tokenTypeIds[b, t] = real ? types[t] : 0;
            }
        }

        var inputs = new List<NamedOnnxValue>(3)
        {
            NamedOnnxValue.CreateFromTensor(_inputIdsName, inputIds),
            NamedOnnxValue.CreateFromTensor(_attentionMaskName, attentionMask),
        };
        if (tokenTypeIds is not null)
            inputs.Add(NamedOnnxValue.CreateFromTensor(_tokenTypeIdsName!, tokenTypeIds));

        using var results = _session.Run(inputs);
        var output = results[0].AsTensor<float>();
        // Output is [batch, 1] (regression-style relevance, ms-marco MiniLM) or
        // [batch, 2] (2-class). Take col 0 for width 1, col 1 (positive class) for width 2.
        int width = output.Dimensions.Length > 1 ? output.Dimensions[^1] : 1;
        var scores = new float[batch];
        for (int b = 0; b < batch; b++)
            scores[b] = width >= 2 ? output[b, 1] : output[b, 0];
        return scores;
    }

    private (int[] Ids, int[] TypeIds) AssemblePair(IReadOnlyList<int> qIds, IReadOnlyList<int> dIds)
    {
        int len = 1 + qIds.Count + 1 + dIds.Count + 1; // CLS q SEP d SEP
        var ids = new int[len];
        var types = new int[len];
        int p = 0;
        ids[p] = _clsId; types[p] = 0; p++;
        foreach (int id in qIds) { ids[p] = id; types[p] = 0; p++; }
        ids[p] = _sepId; types[p] = 0; p++;
        foreach (int id in dIds) { ids[p] = id; types[p] = 1; p++; }
        ids[p] = _sepId; types[p] = 1; p++;
        return (ids, types);
    }

    private IReadOnlyList<int> EncodeWithoutSpecials(string text) =>
        _tokenizer.EncodeToIds(text, addSpecialTokens: false);

    private static IReadOnlyList<int> Truncate(IReadOnlyList<int> ids, int max) =>
        ids.Count <= max ? ids : ids.Take(max).ToList();

    private static string? ExtractText(SearchResult r) => r switch
    {
        EntryResult er => er.Item.Entry.ContentPreview,
        ChunkEntryResult cr => cr.ParentItem.Entry.ContentPreview,
        _ => null,
    };

    private static SearchResult Rescored(SearchResult orig, double newScore) => orig switch
    {
        EntryResult er => er with { Score = newScore },
        ChunkEntryResult cr => cr with { Score = newScore },
        _ => orig,
    };

    private static string? MatchInput(List<string> names, string target)
    {
        foreach (string n in names)
            if (string.Equals(n, target, StringComparison.OrdinalIgnoreCase)) return n;
        foreach (string n in names)
            if (n.Contains(target, StringComparison.OrdinalIgnoreCase)) return n;
        return null;
    }

    /// <summary>
    /// Reads [CLS]/[SEP]/[PAD] ids from a BERT vocab.txt (line number = token id).
    /// Done independently of the tokenizer so we don't depend on which property names
    /// the tokenizer library exposes for special tokens.
    /// </summary>
    private static (int Cls, int Sep, int Pad) ReadSpecialTokenIds(string vocabPath)
    {
        int cls = -1, sep = -1, pad = -1, line = 0;
        foreach (string raw in File.ReadLines(vocabPath))
        {
            string token = raw.TrimEnd('\n', '\r');
            switch (token)
            {
                case "[CLS]": cls = line; break;
                case "[SEP]": sep = line; break;
                case "[PAD]": pad = line; break;
            }
            line++;
        }
        if (cls < 0 || sep < 0 || pad < 0)
            throw new InvalidOperationException(
                $"vocab.txt missing one of [CLS]/[SEP]/[PAD] (cls={cls}, sep={sep}, pad={pad})");
        return (cls, sep, pad);
    }

    public void Dispose() => _session.Dispose();
}
