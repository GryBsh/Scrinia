# Public Surface Conformance Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the local store's write boundary, CLI JSON records, MCP warning statuses, and CLI exit codes tell the same truthful public story.

**Architecture:** Put the persistent filesystem-component rule on the public `MemoryWrite` value, after address resolution and before either store write path can perform I/O. Translate that one typed refusal at the three internal MCP handlers used by both the public tool facade and direct CLI commands. Keep command-specific JSON records only for values actually observed or explicitly echoed, and make no-result/no-op outcomes warnings whose CLI exit is non-zero without converting them to errors.

**Tech Stack:** .NET 10, C#, xUnit, FluentAssertions, `System.Text.Json` source-generated metadata, ConsoleAppFramework, local filesystem store, MCP stdio.

**Spec:** `docs/superpowers/specs/2026-08-23-public-surface-conformance-design.md`

## Global Constraints

- **Local-only surface:** `scri serve` is the local stdio MCP host; do not introduce or mention removed HTTP, remote, web, REST, or server-project paths.
- **One write rule:** the limit is **182 UTF-16 characters in the sanitized persistent leaf subject**, derived from a 255-character component ceiling minus the 73-character longest current durable-write suffix: `.{timestamp}.nmp2.{pid}.{guid}.tmp` from `ArchiveVersion`. It is independent of the current PID width and applies consistently to remember, append, and compact through both MCP and CLI.
- **Ephemeral exemption:** a `~name` is exempt because it has no filesystem component. Validate neither its full display path nor its ignored scope.
- **Sanitization is not validation:** `MemoryNaming.SanitizeSegment` continues to transform existing disk names; do not make it reject during reads, listing, migration, or cleanup.
- **Preserve public `MemoryWrite` compatibility:** retain the public type, namespace, six-parameter constructor signature, constructor parameter names, optional defaults, positional properties, `with` support, and six-value deconstruction shape. Existing source and already compiled callers must continue to construct it.
- **MCP content policy is separate:** retain the 5 MB per-content-element facade policy. Do not apply it to compacted existing content or CLI stdin.
- **Truthful JSON only:** remove `rescuedScopes`, `chunkCount`, `success`, and export `path`; do not replace any with null, empty, sentinel, inferred, or prose-derived values. Reindex instead reports its exact `Embedded`, `Total`, `Skipped`, and `Failed` result counters plus its existing message.
- **Status and exit rule:** `success` exits 0; `warning` and `error` exit 1. A warning remains a warning and never receives `errorCode`.
- **No durable/protocol scope creep:** do not change NMP/2, sidecar, bundle, plugin, embedding, or indexing formats/protocols; do not rename or reject existing long entries during reads.
- **TDD evidence:** every new test must be observed failing before its implementation change, then passing after it. Use `TestHelpers.StoreScope` where a test configures a workspace.
- **Build and suite gates:** use `dotnet build Scrinia.sln`, focused `dotnet test` filters, then `dotnet test Scrinia.sln`. Do not claim success without recorded command output.

---

## File map

| File | Responsibility |
|---|---|
| `src/Scrinia.Core/Models/MemoryWrite.cs` | Public, validated durable-write input; compatibility-preserving constructor/properties/deconstructor and the dedicated refusal type. |
| `src/Scrinia.Mcp/MemoryTools.cs` | Removes only the facade-only path-length policy; keeps transport-size validation and public tool descriptions accurate. |
| `src/Scrinia.Mcp/MemoryTools.Core.cs` | Converts `MemoryWrite` refusal from `Remember`/`Append` into `INVALID_PARAMETER`; makes empty list/search warnings. |
| `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs` | Converts `Compact` refusal and both compact no-ops into typed warnings. |
| `src/Scrinia/Commands/ScriniaCommands.Memory.cs` | Makes direct CLI empty list/search exits non-zero; stops fabricating store/forget JSON fields. |
| `src/Scrinia/Commands/ScriniaCommands.Setup.cs` | Emits factual reindex counters. |
| `src/Scrinia/Commands/ScriniaCommands.Bundle.cs` | Stops emitting fabricated export path JSON. |
| `src/Scrinia/Commands/CliJsonContext.cs` | Registers and declares the exact reduced/replacement JSON records. |
| `tests/Scrinia.Tests/FileMemoryStoreWriteSurfaceTests.cs` | Verifies 182/183 sanitized-subject behavior and no persistent side effects. |
| `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs` | Verifies public MCP facade responses and the warning/no-op matrix. |
| `tests/Scrinia.Tests/CliSurfaceConformanceTests.cs` | Extends the bounded CLI JSON provenance guard and source-generated-context proof. |
| `tests/Scrinia.Tests/BundleExitCodeTests.cs` | Verifies direct CLI JSON/error parity and non-success exit behavior. |
| `tests/Scrinia.Tests/ResponseStatusIsolationTests.cs` | Verifies `EmitMcpResult` returns 0 only for success without inspecting rendered YAML. |
| `docs/cli-reference.md` | Documents the persistent leaf limit, warning exit behavior, and changed JSON records. |
| `docs/getting-started.md` | Changes only statements that promise an affected JSON shape or empty-result exit. |
| `CHANGELOG.md` | Adds the required breaking fix entry. |

---

