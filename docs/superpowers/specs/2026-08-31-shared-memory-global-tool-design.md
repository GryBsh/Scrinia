# Shared Memory and Global Tool Architecture

**Status:** `Proposed` — reviewer approval pending  
**Date:** 2026-08-31  
**Scope:** Packaging the global tool; separating local and shared memory; migrating tracking policy; and retiring the merge subsystem

## 1. Purpose and governing boundary

Scrinia needs shared memory without treating a project's local `.scrinia/` store as a multi-user merge format. This proposed architecture separates local and shared operation:

- `scri serve` serves only `<workspace-root>/.scrinia/`.
- `scri shared serve` serves only shared bases named by that workspace and resolved through the current user's registry.
- The two stdio MCP servers expose disjoint tool names and disjoint stores. No operation routes between them.
- Git transports shared-base changes. The agent runs every Git command. Scrinia never launches Git.
- One NuGet global-tool package installs the user-facing `scri` command. The same packaged command implements both server modes.

If externally approved, this architecture specification will govern multiple independently reviewable implementation plans. Until then, it records the corrected proposal and its pending approval gate; it does not authorize implementation.

## 2. Goals and non-goals

The implementation must deliver:

1. one deterministic per-user home and shared-base registry;
2. explicit local versus project-Git tracking for the local workspace and mandatory project-Git tracking for every shared base;
3. a separately named shared MCP surface with stable provenance;
4. snapshot reads, explicit refresh, and fail-closed shared mutations;
5. categorical process and data boundaries around Git;
6. complete removal of the superseded merge subsystem;
7. one packaged `scri` global-tool command with stable model and plugin locations;
8. migration, security, error, test, and living-documentation contracts in the same release.

The implementation does not add an HTTP service, remote store, hosted synchronization service, daemon, account system, Git credential manager, filesystem watcher, automatic local-to-shared copy, automatic pull or push, cross-base link, local/shared link, implicit mutation target, or per-base provider configuration.

## 3. Deterministic configuration

### 3.1 User home and typed registry

`SCRINIA_HOME` contains per-user state:

```text
SCRINIA_HOME/
  config.json
  models/
  plugins/
```

The home is the absolute, non-empty `SCRINIA_HOME` value when set. Otherwise it is exactly
`Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scrinia")`
on every platform. Scrinia fails configuration if that platform API returns an unusable path. It
does not substitute `%LOCALAPPDATA%`, XDG, or `$HOME` rules of its own.

`SCRINIA_HOME/config.json` is a separate typed, source-generated contract, not the workspace's
flattened dictionary:

```json
{
  "schemaVersion": 1,
  "sharedBases": {
    "team": { "root": "D:\\repos\\team-memory" },
    "architecture": { "root": "/home/alex/repos/architecture-memory" }
  }
}
```

Both top-level properties are required; `schemaVersion` is the integer 1 and `sharedBases` is an
object, including an empty object. Only those properties exist in v1. Base names are canonical map keys and each `root` is an
absolute repository root; a root ending in `.scrinia` is invalid because shared data lives at
`<root>/.scrinia`. The implementation adds these DTOs to a dedicated source-generated JSON
context. Reads fail closed on malformed JSON, duplicate JSON properties, invalid values, or a
`schemaVersion` greater than 1. Writes take an interprocess file lock and atomically replace the
file from a same-directory temporary file; lock or replacement failure leaves the prior bytes
intact and returns an error. No v1 environment variable adds, removes, or repoints a registry
entry. `SCRINIA_HOME` alone selects the registry, models, and plugin directories.

### 3.2 Flattened workspace configuration and precedence

`<workspace-root>/.scrinia/config.json` remains the existing
`Dictionary<string,string>` serialized through `ConfigJsonContext.Default.DictionaryStringString`.
The architecture-owned values are scalar strings:

```json
{
  "Scrinia:Workspace:Tracking": "local",
  "Scrinia:Shared:Enabled": "team;architecture"
}
```

`Scrinia:Workspace:Tracking` is exactly `local` or `project-git`. Its existing workspace-key
precedence is retained: historical single-underscore environment variable, .NET
double-underscore environment variable, workspace config, then the command's default. The exact
v1 environment spellings are `SCRINIA_WORKSPACE_TRACKING` first and
`SCRINIA__WORKSPACE__TRACKING` second. An empty or invalid value is an error, not a request for the
next source.

`Scrinia:Shared:Enabled` is exactly a semicolon-separated string of lowercase canonical names,
for example `team;architecture`. Parsing does not trim, lowercase, normalize, sort, or discard:
an empty string, an empty segment (`team;;architecture`, leading `;`, or trailing `;`), a duplicate,
or a noncanonical name invalidates the workspace configuration. V1 deliberately does not read
`SCRINIA_SHARED_ENABLED` or `SCRINIA__SHARED__ENABLED`; allowing an environment override would
silently expand the bases visible to a workspace.

An absent `Scrinia:Shared:Enabled` key is the sole representation of zero enabled bases. It is
valid workspace configuration, but `scri shared serve` fails with `NO_SHARED_BASES_ENABLED`.
`scri shared disable` atomically removes the key when it disables the final enabled base. An empty
scalar is always invalid and is never normalized to an absent key.

Workspace writes retain every unrelated dictionary entry and are upgraded to the same
same-directory atomic-replace and interprocess-lock discipline as user config. This changes write
safety, not the flattened schema or `ConfigJsonContext`. Configuration is loaded once at server
startup; registry or workspace edits require restart. `shared_refresh` reloads a base generation,
not configuration.

### 3.3 Base names, roots, and visibility

A canonical base name matches `^[a-z][a-z0-9-]{0,31}$`. Scrinia rejects aliases, uppercase,
empty names, and duplicates. Shared tracking is fixed to `project-git`; it is not stored in the
user registry and cannot be configured per workspace or per base. A registered root is an absolute
existing directory that is itself an existing Git repository or worktree by filesystem evidence:
`<root>/.git` is either a directory or a regular UTF-8-without-BOM text file. After removing one
terminal LF or CRLF, the file must contain exactly one nonempty line with the case-sensitive form
`gitdir: <path>` and no leading/trailing whitespace beyond the one required space after the colon.
`<path>` is absolute or is resolved relative to `<root>` and must resolve to an existing directory. Scrinia reads
only this marker for validation and never invokes Git or inspects the index. A `.git` marker found
only in an ancestor is insufficient. Canonical physical comparison rejects duplicate, nested, or
overlapping enabled roots, a root equal to or inside the workspace root, and a `.scrinia` store
that cannot be contained safely.

Visibility is a security boundary. `shared_bases()` returns only names in the validated workspace
enable string, including enabled names whose registry entry or root is unavailable. It emits the
root for an enabled and known base and omits `root` when resolution is unavailable. Disabled registry names and roots never appear in shared
MCP output, warnings, errors, diagnostics, or logs. The user-run `scri shared base list` command is
the only surface in this design that enumerates the complete per-user registry.

### 3.4 Startup with no usable bases

`scri shared serve` fails before MCP transport startup with `NO_SHARED_BASES_ENABLED` when the
Enabled key is absent. When enabled names exist, startup validates each root, Git-root marker, and
managed tracking block. It fails before MCP transport startup if none is available, using the first
applicable per-base code in canonical name order; tracking-block absence, drift, or unsafe content
uses `SHARED_TRACKING_POLICY_DRIFT`. If one base opens, the server starts and retains every other
enabled name as unavailable for `shared_bases()` and aggregate warnings. Consequently a callable
`shared_bases` tool always has at least one enabled base.

## 4. Authoritative and derived data

### 4.1 Classification

**Authoritative files** are the current memory state that cannot be reconstructed from another file in the same store:

- NMP/2 memory bodies and their `.meta.json` sidecars;
- file-entity bodies and sidecars;
- `.scrinia/skills/*.md` and skill sidecars;
- `.scrinia/agent/*.md` and agent sidecars.

**Control files** configure or protect a store and are not memory content: `.scrinia/config.json` and `.scrinia/.gitignore`.

**Derived or runtime files** are rebuildable or transient. The v1 classifier is exhaustive and uses
ordered first-match precedence. Managed ignore patterns are relative to `.scrinia/.gitignore`; static
cleanup pathspecs are rooted at the repository root. The classifier applies these groups in order:

1. the canonical root derived directories, in the table order below, including their descendants;
2. the canonical root derived singleton files, in the table order below;
3. every file or directory at any depth whose final segment ends in `.tmp`, plus every descendant
   of such a directory by ancestry;
4. every root-child file or directory whose final segment ends in `.lock`, plus every descendant of
   such a root-child directory by ancestry.

Each filesystem path receives at most one semantic class: the first matching group wins. The
managed ignore block and static cleanup may use overlapping Git patterns, but overlap does not create
a second semantic class.

| Class | Managed ignore pattern | Static cleanup pathspec |
|---|---|---|
| Embedding/vector indexes | `/embeddings/` | `.scrinia/embeddings` |
| Other search indexes | `/indexes/` | `.scrinia/indexes` |
| Export bundles | `/exports/` | `.scrinia/exports` |
| Archived copies | `/versions/` | `.scrinia/versions` |
| Entity facts cache | `/facts.json` | `.scrinia/facts.json` |
| Restore journal | `/.restore-journal.json` | `.scrinia/.restore-journal.json` |
| Derived temporary trees | `**/*.tmp` | `:(glob).scrinia/*.tmp` and `:(glob).scrinia/**/*.tmp` in one cleanup argv |
| Root operation lock trees | `/*.lock` | `:(glob).scrinia/*.lock` |

There is no active conflict, reconciliation, or migration-scratch class after merge retirement. A
derived file never becomes authoritative because it is tracked accidentally.

The implementation maintains this classifier in one production registry and exhaustive equivalence
tests. The managed ignore patterns and static cleanup pathspecs must denote exactly the classifier's
derived-path union, and the fingerprint exclusion must exclude exactly that same union. The Git
patterns may overlap and need not map one-to-one to semantic classes. The leading `/` anchors keep a
canonical root derived-directory rule from ignoring an authoritative directory with the same name
below a topic or entity directory. No authoritative file, directory, or path segment may match a
reserved suffix rule: no segment at any depth may end in `.tmp`, and no root-child segment may end
in `.lock`. A path below a matching directory remains derived by ancestry even when its own final
segment has no reserved suffix. A new durable file type must be classified before release without
changing these equivalence invariants.

### 4.2 Fingerprint inventory

The shared-base freshness fingerprint covers authoritative files only and excludes the complete
first-match classifier union from §4.1, including every descendant of a derived temporary or lock
tree. For each authoritative regular file, Scrinia obtains its
`/`-separated path relative to `<root>/.scrinia`, sorts by UTF-8 ordinal byte sequence, and feeds
SHA-256 with this unambiguous sequence for every file:

```text
8-byte big-endian path byte length
UTF-8 relative-path bytes
8-byte big-endian content byte length
raw file bytes
```

The resulting lowercase hexadecimal SHA-256 value identifies one authoritative generation. Control, derived, runtime, `.git`, and files outside `.scrinia` never enter the fingerprint.

### 4.3 Cache isolation and collision rules

Every in-memory store, facts cache, search index, vector handle, lock, and generation record is keyed by the canonical physical root identity plus canonical base name. Memory address alone is never a cache key across bases. Equal addresses in two bases remain distinct records.

Workspace and shared derived facts, vectors, and indexes stay inside their own `.scrinia` stores. They are ignored under `project-git` tracking and rebuilt independently. A cache file from one root is never opened for another root, including after a registry name is repointed and the server restarted.

Model downloads use a model identifier plus verified content digest under `SCRINIA_HOME/models`; incomplete downloads use unique temporary names and atomic promotion. Separately installed plugin assets use plugin identifier, plugin version, and RID under `SCRINIA_HOME/plugins`. Keying assets by these identities keeps model, plugin-version, RID, workspace, and shared-base data in distinct namespaces.

### 4.4 Authoritative durable-format matrix

“Entry unavailable” means the address is omitted from search/list/facts and direct recall fails with
the row's code. The base remains available for every other valid entry. “Mutation target” covers
remember, append, forget, compact, and either endpoint of link. Scrinia performs no automatic
numeric format upgrade.

| Authoritative format/state | Read visibility | Base availability | Mutation target | Refresh | `mutation` / code |
|---|---|---|---|---|---|
| NMP body with complete, decode-valid `NMP/2` header and payload | Readable | Available | Writable when its sidecar permits | Succeeds | read/refresh `none`; successful mutation `applied` |
| NMP body declaring `NMP/1` or `NMP/3` | Entry unavailable | Other entries available; warning names only base and logical address | Fails before side effect | Succeeds with warning | `none`; `SHARED_FORMAT_UNSUPPORTED` |
| NMP body with absent/incomplete header or decode-invalid payload | Entry unavailable | Other entries available; warning names only base and logical address | Fails before side effect | Succeeds with warning | `none`; `SHARED_ENTRY_CORRUPT` |
| Memory sidecar absent | Entry unavailable | Other entries available; warning | Target fails before side effect | Succeeds with warning | `none`; `SHARED_ENTRY_CORRUPT` |
| Memory sidecar older than or equal to current v1 | Readable; unknown JSON fields survive every permitted rewrite | Available | Writable | Succeeds | read/refresh `none`; successful mutation `applied` |
| Memory sidecar newer than v1 | Readable and searchable | Available | Delete allowed; direct overwrite, append, compact, and applicable link-side mutation fail; unrelated entries remain writable | Succeeds | delete may be `applied`; refused mutation `none`; `SHARED_FORMAT_READ_ONLY` |
| Memory sidecar malformed | Entry unavailable | Other entries available; warning | Target fails before side effect | Succeeds with warning | `none`; `SHARED_ENTRY_CORRUPT` |
| Memory sidecar has missing, null, blank, or invalid `address`, or missing, null, or blank `artifactKey` | Entry unavailable; never visible | Other entries available; warning | Target fails before side effect | Succeeds with warning | `none`; `SHARED_ENTRY_CORRUPT` |
| Skill/agent sidecar absent, older, or current | Body readable; absent metadata uses the exact defaults in §4.5 | Available | Writable | Succeeds | read/refresh `none`; successful mutation `applied` |
| Skill/agent sidecar malformed or wrong JSON shape | Metadata is absent under §4.5; a readable Markdown body remains visible | Available unless the failure is I/O | Writable and replaces metadata with the exact current shape | Succeeds | read/refresh `none`; successful mutation `applied`; no metadata warning |
| Skill/agent sidecar newer than v1 | Body readable | Available | Target fails before archive or body write | Succeeds | `none`; `SHARED_FORMAT_READ_ONLY` |
| Skill/agent body missing, unreadable, or corrupt | Entry unavailable | Other entries available; warning | Target fails before side effect | Succeeds with warning | `none`; `SHARED_ENTRY_CORRUPT` |
| Ordered Git marker triplet in any authoritative UTF-8 text file | No shared entry is visible | Whole base conflicted | Every mutation fails | Fails | `none`; `SHARED_BASE_CONFLICTED` |

