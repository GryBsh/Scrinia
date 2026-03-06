using Scrinia.Core.Embeddings.Models;

namespace Scrinia.Core.Embeddings;

/// <summary>
/// Hierarchical Navigable Small World (HNSW) graph index for approximate nearest neighbor search.
/// Used when vector count >= 1000 for sub-linear search time; below that threshold, flat scan is faster.
///
/// Parameters:
///   M = 16          max connections per node per layer
///   efConstruction = 200    beam width during index construction
///   MaxLayers = 6   maximum number of graph layers
///
/// Insert: O(M * efConstruction * log N)
/// Search: O(efSearch * log N)
/// Remove: lazy deletion (mark + skip during search)
/// </summary>
public sealed class HnswIndex
{
    private const int M = 16;
    private const int M0 = M * 2; // max connections at layer 0 (doubled per paper)
    private const int EfConstruction = 200;
    private const int MaxLayers = 6;

    private readonly List<HnswNode> _nodes = [];
    private readonly Dictionary<string, int> _nameToId = new(StringComparer.OrdinalIgnoreCase);
    private int _entryPoint = -1;
    private int _maxLevel;
    private readonly Random _rng = new(42);
    private readonly object _lock = new();

    private sealed class HnswNode
    {
        public string Key { get; }
        public float[] Vector { get; }
        public List<int>[] Neighbors { get; }
        public int TopLayer { get; }
        public bool Deleted { get; set; }

        public HnswNode(string key, float[] vector, int topLayer)
        {
            Key = key;
            Vector = vector;
            TopLayer = topLayer;
            Neighbors = new List<int>[topLayer + 1];
            for (int i = 0; i <= topLayer; i++)
                Neighbors[i] = [];
        }
    }

    public int Count
    {
        get { lock (_lock) return _nodes.Count(n => !n.Deleted); }
    }

    /// <summary>Inserts a vector into the HNSW graph.</summary>
    public void Insert(string key, float[] vector)
    {
        lock (_lock)
        {
            // Check for existing key -> update
            if (_nameToId.TryGetValue(key, out int existingId))
            {
                _nodes[existingId].Deleted = true;
            }

            int newLevel = RandomLevel();
            var node = new HnswNode(key, vector, newLevel);
            int nodeId = _nodes.Count;
            _nodes.Add(node);
            _nameToId[key] = nodeId;

            if (_entryPoint < 0)
            {
                _entryPoint = nodeId;
                _maxLevel = newLevel;
                return;
            }

            int ep = _entryPoint;

            // Navigate from top layer down to newLevel + 1 (greedy search for single closest)
            for (int level = _maxLevel; level > newLevel; level--)
            {
                ep = GreedyClosest(vector, ep, level);
            }

            // From min(newLevel, maxLevel) down to layer 0: search + connect
            for (int level = Math.Min(newLevel, _maxLevel); level >= 0; level--)
            {
                var candidates = SearchLayer(vector, ep, EfConstruction, level);

                // Select M (or M0 at layer 0) best neighbors
                int maxConn = level == 0 ? M0 : M;
                var selected = SelectNeighbors(vector, candidates, maxConn);

                // Connect bidirectionally
                node.Neighbors[level].AddRange(selected);
                foreach (int neighbor in selected)
                {
                    _nodes[neighbor].Neighbors[level].Add(nodeId);

                    // Trim neighbor's connections if over limit
                    var neighborConns = _nodes[neighbor].Neighbors[level];
                    if (neighborConns.Count > maxConn)
                    {
                        var trimmed = SelectNeighbors(_nodes[neighbor].Vector, neighborConns, maxConn);
                        neighborConns.Clear();
                        neighborConns.AddRange(trimmed);
                    }
                }

                if (candidates.Count > 0)
                    ep = candidates[0]; // use closest candidate as entry point for next layer
            }

            if (newLevel > _maxLevel)
            {
                _maxLevel = newLevel;
                _entryPoint = nodeId;
            }
        }
    }

    /// <summary>Approximate nearest neighbor search. Returns up to topK results.</summary>
    public IReadOnlyList<(string Key, float Similarity)> Search(float[] query, int topK, int efSearch = 0)
    {
        if (efSearch <= 0) efSearch = Math.Max(topK, 50);

        lock (_lock)
        {
            if (_entryPoint < 0) return [];

            int ep = _entryPoint;

            // Greedy descent from top layer to layer 1
            for (int level = _maxLevel; level > 0; level--)
                ep = GreedyClosest(query, ep, level);

            // Search at layer 0 with ef = efSearch
            var candidates = SearchLayer(query, ep, efSearch, 0);

            // Return top-K (excluding deleted)
            var results = new List<(string Key, float Similarity)>(topK);
            foreach (int id in candidates)
            {
                if (_nodes[id].Deleted) continue;
                float sim = VectorIndex.CosineSimilarity(query, _nodes[id].Vector);
                if (sim > 0)
                    results.Add((_nodes[id].Key, sim));
                if (results.Count >= topK) break;
            }

            results.Sort((a, b) => b.Similarity.CompareTo(a.Similarity));
            return results.Take(topK).ToList();
        }
    }

    /// <summary>Marks a key as deleted (lazy deletion).</summary>
    public void Remove(string key)
    {
        lock (_lock)
        {
            if (_nameToId.TryGetValue(key, out int id))
            {
                _nodes[id].Deleted = true;
                _nameToId.Remove(key);
            }
        }
    }