### Task 1: Make `MemoryWrite` the filesystem-safe, compatibility-preserving boundary

**Files:**
- Modify: `src/Scrinia.Core/Models/MemoryWrite.cs:1-41`
- Modify: `tests/Scrinia.Tests/FileMemoryStoreWriteSurfaceTests.cs:19-40, 49-100, 319-355`

**Interfaces:**
- Consumes: `MemoryNaming.SanitizeSegment(string)` and the existing `FileMemoryStore.StoreAsync(MemoryWrite, CancellationToken)` / `AppendAsync(MemoryWrite, CancellationToken)` contracts.
- Produces: public `InvalidMemoryWriteException : ArgumentException` with `MaxPersistentSubjectLength` and `SanitizedSubjectLength` integer properties; public `MemoryWrite.MaxPersistentSubjectLength == 182`; the existing `MemoryWrite(string Name, string Scope, string[] Content, string? Description = null, Dictionary<string, string[]>? Attributes = null, string[]? Keywords = null)` binary constructor and six-output `Deconstruct` signature.

- [ ] **Step 1: Write the failing core boundary tests**

Add these facts to `FileMemoryStoreWriteSurfaceTests`, using its existing `TempWorkspace` and `FileMemoryStore` helpers. Construct names with `new string('x', 182)` and `new string('x', 183)`; use a raw name such as `new string('x', 181) + "?"` to prove the comparison uses the sanitized result (`?` becomes `_`) rather than raw length.

```csharp
[Fact]
public async Task StoreAndAppend_AllowA182CharacterSanitizedPersistentSubject()
{
    using var tmp = new TempWorkspace();
    string subject = new('x', MemoryWrite.MaxPersistentSubjectLength);

    await tmp.Store.StoreAsync(new MemoryWrite(subject, "local", ["first"]));
    await tmp.Store.AppendAsync(new MemoryWrite(subject, "local", ["second"]));

    tmp.Store.LoadIndex("local").Should().ContainSingle(e => e.Name == subject);
    (await DecodeAsync(tmp.Store, subject)).Should().Be("firstsecond");
}

[Fact]
public void PersistentMemoryWrite_At183SanitizedCharacters_ThrowsBeforeAStoreCanTouchDisk()
{
    using var tmp = new TempWorkspace();
    string subject = new('x', MemoryWrite.MaxPersistentSubjectLength + 1);

    Action construct = () => _ = new MemoryWrite(subject, "local", ["body"]);

    var error = construct.Should().Throw<InvalidMemoryWriteException>().Which;
    error.MaxPersistentSubjectLength.Should().Be(182);
    error.SanitizedSubjectLength.Should().Be(183);
    Directory.EnumerateFileSystemEntries(tmp.Store.GetStoreDirForScope("local"))
        .Should().BeEmpty("construction rejects before a store can create artifact, sidecar, archive, or temp files");
}

[Fact]
public void PersistentMemoryWrite_UsesTheSanitizedLeafLength()
{
    string raw = new('x', 181) + "?";

    Action construct = () => _ = new MemoryWrite(raw, "local", ["body"]);

    construct.Should().NotThrow();
}

[Fact]
public async Task LongEphemeralMemoryWrite_IsExemptAndCreatesNoPersistentFiles()
{
    using var tmp = new TempWorkspace();
    string name = "~" + new string('x', 1_000);

    await tmp.Store.StoreAsync(new MemoryWrite(name, "ignored", ["body"]));

    tmp.Store.GetEphemeral(name[1..]).Should().NotBeNull();
    tmp.Store.LoadIndex("local").Should().BeEmpty();
}
```

Also add a compatibility fact that compiles and runs the old public source shapes:

```csharp
[Fact]
public void MemoryWrite_RetainsItsPublicConstructionAndRecordShapes()
{
    var write = new MemoryWrite("name", "local", ["body"], "description",
        new Dictionary<string, string[]> { ["tag"] = ["one"] }, ["keyword"]);
    var renamed = write with { Name = "renamed" };
    var (name, scope, content, description, attributes, keywords) = renamed;

    (name, scope, content, description, attributes, keywords).Should().NotContainNulls();
}
```

- [ ] **Step 2: Run the focused test to prove red**

Run:

```bash
dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~FileMemoryStoreWriteSurfaceTests"
```

Expected before implementation: the 183-character construction assertion fails because the current positional record accepts it; the compatibility test passes. Record the failure naming the missing `InvalidMemoryWriteException`/limit API, then continue.

- [ ] **Step 3: Replace the positional-record implementation without changing its public contract**

In `MemoryWrite.cs`, retain `public sealed record MemoryWrite` and replace only the compiler-generated positional implementation with explicit public init properties and an explicit constructor having the **same metadata signature and optional defaults**. Do not add an overload and do not change `string[]` or `Dictionary<string, string[]>?` to interface types: either would break existing callers.

Use this shape (retain and update the existing XML docs):

