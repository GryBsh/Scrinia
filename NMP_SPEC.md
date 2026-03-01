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

Documents exceeding 20,000 characters are split into independently decodable chunks, each targeting ~8,000 characters. A `C:{k}` token in the header indicates chunk count.

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
| `<N>B` | Exact original byte count (pre-compression) |
| `CRC32:<hex>` | CRC32 over original bytes (8 uppercase hex chars) |
| `BR+B64` | Codec tag: Brotli-compressed, URL-safe Base64 encoded |
| `C:<k>` | Chunk count (multi-chunk only; absent for single-chunk) |

No `WxH` grid — Base64 rows are not pixels.

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

Multi-chunk detection: first line contains ` C:`.

## Decode

**Single-chunk:** collect Base64 lines between header and `##PAD:`, URL-safe decode, strip PAD bytes, Brotli-decompress.

**Multi-chunk:** for each `##CHUNK:<i>` section, collect Base64 lines until `##PAD:`, decode independently, concatenate all chunks.

## Density

| Payload | NMP/2 chars | Chars/byte |
|---|---|---|
| 1 KB prose | ~780 | ~0.76 |
| 4 KB code | ~2,800 | ~0.68 |

Actual size depends on content compressibility.

## Chunking thresholds

| Parameter | Value |
|---|---|
| Chunk threshold | 20,000 characters (single-chunk below this) |
| Target chunk size | 8,000 characters |
| Split strategy | Prefer newline boundaries within target window |

Chunks are split on the original text before encoding. Concatenating decoded chunks reproduces the original text exactly.
