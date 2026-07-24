---
name: scrinia-dual-keyvalue-parsers
description: Scrinia.Core has two different key:value keyword parsers with different guarantees — mixing them up silently drops case-canonicalization.
metadata:
  type: project
---

Scrinia.Core has two independent implementations of "parse a `key:value`-shaped keyword":

1. `WeightedFieldScorer.ScoreTopicTerm` (src/Scrinia.Core/Search/WeightedFieldScorer.cs, ~line 421) —
   raw `IndexOf(':')` split, no case normalization, no value cap, no URL guard. Safe here because
   the parsed key is only used for an `OrdinalIgnoreCase` equality check in the same method call —
   never persisted.
2. `FieldExtractor.RouteKeywords` (src/Scrinia.Core/Search/FieldExtractor.cs, ~line 78) — regex-based,
   calls `.ToLowerInvariant()` on the key, enforces `MaxValueLength` (120 chars), and skips
   `//`-prefixed values (URL-remainder guard). This is the production write-path used by
   `MemoryTools.Core.cs` and `MemoryOrchestrator.cs` whenever agent keywords get persisted into
   `Fields`.

**Why this matters:** `AttributeMigration.Forward` (src/Scrinia.Core/Migration/AttributeMigration.cs)
added a routing block in commit d1fe1d0 that deliberately mirrors parser #1 (the scorer's raw parse)
"for parity" — but then *persists* the result into `Fields`, which is parser #2's job. Mirroring the
non-normalizing parser while doing the normalizing parser's job silently reintroduces the exact class
of case-fragmentation bug `FieldExtractor` was built to avoid (a `Person:` keyword would persist a
literal `"Person"` key into `Fields` instead of canonical `"person"`), which matters because
`WeightedFieldScorer.cs` has an explicit comment: "dictionary comparers don't survive JSON round-trips" —
so the in-memory `OrdinalIgnoreCase` comparer set by `KnownFields.Merge` doesn't protect exact-key
lookups (e.g. `fields.TryGetValue(KnownFields.ReviewKey, ...)` in MemoryTools.Core.cs) once an entry
round-trips through `FileMemoryStore`'s JSON persistence.

**How to apply:** Whenever reviewing new code in `Scrinia.Core.Migration` or `Scrinia.Core.Search` that
parses `key:value`-shaped keywords, check which of the two parsers it's mirroring and whether that
matches what it does with the result (transient scoring vs. persisted `Fields` write). If it persists,
it must canonicalize the key (`.ToLowerInvariant()` or resolve against `KnownFields.RecommendedKeys`),
cap value length, and consider the URL-remainder guard — i.e. it should reuse or match
`FieldExtractor.RouteKeywords`, not the scorer's parse.
