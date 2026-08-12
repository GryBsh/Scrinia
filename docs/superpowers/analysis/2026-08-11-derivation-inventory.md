# Four-Way Derivation Inventory — before the collapse

**Task 1 of `2026-08-11-store-owned-derivation`. Satisfies spec decision D2.**
Read against the working tree on branch `store-owned-derivation` at `73e5d04`, 2026-08-12.
Every line number below was opened and verified; where the plan's citation was wrong, the
correction is stated.

This document is the authority Task 2 cites. Each difference has an explicit **ruling**. A ruling
of *keep* means the single pipeline implements that behaviour; *drop* means it does not, and Task 2
owes a test showing the surviving behaviour (spec, Testing → "Every difference the D2 inventory
recorded as 'drop' has a test showing the surviving behaviour").

---

## 1. The regions, as actually found

The plan and the spec both describe six regions in a 3×2 grid (store / append / compact) × (MCP /
server). **The grid is wrong in one row.** Corrected:

| # | Region | File | Lines (verified) |
|---|---|---|---|
| R1 | MCP store | `src/Scrinia.Mcp/MemoryTools.Core.cs` | `Remember` at `:109`; derivation `:138-205`; carrier fold `:415-419`; entry `:434-447` |
| R2 | Server store | `src/Scrinia.Server/Services/MemoryOrchestrator.cs` | `StoreAsync` at `:16`; derivation `:19-69`; entry `:152-169` |
| R3 | MCP append (entry-level) | `src/Scrinia.Mcp/MemoryTools.Core.cs` | `Append` at `:1049`; `BuildAppendMetadata` at `:1314`; derivation `:1322-1389`; carrier `:1260-1264` |
| R4 | Server append (entry-level) | `src/Scrinia.Server/Services/MemoryOrchestrator.cs` | `AppendAsync` at `:186`; derivation `:269-275`; per-branch attribute merge `:304-307` (ephemeral) / `:355-364` (persistent) |
| R5 | MCP append — new chunk entry | `src/Scrinia.Mcp/MemoryTools.Core.cs` | `:1147-1160` |
| R6 | Server append — new chunk entry | `src/Scrinia.Server/Services/MemoryOrchestrator.cs` | `:283-289` |
| R7 | Shared chunk derivation (store paths) | `src/Scrinia.Core/Search/TextAnalysis.cs` | `ComputeChunkEntries` `:225-240` |
| R8 | MCP compact | `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs` | `Compact` at `:509-615` |

### Correction: the "compact" row is not compact

The plan's table (and the spec's, at `docs/superpowers/specs/2026-08-11-store-owned-derivation-design.md:26`)
lists compact as MCP `:1147` / server `:283`. Both of those lines are **inside the append path** —
they are the `AnalyzeText(content)` call that builds the newly-appended chunk's `ChunkEntry`
(R5/R6), not compaction.

Real compaction is R8, and:

- **It derives nothing.** `Compact` re-encodes existing content and hands the *existing* entry's
  `Attributes` / `Keywords` / `ContentPreview` straight back to the write seam
  (`MemoryTools.Lifecycle.cs:588-595`), then sets `ChunkEntries = null` (`:606`). No `TextAnalysis`
  call appears anywhere in it.
- **There is no server compact path at all.** `MemoryEndpoints.MapEndpoints` (`:21-31`) registers
  no compact route. `MemoryOrchestrator` has no compact method.

So the four implementations of derivation are R1/R2 (store) and R3/R4 (append), with R5/R6 as a
fifth and sixth partial implementation of the *chunk-entry* half that R7 already implements
correctly. Compact is a metadata-preserving content rewrite, not a derivation site. See D-21 for the
consequence to the spec's "compact needs no third method" claim.

---

## 2. The store-path comment, quoted in full

`src/Scrinia.Server/Services/MemoryOrchestrator.cs:47-60` (the plan cited `:57-66`; the comment
block actually begins at `:47`, and the paragraph the plan means runs `:52-59`):