The NMP body rule is stricter than sidecar compatibility: only NMP/2 is readable or writable.
NMP artifacts are UTF-8 NMP text, not opaque binary blobs. Conflict scanning therefore UTF-8-decodes
and scans every authoritative NMP, JSON, Markdown, and sidecar file before NMP payload decode or JSON
parsing. A base is conflicted only when one authoritative file contains, in order, a line beginning
with `<<<<<<< `, a later line exactly `=======`, and a later line beginning with `>>>>>>> `. Line
starts are the start of the file or the character after LF; CRLF is normalized only for recognizing
the exact separator line. Ordinary embedded marker substrings, marker-like text away from a line
start, or an incomplete or out-of-order set do not conflict the base and proceed to normal format
validation. Scrinia does not inspect `.git`, filenames, duplicate-name conventions, or nonauthoritative
paths to infer conflict. Warnings do not include corrupt content or a
physical path. A refresh publishes the valid entries and their warnings as one generation; only
the ordered marker-triplet row prevents publication.

### 4.5 Exact durable compatibility behavior

These rules are the complete durable compatibility contract.

**Memory `ArtifactEntry` JSON.** A missing property receives its CLR/deserializer value. A missing,
null, blank, or invalid `address` deterministically makes that entry unavailable and skipped with
`SHARED_ENTRY_CORRUPT`; it never yields a visible search, list, facts, or recall result. A missing,
null, or blank `artifactKey` has the same deterministic outcome and code. `originalBytes` and
`chunkCount` are `0`; `createdAt` is
`DateTimeOffset.MinValue`. `contentPreview`, `keywords`, `termFrequencies`, `updatedAt`,
`chunkEntries`, `codeRefs` (including `FileHashes`), `attributes`, `agentKeywords`, and `v` are null.
Missing `v` is interpreted semantically as version 1. Null `agentKeywords` means provenance is
unknown, not an empty agent-keyword set. Unknown extension data is null when absent. Known property
names are matched case-insensitively. Legacy `fields` folds into `attributes`; when both are present,
`attributes` wins wholesale rather than merging per key. Unknown top-level fields round-trip through
accepted rewrites. A malformed or missing individual sidecar, including either invalid required
identity field above, makes only that entry unavailable and skipped with `SHARED_ENTRY_CORRUPT`;
other entries load and remain usable.

The current memory sidecar version is 1. A newer sidecar remains readable and searchable but is
read-only. Delete is allowed. Best-effort derived updates are skipped. Direct overwrite, append,
compact, and link-side mutation, when that entry is the applicable target, fail with
`SHARED_FORMAT_READ_ONLY` before changing authoritative bytes.

**Skill metadata.** Missing values for `basedOn`, `role`, `capabilities`, `scaffold`, `createdAt`,
`updatedAt`, `v`, and unknown extension data (`Unknown`) are all null. **Agent metadata.** Missing
`createdAt`, `updatedAt`, `v`, and `Unknown` are all null. Known skill and agent property names are
case-sensitive camelCase. Missing, malformed, or wrong-shape JSON makes metadata absent while a
readable Markdown body can still load; metadata I/O errors do not degrade and instead make the entry
unavailable. Skills and agents do not semantically index attributes or keywords.

Rewriting a body whose metadata is absent sets `createdAt` and `updatedAt` to the same current UTC
round-trip `o` string, emits no `v`, and cannot preserve unknown data from malformed metadata. With
valid existing metadata, rewrite retains the `createdAt` string exactly, sets `updatedAt` to the
current UTC round-trip `o` string, and does not date-validate either string. Current version is 1 and
missing `v` means 1. A newer metadata version leaves the body readable but refuses rewrite with
`SHARED_FORMAT_READ_ONLY`; unknown fields survive every accepted rewrite.

**Bytes.** Durable Markdown and other durable text are UTF-8 without a BOM. Sidecar JSON is indented,
uses camelCase names, omits null properties, and has no terminal newline. NMP/2 uses its ASCII subset
encoded as UTF-8.

## 5. Shared server and exact MCP surface

### 5.1 Common schema vocabulary and limits

The server exposes exactly eleven tools. Schemas are standalone and inherit no omitted fields from
the local surface. Input objects reject unknown properties; output objects contain exactly
the fields declared below. `base` is a canonical base-name string. `path`, `destination`, and
`pathPrefix` are logical memory-address strings, never physical paths. Required strings are nonnull
and nonblank after Unicode-whitespace trimming. Optional strings, when present, obey the same rule.
`query`, `context`, `reason`, entity strings, attribute keys, keywords, and attribute values are at
most 4,096 UTF-8 bytes unless a narrower bound is stated. Logical addresses and prefixes are at
most 4,096 UTF-8 bytes. An empty `excludeTopics` array and an empty `attributeFilter` object
normalize to omission. Empty optional `keywords` and `attributes` contribute no explicit metadata.
An empty filter value array has the presence-test meaning below. `content` and
`intersect.entities` cannot be empty.

`attributes` and `attributeFilter` have JSON shape `object<string,string[]>`. Attribute keys are
nonblank and at most 128 characters. Attribute values are nonnull arrays; write values are nonblank
strings of at most 4,096 characters. In `attributeFilter`, an empty value array means “the key must
exist”; otherwise at least one listed value must match, and every listed key must match. `keywords`
is `string[]`; each member is nonnull, nonblank, and at most 4,096 characters. The existing
`key:value` routing and content extraction semantics remain unchanged.

`excludeTopics` is an array of distinct nonblank strings, each at most 128 UTF-8 bytes, with at
most 100 members. `attributes`, `attributeFilter`, and `keywords` each contain at most 1,000 keys or
members; each value array contains at most 1,000 members. Duplicate array members are invalid.

Every remember/append path passes both the MCP facade and `MemoryWrite` limits: logical address at
most 4,096 UTF-8 bytes; persistent sanitized subject at most 182 characters; nonempty content;
at most 100,000 chunks; at most 5 MiB UTF-8 per chunk; at most 64 MiB UTF-8 total; and at most
8 MiB NMP-encoded bytes per chunk. Remember content is `string[]` and append content is one string.
Null chunks are invalid. Append has exactly one chunk. These limits apply before derivation or I/O.

Shared bases contain persistent entries only. After an address or prefix is parsed through the
current `MemoryAddress`/`PathParser`, any result classified as Ephemeral is rejected with
`SHARED_EPHEMERAL_NOT_SUPPORTED` before store lookup, cache access, fingerprinting, or storage I/O.
This includes every existing ephemeral spelling, including `~...`, reserved `/temp/...`, their
canonical aliases, and any later syntax that the same parser classifies as Ephemeral. The rule
applies to `path`, `destination`, and `pathPrefix`. `shared_search`, `shared_list`, and
`shared_facts` exclude ephemeral entries from unfiltered output. `shared_recall`, `shared_remember`,
`shared_append`, `shared_forget`, `shared_compact`, and either endpoint of `shared_link` cannot target
one. Consequently every applied shared mutation changes at least one authoritative persistent file
and has a nonempty authoritative manifest.

### 5.2 The eleven standalone tool schemas

The exact output DTOs are:

- `SharedBaseSummary = { base: string, root?: string, availability:
  available|unavailable|stale|conflicted, generation: string|null }`. `root` is an absolute string
  and is present only in `shared_bases` output when the enabled registry entry resolves. A nonnull
  generation is exactly 64 lowercase hexadecimal SHA-256 characters. `available` has a nonnull open
  generation; `unavailable` has null; `stale` and `conflicted` retain the last open generation when
  one existed and otherwise have null.
- `SearchHit = { base: string, path: string, kind: entry|chunk, chunk: integer|null, score: double,
  preview: string, attributes: map<string,string[]> }`. `chunk` is at least 1 exactly when `kind` is
  `chunk` and is null when `kind` is `entry`; `score` is finite, never NaN or infinity.
- `FileDriftRef = { file: string, expectedSha256: string|null, actualSha256: string|null }`.
  `file` is the logical file reference recorded in metadata, not an absolute path. Nonnull digests
  are 64 lowercase hexadecimal characters.
- `ListItem = { base: string, path: string, kind: persistent, created: string|null, updated:
  string|null, description: string|null, attributes: map<string,string[]>, keywords: string[], drift?:
  unchanged|changed|missing, files?: FileDriftRef[] }`. Nonnull timestamps are ISO 8601 UTC strings.
  Null is emitted as null, including readable skill/agent metadata with no corresponding timestamp
  and a null memory `updatedAt`; Scrinia never synthesizes a date for an output DTO. `drift`
  and `files` are both present only in drift mode; `files` contains every recorded file reference in
  ordinal order and is empty when none is recorded. `drift` is `missing` when any reference is
  missing, otherwise `changed` when any digest differs, otherwise `unchanged`. The two fields are
  absent in summary and full modes.
- `RecallResult = { base: string, path: string, chunks: string[], attributes:
  map<string,string[]>, keywords: string[], created: string|null, updated: string|null }`. Nonnull
  timestamps are ISO 8601 UTC strings. The durable value is preserved exactly: missing skill/agent
  metadata and null `updatedAt` remain null and never receive a synthetic date. A chunk-selected
  recall still returns `chunks`, containing exactly one string.
- `FactsEntitiesResult = { base: string, op: entities, rows: EntityCount[], truncated: boolean }`, where
  `EntityCount = { entity: string, count: integer }` and `count >= 1`.
- `FactsProfileResult = { base: string, op: profile, entity: string, count: integer, firstSeen:
  string|null, lastSeen: string|null, coOccurrences: EntityCount[], coOccurrencesTruncated: boolean, timeline:
  string[], timelineTruncated: boolean }`. `count >= 1`, the
  nonnull dates are ISO 8601 UTC strings. A null durable event timestamp remains null and is not
  synthesized. These facts timestamps are derived only from semantically indexed persistent memory
  entries and therefore are not populated from skill/agent metadata. `timeline` contains canonical
  logical paths in ascending nonnull event time, followed by null-time paths, with canonical path as
  the ordinal tie-breaker within either group.
- `FactsIntersectResult = { base: string, op: intersect, paths: string[], truncated: boolean }`. Paths are canonical
  logical addresses in ordinal order. `FactsResult` is exactly the discriminated union of those
  three DTOs by `op`.
- `MutationResult = { base: string, path?: string, destination?: string, mutation:
  applied|none, generation: string, manifest: string[] }`. This DTO exists only on mutation success. `path` is present
  for remember, append, forget, compact, and link; `destination` is present only for link. An applied
  mutation has the post-write generation. A successful idempotent no-op has `mutation: none` and the
  unchanged generation. `manifest` is the deterministic authoritative mutation manifest from §7.2
  and is empty for a no-op. Validation and error envelopes never contain a `MutationResult`, so neither
  `MutationResult.path` nor any other result field leaks a rejected target.
- `RefreshResult = { base: string, previousGeneration: string|null, newGeneration: string,
  availability: available, warnings: SharedWarning[] }`. This DTO exists only on refresh success;
  `newGeneration` is nonnull. A failed refresh reports its resulting availability through the
  top-level stable error/warnings and has no `result` DTO.
- `SharedWarning = { base: string, code: string, diagnostic: string }`. It names only an enabled
  base. `diagnostic` is safe text under §11 and never contains a physical path.

The eleven input and output contracts are:

1. **`shared_bases({})`** — the input is exactly an empty object. It returns `SharedBaseSummary[]`
   in ascending base-name order and always includes every enabled base, including unavailable,
   stale, and conflicted bases. It never returns a disabled registry entry. `generation` is null
   when no valid generation has ever opened. Its aggregate status is `success` only when every
   enabled base is `available`, otherwise `partial`. It has no zero-success-to-error conversion.
   Zero enabled bases prevents server startup, so this tool is not callable in that state.
2. **`shared_search(query, base?, limit=20, pathPrefix?, attributeFilter?, context?,
   excludeTopics?)`** — `query` is a required nonblank string; `base` selects one enabled base and
   omission selects all enabled bases; `limit` is an integer from 1 through 100; `pathPrefix` and
   `context` are optional strings; `attributeFilter` and `excludeTopics` use §5.1. It returns
   `SearchHit[]`. Each selected base contributes at most `limit` candidates; the globally merged
   result contains at most `limit`. An ephemeral `pathPrefix` fails with
   `SHARED_EPHEMERAL_NOT_SUPPORTED`; an omitted prefix never allows an ephemeral result.
3. **`shared_list(base?, offset=0, limit=50, mode="summary", excludeTopics?)`** — omitted `base`
   selects all enabled bases; `offset` is an integer >= 0; `limit` is 1 through 100; `mode` is
   exactly `summary`, `full`, or `drift`; `excludeTopics` uses §5.1. It returns `ListItem[]`.
   Pagination is global after deterministic sorting for every mode. List inventory is persistent
   only and cannot contain an ephemeral item.
4. **`shared_recall(base, path, chunk?)`** — `base` and `path` are required; `chunk` is null or an
   integer from 1 through 100,000. It returns one `RecallResult`. `/skill/` accesses only files in
   that selected base. An ephemeral path is refused before lookup. A missing persistent entry or
   requested chunk returns `SHARED_ENTRY_NOT_FOUND`. A
   missing shared skill uses that same code; there is no
   embedded built-in fallback and no `withBuiltin` parameter.
