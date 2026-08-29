# Unified Memory Addressing and Write Validation

**Status:** Approved architectural design  
**Date:** 2026-08-29  
**Scope:** First track of the remediation sequence

## 1. Goals

This track establishes one public address model for every memory operation and one shared write-validation boundary. It is a public breaking API change, not an internal compatibility layer or transitional implementation.

The implementation must:

1. Introduce an immutable public `MemoryAddress` value type with explicit `Persistent` and `Ephemeral` kinds.
2. Canonicalize `/temp/foo` and `~foo` to the same ephemeral address.
3. Reject malformed, traversal-prone, rooted, invalid, and overlong addresses before they enter core memory flows.
4. Remove `file://` support from public and internal memory flows. Recall accepts logical addresses or inline NMP/2 text, never file URIs.
5. Replace the public `ArtifactEntry.Uri` property with `ArtifactEntry.ArtifactKey`, a store-generated opaque, portable, store-relative identity.
6. Move address and write-size validation into core so CLI, MCP, stores, plugins, and test doubles share the same limits and semantics.
7. Preserve an explicit policy split: CLI memory commands remain persistent-only, while MCP tools and resources support the supported ephemeral operations.
8. Make bundles portable by regenerating local physical locations rather than persisting machine-specific paths.

## 2. Non-goals

This track does not redesign durable-write transactions, plugin state machines, retrieval indexing or ranking, embedding behavior, unrelated documentation, or historical planning/review artifacts. It does not add a server/HTTP surface, restore remote or URI-based CLI options, or introduce a compatibility phase that retains the old public API. It does not change memory content semantics beyond the shared required-content and size limits needed to validate writes.

## 3. Terminology

- **Logical address:** A validated `MemoryAddress` identifying a memory independently of its storage location.
- **Persistent address:** A logical address whose value is a normal memory path and whose data is durable in the configured store.
- **Ephemeral address:** A logical address whose value is in the ephemeral namespace. It is process/session scoped according to the existing ephemeral-store behavior.
- **Canonical address:** The normalized value exposed by `MemoryAddress`; equivalent spellings have one equality and serialization representation.
- **Artifact key:** A store-generated opaque identity for an artifact, portable between stores and independent of physical directories.
- **Inline NMP/2 text:** A complete NMP/2 artifact supplied directly to recall, distinguished from an address by the parser. It is not a URI and is not resolved through the filesystem.
- **Adapter:** CLI, MCP, resource, plugin, or other boundary code that parses raw input and presents core errors to its caller.

## 4. Public API model

### 4.1 `MemoryAddress`

`MemoryAddress` is a public immutable value type. Its constructor/factory is the only supported way to create an address, ensuring that equality, hashing, and serialization operate on canonical data.

```csharp
public enum MemoryAddressKind
{
    Persistent,
    Ephemeral
}

public readonly record struct MemoryAddress
{
    public string Value { get; }
    public MemoryAddressKind Kind { get; }

    public static MemoryAddress Parse(string raw);
    public static bool TryParse(string? raw, out MemoryAddress address,
        out MemoryAddressError error);
}
```

The public exception/result mechanics may follow repository conventions, but the contract is fixed: invalid input must not produce a partially initialized address, `Parse` throws an error carrying `MalformedAddress`, and `TryParse` returns a structured failure carrying that category. The type is immutable, compares by canonical kind and value, and never exposes a physical path or URI.

Canonical examples:

| Input | Result |
|---|---|
| `/notes/design` | persistent value `/notes/design` |
| `/temp/foo` | ephemeral canonical value `foo` |
| `~foo` | ephemeral canonical value `foo` |
| `/temp/foo` and `~foo` | equal addresses |

The canonical textual form for an ephemeral address should be stable and unambiguous in public output (recommended form: `~foo`). Persistent values retain their logical leading slash. No API may infer kind by string concatenation after construction.

### 4.2 Store and request contracts

All public memory-facing contracts use typed values. Exact overload names should follow the existing interfaces, but the shape is:

```csharp
public interface IMemoryStore
{
    ValueTask<ArtifactEntry?> ReadAsync(MemoryAddress address, CancellationToken ct);
    ValueTask<ArtifactEntry> WriteAsync(MemoryAddress address, MemoryWrite write,
        CancellationToken ct);
    ValueTask DeleteAsync(MemoryAddress address, CancellationToken ct);
    IAsyncEnumerable<ArtifactEntry> SearchAsync(MemorySearchRequest request,
        CancellationToken ct);
    IAsyncEnumerable<ArtifactEntry> ListAsync(MemoryListRequest request,
        CancellationToken ct);
}

public sealed record MemoryWrite(
    IReadOnlyList<ReadOnlyMemory<byte>> Content,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Attributes,
    IReadOnlyList<string> Keywords);

public sealed record MemorySearchRequest(
    string Query,
    MemoryAddress? Address = null,
    ...);

public sealed record MemoryListRequest(
    MemoryAddress? Prefix = null,
    ...);
```