```csharp
// Explicit agent-supplied attributes fold in last so they win scalar collisions — the same
// position and the same Normalize call as the MCP write path, so the resulting ATTRIBUTE MAP
// matches a local write of the same input. Merged before the shadow-drop and the TF seeding
// below so both see the agent's keys.
//
// TermFrequencies do NOT match a local write, and that is a known residual rather than an
// oversight. Over the wire the carrier keys (tag/review/anchor/file) arrive inside this one
// flat map and get seeded here; locally SeedTermFrequencies runs before the carrier merge,
// so those keys are never seeded. The server also cannot tell an agent-supplied attribute
// from a carrier one — a wire map is flat by construction — so it treats the caller's
// keywords as agent-supplied and boosts them accordingly. Net effect: BM25 ranking for the
// same memory differs slightly by store mode. Reproducing local's split would mean sending
// the two maps separately, which is a wire-shape change, not a reordering here.
attributes = KnownAttributes.Merge(attributes, KnownAttributes.Normalize(req.Attributes));
```

This comment is accurate and it is the best existing account of the store-path divergence. It
identifies three things: the seeding-order split (D-08), the flat-map inability to separate agent
attributes from carrier ones (D-08), and the keyword-provenance collapse (D-01). It names the fix as
*"sending the two maps separately."*

**The spec does not take that fix.** `MemoryWrite` (spec, "The store write surface") carries a single
`Dictionary<string, string[]>? Attributes` and a single `string[]? Keywords`. It does not send two
maps. So the divergence is closed in the *other* direction — by making the local path behave like the
server's — not by preserving local's split. That inverts this task's stated default for D-08 and
D-01, and it is called out explicitly in each ruling below and in §5.

---

## 3. Difference table — store paths (R1 vs R2)