5. **`shared_facts(base, op, entity?, entities?, key?, limit=50, excludeTopics?)`** — `base` is
   required; `op` is exactly `entities`, `profile`, or `intersect`; `limit` is 1 through 100;
   `excludeTopics` uses §5.1. For `entities`, `key` is optional, nonblank, and at most 128 UTF-8
   bytes, and `entity` and
   `entities` are forbidden. For `profile`, one nonblank `entity` is required and `key` and
   `entities` are forbidden. For `intersect`, `entities` is required with at least two nonblank
   and at most 100 distinct members, and `entity` and `key` are forbidden. An entity is `key:value`
   or a bare value. It returns the corresponding `FactsResult` union member. `limit` truncates
   entities rows, profile co-occurrences and timeline independently, or intersect paths. Facts never
   aggregate across bases. A profile with no match returns `SHARED_ENTITY_NOT_FOUND`; an ambiguous
   bare value returns `INVALID_PARAMETER` without selecting a candidate.
6. **`shared_remember(base, path, content, attributes?, keywords?)`** — all first three fields are
   required; `content` is a nonempty `string[]`; metadata uses §5.1. It rejects an ephemeral path
   before storage, selects exactly one base, and returns `MutationResult` with `path` and the exact
   created or modified body-and-sidecar paths. A `/skill/{name}` write creates or replaces only that
   base's skill and never reads or copies an embedded built-in.
7. **`shared_append(base, path, content, attributes?, keywords?)`** — all first three fields are
   required; `content` is one nonblank string; metadata uses §5.1. It rejects an ephemeral path
   before storage, selects exactly one base, and returns `MutationResult` with `path` and the exact
   created or modified body-and-sidecar paths. Append creates the selected-base entry when absent and has
   no built-in skill fallback.
8. **`shared_forget(base, path)`** — both strings are required. It rejects an ephemeral path before
   lookup, selects exactly one base, and
   returns `MutationResult` with `path` and every authoritative body or sidecar path actually deleted. An absent entry returns
   `SHARED_ENTRY_NOT_FOUND` with top-level `mutation: none` and no `result`. Forgetting a missing skill does not inspect or
   report an embedded built-in.
9. **`shared_compact(base, path, keepRecent=0)`** — `base` and `path` are required;
   `keepRecent` is an integer from 0 through 100,000. It rejects an ephemeral path before lookup,
   selects exactly one base, and returns
   `MutationResult` with `path` and the rewritten body-and-sidecar paths. A single-chunk or otherwise unchanged entry is a successful
   idempotent no-op; shared entries are persistent, so an applied compact archives under the
   selected base's derived `versions` class. Because `versions` is derived in §4.1, an archive path
   never enters the authoritative mutation manifest. If a future format registry makes versions
   authoritative, compact must include the exact archive path; the manifest always follows the
   current registry rather than a hard-coded compact exception.
10. **`shared_link(base, path, destination, reason?)`** — the first three strings are required;
     `reason` is optional safe explanatory text, returned only through `instruction` and never
     persisted. Both logical addresses must parse as persistent and belong to the selected base;
     either ephemeral endpoint fails before either lookup or write. It returns `MutationResult`
      with `path`, `destination`, and both endpoints' exact rewritten body-and-sidecar paths. A qualified other-base or local endpoint fails with
     `CROSS_BASE_LINK_FORBIDDEN` before either write. A missing endpoint fails before either write.
11. **`shared_refresh(base)`** — one required `base`; refreshes exactly that enabled base and returns
    one `RefreshResult`. It never mutates authoritative data and envelope mutation is `none`.

There is no `shared_guide`, `shared_restore`, `shared_reconcile`, unprefixed alias, `scopes`
parameter, ambient restore, cross-base link, or implicit mutation target. Shared skills, sessions,
patterns, and agent profiles are visible only through an explicit base and path.

### 5.3 Aggregation, provenance, and exact envelopes

Every result item and every per-base warning contains the stable field `base` with the canonical logical name. `base` never contains a path. Duplicate addresses in different bases are separate items and are never collapsed.

`shared_search` requests at most `limit` candidates independently from each selected base. It merges
candidates and sorts by descending numeric score, ascending `base`, ascending canonical address,
`entry` before `chunk`, then ascending nonnull chunk number, all textual comparisons ordinal. It
truncates the merged sequence to `limit`. Scores are the unchanged shared-server scoring contract;
no base-order boost exists.

`shared_list` sorts the complete selected snapshot by ascending `base` and ascending canonical address before global pagination. Facts never aggregate across bases because `shared_facts` requires `base`.

Shared responses use the following exact YAML top-level shapes. Field names, casing, nullability,
and conditional presence are part of the contract. Every envelope contains `status`, `warnings`,
`instruction`, `followUp`, and `mutation`. `status` is exactly `success`, `partial`, or `error`.
`warnings` is always a `SharedWarning[]`; warnings describe nonfatal per-base degradation and never
replace a fatal `errorCode`. `instruction` is a safe actionable string when the caller must or may
take a next step and is otherwise null. `followUp` is always a string array of logical memory paths
and is empty when no recall is recommended. `mutation` is always `applied`, `none`, or `unknown`.
`errorCode` and `error` are present together only when `status: error`; `errorCode` is one stable code
from §11 and `error` is its safe human-readable diagnostic. `result` or `results` exists only on
`success` or `partial`, never on validation or error. Collections declared in a present DTO serialize
as `[]` or `{}` when empty. No undeclared top-level `content`, `data`, or local-response field is emitted.

Aggregate success and partial use the same fields; `status` distinguishes them. This exact
`shared_search` partial example shows the shape:

```yaml
status: partial
results:
  - base: architecture
    path: /decisions/storage
    kind: chunk
    chunk: 2
    score: 4.25
    preview: Shared bases use explicit refresh.
    attributes:
      topic:
        - storage
warnings:
  - base: team
    code: SHARED_BASE_UNAVAILABLE
    diagnostic: The enabled base could not be opened.
instruction: null
followUp: []
mutation: none
truncated: false
```

`shared_bases` emits the same aggregate fields without `truncated` or `page`, and applies its
deliberate status exception above. `shared_list` emits the
same aggregate fields without `truncated` and with `page: { offset: integer, limit: integer,
returned: integer, total: integer, hasMore: boolean }`.

`shared_search.truncated` is true exactly when more than `limit` merged candidates existed before
truncation. `shared_list.page.total` is the count after base/topic filtering and before pagination;
`returned` is `results.length`; `hasMore` is `offset + returned < total`. An offset at or beyond
`total` succeeds with empty results. Integer addition is checked and an overflowing offset fails
with `INVALID_PARAMETER`. The search ordering in this section and list pagination are deterministic;
no other tool paginates. Facts applies its per-field truncation rule in §5.2 and emits no page DTO.

Targeted reads use one exact `RecallResult` or discriminated `FactsResult`. This recall example shows
the top-level shape:

```yaml
status: success
base: team
result:
  base: team
  path: /patterns/review
  chunks:
    - Review public contracts before implementation.
  attributes: {}
  keywords:
    - contract review
  created: 2026-08-31T10:00:00.0000000Z
  updated: null
warnings: []
instruction: null
followUp: []
mutation: none
```

Targeted mutation success uses `MutationResult`. This applied link example includes every
link-conditional field:

```yaml
status: success
base: team
result:
  base: team
  path: /patterns/review
  destination: /findings/api
  mutation: applied
  generation: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
  manifest:
    - .scrinia/memories/local-topic-memory-findings/api.nmp2
    - .scrinia/memories/local-topic-memory-findings/api.nmp2.meta.json
    - .scrinia/memories/local-topic-memory-patterns/review.nmp2
    - .scrinia/memories/local-topic-memory-patterns/review.nmp2.meta.json
warnings: []
instruction: |-
  git -C "/srv/team-memory" add -A -- '.scrinia/memories/local-topic-memory-findings/api.nmp2' '.scrinia/memories/local-topic-memory-findings/api.nmp2.meta.json' '.scrinia/memories/local-topic-memory-patterns/review.nmp2' '.scrinia/memories/local-topic-memory-patterns/review.nmp2.meta.json'
  git -C "/srv/team-memory" commit -m "docs(memory): update shared Scrinia memory"
  git -C "/srv/team-memory" push
followUp: []
mutation: applied
```

Refresh success has this exact shape:

```yaml
status: success
base: team
result:
  base: team
  previousGeneration: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
  newGeneration: fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210
  availability: available
  warnings: []
warnings: []
instruction: null
followUp: []
mutation: none
```

Targeted error response after schema, base syntax, and enable membership validate:

```yaml
status: error
base: team
warnings: []
error: The durable write outcome could not be verified.
errorCode: DURABLE_WRITE_FAILED
instruction: Refresh this base before another mutation.
followUp: []
mutation: unknown
```

No error shape contains `result`, including mutation and refresh errors. The example is an uncertain
mutation; a proven pre-write refusal uses `mutation: none`. After the complete parameter/schema stage
succeeds, targeted errors include top-level `base` only after canonical-name syntax and enabled
membership both succeed.

Enable-membership error response after schema and name syntax succeed:

```yaml
status: error
warnings: []
error: The selected base is not enabled for this workspace.
errorCode: SHARED_BASE_NOT_ENABLED
instruction: Enable the base in this workspace before selecting it.
followUp: []
mutation: none
```

The envelope stages are exact:

1. **Parameter/schema validation:** validate JSON shape, required/conditional fields, types, ranges,
   and non-base values. Failure returns `status: error`, `INVALID_PARAMETER` (or
   `MALFORMED_ADDRESS`/`INVALID_PATH` where §11 assigns it), safe `error`, remediation or null
   `instruction`, empty `followUp`, applicable nonfatal `warnings`, and top-level `mutation: none`.
   It omits scalar `base` and every `result`/`results`, because enable membership has not been
   validated even when a submitted base string appears canonical.
2. **Base-name syntax:** after valid schema, canonical-name failure returns `INVALID_PARAMETER` with
   the same field-presence rules and omits scalar `base`.
3. **Enabled membership:** after valid schema and syntax, membership failure returns
   `SHARED_BASE_NOT_ENABLED`, omits scalar `base`, and never echoes the disabled submitted name in
   `error`, `instruction`, warnings, diagnostics, logs, or any other field.
4. **Post-membership processing:** only after all three stages succeed may a targeted envelope echo
   scalar `base`. Success includes `result`; errors include `errorCode`/`error` and no `result`.
   Aggregate tools never emit a scalar top-level `base`; provenance remains on result and warning
   items.

Zero-success aggregate error:

```yaml
status: error
warnings:
  - base: team
    code: SHARED_BASE_UNAVAILABLE
    diagnostic: The enabled base could not be opened.
error: No selected shared base is available.
errorCode: SHARED_BASE_UNAVAILABLE
instruction: Restore an enabled base and restart or refresh it.
followUp: []
mutation: none
truncated: false
```

The zero-success example is `shared_search`. `shared_list` replaces `truncated` with the attempted
page object, whose `returned` and `total` are 0 and `hasMore` is false. `shared_bases` never uses this
conversion: it returns every enabled summary with `status: partial`, even when none is `available`.

Except for `shared_bases`, aggregate reads return `success` when every selected base succeeds and
`partial` when at least one succeeds and at least one produces a warning. They preserve all
successful results. If zero bases succeed, the envelope has no `results` field and contains one
warning for every selected enabled base in canonical name order. `shared_bases` instead always
returns all enabled summaries and uses `partial` whenever at least one summary is not `available`.
Warnings never represent disabled or merely registered bases.
Aggregate output never mentions a disabled registry entry. Targeted operations never return
`partial`.

Every response contains `mutation: applied|none|unknown`:

- `applied`: the requested authoritative mutation completed and the post-write generation fingerprint was recorded;
- `none`: no authoritative side effect occurred;
- `unknown`: an I/O failure prevents Scrinia from proving whether the authoritative mutation reached durable storage.

Read, list, facts, bases, and refresh responses report `mutation: none`. On mutation success, the
top-level mutation and `MutationResult.mutation` are equal. On mutation error, only the top-level
field records `none` or `unknown`. Mutation responses always
include operation-based Git guidance when `applied`, regardless of prompt text or any correlation
identifier. They never claim Git state.

Validation returns the first applicable error in this fixed priority: (1) parameter/JSON-schema
shape and range, (2) canonical base-name syntax, (3) membership in the workspace enable string,
(4) registry knowledge for an enabled name, (5) root-owned Git repository/worktree evidence, (6)
logical-address parsing and security, including Ephemeral classification and
`SHARED_EPHEMERAL_NOT_SUPPORTED`, followed by physical-path security, (7) exact managed tracking
block, (8) base availability or conflict,
(9) freshness or format version, (10) operation-specific validation such as missing link endpoint,
then (11) storage I/O. No lower-priority
check leaks information after a higher-priority failure.

## 6. Freshness, operations, and refresh

### 6.1 Open and snapshots

Opening a base performs physical-path validation, authoritative inventory, conflict scanning,
per-entry format validation, SHA-256 fingerprinting, store creation, and snapshot/index loading in
that order. Entry-specific corruption produces the §4.4 warning and omission; an unsafe path or
authoritative conflict prevents the generation. The generation and warning set become visible
atomically only after all steps succeed.

Reads operate against the opened generation. External edits do not update that generation, so reads remain snapshot-stale until `shared_refresh`. Scrinia does not watch, poll, or opportunistically refresh files.

### 6.2 Per-base operation gate

Every shared operation takes a generation lease from a per-base operation gate. Ordinary reads and one mutation use the current generation. Mutations are serialized for that base. Refresh takes the gate's exclusive generation lock, blocks new operations, and waits for all active reads and mutations to finish.

Immediately before each mutation, while holding the mutation lease, Scrinia revalidates the root
and every traversed authoritative path, then recomputes the authoritative fingerprint. A changed
canonical target fails with `SHARED_BASE_UNSAFE_PATH`; a changed fingerprint fails with
`SHARED_BASE_STALE`. Both perform no write and return `mutation: none`. Unsafe-path errors instruct
the caller to repair the base topology; stale errors instruct the caller to run
`shared_refresh(base)`.

