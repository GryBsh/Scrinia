# NMP/2 Encoding Reference

> Brotli-compressed, URL-safe Base64 encoded artifacts with CRC32 integrity.

See [NMP_SPEC.md](../NMP_SPEC.md) for the byte-level format specification.

---

## Nmp2ChunkedEncoder (Public API)

The entry point for all encoding operations. Located in `Scrinia.Core.Encoding`.

```csharp
// Single-chunk: always produces one chunk regardless of size
string artifact = Nmp2ChunkedEncoder.Encode(text);

// Agent-directed chunking: each element → one independently decodable chunk
// 1 element → single-chunk format, 2+ → multi-chunk format
string artifact = Nmp2ChunkedEncoder.EncodeChunks(["## Auth\n...", "## Users\n..."]);

// Append a new chunk without re-encoding existing chunks (surgical append)
// Single-chunk → promotes to multi-chunk; multi-chunk → appends new section
string updated = Nmp2ChunkedEncoder.AppendChunk(existingArtifact, "new chunk text");

// Chunk access
int count = Nmp2ChunkedEncoder.GetChunkCount(artifact);
string chunkText = Nmp2ChunkedEncoder.DecodeChunk(artifact, chunkIndex); // 1-based
```

**No auto-chunking guarantee**: A single-element `Encode()` or `EncodeChunks(["one"])` always produces single-chunk format, regardless of size. Multi-chunk only via explicit multiple elements or `AppendChunk()`.

---

## Single-Chunk Format

```
NMP/2 {N}B CRC32:{hex} BR+B64
{up to 76 url-safe base64 chars per line}
...
##PAD:{0-2}
NMP/END
```

- **Header**: `{N}B` = original byte count, CRC32 over original UTF-8 bytes (8 hex chars)
- **Data**: URL-safe Base64 (RFC 4648 §5: `A-Z a-z 0-9 - _`), 76 chars/line, no `=` padding
- **Padding**: `##PAD:{n}` — 0-2 zero bytes for 3-byte Base64 alignment
- **Sentinel**: `NMP/END`

---

## Multi-Chunk Format

```
NMP/2 {N}B CRC32:{hex} BR+B64 C:{k}
##CHUNK:1
{independently brotli-compressed + base64 lines}
##PAD:{n}
##CHUNK:2
...
NMP/END
```

- CRC32 computed over full original UTF-8 bytes (pre-split)
- Each chunk independently Brotli-compressed → independently decodable
- `C:{k}` in header indicates chunk count

---

## AppendChunk (Incremental CRC32)

`AppendChunk(existing, newText)` appends a new chunk without re-encoding existing chunks:

1. Extracts existing chunk sections verbatim (`ExtractRawChunkSections`)
2. Compresses only the new chunk (`AppendCompressedChunk`)
3. Recomputes the global CRC32 using `Crc32Combine` (incremental — does not re-read existing data)
4. Assembles a new multi-chunk artifact with the updated header

When called on a single-chunk artifact, it promotes to 2-chunk format. When called on a multi-chunk artifact, it appends as the next `##CHUNK:N` section.

---

## Encoding Pipeline

```
Nmp2ChunkedEncoder (public API)
  └─ Encode(text) → always single chunk
  └─ EncodeChunks(string[]) → 1 elem = single, 2+ = multi-chunk
  └─ AppendChunk(existing, newText) → promotes single→multi or appends
  └─ GetChunkCount(artifact) → count
  └─ DecodeChunk(artifact, index) → text (1-based)

Nmp2Strategy (IEncodingStrategy impl)
  └─ Encode(bytes, options) → Brotli → pad → Base64url → format
  └─ Decode(artifact) → strip → Base64url decode → Brotli decompress
```

Key internals:
- `EncodeMultiChunkFromParts(string[])` — shared multi-chunk encoding (CRC32 over concatenated bytes)
- `AppendCompressedChunk(sb, bytes, number)` — appends one `##CHUNK:N` section
- `ExtractRawChunkSections(artifact, count)` — extracts existing chunk sections verbatim for surgical append

---

## Compression Density

Typical density: **~0.68-0.76 chars/byte** depending on content compressibility. Highly repetitive text compresses better; random/encoded content compresses worse.