| ID | Difference | R1 — MCP | R2 — Server | Ruling |
|---|---|---|---|---|
| **D-01** | Keyword provenance (agent vs auto) | `:145-146` — `RouteKeywords(keywords)` yields the caller's *plain* keywords only; those become the agent set at `+5`, auto terms at `+2`. | `:26-27` — identical code, **different input**. `HttpMemoryStore.WriteArtifactAsync` (`src/Scrinia/HttpMemoryStore.cs:730`) sends `Keywords: meta?.Keywords`, which is R1's already-merged `mergedKeywords` — auto terms included. Every auto term is re-classified agent and gets `+5` instead of `+2`. | **Drop the server outcome.** The regions are byte-equivalent; the defect is the wire shape. `MemoryWrite.Keywords` must carry the caller's raw keyword list, never a derived one. Task 2 must not simply move the code — it must ensure no caller hands derived keywords back in as input. Pinned by the comment quoted in §2 ("it treats the caller's keywords as agent-supplied"). |
| **D-02** | Input text for `ExtractRefAttributes` | `:149-150` — `rawContent = string.Join("\n", content)`. | `:40` — `joined = string.Concat(req.Content)`. | **Keep MCP.** `Concat` glues the last token of chunk *N* to the first of chunk *N+1*, which can destroy or fabricate a path/ref match at a chunk boundary; the newline separator cannot. Currently latent over the wire (`HttpMemoryStore.cs:726` always sends a single-element `[text]`), but reachable by a direct REST client posting multi-element `Content`. **Note for Task 2:** MCP is internally inconsistent here — `AnalyzeText`, `Extract` and `ExtractMentionSpans` all still take the `Concat` form (`:141`, `:196`, `:198`). Unify ref extraction on `Join("\n")` only; changing the *other three* to `Join` would re-tokenize every existing memory and is out of scope. |
| **D-03** | Retrieval anchors | `:170-189` — cleans anchors (drops blank/whitespace, `null` ≡ `[]`), seeds each anchor token into TF at **`+2`**, then folds `anchor` into the map via the carrier at `:415-419` — i.e. *after* seeding, so anchors are never also seeded at `+3`. | No anchor concept. `StoreRequest` (`src/Scrinia.Server/Models/ApiDtos.cs:10-15`) has no `Anchors` field. Anchors reach the server only as `req.Attributes["anchor"]` and are therefore seeded at **`+3`** by `SeedTermFrequencies` (`:65`). | **Drop MCP's `+2`; unify at `+3`.** Forced by D-08 and by `MemoryWrite`, which likewise has no `Anchors` field — under the new shape an anchor *is* an ordinary attribute key and gets the attribute weight. MCP's own comment (`:167-168`) says the curated boost lives in the attribute-score branch at `+15`, not in TF, so the TF weight is not the load-bearing signal. This is a deliberate ranking change: anchor tokens move `+2 → +3`. |
| **D-04** | `FileHashes` / drift baseline | `:375-405` — resolves each `Attributes["file"]` value against `ILocalMemoryStore.WorkspaceRoot`, hashes it, writes `FileHashes: codeRefDict` (`:445`). Skipped when the store is remote (`:387-389` yields `null`), so a remote write has no drift baseline today. | Never computes `FileHashes`. The entry built at `:152-163` has none — even though the server's own store *is* local and has a workspace root. | **Keep MCP, and extend it.** The pipeline runs inside the store, so `FileMemoryStore` can compute hashes on both paths. Consequence: remote writes gain drift detection they do not have today. That is a fix, not a regression, and Task 2 should assert it. |
| **D-05** | Auto-derived `reviewWhen` | `:368-373` — when `reviewWhen` is empty, the name is not ephemeral, and `CountPattern` matches the joined content, sets `reviewWhen = "when counts in this memory change"`. | Absent. | **Keep MCP.** No counter-argument found; the server simply does not implement it. |
| **D-06** | `review` value canonicalization | `:345-361` back-parses `Attributes["review"]`, date-parses the first parseable value into `reviewAfter` and joins the rest into `reviewWhen`; `BuildCarrierAttributes` (`src/Scrinia.Core/Migration/AttributeMigration.cs:48-54`) re-emits `review` in ISO-8601 round-trip (`"o"`) form. Because `review` is **scalar** (`KnownAttributes.IsMultiValued` `:83-86` excludes it; `Merge` `:136-141` replaces scalars), the canonical form *replaces* the raw one. | No such normalization. `review: ["2026-01-01"]` persists verbatim. | **Keep MCP.** This is the spec-header divergence *"MCP and REST disagree on a bare date in a `review` attribute"* and it is closed by adopting MCP's canonicalization. Interacts with D-08: once seeding runs after the fold, the ISO form's junk tokens (`2026`, `00`, `0000000`) seed at `+3`. `AttributeExtractor.cs:111-115` already accepts that as ranking noise and it will now apply identically on both paths. |
| **D-07** | Ephemeral tag/anchor re-fold | `:229-236` builds `ephIncoming` from the `tags` param and `cleanedAnchors` and merges it into `ephAttributes` for the ephemeral entry only. | `:90-101` uses the same `attributes` map as the persistent path; no special case. | **Drop MCP's special case.** Under `MemoryWrite` tags and anchors are already ordinary keys in `Attributes`, so this is a re-fold of values that are already present. Dropping it removes an ephemeral/persistent asymmetry, not a behaviour. |
| **D-08** | **`SeedTermFrequencies` ordering** | `:201` — seeds the map, **then** `:415-419` merges the carrier (`tag`/`anchor`/`review`/`file`). Carrier keys are therefore **never** seeded at `+3`. | `:60` merges `Normalize(req.Attributes)`, `:64` shadow-drops, `:65` seeds. The flat wire map already contains the carrier keys, so they **are** seeded. | **Adopt the server order: assemble the complete map, then seed once, last.** See §4 — this ruling gets its own section because it is forced, it inverts the default, and it changes BM25 for existing memories. |
| **D-09** | Shadow-drop position | `:200` — `DropMentionsShadowedByTypedKeys` after the agent-attribute merge, before seeding. | `:64` — identical position. | **No difference.** Recorded so Task 2 does not "fix" it. Both run it unconditionally; both comments note it is a no-op when nothing shadows. |
| **D-10** | `AttributeMigration.Forward` | `:447`, persistent path only. | `:169`, persistent path only. | **No difference.** Neither ephemeral path runs it. |
| **D-11** | `ComputeChunkEntries` gate | `:203-205` — `content.Length > 1`. | `:67-69` — `req.Content.Length > 1`. | **No difference.** |

---

## 4. The `SeedTermFrequencies` ordering ruling (D-08)

**Ruling: the single pipeline assembles the complete attribute map first, then calls
`AttributeExtractor.SeedTermFrequencies` exactly once, as the last step of attribute handling.**
This is the server's order (R2 `:60-65`), not MCP's.