After a successful mutation, Scrinia updates the opened in-memory snapshot and derived indexes, recomputes the authoritative fingerprint, and atomically records that snapshot and fingerprint as the new generation before releasing the lease. A failure after durable-write initiation returns `mutation: unknown`, blocks further writes for that generation, and requires refresh. No configurable, optional, warning-only, or automatic stale policy exists.

The pre-write fingerprint check is fail-closed against drift detectable at fingerprint F0. It is not
a portable filesystem transaction or compare-and-swap. External Git or filesystem mutation after
F0 and before the Scrinia operation completes is forbidden and unsupported. Scrinia cannot make
that interval race-free across supported filesystems: a concurrent external change may be
overwritten by Scrinia or may be incorporated into post-write fingerprint F1. This is an explicit
residual data-loss risk, not a guarantee hidden behind “detected race” wording.

After durable-write initiation, Scrinia revalidates every authoritative path and file, scans for
ordered marker triplets, parses every authoritative format, and recomputes F1 before publishing the
new generation. Any invalid format, unsafe path, or conflict found in that pass returns
`mutation: unknown`, marks the base `stale` (or `conflicted` for the marker triplet), and blocks all
further writes until a successful refresh. A valid concurrent external change can pass validation
and become part of F1; accepting it does not prove that no other concurrent bytes were lost.

Operational serialization is therefore mandatory: complete Git and other filesystem operations
only while no shared mutation is running, then call `shared_refresh` for that base before the next
Scrinia mutation or freshness-dependent read. Acceptance tests must cover detectable pre-write drift and
post-write invalidation. They do not claim to prevent the impossible F0-to-F1 race.

### 6.3 Explicit refresh

`shared_refresh(base)`:

1. resolves the one enabled base;
2. acquires its exclusive generation lock;
3. waits for active operations;
4. disposes the old store and all base-scoped caches;
5. re-resolves and revalidates the physical root and containment;
6. inventories and validates authoritative files;
7. computes the fingerprint;
8. opens and indexes a replacement store;
9. atomically publishes the replacement generation and fingerprint;
10. releases the lock.

No operation observes a half-open replacement. A failed refresh leaves the base unavailable for new operations until a later successful refresh or server restart. It does not revive or mutate the disposed generation.

Added or deleted valid authoritative files change the fingerprint, cause pre-mutation stale failure,
and become visible after refresh. Entry-specific malformed or unsupported files follow §4.4:
refresh succeeds with warnings and excludes those entries. Conflict markers anywhere in an
authoritative file only when they form the ordered line-start triplet in §4.4 make the whole refresh
fail with `SHARED_BASE_CONFLICTED`. Derived corruption triggers rebuild and
does not alter the authoritative fingerprint. Every bad target fails closed for its mutation.

## 7. Git boundary and prompt advisory transport

### 7.1 Categorical prohibition

Scrinia never launches `git` for any reason. This applies to libraries, setup, config, migration, both MCP servers, hooks, packaging, tests, and plugins shipped by the project. Scrinia does not inspect or modify the Git index, Git configuration, refs, status, staging state, remotes, credentials, or hooks. Its sole `.git` access is the filesystem-only root evidence in §3.3: directory existence or validation of a worktree marker file's `gitdir:` target directory. It does not read repository objects or other administrative files.

Local-workspace repository discovery remains filesystem-only and may walk parents. Shared-base
registration does not walk parents: the supplied root itself must satisfy §3.3, including reading a
worktree marker file only far enough to validate its sole `gitdir:` line and target directory.

Scrinia writes only its own files, including `.scrinia/.gitignore`. It prints exact commands for the agent to run and never claims that those commands ran or that content is tracked, committed, pushed, clean, or synchronized.

### 7.2 Authoritative mutation manifest and Git guidance

Every successful shared mutation returns a deterministic authoritative mutation manifest. Each
entry is a `/`-separated path relative to the registered repository root, sorted by UTF-8 ordinal
bytes and deduplicated. The manifest contains only exact authoritative files that operation created,
modified, or deleted: NMP bodies and memory sidecars, file-entity bodies and sidecars, and skill or
agent Markdown bodies and sidecars. It contains `.scrinia/.gitignore` only when a successful
`scri shared base add` actually creates or modifies that control file. No other
control file, derived file, directory, wildcard, or directory pathspec may enter a manifest. A
successful no-op has an empty manifest and emits no staging command.

The operation mappings are exhaustive:

- remember and append list the exact body and sidecar created or rewritten;
- forget lists every exact authoritative body or sidecar actually deleted;
- compact lists the rewritten body and sidecar; its `versions` archive is excluded while §4.1
  classifies versions as derived, and is included only if the durable-format registry later makes
  that archive authoritative;
- link lists both endpoints' exact rewritten bodies and sidecars;
- successful `shared base add` lists only `.scrinia/.gitignore` when that command changed the file.

No shared command modifies tracking-block drift. A registered base whose managed block is missing or
drifted is refused. The operator manually restores the exact §8.2 managed block while preserving every
unrelated line, then invokes `shared_refresh` or restarts the shared server. That manual edit is not
a Scrinia mutation and produces no manifest or staging claim.

For a nonempty manifest, the staging guidance is exactly one command with every manifest path as an
individual shell-quoted argument:

```text
git -C "<registered-root>" add -A -- '<manifest-path-1>' '<manifest-path-2>'
git -C "<registered-root>" commit -m "docs(memory): update shared Scrinia memory"
git -C "<registered-root>" push
```

`-A` stages manifest-listed deletions as well as writes. Scrinia never substitutes the store
directory as a staging pathspec and never adds a derived path to this command.
Shell quoting uses the reversible platform rules in §8.3; JSON command output represents argv
directly.

Conflict guidance prints:

```text
# Resolve the listed files manually, then stage and commit them with Git.
# When no shared mutation is running, call shared_refresh for this base.
```

Scrinia never prints a destructive cleanup command as an automatic next step.

### 7.3 Prompt transport belongs only to existing hooks and `scri hint`

The shared MCP server never receives, requests, correlates, caches, or infers an initiating prompt.
No MCP response behavior depends on prompt text, session identity, tool-call timing, or a correlation
identifier. Prompt advisory is solely the existing supported agent hooks invoking `scri hint`.

`scri hint [prompt]` applies this exact algorithm:

1. If a positional prompt was supplied, select it and do not read stdin. Otherwise start one stdin
   read with a two-second deadline and a 65,537-byte buffer. More than 65,536 bytes, invalid UTF-8,
   timeout, or read error emits no output and exits 0.
2. Strip one leading UTF-8 BOM, then trim Unicode whitespace from both ends. Empty text emits no
   output and exits 0.
3. If the first remaining character is `{`, parse exactly one JSON value with only trailing Unicode
   whitespace permitted. It must be an object containing an exact case-sensitive `prompt` property
   whose value is a string. Extra properties are accepted. Malformed JSON, a nonobject, a missing
   property, a nonstring property, or any non-whitespace after the object emits no output and exits
   0. Input beginning with `{` never falls back to raw text.
4. Otherwise select the trimmed text verbatim as the raw prompt.
5. Normalize the selected prompt to Unicode NFKC, lowercase invariantly, and tokenize into maximal
   Unicode letter-or-digit runs. Commit intent is present when any token is exactly `commit`,
   `commits`, `committed`, or `committing`. Push intent is present when any token is exactly `push`,
   `pushes`, `pushed`, or `pushing`. Negation does not suppress either advisory.
6. A supported prompt hook emits the commit reminder when commit intent exists and the push reminder
   when push intent exists, in that order. An unsupported client or client without a verified prompt
   hook emits no advisory. Search, formatting, or any other advisory failure emits no output and
   exits 0. Advisory never blocks a prompt.

No synonym beyond the enumerated inflections, model inference, repository inspection, or MCP
observation changes the result. These reminders are not evidence of a shared mutation and name no
base or root. The shared MCP schema has no prompt field, correlation field, session identifier, or
prompt-dependent response branch.

Phase 4 fixes the generated OpenCode template at
`src/Scrinia.Mcp/plugins/opencode/scrinia.ts`: the prompt transform invokes the command arguments
`hint --client opencode`, sends the prompt on stdin, and replaces the erroneous `save` invocation;
it does not add `--hook`. Claude Code, Codex, Copilot, and OpenCode retain only their already
supported hook transport. A client without a verified prompt hook gets no fallback advisory.

## 8. Workspace tracking and migration

### 8.1 Exact configuration CLI

The release must add only these command forms:

```text
scri shared base add <name> <absolute-root> [--json]
scri shared base remove <name> [--json]
scri shared base list [--json]
scri shared enable <name> [--json]
scri shared disable <name> [--json]
scri tracking status [--json]
scri tracking set local|project-git [--json]
scri tracking migrate-legacy --to local|project-git [--json]
```

`base add` fixes the new shared base to `project-git`. It validates a canonical name, an absolute
existing root, the root's own Git repository/worktree marker under §3.3, non-`.scrinia` root, safe
containment, and all registry root collisions. It then creates or validates `<root>/.scrinia/` and
the exact managed derived-only block in §8.2. Only after those filesystem writes and validations
succeed does it take the user-config lock, reload and revalidate the user schema, name, and all root
collisions, and atomically commit the registry entry. A failure before or during registry commit
leaves the registry unchanged and restores the pre-add `.gitignore` bytes; it removes a newly
created `.scrinia` only when the directory is empty. Recovery restores any interrupted Scrinia-managed
filesystem write before a later add, startup, refresh, or mutation. Scrinia neither invokes Git nor
inspects the index during this transaction. Adding an identical name/root pair is idempotent only
when the Git-root and managed-block validations still pass. Adding an existing name with another
root returns `SHARED_BASE_ALREADY_EXISTS`. `base remove` is an idempotent success when absent and
edits only user config. `base list` enumerates the complete registry in canonical name order and
reports root and tracking-block validation; unlike MCP, it is intentionally user-visible.
After a successful registry commit, `base add` emits a manifest containing
`.scrinia/.gitignore` only when initialization actually created or modified it, prints the exact
`git -C <registered-root> add -A -- <individually shell-quoted manifest path>` command from §7.2,
then every static cleanup command from §8.3 in classifier order. It prints nothing before success,
never emits a directory staging pathspec, and never claims the commands ran. Static derived cleanup
retains the class pathspecs in §8.3.

`enable` requires a known valid registry name, atomically rewrites the exact semicolon scalar, and
is an idempotent success when already enabled. `disable` is an idempotent success even when the name
is absent from the enable string; it never removes the registry entry. Enable/disable preserve the
remaining order and append a newly enabled name at the end. Validation completes before the
workspace lock and replacement, so a failed command changes neither file nor cache.

The `tracking` commands apply only to the local workspace's `.scrinia`; they cannot inspect or alter
a shared base's fixed `project-git` policy. `tracking status` reports `local`, `project-git`, `required`, or `fresh-default-local`, plus
filesystem/config observations and `gitIndexStatus: UNVERIFIED`. `tracking set` validates the
policy, atomically writes the workspace setting and exact managed block in `.gitignore` as one recoverable
transaction, and succeeds idempotently when both already match. In recognized `legacy-clean`,
`legacy-mixed`, or `legacy-manual` state it makes no change and fails with
`LEGACY_MIGRATION_REQUIRED`, directing the
user to `scri tracking migrate-legacy --to <requested-policy>`. It does not clean legacy merge
artifacts; that belongs only to `migrate-legacy`. `migrate-legacy --to` performs the cleanup and
then the same policy transaction. It is also an explicit policy-transition command after migration:
`--to local` is an exit-0 no-op from `local` and a deliberate atomic `project-git`→`local` change;
`--to project-git` is an exit-0 no-op from `project-git` and a deliberate atomic
`local`→`project-git` change. No command accepts an implicit `--to` value.

Human output and `--json` represent the same fields and outcome. JSON is one source-generated
object and writes no ANSI. Every JSON object has `status`, `command`, `changed`, `warnings`,
`gitIndexStatus`, and `gitCommands`, where each `gitCommands` member is one argv string array;
failures also have `error` and `errorCode`. Base add/remove
and enable/disable add scalar `base`; successful base add additionally emits the §7.2 `manifest`,
containing only `.scrinia/.gitignore` when initialization changed it and otherwise empty; base list adds `bases` items with `name`, `root`, and
`validation`; tracking commands add `tracking` and `state`. Unused scalar fields are omitted and
all list fields are emitted, including when empty. Exit codes are exact: `0` success, including idempotent no-op and
successful migration with nonblocking warnings; `2` usage, schema, canonical-name, or invalid
configuration; `3` policy/state refusal such as authoritative data requiring a choice or unsafe
unrecognized managed-file content; `4` lock, permission, filesystem, or atomic-commit failure.
Every command validates only configuration and filesystem state. It never inspects the Git index and labels
index/tracking conclusions `UNVERIFIED`.

#### 8.1.1 Exhaustive CLI failure codes

Every nonzero `--json` outcome emits exactly one code from this table. Human mode uses the same code.
There is no undeclared catch-all. Precedence is the first applicable row within the command after
command-line parsing; where a row names a more specific code, it precedes `IO_FAILURE`.