```csharp
public sealed class InvalidMemoryWriteException : ArgumentException
{
    public InvalidMemoryWriteException(int maxPersistentSubjectLength, int sanitizedSubjectLength)
        : base($"Persistent memory subject exceeds {maxPersistentSubjectLength} sanitized characters " +
               $"(received {sanitizedSubjectLength}).", "Name")
    {
        MaxPersistentSubjectLength = maxPersistentSubjectLength;
        SanitizedSubjectLength = sanitizedSubjectLength;
    }

    public int MaxPersistentSubjectLength { get; }
    public int SanitizedSubjectLength { get; }
}

public sealed record MemoryWrite
{
    public const int MaxPersistentSubjectLength = 182;

    public MemoryWrite(
        string Name,
        string Scope,
        string[] Content,
        string? Description = null,
        Dictionary<string, string[]>? Attributes = null,
        string[]? Keywords = null)
    {
        if (!MemoryNaming.IsEphemeralName(Name))
        {
            int length = MemoryNaming.SanitizeSegment(Name).Length;
            if (length > MaxPersistentSubjectLength)
                throw new InvalidMemoryWriteException(MaxPersistentSubjectLength, length);
        }

        this.Name = Name;
        this.Scope = Scope;
        this.Content = Content;
        this.Description = Description;
        this.Attributes = Attributes;
        this.Keywords = Keywords;
    }

    public string Name { get; init; }
    public string Scope { get; init; }
    public string[] Content { get; init; }
    public string? Description { get; init; }
    public Dictionary<string, string[]>? Attributes { get; init; }
    public string[]? Keywords { get; init; }

    public void Deconstruct(out string name, out string scope, out string[] content,
        out string? description, out Dictionary<string, string[]>? attributes, out string[]? keywords)
    {
        name = Name; scope = Scope; content = Content; description = Description;
        attributes = Attributes; keywords = Keywords;
    }
}
```

Keep the type a `record` so generated equality, cloning, and `with` remain available. The explicit constructor preserves the precompiled `newobj` target because its parameter types, order, and defaults are unchanged; init accessors preserve source use of `with`. Do not validate a value assigned through `with`: the spec defines constructor validation, and forcing a setter-time rule would require a different public property model and broaden behavior beyond this stream.

- [ ] **Step 4: Run focused core tests and prove the side-effect boundary**

Run:

```bash
dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~FileMemoryStoreWriteSurfaceTests"
```

Expected: PASS. In the task report, explicitly state that the 183-character refusal happens at `new MemoryWrite(...)`, before `StoreAsync` / `AppendAsync` reach `ArchiveVersion`, `WriteArtifactAsync`, `WriteSidecarCore`, or temporary-file creation; that `ArchiveVersion`'s temporary filename is the longest current durable write filename; and that the 1,000-character `~name` remains in ephemeral storage.

- [ ] **Step 5: Commit the independent core boundary**

```bash
git add src/Scrinia.Core/Models/MemoryWrite.cs tests/Scrinia.Tests/FileMemoryStoreWriteSurfaceTests.cs
```

---

### Task 2: Translate the one boundary failure on all three write routes

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.cs:74-75, 177-212, 327-352, 386-399`
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs:111-231, 999-1057`
- Modify: `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs:1121-1302`
- Modify: `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs`

**Interfaces:**
- Consumes: `InvalidMemoryWriteException` from Task 1.
- Produces: `McpResponse` with `Status == "error"` and `ErrorCode == ErrorCodes.InvalidParameter` for invalid persistent names from `Remember`, `Append`, and `Compact`; public `MemoryRemember`, `MemoryAppend`, and `MemoryCompact` serialise the same envelope.

- [ ] **Step 1: Write failing internal-handler and public-facade route tests**

Add a table-driven fact to `ScriniaMcpToolsTests`. Seed a valid two-chunk target for the compact case, then use an over-limit requested target where the route constructs a replacement `MemoryWrite`. Assert typed handler responses rather than substring matching rendered YAML; separately parse facade YAML with the project's existing `ResponseParser.Parse`.

```csharp
[Theory]
[InlineData("remember")]
[InlineData("append-create")]
[InlineData("compact")]
public async Task InvalidPersistentSubject_EveryWriteHandlerReturnsInvalidParameter(string route)
{
    using var scope = new TestHelpers.StoreScope();
    var tools = new ScriniaMcpTools();
    string longName = new('x', MemoryWrite.MaxPersistentSubjectLength + 1);

    McpResponse response = route switch
    {
        "remember" => await tools.Remember(["body"], longName),
        "append-create" => await tools.Append("body", longName),
        "compact" => await CompactLongTargetAsync(tools, longName),
        _ => throw new InvalidOperationException(route),
    };

    response.Status.Should().Be("error");
    response.ErrorCode.Should().Be(ErrorCodes.InvalidParameter);
}
```

Implement `CompactLongTargetAsync` as a legacy-on-disk fixture, because a newly created over-limit target is correctly refused before it can become compactable. In the `StoreScope` store directory, create the artifact with `Nmp2ChunkedEncoder.EncodeChunks(["first", "second"])` at `store.ArtifactPath(longName, "local")`, then write this exact matching sidecar at `Path.Combine(store.GetStoreDirForScope("local"), MemoryNaming.SanitizeSegment(longName) + ".meta.json")`:

```csharp
var entry = new ArtifactEntry(longName, "", "firstsecond".Length, 2, DateTimeOffset.UtcNow);
await File.WriteAllTextAsync(sidecarPath, JsonSerializer.Serialize(entry));
```