**Why the default does not apply here.** The stated default is that MCP wins. It cannot win this
row, because MCP's behaviour is not expressible under the input shape the spec chose. MCP's split
exists only because `Remember` receives `tags`, `anchors`, `reviewAfter`, `reviewWhen` and the
computed `codeRefDict` as *separate parameters*, assembles them into a carrier map, and merges that
map after seeding. The spec's `MemoryWrite` has none of those parameters — it has one flat
`Attributes` map, exactly like `StoreRequest` today. A pipeline handed one flat map has no way to
know which keys were "carrier", which is precisely what the comment in §2 says
(*"a wire map is flat by construction"*). The comment names the alternative — send the two maps
separately — and the spec declined it. Preserving MCP's order would therefore require re-introducing
the two-map wire shape the spec rejected.

**What changes, observably.** Attribute values under `tag`, `review` and `file` currently seed **0**
TF locally and `+3` remotely; after this ruling they seed `+3` on both. `anchor` values currently
seed `+2` locally and `+3` remotely; after, `+3` on both (D-03).

**Either answer changes BM25 for existing memories.** Term frequencies are persisted in the sidecar,
not recomputed at query time, so nothing changes for a memory until it is rewritten — but the moment
a memory is re-stored or appended to under the new pipeline, its ranking shifts relative to
neighbours that have not been rewritten. Choosing MCP's order instead would shift it the other way
(remote-written memories would *lose* their carrier-key weights). There is no order that leaves
existing rankings untouched; the choice is only which direction the shift goes and whether the two
store modes agree afterwards. This ruling picks agreement.

**One consequence Task 2 must handle:** a single seed pass means the round-tripped `review`
timestamp's junk tokens now seed locally too (D-06). Accepted per `AttributeExtractor.cs:111-115`.

---

## 5. Difference table — append paths (R3 vs R4)

Scope note, verified: `HttpMemoryStore.WriteArtifactAsync` (`src/Scrinia/HttpMemoryStore.cs:712-738`)
POSTs to `/memories` for **both** store and append, so MCP-in-remote-mode reaches R2, never R4.
`MemoryOrchestrator.AppendAsync` (R4) is reachable only by a direct REST client calling
`POST /memories/{name}/append` (`MemoryEndpoints.cs:25`). That does not make R4's defects academic —
it makes them invisible to the MCP test suite, which is why they drifted.