| Code | Commands and exact condition | Exit class |
|---|---|---|
| `CLI_INVALID_ARGUMENT` | Any listed command: unknown/missing option or positional, invalid `--to`, invalid canonical name syntax, nonabsolute root argument, or other command-line shape/type/range failure before config load | 2 |
| `WORKSPACE_CONFIG_MALFORMED` | `shared enable/disable`, all `tracking` commands: an existing workspace config is malformed, duplicate-valued, wrong-shaped, or contains an invalid flattened scalar; an absent file remains the valid empty workspace configuration | 2 |
| `USER_CONFIG_MALFORMED` | `shared base add/remove/list`, `shared enable`: user config malformed, duplicate-valued, or wrong-shaped | 2 |
| `USER_CONFIG_SCHEMA_UNSUPPORTED` | Any command that reads user config: `schemaVersion` is newer than 1 | 2 |
| `SHARED_BASE_ALREADY_EXISTS` | `shared base add`: name exists with a different root | 3 |
| `SHARED_BASE_NOT_FOUND` | `shared enable`: the canonical name is absent from the registry. V1 `shared base remove` remains idempotent when absent | 3 |
| `SHARED_BASE_ROOT_COLLISION` | `shared base add`: except for the validated identical name/root idempotent pair, the canonical root duplicates another name's root or is nested, overlapping, equal to, or inside a forbidden workspace/registered root | 3 |
| `SHARED_BASE_UNSAFE_PATH` | `shared base add/list`, `shared enable`: root or `.scrinia` containment uses a forbidden link/reparse topology or changes identity during validation | 3 |
| `SHARED_BASE_NOT_GIT_ROOT` | `shared base add/list`, `shared enable`: supplied root itself lacks the valid directory/worktree-file evidence in §3.3 | 3 |
| `SHARED_TRACKING_POLICY_DRIFT` | `shared base add` for an already registered/idempotent pair, `shared base list`, or `shared enable`: the required existing shared block is missing or drifted; any of those commands: markers are duplicated, malformed, or collide with unsafe custom content. First registration may create a wholly absent block in an otherwise safe file | 3 |
| `LEGACY_MIGRATION_REQUIRED` | `tracking set`: recognized `legacy-clean`, `legacy-mixed`, or `legacy-manual` state exists. The command makes no change and directs `scri tracking migrate-legacy --to <requested-policy>` | 3 |
| `MANAGED_FILE_UNSAFE` | `tracking set/migrate-legacy`: managed-file bytes are unattributable, marker ownership is unsafe, or local `.gitignore`, `.gitattributes`, merge config, or recovery journal cannot be safely preserved | 3 |
| `TRACKING_POLICY_REQUIRED` | `tracking status` and local authoritative mutation surfaces: authoritative local data exists without an explicit local policy | 3 |
| `TRACKING_POLICY_INVALID` | `tracking status/set/migrate-legacy`: local Tracking scalar or recognized managed ignore policy is unsupported or disagrees with requested policy | 3 |
| `MIGRATION_MANUAL_CLEANUP_REQUIRED` | `tracking migrate-legacy`: legacy bytes cannot be safely removed automatically but remain readable enough to give manual cleanup guidance | 3 |
| `CONFIG_LOCK_TIMEOUT` | Any mutating config command: interprocess user/workspace/config transaction lock deadline expires | 4 |
| `CONFIG_WRITE_FAILED` | Any mutating config command: temporary write, flush, atomic replacement, rollback, or recovery-journal commit fails after classification | 4 |
| `IO_FAILURE` | Any listed command: otherwise-classified filesystem I/O failure while reading, validating, creating, or listing state; never substitutes for a code above | 4 |

`SHARED_BASE_UNSAFE_PATH`, `SHARED_TRACKING_POLICY_DRIFT`, and `TRACKING_POLICY_REQUIRED` reuse the
identical shared-runtime meaning where the condition is identical. Argument parsing precedes config
reads; user config precedes registry lookup; workspace config precedes enable-list mutation; path,
Git-root, collision, and managed-policy checks precede writes. Within local tracking classification,
unattributable or unsafe managed content maps to `MANAGED_FILE_UNSAFE` before any recognized legacy
state; recognized `legacy-clean`, `legacy-mixed`, or `legacy-manual` then maps `tracking set` to
`LEGACY_MIGRATION_REQUIRED`; `tracking migrate-legacy` continues to its recognized cleanup or maps a
readable but nonautomatable legacy state to `MIGRATION_MANUAL_CLEANUP_REQUIRED`. Lock timeout
precedes attempted replacement; a classified write/recovery failure is `CONFIG_WRITE_FAILED`, not
`IO_FAILURE`.
Warnings are exit 0 only when the requested transaction is complete and no unsafe state remains—for
example preserved unrelated user lines or an absent idempotent remove. A warning never downgrades a
policy drift, unsafe file, manual-cleanup requirement, lock failure, or write failure.

Single-config commands use one locked atomic replacement. `tracking set` atomically updates only
`.scrinia/config.json` and `.scrinia/.gitignore`. `migrate-legacy` atomically updates those files and,
when present and recognized, `.scrinia/.gitattributes` and `.scrinia/merge.config`; a same-directory
recovery journal protects that set but is not policy content. Both commands preserve all unrelated
workspace-config entries and every unrelated line outside recognized Scrinia-managed content in its
original relative order. They stage every changed Scrinia-managed file, replace only after all
validation and staging succeed, and restore original bytes on a failed replacement.
Recovery completes before the next tracking command or memory mutation. Thus exit 2 or 3 has no
side effect, exit 4 leaves either the old complete state or a journal that deterministically
restores it, and exit 0 exposes the complete new filesystem state. Printed Git guidance follows
the successful filesystem commit and is not part of the transaction.

### 8.2 Policies and ignore files

Fresh workspaces with no authoritative data default to `local`; the first authoritative write
atomically persists that value and the local ignore file. A workspace with authoritative data and
no Tracking value returns `TRACKING_POLICY_REQUIRED` for remember, append, forget, compact, link,
skill/agent writes, import, or any other authoritative memory mutation until `tracking set` or
`tracking migrate-legacy` succeeds. Reads remain available.

Under `local`, Scrinia writes this exact LF-terminated two-line managed region in
`.scrinia/.gitignore`, which ignores the entire store including its control files:

```gitignore
# Scrinia tracking policy: local
*
```

On a fresh file this region is the whole file. During a recognized policy transition, unrelated
lines outside the prior Scrinia-managed region or project-Git marker pair remain in their original
relative order. Only recognized Scrinia-managed content is replaced; unsafe or unattributable
ownership is refused before writing.

Under local-workspace `project-git` and for every shared base, Scrinia owns exactly one derived-only
block in `.scrinia/.gitignore`. The marker lines and registry lines are byte-exact and ordered:

```gitignore
# >>> Scrinia managed: project-git derived files >>>
/embeddings/
/indexes/
/exports/
/versions/
/facts.json
/.restore-journal.json
**/*.tmp
/*.lock
# <<< Scrinia managed: project-git derived files <<<
```

The file is UTF-8 without BOM, uses LF line endings, and ends with exactly one LF. On creation the
block is the whole file. During first registration, an existing safe file without either marker
receives the block after its unrelated lines. On update, unrelated user lines outside the markers are preserved in their
original order; line endings are normalized to LF when Scrinia commits the file. Exactly one opening
marker followed by exactly one closing marker is required after registration. A missing block on an already registered shared
base, duplicate or unmatched marker, marker text used by custom content, changed/reordered managed
line, or noncanonical bytes inside the block is unsafe policy drift. Scrinia never overwrites such a
collision and returns `SHARED_TRACKING_POLICY_DRIFT` for shared add/startup/refresh/mutation. A local
workspace tracking command returns `MANAGED_FILE_UNSAFE` for unsafe marker ownership and
`TRACKING_POLICY_INVALID` for a recognized but drifted local policy.

Every shared startup, refresh, and mutation validates the Git-root evidence and this block before it
opens, publishes, or writes a generation. Missing, drifted, or unsafe content makes the base
unavailable with `SHARED_TRACKING_POLICY_DRIFT`; no Git guidance is emitted from an unsafe base, so
guidance can never stage unclassified caches. `embeddings/` contains vector indexes and `indexes/`
contains other rebuildable search indexes. The first-match classifier is the semantic authority; the
generated block and cleanup pathspecs cover exactly its union, and fingerprinting excludes exactly
that union even when Git patterns overlap.

No shared command modifies this block after registration. For an already registered base, any missing or drifted block
is refused. The operator must manually restore the exact block above while preserving unrelated
lines, then invoke `shared_refresh` or restart `scri shared serve`. Scrinia does not emit a mutation
manifest for that manual restoration and does not verify its Git state.

Ignore files do not change files already tracked by Git. Scrinia states this limitation and prints agent-run commands; it does not inspect the index or run those commands.

There is no automatic local-to-shared copy. Changing tracking policy changes only the workspace's local `.scrinia` ignore contract.

### 8.3 Legacy migration state machine

Scrinia's filesystem states and transitions are exhaustive. Each cell below is normative; `change`
means one recoverable atomic transaction over the exact managed files stated in §8.1.1. Every exit-0
result reports `gitIndexStatus: UNVERIFIED` and makes no claim about Git state.

| State | Detection without Git | `tracking status` | `set local` | `set project-git` | `migrate-legacy --to local` | `migrate-legacy --to project-git` |
|---|---|---|---|---|---|---|
| `fresh` | no authoritative data, no Tracking | exit 0, `fresh-default-local` | change to `local`, exit 0 | change to `project-git`, exit 0 | change to `local`, exit 0 | change to `project-git`, exit 0 |
| `policy-required` | authoritative data, no Tracking, no managed legacy files | `TRACKING_POLICY_REQUIRED`, exit 3 | change to `local`, exit 0 | change to `project-git`, exit 0 | change to `local`, exit 0 | change to `project-git`, exit 0 |
| `legacy-clean` | authoritative data plus only recognized managed `.gitattributes` lines and/or byte-equivalent default `merge.config` | exit 0, state `legacy-clean`, print migration direction | `LEGACY_MIGRATION_REQUIRED`, exit 3, no bytes changed | `LEGACY_MIGRATION_REQUIRED`, exit 3, no bytes changed | remove recognized artifacts and change to `local`, exit 0 | remove recognized artifacts and change to `project-git`, exit 0 |
| `legacy-mixed` | managed `.gitattributes` lines coexist with attributable user lines, or an attributable nondefault `merge.config` exists | exit 0, state `legacy-mixed`, print migration direction | `LEGACY_MIGRATION_REQUIRED`, exit 3, no bytes changed | `LEGACY_MIGRATION_REQUIRED`, exit 3, no bytes changed | remove only recognized managed lines, preserve unrelated content, change to `local`, warn, exit 0 | remove only recognized managed lines, preserve unrelated content, change to `project-git`, warn, exit 0 |
| `legacy-manual` | legacy content is safely readable and attributable but cannot be removed automatically without discarding unrecognized bytes | exit 0, state `legacy-manual`, print manual-cleanup direction | `LEGACY_MIGRATION_REQUIRED`, exit 3, no bytes changed | `LEGACY_MIGRATION_REQUIRED`, exit 3, no bytes changed | `MIGRATION_MANUAL_CLEANUP_REQUIRED`, exit 3, no bytes changed | `MIGRATION_MANUAL_CLEANUP_REQUIRED`, exit 3, no bytes changed |
| `legacy-unsafe` | managed-file bytes are unattributable, marker ownership is unsafe, a path is unsafe, or a legacy file cannot be classified/read; malformed config, lock, and I/O failures retain their more specific classifications | `MANAGED_FILE_UNSAFE`, exit 3 | `MANAGED_FILE_UNSAFE`, exit 3, no bytes changed | `MANAGED_FILE_UNSAFE`, exit 3, no bytes changed | `MANAGED_FILE_UNSAFE`, exit 3, no bytes changed | `MANAGED_FILE_UNSAFE`, exit 3, no bytes changed |
| `local` | valid Tracking and exact local managed ignore, with unrelated lines preserved | exit 0, `local` | no-op, exit 0 | deliberate change to `project-git`, exit 0 | no-op, exit 0 | deliberate change to `project-git`, exit 0 |
| `project-git` | valid Tracking and exact derived-only managed block, with unrelated lines preserved | exit 0, `project-git` | deliberate change to `local`, exit 0 | no-op, exit 0 | deliberate change to `local`, exit 0 | no-op, exit 0 |
| `policy-drift` | Tracking exists and marker ownership is attributable, but recognized managed-block bytes disagree with the selected policy | `TRACKING_POLICY_INVALID`, exit 3 | `TRACKING_POLICY_INVALID`, exit 3, no bytes changed | `TRACKING_POLICY_INVALID`, exit 3, no bytes changed | `TRACKING_POLICY_INVALID`, exit 3, no bytes changed | `TRACKING_POLICY_INVALID`, exit 3, no bytes changed |

These cells map every command/state pair. Malformed configuration, lock timeout, classified write
failure, and otherwise-classified I/O retain the §8.1.1 precedence and codes
`WORKSPACE_CONFIG_MALFORMED`, `CONFIG_LOCK_TIMEOUT`, `CONFIG_WRITE_FAILED`, and `IO_FAILURE`; they
supersede a cell only at the stage named by that precedence. `MANAGED_FILE_UNSAFE` is reserved for unattributable or
unsafe content; it never substitutes for the recognized `legacy-clean` or `legacy-mixed` refusal.
For recognized legacy states, `LEGACY_MIGRATION_REQUIRED` precedes policy-choice validation after
the requested `local|project-git` value and workspace configuration have validated, and precedes all
lock/write attempts because `tracking set` performs no transaction.

Git states—untracked, ignored, partially tracked, and fully tracked—are not Scrinia states. The CLI
prints the inspection commands below and reports `UNVERIFIED`; the agent classifies Git state.

The status and migration commands print:

```text
git -C "<workspace-root>" status --short
git -C "<workspace-root>" ls-files
git -C "<workspace-root>" check-ignore -v --no-index .scrinia/config.json .scrinia/facts.json
```

For `local`, guidance prints:

```text
git -C "<workspace-root>" status --short
git -C "<workspace-root>" ls-files
# Remove tracked Scrinia paths only by individually quoted exact file path; never use a .scrinia directory pathspec.
```

For `project-git`, guidance prints:

```text
git -C "<workspace-root>" rm -r --cached --ignore-unmatch -- .scrinia/embeddings
git -C "<workspace-root>" rm -r --cached --ignore-unmatch -- .scrinia/indexes
git -C "<workspace-root>" rm -r --cached --ignore-unmatch -- .scrinia/exports
git -C "<workspace-root>" rm -r --cached --ignore-unmatch -- .scrinia/versions
git -C "<workspace-root>" rm -r --cached --ignore-unmatch -- .scrinia/facts.json
git -C "<workspace-root>" rm -r --cached --ignore-unmatch -- .scrinia/.restore-journal.json
git -C "<workspace-root>" rm -r --cached --ignore-unmatch -- ":(glob).scrinia/*.tmp" ":(glob).scrinia/**/*.tmp"
git -C "<workspace-root>" rm -r --cached --ignore-unmatch -- ":(glob).scrinia/*.lock"
git -C "<workspace-root>" add -A -- '<exact-control-or-authoritative-path-1>' '<exact-control-or-authoritative-path-2>'
git -C "<workspace-root>" status --short
```