Search/list fields that currently carry raw memory names, prefixes, or references must use `MemoryAddress` or a dedicated typed address filter as appropriate. A dedicated filter must contain only already-parsed `MemoryAddress` values and its own typed operators; it must not accept an unvalidated string internally. `MemoryWrite` is the shared parsed write payload; adapters convert raw content into it before store/plugin calls.

`IPluginStore`, plugin event payloads, mutation payloads, and test doubles follow the same typed contracts. A plugin callback must receive `MemoryAddress`, not a string that it must reinterpret. Public `ArtifactEntry` changes from `Uri` to `ArtifactKey`:

```csharp
public sealed record ArtifactEntry
{
    public MemoryAddress Address { get; init; }
    public string ArtifactKey { get; init; }
    // Existing metadata/content members remain subject to the validated model.
}
```

The old public names and URI behavior are removed rather than marked as aliases.

## 5. Parsing and validation rules

### 5.1 Address parsing

Address parsing is performed at every raw-input boundary and nowhere else. The general address parser must:

- reject null, empty, whitespace-only, malformed, and overlong values;
- reject `file://` and every other URI-shaped input, including scheme-prefixed forms, authority forms, query/fragment forms, and percent-encoded attempts to disguise them. Recall's form detector recognizes these values first so recall reports `UnsupportedInputForm`; direct address parsing reports `MalformedAddress`;
- reject rooted physical paths, drive-qualified paths, UNC paths, backslashes, NUL/control characters, and platform-specific separators;
- reject `.` and `..` segments, traversal sequences, and any normalization that would change the intended logical segments;
- reject empty segments, invalid segment characters, and names that violate the store's logical-name rules;
- enforce one shared maximum address length, measured using the specified UTF-8 byte count;
- classify `/temp/<name>` and `~<name>` as ephemeral and normalize them to one canonical ephemeral value;
- reject `/temp` without a name and reject attempts to nest or escape the ephemeral namespace;
- classify all other valid logical paths as persistent, including reserved prefixes such as `/skill`, `/sessions`, and `/findings`.

Canonicalization must happen before policy checks, equality, lookup, event publication, or persistence. Thus `/temp/foo` is checked as ephemeral, not as a persistent path that happens to contain `temp`.

### 5.2 Recall input forms

Recall accepts exactly two forms: a logical address or inline NMP/2 text. The adapter first identifies and parses a supported form. A `file://` value is an explicit unsupported-input-form error, not a filesystem lookup. Inline NMP/2 is decoded using the existing encoding strategy after form detection and does not become a `MemoryAddress`. Invalid artifact text is reported as an invalid inline-artifact error under the unsupported-input-form category. No public or internal resolver may produce an address from a file URI, and `ArtifactUri()` is removed.

### 5.3 Shared write validation

Core owns the following validation before a store or plugin mutation is attempted:

- a non-null content collection containing at least one non-null content element;
- maximum UTF-8 byte size for each content element;
- maximum total write size;
- maximum chunk count and any per-chunk encoded-size limit;
- maximum address length;
- valid attributes and keywords under existing model rules.

Limits are defined once in a shared core policy/options object and consumed by all write paths. Adapters must not duplicate or weaken these checks. Validation is deterministic, occurs before side effects, and reports the first applicable category plus the relevant limit and measured value where safe.

## 6. Adapter data flow and policy

The flow is:

`raw adapter input → parse MemoryAddress/form → enforce adapter policy → core validate MemoryWrite → store/plugin operation → adapter response`

CLI memory commands parse addresses through the shared parser and enforce persistent-only policy for operations that target memory. An ephemeral address supplied to a CLI memory command produces `ForbiddenAddressKind`; it is not silently rewritten or stored persistently. CLI and MCP must call the same internal/core operation path so validation cannot be bypassed by the CLI's direct calls to internal tool implementations.

MCP tools parse logical addresses and inline recall artifacts, then allow ephemeral operations only where the tool's operation supports them. MCP resources use the same parser and policy matrix as MCP tools. Any address-bearing chat-agent recall path is included in this boundary audit. Unsupported combinations produce the explicit policy error rather than falling through to a persistent store.

Resource identifiers are logical address inputs, not filesystem URIs. Their template and decoding rules must preserve canonicalization and reject encoded traversal or URI-shaped values.

## 7. Artifact keys and storage mapping

`ArtifactKey` is generated by the store and is opaque to callers. It is portable and store-relative:

- it uses `/` as its only separator;
- it is never rooted, drive-qualified, UNC-shaped, or URI-shaped;
- it contains no host, workspace, absolute directory, or OS-specific path information;
- callers must not derive it by concatenating physical paths;
- equality and transport use the key as opaque data, not as a path.

Only `FileMemoryStore` maps an `ArtifactKey` to a physical path. That mapping validates the key again at the storage boundary and confines the result beneath the configured store root. Other stores may use the key directly or map it to their own local representation without exposing that representation.

Bundles serialize logical addresses, artifact content, metadata, and portable artifact keys as data. Import regenerates local physical locations and rebuilds any local key/index associations; it must not recreate the source machine's directory tree or trust an absolute/URI-shaped key. Export/import round trips therefore work across operating systems and workspace roots.