| ID | Difference | R3 — MCP | R4 — Server | Ruling |
|---|---|---|---|---|
| **D-12** | Agent keywords | `:1332-1334` — `curatedKeywords = UnionDistinct(existingKeywords, newKeywords)` passed to `MergeKeywordsWithSource` as the agent set. | `:270` — `MergeKeywords(null, autoKeywords)`. | **Keep MCP.** The consequence is not ranking, it is **data loss**: R4 writes `Keywords: mergedKeywords` (`:316` ephemeral, `:373` persistent) with no carry-forward, so a REST append **destroys every curated keyword on the memory**. This is the known-wrong implementation of the four. |
| **D-13** | TF boost | `:1335-1343` — agent `+5`, auto `+2`. | `:271-275` — everything `+2`. | **Keep MCP.** Matches both store paths (R1 `:156-164`, R2 `:30-38`). R4 is the outlier against its own file. |
| **D-14** | Boost target | `:1338` — `IndexTokensForKeyword(kw)`. | `:273` — the raw `kw`. | **Keep MCP.** Verified **currently unobservable**: R4's `mergedKeywords` are all auto terms from `AnalyzeText`, which are `Tokenize` output — lowercased, ≥2 chars, non-stopword, and colon-free because `Tokenize` splits on every non-alphanumeric (`TextAnalysis.cs:44-69`). For such a token `IndexTokensForKeyword` (`:160-174`) returns `Tokenize(token) == [token]`. The divergence becomes observable only once D-12 restores agent keywords, which *can* be `prefix:value`. So D-14 is not independently a defect today — it is a latent one that D-12 activates, and both must land together. |
| **D-15** | Anchors | `:1348-1357` — unions existing ∪ new, re-seeds each token at `+2` (TF is rebuilt from scratch per append, so carried-forward anchors must re-seed). | No anchors; `AppendRequest` is `record AppendRequest(string Content)` (`ApiDtos.cs:24`). Existing anchors survive only because `existingEntry.Attributes` is merged at `:357`. | **Keep the union carry-forward; drop the separate `+2` re-seed.** Under D-08 the merged map is seeded once at `+3`, which R4 already does at `:358`. The union itself is load-bearing and must survive — see D-17. |
| **D-16** | Tags | `:1359` `UnionDistinct(existingTags, newTags)`, refolded at `:1383-1387`. | None; `tag` survives via `existingEntry.Attributes` at `:357`. | **Keep MCP's union — as an explicit append-path rule, not via `IsMultiValued`.** See D-17. |
| **D-17** | *(cross-cutting)* union-vs-replace for `tag` / `anchor` | Store replaces (`:415-419` carrier → `Merge` → scalar newer-wins). Append unions (`:1359`, `:1348`). | n/a — no inputs. | **Keep the asymmetry, and encode it in the pipeline.** `tag`, `anchor` and `review` are all **scalar** in `KnownAttributes.IsMultiValued` (`:83-86` — only entity keys, reference keys and `mention` are multi-valued), so `KnownAttributes.Merge` **replaces** them. Once `MemoryWrite.Attributes` becomes the only channel for tags/anchors, a caller-supplied `tag` on append would silently *replace* the memory's tag set, where MCP unions it. Two ways to fix; take the first: **(a)** the pipeline's append entry point unions `tag`/`anchor` explicitly before merging, or **(b)** add them to `IsMultiValued`. Reject (b): it would make tags unclearable on re-store, which is a behaviour store deliberately has. |
| **D-18** | Explicit caller attributes | `:1376` — `Merge(incoming, Normalize(attributes))`, so caller attributes win over the existing entry's. | None; `AppendRequest` carries no attributes. | **Keep MCP.** `MemoryWrite.Attributes` supplies this on the append path. |
| **D-19** | Routed caller keywords → attributes | `:1152` `RouteKeywords(keywords)` → `routedNewAttributes`, merged at `:1374`. | None. | **Keep MCP.** Without it, a `person:maria` keyword on a REST append never becomes an attribute. |
| **D-20** | Description / `ContentPreview` | `:1179-1181` / `:1225-1227` — new description wins, else preserve the existing `ContentPreview`, else `Generate(fullText)`. | `:302` / `:337` — preserve existing, else `Generate(fullText)`. No description input. | **Keep MCP.** R4 is the degenerate two-way case of MCP's three-way rule; adopting MCP is strictly a superset. |
| **D-21** | `file` / `FileHashes` carry-forward | `:1260-1264` carries `existingEntry.FileHashes.Keys` into the `file` attribute; `:1288` preserves `FileHashes: existingEntry?.FileHashes`. | `:360-362` carries existing `review` but passes `fileKeys: null`, and the entry built at `:366-377` has **no** `FileHashes` at all. | **Keep MCP.** A REST append currently **destroys the drift baseline**. Same class of loss as D-12. |
| **D-22** | Ref extraction input | `:1375` — `ExtractRefAttributes(appendedContent)`. | `:305` / `:356` — `ExtractRefAttributes(content)`. | **No difference.** Append takes a single `string content`, so D-02's `Concat`/`Join` question does not arise. |

---

## 6. Difference table — new-chunk entry (R5 vs R6 vs R7)

R7 (`TextAnalysis.ComputeChunkEntries`) is already shared by both *store* paths. R5 and R6 are
hand-rolled partial re-implementations of it for the single chunk an append adds.

| ID | Difference | R7 (store) | R5 / R6 (append) | Ruling |
|---|---|---|---|---|
| **D-23** | Empty-preview guard | `:235` — `string.IsNullOrEmpty(preview) ? null : preview`. | R5 `:1158`, R6 `:287` — `ContentPreview.Generate(content)` unguarded. `Generate` returns `""` for empty/whitespace input (`src/Scrinia.Core/ContentPreview.cs:16`), so an empty appended chunk yields `ContentPreview: ""` where the store path yields `null`. | **Keep R7's guard.** One function produces chunk entries; the unguarded form is dropped. Distinguishable in a sidecar round-trip (`""` vs absent). |
| **D-24** | Keyword boost target | `:231` — raw `k`. | R5 `:1148`, R6 `:284` — raw `k`. | **No difference**, and equivalent to `IndexTokensForKeyword` by the D-14 argument (auto keywords only). Use `IndexTokensForKeyword` in the pipeline for uniformity; behaviour is identical. |
| **D-25** | `ChunkIndex` base | `:234` — `i + 1` (1-based). | R5 `:1157`, R6 `:286` — `chunkCount`, the post-append count, also 1-based and contiguous with R7's numbering. | **No difference.** Recorded so Task 2 does not "fix" it into an off-by-one. |
| **D-26** | Attribute seeding into chunk TF | None — `ComputeChunkEntries` seeds no attributes into per-chunk TF. | None. | **No difference.** Chunk-level TF carries content terms only, on all paths. Preserve. |