Every classifier-derived cleanup command is rooted at the repository root and printed statically in
the exact §4.1 first-match group order, whether or not a matching file exists in the worktree or
index. The temporary-tree command deliberately carries both root and recursive Git glob pathspecs;
their overlap is safe, and together they cover any matching file or directory, every descendant by
ancestry, and root, nested, absent-from-worktree, and indexed `*.tmp` paths. The root-lock pathspec
likewise covers a matching root-child file or directory and every descendant of a matching
directory.
`--ignore-unmatch` makes absent classes nonfatal and therefore covers deleted-but-still-indexed
paths without filesystem discovery.
Scrinia does not inspect the index and cannot verify the command's result. Guidance requires the
agent to inspect status before commit and never asserts the resulting state.

The command contract is an argv sequence, not a concatenated shell string. For example, the first
cleanup command is exactly `git`, `-C`, the absolute workspace root as one argument, `rm`, `-r`,
`--cached`, `--ignore-unmatch`, `--`, `.scrinia/embeddings`. Every pathspec after `--` is one argv
argument. Human output renders each argument with the platform's standard reversible quoting:
POSIX output single-quotes an argument and represents an embedded single quote as `'"'"'`;
PowerShell output single-quotes an argument and doubles embedded single quotes; Windows native
process invocation uses the documented `CommandLineToArgvW` inverse escaping for quotes and runs of
backslashes. JSON `gitCommands` stores each command as an array of argv strings, so consumers never
parse display quoting. `<workspace-root>` in examples means that single absolute-root argument and
is not emitted literally. The project-Git add command substitutes an individually quoted exact
control or authoritative path for each placeholder, ordered by UTF-8 ordinal bytes; it never emits a
directory pathspec, wildcard, or derived path.

Legacy cleanup is byte- and line-conservative. It removes only these exact LF-normalized managed
lines from `.scrinia/.gitattributes`, wherever they occur, and leaves every other line and its
relative order intact:

```gitattributes
*.meta.json merge=scrinia-meta
*.nmp2 merge=scrinia-nmp2
```

It deletes `.gitattributes` only when no bytes remain after those removals. It deletes
`.scrinia/merge.config` only when its bytes equal the default managed output produced by
`SerializeMergeConfig(0.7, "conflict")` plus one LF. Any other `merge.config` remains and produces
a warning. It never deletes a user directory merely because its name contains `conflict`. It prints these
exact agent-run commands and treats a missing key or command failure as the agent's concern:

```text
git -C "<workspace-root>" config --local --unset-all merge.scrinia-meta.driver
git -C "<workspace-root>" config --local --unset-all merge.scrinia-nmp2.driver
```

Migration itself remains successful when recognized filesystem cleanup and policy persistence
succeed; the CLI does not claim either Git key was present or removed.

## 9. Merge-subsystem retirement

The release deletes the active merge architecture in full:

- `src/Scrinia.Merge/` and `tests/Scrinia.Merge.Tests/`;
- the `scri-merge` release artifact and packaging metadata;
- `setup --multi-user` and all merge-driver installation or repair paths;
- MCP and CLI `reconcile` methods, registration, responses, and tests;
- merge configuration keys, merge helpers, merge-only metadata, hooks, fixtures, and conflict scratch conventions;
- the merge-safety skill and current living documentation that teaches merge or reconcile workflows;
- current `docs/multi-user-setup.md`, replaced by shared-base collaboration guidance.

Project-Git conflicts are resolved manually by the agent. Scrinia performs no union, ours/theirs choice, conflict-marker cleanup, stage, retry, or merge.

Dated historical design, planning, and review documents and released changelog entries remain unchanged. Removal tests distinguish those preserved historical references from active code, package metadata, skills, and living documentation.

## 10. Packaging, models, and plugins

### 10.1 One packaged command

One NuGet global-tool package declares the user-facing command `scri`. “One binary” means one packaged user-facing command. The same command supports `scri serve`, `scri shared serve`, and configuration/tracking commands.

The package contains:

- the managed `scri` command and its required managed dependencies;
- embedded prompts, built-in skills, schemas, and setup templates required offline;
- the package readme.

The package contains no `Scrinia.Merge`, `scri-merge`, Vulkan plugin assembly or executable,
RID-native plugin asset, downloaded model, repository, `.scrinia` data, or mutable cache.

### 10.2 Model and plugin placement

Built-in Model2Vec downloads and verified model files live under `SCRINIA_HOME/models`. The global-tool installation directory remains immutable.

The optional Vulkan plugin is excluded from the initial NuGet package and remains a separately installed RID-specific asset. It can include another executable; that does not violate the one-command package contract. Immutable plugin binaries and native libraries live only under `SCRINIA_HOME/plugins/<plugin>/<version>/<rid>/` after separate installation.

Plugin assets are classified as follows:

- immutable plugin executable, managed assemblies, and native libraries: `SCRINIA_HOME/plugins`;
- plugin-downloaded models: `SCRINIA_HOME/models` under collision-safe model identity;
- plugin mutable runtime state that is not derivable from a workspace: a plugin-specific `state/` directory under its version/RID directory;
- workspace/shared facts, vectors, and indexes derived from memory: that store's `.scrinia`, under project-Git derived ignore rules.

No mutable asset is written to `AppContext.BaseDirectory`, the NuGet tool package directory, or a different base's `.scrinia`.

### 10.3 Packaging acceptance

Local-feed smoke tests must pack the release configuration, install into an empty isolated tool path, invoke `scri --version`, start and handshake with both server modes, locate embedded resources, download or reuse Model2Vec under an isolated `SCRINIA_HOME`, prove the Vulkan plugin is absent, restart after user/workspace config edits, and uninstall cleanly. Tests must run without a source checkout in the process working directory.

## 11. Stable error and side-effect contract

Shared behavior uses these stable identifiers:

| Identifier | Meaning and request behavior |
|---|---|
| `UNKNOWN_SHARED_BASE` | An enabled name is absent from the user registry. Selector requests fail whole with `mutation: none`; aggregate startup records that enabled base unavailable. |
| `SHARED_BASE_NOT_ENABLED` | A syntactically valid explicit selector is absent from the workspace enable string. Scrinia returns this before registry lookup, does not say whether it is registered, and does not echo a disabled registry name or root. |
| `SHARED_BASE_UNAVAILABLE` | Root, permissions, or base-open/refresh I/O prevents safe use. Entry-specific malformed data uses `SHARED_ENTRY_CORRUPT`. Explicit requests fail whole; aggregate reads return partial only when another base succeeds. |
| `NO_SHARED_BASES_ENABLED` | The workspace has no enabled names. Shared-server startup rejects before MCP transport; once any name is enabled, a more specific per-base availability or policy code applies if none can open. |
| `SHARED_BASE_STALE` | Pre-mutation fingerprint differs or a prior uncertain mutation blocked the generation. Mutation fails with `none` before write or `unknown` after uncertain I/O. |
| `CROSS_BASE_LINK_FORBIDDEN` | Link endpoints do not belong to the one selected base. The request fails whole with `mutation: none`. |
| `SHARED_BASE_CONFLICTED` | An authoritative UTF-8 text file contains the ordered line-start marker triplet from §4.4. Explicit requests fail whole; aggregate reads report partial only when another base succeeds. |
| `SHARED_BASE_UNSAFE_PATH` | Root or a traversed authoritative file uses a descendant link/reparse target, escapes containment, or changed canonical target. The operation fails with `mutation: none`. |
| `SHARED_BASE_NOT_GIT_ROOT` | A shared root itself lacks the required repository/worktree filesystem evidence. Add/startup/refresh refuses the base without invoking Git. |
| `SHARED_TRACKING_POLICY_DRIFT` | The shared base's exact managed derived-only block is missing, drifted, reordered, duplicated, or unsafe. The base is unavailable and no staging guidance is emitted. |
| `SHARED_EPHEMERAL_NOT_SUPPORTED` | A shared `path`, `destination`, or `pathPrefix` parses as Ephemeral under the current `MemoryAddress`/`PathParser`, including `~...` and `/temp/...`. The request fails before storage with `mutation: none`; aggregate reads never return ephemeral entries. |
| `SHARED_ENTRY_CORRUPT` | One authoritative entry is malformed, unreadable, decode-invalid, lacks a valid nonblank memory-sidecar address, or lacks a nonblank artifact key. The entry is unavailable and never visible; other entries remain usable. |
| `SHARED_FORMAT_UNSUPPORTED` | An authoritative NMP body is not NMP/2. The entry is unavailable and cannot be mutated. |
| `SHARED_FORMAT_READ_ONLY` | A newer sidecar is readable but cannot be a mutation target. |
| `SHARED_ENTRY_NOT_FOUND` | Recall names no entry or chunk, or forget, compact, or either link endpoint names no entry in the selected base. No built-in skill fallback is inspected. |
| `SHARED_ENTITY_NOT_FOUND` | A facts profile entity has no matching entity in the selected base. |
| `TRACKING_POLICY_REQUIRED` | Existing authoritative workspace data has no explicit Tracking value. Local authoritative mutations are blocked pending `tracking set` or migration. |
| `INVALID_PARAMETER` | JSON shape, required field, enum, range, content, metadata, canonical base syntax, or operation-conditional argument is invalid. |
| `MALFORMED_ADDRESS` | A logical memory address cannot be parsed or canonicalized. |
| `INVALID_PATH` | A caller-supplied non-base path is outside its allowed logical or workspace sandbox. |
| `DURABLE_WRITE_FAILED` | Storage failed before or during a durable write; `mutation` distinguishes `none` from `unknown`. |

No undeclared runtime identifier is emitted. CLI-only failures use the exhaustive §8.1.1 table.
Malformed logical addresses use `MALFORMED_ADDRESS`; content,
metadata, fact-argument, schema, and range failures use `INVALID_PARAMETER`; sandboxed non-base path
failures use `INVALID_PATH`. A missing required `base` is `INVALID_PARAMETER`, not
`UNKNOWN_SHARED_BASE`.

Safe diagnostics include canonical base name, logical address, operation, and remediation. They exclude physical paths except in `shared_bases` and mutation Git guidance; they exclude content, credentials, provider secrets, Git metadata, and environment values.

## 12. Security and privacy

Shared content is explicit twice: the user registers a base and the workspace enables its name. Enabling one base does not expose any other registered base. Both servers remain local stdio processes.

The implementation applies `PathContainment` to the registered root and to every traversed
authoritative file before fingerprint reads, ordinary reads that open the file, refresh, and
mutation. It rejects a descendant symbolic link, junction, or reparse point even when its current
target remains inside the root; shared authoritative topology must consist of ordinary directories
and files. It records canonical targets for the generation and rejects a changed target with
`SHARED_BASE_UNSAFE_PATH`. Validation runs on open/refresh and again immediately before every
write. Canonical names become physical paths only through registry lookup.

Portable .NET APIs cannot prove the absence of hard links, bind mounts, mount aliases, or every
filesystem-specific alias. Those are accepted residual risks only for user-controlled local roots.
The design does not claim stronger containment. External topology or content changes racing an
operation are forbidden and unsupported; detectable invalidity fails the operation or blocks the
generation, while a valid concurrent change can be incorporated into F1 and an overwritten change
can be lost. Operators must serialize Git/filesystem work with Scrinia mutations and must not place
shared bases on adversarial or remotely mutable filesystems.

Shared operations use the existing process-wide provider configuration and its unchanged environment/config precedence. This design does not create provider-per-base selection, credentials, routing, or privacy promises. Enabling multiple bases broadens only the files queried by an all-base shared operation; provider behavior remains the existing documented behavior. Shared docs state explicitly when configured providers transmit query or content data outside the machine.

## 13. Delivery phases and plan boundaries

One architecture spec governs five independently reviewable plans. Each living file category has
one owning phase. A later phase consumes earlier output and does not repeat its removal work.

### Phase 1 — Merge retirement

- **Exact inputs:** `src/Scrinia.Merge/**`, `tests/Scrinia.Merge.Tests/**`, solution/project/package
  references, merge setup and process-launch code, reconcile CLI/MCP/Core paths, merge config DTOs,
  merge-only scratch conventions, merge-safety skill, and the troubleshooting passages whose
  purpose is the obsolete merge workflow.
- **Owned outputs:** all obsolete active merge artifacts are removed. Phase 1 owns the minimal
  manual conflict/cleanup replacement in `docs/troubleshooting.md` only under the explicitly named
  `Manual Git conflict cleanup after merge retirement` heading. It also owns only the explicitly
  named merge/reconcile removals in living docs, including the `scri reconcile` section and
  `setup --multi-user` option row in `docs/cli-reference.md`; it does not own an entire documentation
  file or rewrite onboarding, shared technical references, config/tracking, models, plugins, or
  package guidance. Dated history and released changelog entries remain byte-for-byte unchanged.
- **Exit tests:** solution and package enumeration, reflected MCP tools, CLI help, process-launch
  scan, skill/resource manifest, active-artifact merge-term allowlist, minimal-manual-guidance snapshot,
  and historical hash manifest pass. No temporary Phase 1 adapter remains.

### Phase 2 — Config and tracking

- **Exact inputs:** existing `WorkspaceConfig` flattened dictionary and `ConfigJsonContext`,
  `WorkspaceSetup.GetConfigValue` precedence, `DurableFormats`, store layout, `PathContainment`, and
  Phase 1's manual cleanup contract.
- **Owned outputs:** typed/source-generated user config, atomic locked user/workspace config writes,
   exact Tracking/Enabled parsing, registry and enable CLI, tracking CLI/state machine, authoritative
   classification, exact managed block, legacy filesystem cleanup, printed tracking/migration Git
   commands, and only the config/tracking/shared-base command sections of living reference docs.
- **Exit tests:** exact config examples/types and environment spellings, flattened compatibility,
   atomic-write fault injection, transactional base add, root-owned Git marker/worktree-file cases,
   fixed shared `project-git`, exact marker-block bytes, exact derived-set equivalence, every
   migration state/transition and exhaustive CLI error/precedence/exit code, Git status `UNVERIFIED`,
   no Git process or index inspection, and no local-to-shared copy pass. `TRACKING_POLICY_REQUIRED` is the
   permanent behavior for authoritative data without a configured policy, not a compatibility
   adapter. Phase 2 leaves no temporary diagnostic or adapter.

