# NMP/2 — Named Memory Protocol v2

NMP (Named Memory Protocol) version 2 is scrinia's sole encoding format. It unconditionally Brotli-compresses content, then encodes the result as URL-safe Base64. Designed for maximum density when an LLM needs to store or pass artifacts, not inspect them byte-by-byte.

## Format

### Single-chunk artifact

```
NMP/2 3669B CRC32:377DAAC3 BR+B64
KLUv_QBYTQAAUXNpbmcgTWljcm9zb2Z0LkV4dGVuc2lvbnMuRGVwZW5kZW5jeUluamVjdGlvbjtD
dXNpbmcgTnVybC5Db21tYW5kcztDdXNpbmcgTnVybC5Db21wcmVzc2lvbjtDdXNpbmcgTnVybC5F
...
##PAD:2
NMP/END
```

### Multi-chunk artifact

Multiple content elements are encoded as independently decodable chunks. A `C:{k}` token in the
header indicates chunk count. A single element is not automatically split by size.

```
NMP/2 50000B CRC32:A1B2C3D4 BR+B64 C:3
##CHUNK:1
KLUv_QBY...
##PAD:1
##CHUNK:2
TQAAUXNp...
##PAD:0
##CHUNK:3
bmcgTnVy...
##PAD:2
NMP/END
```

CRC32 is computed over the full original UTF-8 bytes (pre-split). Each chunk is independently Brotli-compressed and Base64-encoded.

## Header

`NMP/2 <N>B CRC32:<hex> BR+B64 [C:<k>]`

| Field | Purpose |
|---|---|
| `NMP/2` | Format identifier + version |
| `<N>B` | Mandatory nonnegative decimal original byte count (pre-compression) |
| `CRC32:<hex>` | CRC32 over original bytes (exactly 8 hex characters; encoder uses uppercase) |
| `BR+B64` | Codec tag: Brotli-compressed, URL-safe Base64 encoded |
| `C:<k>` | Optional positive decimal chunk count, at most 100,000; absent for single-chunk |

No `WxH` grid — Base64 rows are not pixels.

Missing or malformed length/CRC32 metadata, an unsupported codec, and out-of-range chunk counts
are rejected. Header parsing enforces metadata grammar and the chunk-count range. Full decoding
enforces the aggregate 64 MiB decoded limit and verifies declared length/CRC32 against the complete
original bytes. A selected-chunk read validates body structure and that chunk's decode bound;
it does not establish whole-memory integrity. Valid empty content (`0B`, CRC32 `00000000`)
remains valid; LF and CRLF artifact line endings remain compatible.

## Data lines

- Plain URL-safe Base64 — no row-index prefix, no `│` separator
- URL-safe Base64 alphabet: `A-Z a-z 0-9 - _` (RFC 4648 §5, no `+`, `/`, or `=`)
- Up to 76 characters per line (PEM-style default)
- No `=` padding — byte count in header enables exact reconstruction

## Footer

- `##PAD:<N>` — 0–2 zero bytes appended to Brotli output for 3-byte Base64 alignment
- `NMP/END` — unambiguous termination sentinel

For multi-chunk artifacts, each chunk has its own `##PAD:<N>` line. The final `NMP/END` follows the last chunk.

## `CanDecode` check

```
StartsWith("NMP/2 ") && Contains("NMP/END")
```

This is a format probe, not complete validation. Header parsing, full decoding, and selected-chunk
reads have the distinct validation boundaries described above. Multi-chunk detection: first line
contains ` C:`.

## Decode

**Single-chunk:** collect Base64 lines between header and `##PAD:`, URL-safe decode, strip PAD bytes, Brotli-decompress.

**Multi-chunk:** for each `##CHUNK:<i>` section, collect Base64 lines until `##PAD:`, decode independently, concatenate all chunks.

## Density

| Payload | NMP/2 chars | Chars/byte |
|---|---|---|
| 1 KB prose | ~780 | ~0.76 |
| 4 KB code | ~2,800 | ~0.68 |

Actual size depends on content compressibility.

## Chunk boundaries

The caller supplies content elements; each element forms a chunk. Concatenating decoded chunks
reproduces the original UTF-8 bytes exactly. Chunk limits bound decoding, not an automatic
text-splitting policy.