---

## 7. Compact (R8) — the spec's subset claim

The spec states (*"Compact needs no third method"*): *"The D2 inventory must confirm compact's
derivation is genuinely a subset of store's; if it is not, that difference is a recorded keep-or-drop
like any other."*

**It is not a subset, because compact does not derive.** R8 preserves the existing entry's
`Attributes`, `Keywords` and `ContentPreview` verbatim and nulls `ChunkEntries`
(`MemoryTools.Lifecycle.cs:588-608`). Routing it through the derivation pipeline changes three
things:

| ID | Change | Ruling |
|---|---|---|
| **D-27** | Keywords and TF would be **re-derived** from the compacted text rather than preserved. For `keepRecent = 0` the text is unchanged (all chunks merged into one), so TF is unchanged. For `keepRecent > 0` the text is genuinely truncated, and today the entry keeps TF for content that no longer exists. | **Accept the change (drop the preservation).** Re-deriving after a truncating compact is more correct — the current behaviour leaves BM25 weights pointing at dropped chunks. |
| **D-28** | `ChunkEntries` would be **repopulated** for `keepRecent > 1` (the pipeline's `Content.Length > 1` gate), where today they are nulled with the comment *"chunk-level entries no longer valid after compaction"* (`:606`). For `keepRecent ≤ 1` the pipeline also yields `null`, matching today. | **Accept the change.** Freshly computed entries for the surviving chunks are valid; the null was a consequence of not re-deriving, not a requirement. |
| **D-29** | The existing `Keywords` handed back in as `MemoryWrite.Keywords` would be treated as **agent** keywords and boosted `+5`, even though most were auto-derived. | **Blocked by D-01, and fixed by the same rule.** Compact must pass only the memory's genuinely-curated keywords, or none. Task 2 must not hand a derived keyword list back into the pipeline as input — this is the identical mistake `HttpMemoryStore.cs:730` makes today. |

With D-27, D-28 and D-29 recorded, compact can route through `StoreAsync` and the spec's "no third
method" holds. `CreatedAt` preservation and `ArchiveVersion` are already `StoreAsync` behaviour
(`MemoryOrchestrator.cs:129-133`), so those are not at risk.

**There is no server compact to reconcile.** Compact remains MCP-only unless someone adds a route.

---

## 8. Rulings summary

**Keep (MCP wins):** D-02, D-04, D-05, D-06, D-12, D-13, D-14, D-16, D-18, D-19, D-20, D-21, D-23.

**Drop (server wins or the behaviour disappears):** D-01 *(the outcome; the code is identical)*,
D-03, D-07, D-08, D-15 *(the `+2` re-seed only)*.

**Accepted behaviour changes:** D-27, D-28.

**No difference — do not "fix":** D-09, D-10, D-11, D-22, D-24, D-25, D-26.

**Structural rulings Task 2 must encode:** D-17 (append unions `tag`/`anchor`; do **not** change
`IsMultiValued`), D-29 (never feed derived keywords back in as input).

## 9. Two rulings the caller should confirm

1. **D-08 inverts the stated default.** The task's default is "MCP wins"; D-08 rules for the server's
   order. The reason is that MCP's order is not expressible under the spec's own `MemoryWrite`
   shape, and the comment in §2 confirms it (a flat map cannot separate carrier from agent keys).
   The comment's named fix — two maps on the wire — is the option the spec declined. If the intent
   was to preserve local's split, `MemoryWrite` needs a second map and D-03/D-06/D-08 all change.
2. **D-17 is not settled by "MCP wins"**, because MCP itself replaces `tag`/`anchor` on store and
   unions them on append. The ruling keeps both, implemented as an explicit append-path rule. The
   rejected alternative (adding `tag`/`anchor` to `IsMultiValued`) would make tags unclearable on
   re-store.

Everything else was explicable from the code and needed no coin toss.