    /// <summary>Serializes the HNSW graph to a binary stream.</summary>
    public void Save(Stream stream)
    {
        lock (_lock)
        {
            using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            writer.Write("HNSW"u8);
            writer.Write(_entryPoint);
            writer.Write(_maxLevel);
            writer.Write(_nodes.Count);

            foreach (var node in _nodes)
            {
                byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(node.Key);
                writer.Write((ushort)nameBytes.Length);
                writer.Write(nameBytes);
                writer.Write(node.TopLayer);
                writer.Write(node.Deleted);
                writer.Write(node.Vector.Length);
                foreach (float f in node.Vector)
                    writer.Write(f);

                // Write neighbor lists per layer
                for (int level = 0; level <= node.TopLayer; level++)
                {
                    writer.Write(node.Neighbors[level].Count);
                    foreach (int n in node.Neighbors[level])
                        writer.Write(n);
                }
            }
        }
    }

    /// <summary>Deserializes an HNSW graph from a binary stream.</summary>
    public static HnswIndex Load(Stream stream)
    {
        var index = new HnswIndex();
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        byte[] magic = reader.ReadBytes(4);
        if (!magic.AsSpan().SequenceEqual("HNSW"u8))
            throw new FormatException("Invalid HNSW file format.");

        index._entryPoint = reader.ReadInt32();
        index._maxLevel = reader.ReadInt32();
        int nodeCount = reader.ReadInt32();

        for (int i = 0; i < nodeCount; i++)
        {
            ushort nameLen = reader.ReadUInt16();
            string key = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(nameLen));
            int topLayer = reader.ReadInt32();
            bool deleted = reader.ReadBoolean();
            int dims = reader.ReadInt32();
            float[] vector = new float[dims];
            for (int d = 0; d < dims; d++)
                vector[d] = reader.ReadSingle();

            var node = new HnswNode(key, vector, topLayer) { Deleted = deleted };

            for (int level = 0; level <= topLayer; level++)
            {
                int neighborCount = reader.ReadInt32();
                for (int n = 0; n < neighborCount; n++)
                    node.Neighbors[level].Add(reader.ReadInt32());
            }

            index._nodes.Add(node);
            if (!deleted)
                index._nameToId[key] = i;
        }

        return index;
    }

    // -- Internal algorithms --

    private int RandomLevel()
    {
        int level = 0;
        while (_rng.NextDouble() < (1.0 / M) && level < MaxLayers - 1)
            level++;
        return level;
    }

    /// <summary>Greedy closest node search at a single layer.</summary>
    private int GreedyClosest(float[] query, int entryPoint, int level)
    {
        int current = entryPoint;
        float bestDist = Distance(query, _nodes[current].Vector);

        bool changed = true;
        while (changed)
        {
            changed = false;
            if (level > _nodes[current].TopLayer) break;

            foreach (int neighbor in _nodes[current].Neighbors[level])
            {
                if (neighbor >= _nodes.Count) continue;
                float d = Distance(query, _nodes[neighbor].Vector);
                if (d < bestDist)
                {
                    bestDist = d;
                    current = neighbor;
                    changed = true;
                }
            }
        }

        return current;
    }

    /// <summary>Beam search at a single layer, returning up to ef nearest node IDs sorted by distance.</summary>
    private List<int> SearchLayer(float[] query, int entryPoint, int ef, int level)
    {
        var visited = new HashSet<int> { entryPoint };
        var candidates = new PriorityQueue<int, float>(); // min-heap by distance
        var results = new PriorityQueue<int, float>(); // max-heap (negate distance for max behavior)

        float epDist = Distance(query, _nodes[entryPoint].Vector);
        candidates.Enqueue(entryPoint, epDist);
        results.Enqueue(entryPoint, -epDist); // negated for max-heap behavior

        while (candidates.Count > 0)
        {
            candidates.TryDequeue(out int current, out float currentDist);

            // Get furthest result distance
            results.TryPeek(out _, out float negFurthestDist);
            float furthestDist = -negFurthestDist;

            if (currentDist > furthestDist && results.Count >= ef)
                break;

            if (level > _nodes[current].TopLayer) continue;

            foreach (int neighbor in _nodes[current].Neighbors[level])
            {
                if (neighbor >= _nodes.Count || !visited.Add(neighbor))
                    continue;

                float d = Distance(query, _nodes[neighbor].Vector);

                results.TryPeek(out _, out float negFDist2);
                float fDist2 = -negFDist2;

                if (d < fDist2 || results.Count < ef)
                {
                    candidates.Enqueue(neighbor, d);
                    results.Enqueue(neighbor, -d);

                    if (results.Count > ef)
                        results.Dequeue(); // remove the furthest
                }
            }
        }

        // Drain results into a list sorted by distance (ascending)
        var resultList = new List<(int Id, float Dist)>(results.Count);
        while (results.Count > 0)
        {
            results.TryDequeue(out int id, out float negDist);
            resultList.Add((id, -negDist));
        }
        resultList.Sort((a, b) => a.Dist.CompareTo(b.Dist));
        return resultList.Select(r => r.Id).ToList();
    }

    /// <summary>Selects best neighbors from candidates using simple heuristic (closest by distance).</summary>
    private List<int> SelectNeighbors(float[] query, IEnumerable<int> candidates, int maxCount)
    {
        return candidates
            .Where(id => id < _nodes.Count && !_nodes[id].Deleted)
            .Select(id => (Id: id, Dist: Distance(query, _nodes[id].Vector)))
            .OrderBy(x => x.Dist)
            .Take(maxCount)
            .Select(x => x.Id)
            .ToList();
    }

    /// <summary>Distance = 1 - cosine similarity (for use as a minimization metric).</summary>
    private static float Distance(float[] a, float[] b) =>
        1.0f - VectorIndex.CosineSimilarity(a, b);
}