Do not call `StoreAsync` or `Upsert`: both are write surfaces under test and cannot create the deliberately legacy-invalid fixture. Call `tools.Compact(longName)` afterward and assert it returns the typed refusal before `ArchiveVersion`/`WriteArtifactAsync` can replace the fixture. Add a matching three-row facade theory invoking `MemoryRemember`, `MemoryAppend`, and `MemoryCompact`, then assert parsed `status: error` and `errorCode: INVALID_PARAMETER`. The point is to cover the two caller families independently: facade schema path and direct internal-handler path.

- [ ] **Step 2: Run route tests to prove red**

Run:

```bash
dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~InvalidPersistentSubject_EveryWrite"
```

Expected before translation: handler/facade execution faults with uncaught `InvalidMemoryWriteException` rather than returning the MCP envelope. The compact row reaches the constructor from a readable legacy fixture; this is intentional proof that existing long entries remain readable but cannot be rewritten under the new filesystem-safe contract.

- [ ] **Step 3: Remove only the obsolete facade path-length guard**

Delete `MaxPathLength` and `MemoryRemember`'s `path.Length > MaxPathLength` branch from `MemoryTools.cs`. Leave `MaxContentBytesPerElement` and both remember/append content checks exactly in place. Do not add a `path.Length` check to append or compact: the durable constructor is now the only persistent-subject rule.

- [ ] **Step 4: Catch and classify the dedicated exception at each write construction**

At the only three production `new MemoryWrite(...)` sites—`Remember` in `MemoryTools.Core.cs`, `Append` in the same file, and `Compact` in `MemoryTools.Lifecycle.cs`—catch only `InvalidMemoryWriteException` around the call that reaches the write input, then return:

```csharp
return ResponseBuilder.Error(
    ex.Message,
    ErrorCodes.InvalidParameter,
    $"Use a persistent memory subject of at most {MemoryWrite.MaxPersistentSubjectLength} sanitized characters.");
```

Keep unrelated `IOException`, encoder, store, and cancellation failures unhandled by this narrow translation. The catch must cover each handler's actual `new MemoryWrite(...)` construction, not only `StoreAsync`, so failure is classified before a transport wrapper can turn it into an uncaught error. Do not add a second sanitization/length calculation in `FileMemoryStore.StoreAsync` or `AppendAsync`: their defense is accepting the validated `MemoryWrite` value.

Update only public `MemoryTools.cs` descriptions that promise successful compact no-ops, after Task 4 changes their status; do not alter unrelated tool prose.

- [ ] **Step 5: Run focused route and legacy handler tests**

Run:

```bash
dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~InvalidPersistentSubject_EveryWrite|FullyQualifiedName~ScriniaMcpToolsTests"
```

Expected: PASS. Record six observed cases: internal `Remember`, `Append` create, and `Compact`; public `MemoryRemember`, `MemoryAppend`, and `MemoryCompact`. Each must be `error`/`INVALID_PARAMETER`, and none may throw through the test transport.

- [ ] **Step 6: Commit route parity**

```bash
git add src/Scrinia.Mcp/MemoryTools.cs src/Scrinia.Mcp/MemoryTools.Core.cs \
        src/Scrinia.Mcp/MemoryTools.Lifecycle.cs tests/Scrinia.Tests/ScriniaMcpToolsTests.cs
```

---

### Task 3: Correct every affected CLI JSON record and pin its provenance

**Files:**
- Modify: `src/Scrinia/Commands/CliJsonContext.cs:9-28, 67-75`
- Modify: `src/Scrinia/Commands/ScriniaCommands.Memory.cs:396-420, 489-507`
- Modify: `src/Scrinia/Commands/ScriniaCommands.Setup.cs:662-682`
- Modify: `src/Scrinia/Commands/ScriniaCommands.Bundle.cs:46-64`
- Modify: `tests/Scrinia.Tests/CliSurfaceConformanceTests.cs:20-181`
- Modify: `tests/Scrinia.Tests/BundleExitCodeTests.cs:16-112`

**Interfaces:**
- Consumes: `ForceReindexAsync` result fields `Embedded`, `Total`, `Skipped`, `Failed`; store input bytes; `McpResponse.ToYaml()` only as `Message`.
- Produces: `CliReindexOutput(int Embedded, int Total, int Skipped, int Failed, string Message)`, `CliStoreOutput(string Name, long OriginalBytes, string Message)`, `CliForgetOutput(string Name, string Message)`, and `CliExportOutput(string Message)` registered through `CliJsonContext`.

- [ ] **Step 1: Write failing serialization and direct-command tests**

In `BundleExitCodeTests`, redirect console output using the project’s existing serial workspace collection pattern, invoke each command with `json: true`, parse with `JsonDocument.Parse`, and assert properties by camel-case wire name:

```csharp
root.TryGetProperty("chunkCount", out _).Should().BeFalse();
root.TryGetProperty("success", out _).Should().BeFalse();
root.TryGetProperty("path", out _).Should().BeFalse();
root.TryGetProperty("rescuedScopes", out _).Should().BeFalse();
root.GetProperty("embedded").GetInt32().Should().Be(expected.Embedded);
root.GetProperty("total").GetInt32().Should().Be(expected.Total);
root.GetProperty("skipped").GetInt32().Should().Be(expected.Skipped);
root.GetProperty("failed").GetInt32().Should().Be(expected.Failed);
```