## 8. Error contract

Core exposes stable error categories suitable for adapter mapping:

```csharp
public enum MemoryErrorCategory
{
    MalformedAddress,
    UnsupportedInputForm,
    ForbiddenAddressKind,
    InvalidWriteContent
}

public sealed record MemoryError(
    MemoryErrorCategory Category,
    string Code,
    string Message,
    string? Field = null,
    long? Limit = null,
    long? Actual = null);
```

`MalformedAddress` covers syntactic, traversal, rooted, invalid-character, and address-length failures. `UnsupportedInputForm` covers file URIs, unsupported URI forms, and invalid inline NMP/2 input. `ForbiddenAddressKind` covers a valid address rejected by an adapter policy, especially ephemeral addresses in CLI memory commands. `InvalidWriteContent` covers required-content, element, total, chunk, attribute, and keyword limits.

Adapters translate these categories into their native result/exit/error protocol without changing category meaning. Error messages must identify the rejected field and constraint, never echo unsafe raw input in a way that creates a second URI/path interpretation.

## 9. Migration and release notes

This release must call out the breaking changes prominently:

- `MemoryAddress` replaces raw memory strings in public memory APIs, plugin contracts, events, mutations, search/list requests, and test doubles.
- `ArtifactEntry.Uri` is removed and replaced by `ArtifactKey`.
- `ArtifactUri()` and URI-based resolution are removed.
- `file://` recall and all file-URI memory flows are unsupported.
- `/temp/name` and `~name` now identify one canonical ephemeral address.
- CLI memory commands reject ephemeral addresses; MCP tools/resources support their approved ephemeral operations.
- Invalid addresses and writes now fail consistently under shared core limits before storage/plugin side effects.

Consumers must construct addresses through `MemoryAddress.Parse`/`TryParse`, stop persisting or comparing physical URIs, and treat `ArtifactKey` as opaque. Existing persisted metadata containing URI fields is not exposed through the new API; migration/import code must discard physical location data and regenerate local artifact locations and keys. It must not be reintroduced as a public compatibility property. Bundle readers must accept only the new portable representation after this breaking release.

## 10. Testing and acceptance criteria

The implementation is accepted only when tests enumerate the complete surface rather than sampling representative paths:

1. `MemoryAddress` tests cover equality/canonicalization of `/temp/foo` and `~foo`, persistent classification, serialization, hashing, and every malformed, traversal-prone, rooted, URI-shaped, invalid, and overlong input class.
2. Every MCP tool and every MCP resource address/input form is tested for parsing, ephemeral policy, file-URI rejection, inline NMP/2 handling, and uniform write limits.
3. Every CLI memory command is tested for persistent success, ephemeral rejection, file-URI rejection, and the same content/size/chunk limits as MCP.
4. Every store implementation, including `FileMemoryStore`, receives typed addresses and validates the artifact-key boundary. Test doubles cannot compile against raw-string contracts.
5. Every plugin callback, event, and mutation payload is tested for typed address propagation and canonical ephemeral values.
6. Write tests cover required content, per-element bytes, total bytes, chunk count/size, and address limits through each adapter and directly through core.
7. Artifact-key tests prove portable separators and non-rooted/non-URI output, physical-path isolation in `FileMemoryStore`, and local-location regeneration during bundle export/import.
8. Source-generated JSON contexts register every new public request, response, error, enum, and value type required by serialization; tests exercise serialization with trimming-compatible source generation.
9. Repository searches or documentation assertions prove that public guidance does not advertise `file://`, `ArtifactUri()`, or `ArtifactEntry.Uri` as supported inputs or API.

Acceptance also requires that invalid input causes no store/plugin mutation, all adapters present categorized errors, and the complete solution builds and passes its default test suite.

## 11. Rollout and risks

Rollout is one breaking release track: introduce the value type and shared validator, convert contracts and adapters, convert stores/plugins/test doubles, update bundles and generated JSON registration, then remove URI behavior and old members. Changes should be landed in dependency order within the track so the compiler exposes every raw-string contract that remains.

Primary risks are missed address-bearing surfaces, policy drift between CLI and MCP, accidental physical-path leakage through artifact keys, and incomplete source-generated serialization registration. Mitigations are exhaustive surface enumeration, one core validation path, a storage-only physical mapping rule, and the acceptance tests above. A further risk is confusing inline NMP/2 text with an address; explicit form detection and dedicated unsupported-input errors prevent that ambiguity. No mitigation in this track should expand into transaction, plugin-lifecycle, or retrieval redesign.

## 12. Spec self-review

- Placeholder scan: passed; no deferred marker or unspecified decision remains.
- Consistency: passed; recall classifies URI-shaped input before address parsing so its error category is distinct from direct address parsing, and canonicalization precedes policy checks everywhere.
- Scope: passed; the design is limited to unified addressing, write validation, affected contracts/adapters/storage mapping, bundles, and their verification.
- Ambiguity: passed; ephemeral canonical form, parser responsibilities, error categories, artifact-key ownership, migration behavior, and exhaustive test surfaces are explicit.