### Phase 3 — Shared server

- **Exact inputs:** final Phase 2 registry, enable parser, containment, authoritative classification,
  and existing local operation DTO/validation/store behavior.
- **Owned outputs:** `scri shared serve`; exactly eleven tools and standalone schemas; shared response
   DTO/YAML envelope; privacy filtering; operation gate, fingerprints, format matrix, refresh,
   errors, base-scoped derived caches, operation-based Git guidance in shared responses, and the
   living shared-MCP technical reference sections. It does not own plugin discovery, model location,
   setup diagnostics, or global-tool packaging sections in those files.
- **Exit tests:** no-base startup failure; disabled-name non-disclosure; all schemas, selectors,
   limits, stage-specific envelopes without error results, error priority, `shared_bases` aggregate
   exception, every exact durable default/version/rewrite row, descendant-link and target-change
   cases, mandatory managed-block validation on startup/refresh/mutation, aggregation ordering,
   stale/uncertain writes, refresh races, and no Git process pass. Phase 3 owns
  every temporary shared adapter and removes all of them before Phase 3 exits; Phase 4 receives only
  final contracts.

### Phase 4 — Hooks and guidance

- **Exact inputs:** final Phase 3 tool/envelope contract, existing `scri hint` and supported hook
  adapters/templates, Phase 1's minimal manual guidance, and the onboarding/multi-user documents
  assigned to Phase 4 in §13.1.
- **Owned outputs:** only the 64 KiB fail-silent prompt-hint transport, deterministic intent
  extension, OpenCode `save`→`hint` fix, supported hook templates and agent artifacts, and expanded
  shared onboarding replacement in README, `docs/getting-started.md`, `docs/multi-user-setup.md`,
  the shared-base threat/onboarding portions of `docs/security.md`, AGENTS/guide, and current
  non-merge skills. Phase 4 consumes Phase 3 response wording unchanged and does not edit the shared
  response contract, shared-MCP technical reference, or any Phase 5 installation/model/plugin section.
- **Exit tests:** positional/raw/exact-JSON prompt transport, malformed/oversize/error exit 0,
  unsupported-client silence, token matrix, MCP prompt isolation, unchanged operation-only mutation guidance,
  privacy/path wording, mandatory shared-project-Git onboarding, shared skill behavior, disjoint
  section-ownership manifest, and historical hash gate pass. Phase 4 replaces
  obsolete merge onboarding only in its assigned documents and never repeats active merge-artifact
  removal or edits Phase 1's troubleshooting heading.

### Phase 5 — Global packaging

- **Exact inputs:** both final server modes, final embedded resources/readme, model resolver, and
  optional plugin installation contract.
- **Owned outputs:** the global-tool package manifest, immutable managed `scri` payload,
  `SCRINIA_HOME/models`, separate `SCRINIA_HOME/plugins` layout, release notes, and package-only
  installation/global-package documentation. Phase 5 exclusively owns every plugin, global-tool,
  model-relocation, user-facing setup, and plugin-diagnostic section touched by this architecture in
  `docs/getting-started.md`, `docs/troubleshooting.md`, `docs/architecture/cli.md`,
  `docs/plugin-authoring.md`, `docs/security.md`, and `docs/cli-reference.md`. This includes replacing
  every living `{exeDir}/models` and `{exeDir}/plugins` location with the applicable
  `SCRINIA_HOME/models` or versioned `SCRINIA_HOME/plugins` contract.
- **Exit tests:** exact package allowlist/denylist, isolated install/update/uninstall, both handshakes,
  resources/readme, mutable model reuse, separately installed plugin assets/state, no Merge or
  Vulkan/native package payload, trim/AOT, and supported-platform smoke tests pass. Tests and a grep
  gate prove that no living documentation or source-generated `--help`, setup message, plugin
  discovery diagnostic, or error text contains stale `{exeDir}/plugins` or `{exeDir}/models` paths;
  dated historical documents and released changelog entries are exempt. No compatibility adapter
  remains.

### 13.1 Exclusive artifact ownership

| Living category | Sole owner |
|---|---|
| Merge projects/tests/setup/reconcile/config/skill/package references; explicitly named merge/reconcile removals; only `docs/troubleshooting.md` heading `Manual Git conflict cleanup after merge retirement` | Phase 1 |
| User/workspace config, registry/enable/tracking/migration CLI, ignore files, classification, and only corresponding command/config sections in living references | Phase 2 |
| Shared server, eleven MCP tools, shared DTOs/adapters, format/freshness/containment runtime, operation-based Git response guidance; shared-MCP sections of `docs/architecture/overview.md`, `docs/architecture/cli.md`, and `docs/architecture/core.md` | Phase 3 |
| Hook/hint transport, generated OpenCode template and agent artifacts; shared onboarding replacement in `README.md`, `docs/getting-started.md`, `docs/multi-user-setup.md`, shared threat guidance in `docs/security.md`, `AGENTS.md`, `src/Scrinia.Mcp/prompts/guide.md`, supported hook templates, and current non-merge skills | Phase 4 |
| NuGet global-tool manifest/content, model/plugin installation and relocation, and corresponding setup/plugin diagnostics in `docs/getting-started.md`, `docs/troubleshooting.md`, `docs/architecture/cli.md`, `docs/plugin-authoring.md`, `docs/security.md`, and `docs/cli-reference.md`; new `docs/installation.md`, new `src/Scrinia/package-readme.md`, and unreleased `CHANGELOG.md` section | Phase 5 |
| Dated `docs/superpowers/**`, `docs/planning/**`, `docs/reviews/**`, released changelog entries | No implementation phase; preserved history |

Ownership is by disjoint section/line subject when a file has more than one owner. In
`docs/troubleshooting.md`, Phase 1 owns only the new manual merge-conflict cleanup heading; Phase 5
owns `"Model not found" / Semantic search not working`, `Vulkan plugin not loading`, `Plugin crashes /
"degraded to BM25-only"`, and `Plugin not found despite being installed`, plus their setup/plugin
diagnostics. In the other five Phase 5 documents named above, Phase 5 owns only global-tool
installation, model location/download/setup, plugin location/discovery/installation/security, and
related diagnostics; Phase 4 owns shared onboarding/security replacement and Phase 2/3 own their
command/runtime reference sections. No phase edits another phase's lines. Unlisted living documents,
including `docs/architecture/embeddings.md`, are unchanged.

## 14. Complete acceptance-test matrix

Acceptance covers the full contract, not representative examples:

1. **Configuration:** typed user schema and source generation; exact
   `Environment.SpecialFolder.LocalApplicationData/Scrinia` default; `SCRINIA_HOME`; flattened
   workspace dictionary/`ConfigJsonContext` compatibility; exact Tracking environment precedence;
   no Enabled environment override; absent Enabled key as zero enabled; disabling the final base
   atomically removes the key; empty scalar and semicolon empty/duplicate/case failures; shared
   startup returns `NO_SHARED_BASES_ENABLED` for the absent key; atomic locks and fault recovery;
   malformed/newer user schema; restart semantics; shared policy is fixed `project-git` and cannot
   be overridden by user/workspace config while local-workspace Tracking remains `local|project-git`.
2. **Privacy and startup:** no-base startup fails; `shared_bases` includes enabled-unavailable bases;
   disabled names/roots are absent from every MCP result, warning, error, and captured log; only
   user-run `shared base list` enumerates the registry.
3. **Paths and shared registration:** every root layout and overlap; root-at-`.scrinia`; root whose
   ancestor but not root has `.git`; valid root `.git` directory; valid and malformed worktree
   `.git` text files; missing target directory; workspace equality; transactional base add proves
   the registry changes only after `.scrinia` and managed-block commit succeeds; fault recovery;
   existing unsafe custom marker collision is preserved and refused; unrelated lines outside the
   exact marker pair are preserved; no Git process or index inspection;
   descendant symlink/junction/reparse points; changed canonical targets; checks before fingerprint,
   open, refresh, and write; hard-link/bind-mount residual-risk wording contains no impossible claim.
4. **Formats and conflicts:** every row of §4.4 and every exact default in §4.5 for NMP body, memory
   `ArtifactEntry`, skill metadata, and agent metadata; missing/null/default values, case sensitivity,
   `fields` precedence, provenance-unknown semantics, missing/null/blank/invalid `address` and
   missing/null/blank `artifactKey` always omitted with `SHARED_ENTRY_CORRUPT`, malformed/missing/wrong-shape versus I/O,
   unknown-field preservation, accepted rewrite bytes/timestamps/version behavior, newer readable
   read-only operations including delete and skipped derived updates, UTF-8/BOM/newline goldens;
   no numeric upgrade; entry-specific warning/visibility; bad-target
   mutation; refresh success; UTF-8 NMP text scanned before decode; ordered line-start marker triplet
   detection in NMP/JSON/Markdown/sidecars; non-detection for embedded, incomplete, out-of-order, or
   non-line-start substrings; no filename, `.git`, duplicate, or scratch heuristic.
5. **Tools:** reflected surface is exactly eleven names; every standalone schema, conditional facts
    argument, selector, range/default, write/metadata limit, shared-skill no-fallback rule, unknown
    property refusal, and cross-base link rule has positive and negative tests. Schema and
    table-driven address tests cover every current `MemoryAddress`/`PathParser` Ephemeral spelling,
    including `~...` and `/temp/...`, for `path`, `destination`, and `pathPrefix`: each fails with
    `SHARED_EPHEMERAL_NOT_SUPPORTED` before storage. Search/list/facts inventory tests prove no
    ephemeral result can escape, and every applied shared mutation has authoritative files.
6. **DTOs, envelopes, and aggregation:** source-generated serialization and exact YAML goldens for
   every DTO and each success/partial/error shape; all conditional fields and nullability; finite
    scores, nullable timestamp and digest constraints, drift file refs, facts union discriminators, mutation
   generations, refresh warnings, pagination and truncation; scalar and item provenance; all
   `mutation` values; deterministic search/list ordering; duplicate addresses; zero-success
   `SHARED_BASE_UNAVAILABLE` omits results and warns only for enabled selected bases; parameter/schema,
   syntax, and enable-stage failures omit scalar base and every result DTO; disabled names never
   echo; post-membership errors echo only the enabled scalar base and omit result; fixed error
   priority; deterministic authoritative mutation manifests for remember, append, forget, compact,
    and both link endpoints; null `ListItem`/`RecallResult` timestamps for readable skill/agent
    metadata and null `updatedAt` round-trip as null without date synthesis; exact individually
    quoted `add -A` argv; no directory, wildcard, control
   file other than applicable `.scrinia/.gitignore`, or derived path in a manifest; a deliberately
   pretracked derived file never appears in mutation staging guidance; `shared_bases` always returns every enabled summary, is `partial` for any summary that is not
   `available`, and never converts a result in which no summary is `available` to `error`.
7. **Freshness/concurrency:** added/deleted/renamed authoritative files, derived-only changes,
   concurrent reads, serialized Scrinia mutations, refresh waiting/swap/failure, detectable F0
   topology/file drift, post-write validation of every authoritative file, invalid post-write format
   producing `unknown` and blocked writes, valid concurrent change acceptance into F1, uncertain
   writes, and stale snapshot reads. Tests and test names state that the forbidden F0-to-F1 external
   race cannot be prevented portably; no test claims race prevention or no-data-loss atomicity.
8. **Prompt transport:** positional precedence without stdin read; raw stdin; BOM stripping and
   Unicode trimming; empty input; exactly 65,536-byte acceptance and 65,537-byte rejection;
   two-second timeout, read failure, and invalid UTF-8; exact one-object JSON with trailing
   whitespace and extra-property acceptance; malformed/nonobject/missing/nonstring and no raw-JSON
   fallback; NFKC/token boundaries; every commit/push inflection, punctuation, case, negation, and
   false-positive near miss; advisory failures silently exit 0; unsupported/no-hook clients;
   OpenCode template invokes `hint` instead of `save`; shared MCP has no prompt/correlation field;
   mutation Git guidance remains operation-based.
9. **Tracking/migration:** fresh local default; authoritative no-policy write block and read access;
   exact commands/args/JSON/exit codes/idempotency; all state-machine transitions; atomic fault cases;
   only two managed gitattributes lines removed; empty-only deletion; byte-exact merge.config rule;
   user conflict-named directories retained; exact two config-unset argv sequences; every static
   classifier-derived cleanup command includes `--ignore-unmatch` and is printed even when its path
   does not exist or is not indexed; root and nested temporary paths use safe static Git glob
    pathspecs; file and directory suffix cases plus descendants by ancestry for both temporary and
    root-lock trees; argv and root/pathspec quoting goldens; Git status `UNVERIFIED`
   and no claim that cleanup changed the index; exact project-Git marker lines, UTF-8/no-BOM/LF/final
   newline rules, anchored root patterns, startup/refresh/mutation drift refusal, and exact set
   equivalence among the ordered first-match classifier union, managed ignore coverage, fingerprint
   exclusions, and static root-relative cleanup coverage. Overlapping Git patterns are allowed;
    every filesystem path receives one first-match semantic class; authoritative files, directories,
    and path segments matching the reserved `.tmp` or root-child `.lock` suffix rules are rejected;
    deleted-but-indexed derived paths are covered. Every state-machine cell has a status, code,
    exit class, and byte-change assertion; both `migrate-legacy` same-policy transitions are exit-0
    no-ops, both cross-policy transitions are deliberate atomic changes, unrelated config entries
    and lines survive, and Git state remains `UNVERIFIED`. Registered shared policy drift has no
    command that modifies drift: tests require manual exact-block restoration followed by refresh or restart, and
    `.scrinia/.gitignore` appears in a manifest only for successful `shared base add`.