Use a controlled embedding-provider test double already used by reindex tests to obtain the expected actual reindex result; do not assert a guessed number. For store, assert `name` is the canonical resolved output and `originalBytes` equals `Encoding.UTF8.GetByteCount(input)`, while explicitly asserting `chunkCount` is absent. For forget/export, preserve and assert the `message` field but assert the constant/fabricated properties are absent.

Add a `CliSurfaceConformanceTests` fact that reads `CliJsonContext.cs` and inspects each four record constructor declaration. It must enumerate every constructor parameter and require a named provenance classification in the test: operation result (`Embedded`, `Total`, `Skipped`, `Failed`), explicitly documented input echo (`Name`, `OriginalBytes`), or emitted content (`Message`). Include the four removed wire properties as negative assertions. This is a bounded guard on all command records in that source file, not a reflection-free “looks right” test.

- [ ] **Step 2: Run JSON tests to prove red**

Run:

```bash
dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~CliSurfaceConformanceTests|FullyQualifiedName~CliJson"
```

Expected before implementation: failures find `chunkCount`, `success`, export `path`, and `rescuedScopes`; reindex lacks `total`, `skipped`, and `failed`. If a test observes an unavailable provider instead of the JSON record, correct the test setup rather than weakening the assertions.

- [ ] **Step 3: Change the records and source-generated registrations together**

In `CliJsonContext.cs`, retain the four `[JsonSerializable]` attributes but replace the record declarations with:

```csharp
internal sealed record CliStoreOutput(string Name, long OriginalBytes, string Message);
internal sealed record CliForgetOutput(string Name, string Message);
internal sealed record CliExportOutput(string Message);
internal sealed record CliReindexOutput(int Embedded, int Total, int Skipped, int Failed, string Message);
```

Do not use reflection serialization. Keeping the registrations verifies trimming/AOT-compatible output for all revised records.

- [ ] **Step 4: Source each remaining field directly and delete fabricated arguments**

Make each call site match its new record exactly:

```csharp
WriteJson(new CliStoreOutput(qualifiedName, bytes, result),
    CliJsonContext.Default.CliStoreOutput);
WriteJson(new CliForgetOutput(name, result), CliJsonContext.Default.CliForgetOutput);
WriteJson(new CliExportOutput(result), CliJsonContext.Default.CliExportOutput);
WriteJson(new CliReindexOutput(result.Embedded, result.Total, result.Skipped, result.Failed, summary),
    CliJsonContext.Default.CliReindexOutput);
```

`Name` and `OriginalBytes` are allowed because this command explicitly reports its canonical target and submitted input size. `Message` is allowed because it is the emitted human summary. Do not infer persisted chunk count from the one-element CLI input; do not synthesize a boolean success; do not present `""` as a path; do not derive structured fields by parsing YAML.

- [ ] **Step 5: Run the focused JSON and source-generation suite**

Run:

```bash
dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~CliSurfaceConformanceTests|FullyQualifiedName~BundleExitCodeTests|FullyQualifiedName~Reindex"
```

Expected: PASS. Confirm the compiler generated `CliJsonContext.Default` metadata for all four revised record types and no call site has an obsolete constructor arity.

- [ ] **Step 6: Commit schema truthfulness**

```bash
git add src/Scrinia/Commands/CliJsonContext.cs src/Scrinia/Commands/ScriniaCommands.Memory.cs \
        src/Scrinia/Commands/ScriniaCommands.Setup.cs src/Scrinia/Commands/ScriniaCommands.Bundle.cs \
        tests/Scrinia.Tests/CliSurfaceConformanceTests.cs tests/Scrinia.Tests/BundleExitCodeTests.cs
```

---

### Task 4: Apply the complete warning/no-op matrix and CLI exit rule

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs:640-652, 896-902`
- Modify: `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs:1164-1170`
- Modify: `src/Scrinia.Mcp/MemoryTools.cs:380-399`
- Modify: `src/Scrinia/Commands/ScriniaCommands.Memory.cs:41-49, 257-264`
- Modify: `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs`
- Modify: `tests/Scrinia.Tests/BundleExitCodeTests.cs`
- Modify: `tests/Scrinia.Tests/ResponseStatusIsolationTests.cs`

**Interfaces:**
- Consumes: `ResponseBuilder.Warning`, `McpResponse.IsWarning`, and `ScriniaCommands.EmitMcpResult`.
- Produces: warning responses for empty MCP `search`, empty MCP `list`, one-chunk `compact`, and `keepRecent >= chunkCount`; CLI exit 1 while preserving list/search arrays and compact YAML/JSON payloads.

- [ ] **Step 1: Write the failing MCP warning matrix**

Add one fact per row below to `ScriniaMcpToolsTests`; assert `Status`, `IsWarning`, `IsError == false`, action/content continuity, and the absence of `ErrorCode`:

| Tool/outcome | Required status |
|---|---|
| `Guide` with no stored context | `success` |
| `Restore` with empty workspace | `success` |
| `Search` with no matches | `warning` |
| `List` with no entries | `warning` |
| `Compact` on one chunk | `warning` |
| `Compact` with `keepRecent >= chunkCount` | `warning` |
| `Reconcile` clean scan | `success` |
| `Remember`, `Append`, `Forget`, `Recall`, `Link` successful requested mutation/read | `success` |
| `Facts` existing disambiguation/not-found response | existing formatter warning behavior unchanged |

The compact facts must check its existing explanatory wording and `WithAction("compacted")`; search/list must keep `searched`/`listed` actions and their truncation advice. The clean-reconcile and empty-restore/guide facts are anti-regression cases: do not classify every empty diagnostic as warning.

- [ ] **Step 2: Write failing CLI exit and payload tests**

Add direct command tests with `json: true` plus non-JSON cases where existing output is important:

```csharp
[Fact]
public async Task CliList_EmptyJsonKeepsAnEmptyArrayButExitsOne()
{
    using var scope = new TestHelpers.StoreScope();
    int exit = await new ScriniaCommands().List(workspaceRoot: scope.WorkspaceDir, json: true);

    exit.Should().Be(1);
    // Parse captured JSON and assert memories: [] and total: 0.
}

[Fact]
public async Task CliSearch_EmptyJsonKeepsAnEmptyArrayButExitsOne()
{
    using var scope = new TestHelpers.StoreScope();
    int exit = await new ScriniaCommands().Search("absent", workspaceRoot: scope.WorkspaceDir, json: true);

    exit.Should().Be(1);
    // Parse captured JSON and assert results: [], total: 0, query: "absent".
}
```

Also prove non-empty list/search exit 0; a multi-chunk compact exits 0; one-chunk compact and `keepRecent >= chunkCount` exit 1 through `EmitMcpResult`; and direct `EmitMcpResult` behavior is 0 for `ResponseBuilder.Success`, 1 for `ResponseBuilder.Warning`, and 1 for `ResponseBuilder.Error`. Keep the latter test inside `ResponseStatusIsolationTests` by exposing the minimal testable internal helper only if the assembly already grants internals access; do not make the production method public merely for testing.

- [ ] **Step 3: Run the warning/exit tests to prove red**

Run:

```bash
dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~Warning|FullyQualifiedName~EmptyJson|FullyQualifiedName~EmitMcpResult|FullyQualifiedName~ResponseStatusIsolationTests"
```

Expected before implementation: four MCP no-result/no-op cases report `success`; direct empty CLI list/search return 0. Existing `EmitMcpResult` warning/error tests should already pass and remain a guard rather than being deleted.

- [ ] **Step 4: Make only the four specified MCP outcomes warnings**

Replace these constructors while preserving their current content, action, and truncation advice:

```csharp
return ResponseBuilder.Warning("No memories stored.").WithAction("listed")
    .WithTruncationAdvice(ListTruncationAdvice);
return ResponseBuilder.Warning("No matching memories found.").WithAction("searched")
    .WithTruncationAdvice(SearchTruncationAdvice);
return ResponseBuilder.Warning("Already a single chunk, nothing to compact.").WithAction("compacted");
return ResponseBuilder.Warning($"Nothing to compact — keepRecent ({keepRecent}) >= chunk count ({chunkCount}).")
    .WithAction("compacted");
```

Do not change `UpdateMeta`, empty bundle import, `FactsResponseFormatter`, guide, restore, or reconcile except to retain their existing classification. Revise the public compact description only enough to say a no-op is returned as a warning; it must no longer promise every no-op is a success.

- [ ] **Step 5: Change direct CLI empty outcomes to exit 1 without changing payloads**

In `ScriniaCommands.Memory.cs`, leave each existing `WriteJson(new CliListOutput([], 0, null), ...)`, `WriteJson(new CliSearchOutput([], 0, query), ...)`, and terminal message untouched, but return 1 in the two empty branches. Non-empty branches remain 0. Do not wrap these direct command shapes in `CliMcpOutput`: preserving the useful arrays is an explicit compatibility requirement.

- [ ] **Step 6: Run focused status/CLI tests**

Run:

```bash
dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~ScriniaMcpToolsTests|FullyQualifiedName~BundleExitCodeTests|FullyQualifiedName~ResponseStatusIsolationTests"
```

Expected: PASS. Record the full matrix result, including the three intentional success anti-cases (guide empty context, restore empty workspace, reconcile clean scan), and verify warning YAML has `status: warning` but no `errorCode`.

- [ ] **Step 7: Commit status and exit conformance**

```bash
git add src/Scrinia.Mcp/MemoryTools.Core.cs src/Scrinia.Mcp/MemoryTools.Lifecycle.cs \
        src/Scrinia.Mcp/MemoryTools.cs src/Scrinia/Commands/ScriniaCommands.Memory.cs \
        tests/Scrinia.Tests/ScriniaMcpToolsTests.cs tests/Scrinia.Tests/BundleExitCodeTests.cs \
        tests/Scrinia.Tests/ResponseStatusIsolationTests.cs