10. **Removal, ownership, history, and traceability:** Phase 1 owns active merge removal and its one
     minimal manual-cleanup document; Phase 2 owns config/tracking docs; Phase 3 owns the complete
     shared response contract and operation guidance and has no temporary adapter at exit; Phase 4
     owns only hint transport, agent artifacts, and onboarding/multi-user replacement and cannot
      alter Phase 3 wording; Phase 5 owns all global-tool/plugin/model relocation and setup-diagnostic
      sections in the six named docs. Troubleshooting ownership is disjoint by the exact heading and
      line subjects in §13.1. Tests and grep reject `{exeDir}/plugins` and `{exeDir}/models` in living
      docs, source help, setup messages, and plugin diagnostics while exempting dated history. A
      manifest assigns every changed living section once; historical hashes remain unchanged; every ledger decision links
     to a review-record section whose source is declared, and every reviewer objection has an
     explicit closure without an invented approval.
11. **Git/package/plugins:** process instrumentation proves no Git process; exact global package
    allowlist excludes Merge, Vulkan, and native assets; mutable Model2Vec is under
    `SCRINIA_HOME/models`; separate plugin binaries/native assets are immutable and plugin models,
    runtime state, workspace/base vectors, and facts follow §10 classification.
12. **Build gates:** full tests, release build, NuGet pack validation, isolated tool install and both
    handshakes, embedded resources/readme, trim/AOT analysis, and supported-platform smoke tests.
13. **CLI errors:** every command/error pair in §8.1.1 has human and `--json` goldens, precedence and
   exit-class tests; every nonzero JSON failure uses one declared code; warning-only exit 0 is
   accepted only with no unsafe state; recognized `legacy-clean` and `legacy-mixed` make
   `tracking set` change no bytes and return `LEGACY_MIGRATION_REQUIRED` with the exact
   `migrate-legacy --to` direction; unattributable/unsafe content remains `MANAGED_FILE_UNSAFE`;
   `IO_FAILURE` covers only otherwise-classified I/O.

## 15. Decision ledger and review trace

The provenance record is
[`../reviews/2026-08-31-shared-memory-global-tool-spec-review.md`](../reviews/2026-08-31-shared-memory-global-tool-spec-review.md).
This ledger records where each governing decision came from; it does not approve itself.

| Decision | Review source |
|---|---|
| One `SCRINIA_HOME` owns registry, models, and separately installed plugins. Shared names are canonical identifiers mapped to absolute roots and enabled by workspace name. | [Round 1 context](../reviews/2026-08-31-shared-memory-global-tool-spec-review.md#round-1-context-and-contract-gaps) |
| Absent Enabled means zero; disabling the last base removes the key; empty is invalid; startup fails `NO_SHARED_BASES_ENABLED`. | [R1-02](../reviews/2026-08-31-shared-memory-global-tool-spec-review.md#r1-02-zero-enabled-bases) |
| Search/list aggregate by default; recall/facts/mutations/refresh target one enabled base; links stay within that base; shared mode has no guide, restore, reconcile, ambient skill fallback, or implicit target. | [R1-01](../reviews/2026-08-31-shared-memory-global-tool-spec-review.md#r1-01-complete-tool-and-response-contracts), [R3-04](../reviews/2026-08-31-shared-memory-global-tool-spec-review.md#r3-04-eleven-complete-tool-contracts) |
| Shared DTOs and YAML envelopes have exact standalone fields, pagination/truncation, privacy-preserving conditional base presence, and no placeholder payload. | [R1-01](../reviews/2026-08-31-shared-memory-global-tool-spec-review.md#r1-01-complete-tool-and-response-contracts) |
| Prompt advisory is hook-only through the exact fail-silent `scri hint` parser; shared MCP has no prompt or correlation field; OpenCode uses `hint`, not `save`. | [R1-03](../reviews/2026-08-31-shared-memory-global-tool-spec-review.md#r1-03-exact-hint-parser-and-token-rules) |
| Snapshot writes use fail-closed F0 checks but cannot prevent the forbidden F0-to-F1 external race; operational serialization is mandatory and residual data loss is explicit. | [R2-01](../reviews/2026-08-31-shared-memory-global-tool-spec-review.md#r2-01-freshness-race-and-residual-risk) |
| Only an ordered Git marker triplet at line starts conflicts a base; UTF-8 NMP text is scanned before decode; `.git`, names, and scratch conventions are irrelevant. | [R2-02](../reviews/2026-08-31-shared-memory-global-tool-spec-review.md#r2-02-conflict-taxonomy), [R3-03](../reviews/2026-08-31-shared-memory-global-tool-spec-review.md#r3-03-nmp-text-scan-order) |
| Scrinia never launches or inspects Git; it prints the complete static classifier-derived cleanup argv with `--ignore-unmatch` and cannot verify index results. | [R2-03](../reviews/2026-08-31-shared-memory-global-tool-spec-review.md#r2-03-static-git-cleanup-contract) |
| Tracking policy absence for an existing authoritative store permanently returns `TRACKING_POLICY_REQUIRED`; it is not an adapter. | [R3-01](../reviews/2026-08-31-shared-memory-global-tool-spec-review.md#r3-01-exclusive-phase-ownership) |
| Phases 1–5 have exclusive artifact ownership; Phase 3 owns final shared response wording and removes adapters, while Phase 4 owns only hints, agent artifacts, and onboarding replacement. | [R3-01](../reviews/2026-08-31-shared-memory-global-tool-spec-review.md#r3-01-exclusive-phase-ownership) |
| One NuGet global-tool command ships both server modes; Vulkan remains a separate RID asset; existing provider precedence is unchanged. | [Round 1 context](../reviews/2026-08-31-shared-memory-global-tool-spec-review.md#round-1-context-and-contract-gaps) |
| Acceptance includes privacy, zero-key config, parser boundaries, DTO/YAML goldens, race limits, marker taxonomy, absent-path cleanup commands, ownership, and trace links. | [R3-05](../reviews/2026-08-31-shared-memory-global-tool-spec-review.md#r3-05-acceptance-and-final-self-review) |
| Dated historical artifacts remain unchanged after any later approval and implementation. | [R3-02](../reviews/2026-08-31-shared-memory-global-tool-spec-review.md#r3-02-review-provenance) |
| Shared bases are fixed `project-git`; base add validates the supplied Git root and exact managed block transactionally without invoking Git; all later shared access revalidates it. | [R4-01](../reviews/2026-08-31-shared-memory-global-tool-spec-review.md#r4-01-fixed-shared-tracking-and-one-to-one-derived-policy) |
| Validation/error envelopes never contain result DTOs; base provenance begins only after schema, syntax, and enabled-membership validation. | [R4-02](../reviews/2026-08-31-shared-memory-global-tool-spec-review.md#r4-02-error-stage-and-shared-bases-contracts) |
| `shared_bases` is the aggregate exception and always returns every enabled summary, using `partial` for any base that is not `available`. | [R4-02](../reviews/2026-08-31-shared-memory-global-tool-spec-review.md#r4-02-error-stage-and-shared-bases-contracts) |
| Durable compatibility behavior and every CLI failure code are enumerated rather than delegated to implementation behavior or a catch-all. | [R4-03](../reviews/2026-08-31-shared-memory-global-tool-spec-review.md#r4-03-exact-compatibility-and-cli-errors) |
| Phase 5 owns all global-tool, plugin, model-relocation, setup, and plugin-diagnostic sections in the six named living docs; shared onboarding remains Phase 4 and merge-only cleanup remains Phase 1. | [R4-04](../reviews/2026-08-31-shared-memory-global-tool-spec-review.md#r4-04-documentation-ownership-and-approval-provenance) |
| Every successful shared mutation returns only its exact authoritative changed/deleted paths and stages them individually with `add -A`; broad store staging and derived manifest entries are forbidden. | [R5-01](../reviews/2026-08-31-shared-memory-global-tool-spec-review.md#r5-01-exact-authoritative-mutation-manifests) |
| Derived paths use ordered first-match semantics; recursive temporary trees and root-lock trees are distinct precedence groups, while overlapping Git patterns satisfy exact set equivalence without a bijection. | [R5-02](../reviews/2026-08-31-shared-memory-global-tool-spec-review.md#r5-02-ordered-derived-classifier-and-exact-set-equivalence) |
| Missing/null/blank/invalid memory identity fields deterministically hide only that entry with `SHARED_ENTRY_CORRUPT`. | [R5-03](../reviews/2026-08-31-shared-memory-global-tool-spec-review.md#r5-03-corrupt-memory-sidecar-identity-is-never-visible) |
| Recognized legacy-clean or legacy-mixed state makes `tracking set` fail without change using `LEGACY_MIGRATION_REQUIRED`; unsafe content remains `MANAGED_FILE_UNSAFE`. | [R5-04](../reviews/2026-08-31-shared-memory-global-tool-spec-review.md#r5-04-recognized-legacy-state-requires-explicit-migration) |
| Durable DTO timestamps are nullable wherever the underlying readable format permits null; null is preserved without synthesis. | [R6-01](../reviews/2026-08-31-shared-memory-global-tool-spec-review.md#r6-01-nullable-durable-timestamps) |
| Shared bases are persistent-only; every parser-classified Ephemeral address is refused before storage and aggregate reads exclude ephemeral entries. | [R6-02](../reviews/2026-08-31-shared-memory-global-tool-spec-review.md#r6-02-shared-bases-are-persistent-only) |
| Temporary and root-lock suffix rules classify matching files or directories and all descendants by ancestry. | [R6-03](../reviews/2026-08-31-shared-memory-global-tool-spec-review.md#r6-03-temporary-and-lock-suffixes-define-trees) |
| Every tracking command/state cell is defined; same-policy migration is an exit-0 no-op and opposite-policy migration is a deliberate atomic transition with unrelated content preserved and Git unverified. | [R6-04](../reviews/2026-08-31-shared-memory-global-tool-spec-review.md#r6-04-complete-tracking-transition-matrix) |
| Registered-base policy drift has no modifying command: it requires manual exact-block restoration followed by refresh or restart, and only successful base add may manifest `.scrinia/.gitignore`. | [R6-05](../reviews/2026-08-31-shared-memory-global-tool-spec-review.md#r6-05-no-automatic-tracking-block-repair) |
| All dated source labels are declared; shared fixed-project-Git/base registration traces to R4-01, classifier semantics to R5-02, and no final approval is claimed. | [R6-06](../reviews/2026-08-31-shared-memory-global-tool-spec-review.md#r6-06-complete-source-labels-and-decision-references) |

## 16. Historical-document policy

This file is revised now to incorporate the round-6 approval-gate corrections and user-selected
product decisions. It remains proposed until an external reviewer returns `APPROVE`. If approved and
committed, it becomes a dated historical architecture artifact. Implementation changes only living
documentation—README, top-level guides, architecture references, CLI reference, security,
troubleshooting, agent guidance, and current skills—plus new release or migration notes. It does not
rewrite this file, other dated files under `docs/superpowers/`, `docs/planning/`, or `docs/reviews/`,
or released changelog entries.

## 17. Spec self-review

- **Placeholders — author check complete; external verification pending:** No known unresolved marker, branch, deferred decision, or empty-object payload remains.
- **Config examples/types — author check complete; external verification pending:** User config is typed/schema-versioned; workspace config is the
  existing flattened `Dictionary<string,string>` with scalar Tracking and semicolon Enabled; exact
  Tracking environment names and LocalApplicationData default are stated.
- **Codes — author check complete; external verification pending:** Every runtime and CLI format, availability, path, freshness, selector, tracking,
  config, migration, and storage outcome has one declared code; `IO_FAILURE` is limited to otherwise-
  classified I/O. Recognized legacy state and unsafe managed content have distinct codes and fixed
  precedence.
- **Mutation manifests — author check complete; external verification pending:** Every successful shared mutation maps to exact root-relative
  authoritative files; deletions and both link entries are represented; derived files and broad
  directory staging are excluded; the successful-`shared base add` `.gitignore` exception is
    explicit, and registered-base drift has no modifying command.
- **Derived classifier — author check complete; external verification pending:** First-match precedence assigns one semantic class per path;
  temporary and root-lock suffixes classify file-or-directory trees and descendants by ancestry,
  root locks are not duplicated, authoritative path segments matching reserved suffix rules are
  prohibited, and ignore/cleanup/fingerprint consumers satisfy exact set equivalence without a
  pattern bijection.
- **Entry identity — author check complete; external verification pending:** Missing, null, blank, or invalid memory identity never yields a visible
  entry and deterministically reports `SHARED_ENTRY_CORRUPT` without affecting valid neighbors.
- **Persistent shared boundary and timestamps — author check complete; external verification pending:** All shared address-bearing inputs reject
  parser-classified Ephemeral addresses with `SHARED_EPHEMERAL_NOT_SUPPORTED`; aggregate inventory
  never returns ephemeral entries. `ListItem` and `RecallResult` preserve nullable durable timestamps
  exactly and never synthesize dates.
- **Tracking transitions — author check complete; external verification pending:** Every state/command cell, managed file, atomicity rule, no-op,
  deliberate policy change, unrelated-line preservation rule, error, exit class, and
  `gitIndexStatus: UNVERIFIED` outcome is explicit.
- **Modal ambiguity — author check complete; external verification pending:** Normative behavior uses required outcomes; no optional stale policy,
  implicit selector, automatic refresh, inferred tracking choice, prompt correlation, or per-base
  provider behavior remains.
- **Phase ownership — author check complete; external verification pending:** Every living section has one owner; Phase 1 merge-only cleanup, Phase
  4 onboarding, and Phase 5 plugin/global-tool/model/setup diagnostics are disjoint, including within
  troubleshooting.
- **Contradictions — author check complete; external verification pending:** Snapshot reads coexist with fingerprint-gated writes; entry corruption
  permits warning refresh while conflict markers fail the base; enabled-unavailable visibility does
  not expose disabled registry data; hook advisory is separate from operation-based MCP guidance;
  one managed package coexists with a separately installed plugin.
- **Scope — author check complete; external verification pending:** The document governs five independently reviewable plans under one architecture and excludes implementation detail unrelated to shared memory, tracking, merge retirement, guidance, or packaging.
- **Approval provenance — author check complete; external verification pending:** The status is `Proposed`, the review outcome is pending, and no
  architecture approval is claimed without an external `APPROVE` result. The separate provenance
  record and linked ledger account for every added norm. Provider-
  per-base behavior and prompt-to-MCP transport are absent. Exact schemas, ordering, locks,
  fingerprints, migration states, package contents, and tests add no unapproved product surface.