```

---

### Task 5: Document the breaking contract precisely

**Files:**
- Modify: `docs/cli-reference.md:40-77, 140-180, 268-300, 391-420`
- Modify: `docs/getting-started.md` only if it currently promises affected JSON properties or exit-0 empty outcomes
- Modify: `CHANGELOG.md:11-33`

**Interfaces:**
- Consumes: final CLI records and status/exit behavior from Tasks 1–4.
- Produces: user-facing documentation that names the 182-character sanitized persistent leaf limit, warning exit 1, exact revised JSON schemas, and the breaking migration actions.

- [ ] **Step 1: Add failing documentation assertions to the CLI conformance test**

Extend `CliSurfaceConformanceTests` with a source-text documentation pin that reads only living docs. Require `docs/cli-reference.md` to contain all of the following exact machine-relevant terms:

```text
182
sanitized persistent subject
warning
exit 1
embedded
total
skipped
failed
```

Require it not to promise `rescuedScopes`, `chunkCount` for `scri store`, `success` for `scri forget`, or export `path`. Scope checks to the affected command sections so unrelated historical discussion cannot create a false failure. Do not scan dated `docs/superpowers/` plans/specs: they are historical artifacts, not living documentation.

- [ ] **Step 2: Run the docs pin to prove red**

Run:

```bash
dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~CliSurfaceConformanceTests"
```

Expected before documentation edits: FAIL because the live reference lacks the new limit/schema/exit explanation.

- [ ] **Step 3: Update the living CLI reference**

In the `scri store` and `scri append` sections, state:

> Persistent memory subjects may be at most 182 characters after filename sanitization. The limit applies to the leaf subject, not the whole scoped display path. It derives from `ArchiveVersion`'s longest current durable-write temporary filename, which includes the subject, timestamp, `.nmp2`, PID, GUID, and `.tmp`. `~name` is ephemeral and is not subject to this filesystem limit (although the CLI refuses ephemeral addresses because each invocation exits immediately).

Add a shared `Exit status` subsection near the command overview:

> Commands exit 0 only when the requested operation succeeds. They exit 1 for an error and for a warning: a completed command that found no matches, had no memories to list, or could not compact because no chunks would be removed. Warning output remains structured as `status: warning`, not an error.

Document the exact JSON migrations in their owning command sections:

- `scri reindex --json`: `embedded`, `total`, `skipped`, `failed`, `message`; remove `rescuedScopes`.
- `scri store --json`: `name`, `originalBytes`, `message`; remove `chunkCount`.
- `scri forget --json`: `name`, `message`; remove `success`.
- `scri bundle export --json`: `message`; remove `path`.

For each removed field, state the consumer action: stop reading it; use the documented observed counter, input echo, response message, or process exit/status instead. Do not claim a JSON universal envelope and do not invent a JSON status for commands that do not already emit `CliMcpOutput`.

Update `docs/getting-started.md` only if a current claim is demonstrably affected. If none is, leave it unchanged and record “reviewed; no affected promise” in the task report.

- [ ] **Step 4: Add the release note**

Under `## [Unreleased]`, add `### Fixed (breaking) — public outcomes now match durable writes and CLI data` before older entries. Cover all three impacts in concise bullets:

1. persistent sanitized subjects over 182 characters now fail early as `INVALID_PARAMETER`, with no filesystem side effects; existing entries remain readable and are not renamed;
2. empty search/list and no-op compact are warnings and exit 1 rather than successful exit 0;
3. the four fabricated JSON properties were removed and reindex now exposes `embedded`, `total`, `skipped`, and `failed`.

State that NMP/2, sidecars, bundles, and plugin/embedding layouts did not change. Do not reference removed-server surfaces.

- [ ] **Step 5: Run the docs pin and inspect the rendered examples**

Run:

```bash
dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~CliSurfaceConformanceTests"
```

Expected: PASS. Manually review every JSON snippet/property list in the affected `cli-reference.md` sections for the four removed names and confirm no stale one remains.

- [ ] **Step 6: Commit documentation and release notes**

```bash
git add docs/cli-reference.md docs/getting-started.md CHANGELOG.md tests/Scrinia.Tests/CliSurfaceConformanceTests.cs
```

---

### Task 6: Perform focused, full, and compatibility verification

**Files:**
- Modify only if a test exposes a real mismatch in the files named by Tasks 1–5.

**Interfaces:**
- Consumes: every preceding task and no removed surface.
- Produces: recorded evidence that the contract is complete across core, internal handlers, public MCP facade, direct CLI, JSON source generation, docs, and the full solution.

- [ ] **Step 1: Build the full solution**

Run:

```bash
dotnet build Scrinia.sln
```

Expected: 0 errors. Resolve compiler failures caused by changed `MemoryWrite` positional/deconstruction shape or revised JSON constructor arities before testing further; those are compatibility regressions, not test noise.

- [ ] **Step 2: Run the focused conformance groups**

Run each command separately and record its pass count:

```bash
dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~FileMemoryStoreWriteSurfaceTests"
dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~ScriniaMcpToolsTests"
dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~CliSurfaceConformanceTests"
dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~BundleExitCodeTests|FullyQualifiedName~ResponseStatusIsolationTests"
```

Expected: all pass. Confirm the focused evidence covers: 182/183 boundary; ephemeral exemption; source/record compatibility; three internal write handlers; three public MCP facade methods; all four JSON migrations; source-generated metadata; full warning/no-op matrix; empty CLI payload preservation; and 0/1 exit semantics.

- [ ] **Step 3: Run a deliberate mutation check for each new guard**

Make one temporary mutation at a time, run the narrowest filter, observe red, then revert before the next mutation:

1. Change `MaxPersistentSubjectLength` to `183`; expect the core 183 refusal test to fail.
2. Change one `ResponseBuilder.Warning` for empty search back to `Success`; expect the MCP matrix and CLI empty-search exit test to fail.
3. Add `int ChunkCount` back to `CliStoreOutput`; expect the JSON negative assertion/provenance pin to fail.
4. Change direct empty `List` back to `return Task.FromResult(0)`; expect its exit test to fail while its array assertion still passes.
5. Remove `CliReindexOutput`’s `[JsonSerializable]` attribute; expect the build or source-generated-context test to fail.

Record the named failing test for every mutation. Revert each temporary change fully; no mutation belongs in the final commit.

- [ ] **Step 4: Run the full suite**

Run:

```bash
dotnet test Scrinia.sln
```

Expected: 0 failed. If a legacy test depends on a removed fabricated property or an old empty-success exit, update it to the new public contract and identify the affected consumer in the task report; do not preserve a lie to keep an old assertion green.

- [ ] **Step 5: Perform the final contract self-review**

Review the implementation and tests against this checklist before the final commit:

- [ ] Every `new MemoryWrite(...)` in `src/` is enumerated; only `Remember`, `Append`, and `Compact` require response translation, and all three are covered from internal and public facade callers.
- [ ] The validation compares only `MemoryNaming.SanitizeSegment(Name).Length` for non-ephemeral writes, reports both stable limit and offending sanitized length, and introduces no store-layer duplicate calculation.
- [ ] The public `MemoryWrite` constructor metadata signature, optional defaults, properties, `with`, and six-value deconstruction remain usable by source and binary callers.
- [ ] No archive, artifact, sidecar, or temporary file is created for the 183-character refusal; no existing entry is rejected merely by list/recall.
- [ ] All four JSON decisions are exact: reindex has four observed counters; store/forget/export have no fabricated field or replacement sentinel; all use `CliJsonContext`.
- [ ] The twelve-tool warning matrix has no contradiction: only empty search/list and two compact no-ops change here; clean reconcile, empty restore, and guide remain successes; facts/update-meta/import retain their established special handling.
- [ ] CLI list/search preserve their old empty arrays/messages while exiting 1; `EmitMcpResult` exits 0 only for success; warnings have no `errorCode`.
- [ ] Living docs and CHANGELOG state every breaking change, historical planning artifacts are untouched, and no removed-server path appears.
- [ ] No `TODO`, `TBD`, placeholder, unresolved test name, or source/test modification outside the stated scope remains.

- [ ] **Step 6: Commit the verified stream**

```bash
git status --short
        src/Scrinia.Mcp/MemoryTools.Core.cs src/Scrinia.Mcp/MemoryTools.Lifecycle.cs \
        src/Scrinia/Commands/ScriniaCommands.Memory.cs src/Scrinia/Commands/ScriniaCommands.Setup.cs \
        src/Scrinia/Commands/ScriniaCommands.Bundle.cs src/Scrinia/Commands/CliJsonContext.cs \
        tests/Scrinia.Tests/FileMemoryStoreWriteSurfaceTests.cs tests/Scrinia.Tests/ScriniaMcpToolsTests.cs \
        tests/Scrinia.Tests/CliSurfaceConformanceTests.cs tests/Scrinia.Tests/BundleExitCodeTests.cs \
        tests/Scrinia.Tests/ResponseStatusIsolationTests.cs docs/cli-reference.md docs/getting-started.md CHANGELOG.md
```

Expected: only intended implementation, test, and living-document files are staged. Do not stage this historical plan/spec as part of the implementation commit unless the repository convention explicitly commits planning artifacts separately.

## Self-review results

- **Spec coverage:** Tasks 1–2 cover H3's exact 182-character sanitized leaf rule, derived from `ArchiveVersion`'s longest current durable-write temporary filename, with the ephemeral exemption, three write routes, typed MCP translation, and `MemoryWrite` source/binary compatibility. The same boundary applies to remember, append, and compact through both MCP and CLI. Task 3 covers all four JSON decisions and source-generated serialization. Task 4 enumerates all twelve MCP classifications, preserves the intentional success cases, and proves direct CLI/adapter exits. Task 5 covers release/docs requirements. Task 6 supplies focused tests, mutation evidence, full suite, and the no-deferral enumeration review.
- **Placeholder scan:** No TBD/TODO/future-work directives, unnamed files, unnamed methods, or generic “write tests” instructions remain. Each test-bearing task supplies concrete fact names, assertions, commands, and expected red/green behavior.
- **Contradiction scan:** The plan never calls a warning an error, never asks direct CLI list/search to emit an MCP envelope, and does not apply the 5 MB transport policy to compact/CLI. It preserves clean reconcile, guide, and empty restore as successes.
- **Type consistency:** `MemoryWrite` retains `string`, `string[]`, and `Dictionary<string, string[]>?`; the exception is `InvalidMemoryWriteException`; error code is `ErrorCodes.InvalidParameter`/`INVALID_PARAMETER`; all revised output-record names and constructor parameter types match the stated call sites.

## Execution handoff

Plan complete and saved to `docs/superpowers/plans/2026-08-23-public-surface-conformance.md`.

Two execution options:

1. **Subagent-Driven (recommended):** Dispatch a fresh subagent per task and review between tasks.
2. **Inline Execution:** Execute tasks in this session with checkpoints.
