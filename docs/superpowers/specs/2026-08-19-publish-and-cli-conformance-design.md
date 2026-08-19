# Publish and CLI Conformance — Design

**Date:** 2026-08-19
**Findings:** M2, L1a, L2 of the 2026-08-16 whole-codebase design review, plus two items carried from branch 4 (embeddings-search-consolidation).
**Branch:** `fix/publish-and-cli-conformance`, based on `main` after the branch 7a merge.

## The shape

Five surfaces grew instance by instance with nothing forcing them to agree:

- two publish scripts that share no source of truth,
- a CLI option family with no conformance check,
- an environment-variable convention documented in two mutually exclusive forms, only one of which resolves,
- a read-only store gate whose comment describes enforcement that no shipped configuration performs,
- two guardrails identified on branch 4 and never written.

The review's root-cause section states the rule this branch applies: fix the *shape* that lets instances diverge, not only the instance that diverged. Every section below therefore pairs a fix with the artifact that fails when a further instance appears.

## Decisions taken before design

Three were the user's, recorded here because each foreclosed a cheaper option:

1. **Publish: a shared manifest, both scripts kept.** The alternative considered and rejected was collapsing to `publish.ps1` alone (pwsh 7 runs on Linux and macOS) and deleting `publish.sh`. Keeping both means the manifest fixes only the project list, and the remaining divergences are fixed by hand.
2. **Environment variables: accept both forms, correctly.** Single-underscore stays working; the double-underscore lookup is repaired so it actually resolves. Rejected: single-only (leaves the CLI and server disagreeing on the same key) and double-only (breaks existing users and contradicts the docs).
3. **`scri migrate`: a clean break.** No deprecated alias. Consistent with branch 6's standing rule that at `0.5.0` breaking changes are permitted and recorded rather than shimmed.

## Corrections to the review

The review's findings were re-verified against the tree at `main`. Four statements need amending, and the design depends on the amended versions.

**C-1. L2's exit-code claim is overstated.** The review says "several commands return 0 unconditionally regardless of outcome." All 23 command entrypoints were enumerated and inspected. The only genuine always-zero command is `hint`, and it is *correct*: a `UserPromptSubmit` hook that exits non-zero can fail the user's prompt. `migrate` and `migrate_attributes` also end in an unconditional `return Task.FromResult(0)`, but each has a real failure path above it (`Migrate` returns 1 for a missing `.scrinia/`; `MigrateAttributes` throws for a non-file-backed store). `facts` routes through `EmitMcpResult`, which returns non-zero on error. `BundleCommands` is a pure forwarder to implementations that do return non-zero. So this item becomes: document `hint`'s deliberate always-zero at the return, and pin *the ability to fail* mechanically rather than auditing prose.

**C-2. The two plugin output layouts are both supported; neither script is broken on that axis.** `WorkspaceSetup.ResolveEmbeddingsPluginExe` (`src/Scrinia/Commands/WorkspaceSetup.cs:492-494`) tries `plugins/{name}/{name}{ext}` first and `plugins/{name}{ext}` second. `publish.ps1` produces the first, `publish.sh` the second. The layout is therefore a free choice, and this design picks the subdirectory form for a reason unrelated to discovery: it removes the need for the destructive cleanup step (below).

**C-3. The review names `publish.sh:41-42, :85` and the missing merge driver. It does not name three further divergences that the same fix must cover**, or the manifest will be declared a success while the scripts still disagree:

- the switch is `--with-embeddings` in `publish.sh` and `-WithVulkan` in `publish.ps1`;
- `publish.ps1` overwrites the published native DLLs with the Vulkan-compiled variants (`publish.ps1:60-73`); `publish.sh` does not, so the same switch ships Vulkan on Windows and CPU-only on Linux;
- both scripts silently skip that overwrite when the source directory is absent, and on `osx-arm64` the path always resolves to the *linux* package (`publish.ps1:65-69`), so macOS silently never gets it.

**C-4. The `find` is destructive for the reason the review gives, and the fix it suggests is not the one this design takes.** `find "$plugins_dir" -maxdepth 1 ! -name "scri-plugin-*" ! -name "." -exec rm -rf {} +` emits the start path itself, whose basename is `plugins`, matching neither guard. The review suggests `-mindepth 1`. This design instead deletes the cleanup step entirely by switching to the subdirectory layout, where no stray files land beside the plugin. A deleted step cannot be got wrong later; a corrected one can.

## Section 1 — the publish manifest (M2)

### The manifest

A new tab-separated file at the repository root, `publish.projects.tsv`, with a header row and one row per shipped project:

```
id	project	condition	profile	output
scri	src/Scrinia/Scrinia.csproj	always	trimmed	rid
scri-merge	src/Scrinia.Merge/Scrinia.Merge.csproj	always	plain	rid
scri-plugin-embeddings	src/Scrinia.Plugin.Embeddings.Cli/Scrinia.Plugin.Embeddings.Cli.csproj	with-embeddings	plain	plugin
```

Columns:

- `id` — the published binary's name; also the plugin subdirectory name for `output=plugin`.
- `project` — path relative to the repository root, forward slashes.
- `condition` — `always`, or `with-embeddings` to publish only when the switch is given.
- `profile` — `trimmed` (self-contained, single-file, `PublishTrimmed`, trim warnings as errors) or `plain` (`--runtime` and `--configuration` only; the project file configures the rest).
- `output` — `rid` for the per-RID directory, `plugin` for `{rid_dir}/plugins/{id}`.

Tab-separated rather than JSON so `publish.sh` needs no `jq`, which is not guaranteed present. The header row exists so PowerShell can use `Import-Csv` with a tab delimiter rather than hand-splitting; `publish.sh` skips it with `tail -n +2`.

`.gitattributes` gains `publish.projects.tsv text eol=lf`. Without it a CRLF checkout leaves a carriage return on the last field of every row, which would silently corrupt the `output` column. The bash reader *also* strips it defensively — the attribute states the intent, the strip survives a user whose git configuration ignores it.

### Divergences fixed by hand

The manifest fixes the project list. These three it cannot reach:

1. **The destructive cleanup goes away.** `publish.sh` publishes the plugin into `{rid_dir}/plugins` and then runs the `find` described in C-4. Publishing into `{rid_dir}/plugins/scri-plugin-embeddings` instead — the layout `publish.ps1` already uses and the resolver prefers — means nothing strays beside the plugin, so the `find` is deleted rather than corrected. The `ext` variable computed at `publish.sh:80-84` and never used is deleted with it.
2. **The Vulkan overwrite is ported to `publish.sh`,** using `$HOME/.nuget/packages` where `publish.ps1` uses the Windows profile path, and the same `llamasharp.backend.vulkan.linux` package for non-Windows RIDs.
3. **A missing Vulkan source directory becomes loud in both scripts.** A one-line warning to stderr naming the path that was not found. This does not make macOS work — `osx-arm64` has no Vulkan backend package in this dependency set — but it converts a silent omission into a visible one.

Separately, `publish.ps1`'s `-WithVulkan` is renamed `-WithEmbeddings` to match `--with-embeddings`. The switch decides whether the embeddings plugin ships at all; Vulkan is the backend it happens to carry. Breaking change, recorded in the CHANGELOG.

### The pin

A test in `tests/Scrinia.Tests` asserting four things:

1. Every `project` path in the manifest exists on disk.
2. Neither `publish.sh` nor `publish.ps1` contains a literal `.csproj` path — the project list can only come from the manifest.
3. Every project under `src/` that produces an executable — `<OutputType>Exe</OutputType>` or `Sdk="Microsoft.NET.Sdk.Web"` — is either in the manifest or in a **named exclusion set**, each entry carrying its reason:
   - `Scrinia.AppHost` — Aspire development orchestrator, never shipped to users.
   - `Scrinia.Server` — ships as a container image (`src/Scrinia.Server/Dockerfile`, `docker-compose.yml`), not through the publish scripts.
   - The benchmark harnesses `Scrinia.Retrieval.Benchmarks` and `Scrinia.Benchmarks` live outside `src/` and so are outside the enumeration's root; they are named here so their absence is deliberate rather than incidental.
4. The LLamaSharp Vulkan backend version literal appearing in both scripts equals the `LLamaSharp.Backend.Vulkan` `PackageReference` version in `src/Scrinia.Plugin.Embeddings/Scrinia.Plugin.Embeddings.csproj` (`0.25.0` at the time of writing). A drift here makes the overwrite path not exist, which is exactly the silent skip fixed above.

Rule 3 is the one that matters: it is what makes "the next project added cannot land in only one script" true, because a new executable fails the test until someone decides.

## Section 2 — CLI conformance (L2)

### The conformance test

A test reflecting over the command entrypoints — public methods on `ScriniaCommands` and `BundleCommands` returning `int` or `Task<int>` — asserting three mechanical rules:

1. **No parameter is named `workspace`.** The workspace root parameter is `workspaceRoot` everywhere. Catches the live `migrate` defect.
2. **Every `bool` parameter defaults to `false`.** A flag is opt-in; a `bool backup = true` inverts the family's polarity and cannot be expressed as a bare `--flag`. Catches the live `migrate` defect.
3. **Every entrypoint returns `int` or `Task<int>`.** A command returning `void` or `Task` cannot report failure at all. Nothing violates this today; the rule keeps it that way.

Each rule carries a named exclusion set, empty at merge, following the pattern established by the branch 7a round-trip pin: an exclusion must be named and justified in code, never silently absent.

XML `<param>` documentation is deliberately *not* asserted. It is not visible to runtime reflection without `GenerateDocumentationFile` and a doc-XML parse, and the cost outweighs the defect class.

### `scri migrate`

Three changes, all breaking, all recorded:

- `--workspace` becomes `--workspace-root` (parameter `workspace` → `workspaceRoot`).
- `--backup` (default `true`) becomes `--skip-backup` (default `false`), matching `migrate_attributes` at `ScriniaCommands.Migration.cs:340`. The call through to `StoreLayoutMigration.Migrate(root, dryRun, backup)` passes `!skipBackup`.
- `--json` is added, emitting a new `CliStoreMigrateOutput` record through `CliJsonContext`, carrying the fields the human path prints: `Ran`, `Moved`, `Conflicts`, `BackupPath`, `BackupSkipped`, `DryRun`.

`docs/cli-reference.md:497` is updated to the new signature.

While adding the record: the existing `CliMigrateOutput` doc comment (`CliJsonContext.cs:93`) says it is the output of `scri migrate`. It is the output of `scri migrate_attributes`. Corrected in place — the two records would otherwise be indistinguishable to a reader.

### `scri hint --json`

`ScriniaCommands.HintCommand.cs:92-97` builds JSON by string interpolation, escaping `Name` with a two-character escaper and interpolating `Scope` with no escaping at all. Replaced with two records — `CliHintOutput(int Count, CliHintMatch[] Matches)` and `CliHintMatch(string Scope, string Name, double Score)` — serialized through the existing `CliJsonContext` and `WriteJson`. `JsonEscape` (`:149`) is deleted; it has no other caller.

The hook envelope is *not* affected: `Hint.BuildHookEnvelope` (`src/Scrinia/Commands/Hint.cs:215-228`) already uses `Utf8JsonWriter` correctly. Only `--json` hand-rolls.

Pinned by a test storing a memory whose name contains characters JSON must escape, running the `--json` path, and parsing the result with `JsonDocument` — which the current implementation fails.

### The environment-variable convention

`WorkspaceSetup.GetConfigValue` (`src/Scrinia/Commands/WorkspaceSetup.cs:637-650`) performs two lookups. The first replaces `:` with `_`, collapses doubled underscores and uppercases, giving `SCRINIA_EMBEDDINGS_PROVIDER`. The second replaces `:` with `_` and `.` with `_` and uppercases — which, for any key containing no `.` and no doubled underscore, produces a byte-identical string to the first. Every key in the repository is such a key, so the second lookup can never find anything the first did not. The method's own doc comment claims `SCRINIA__EMBEDDINGS__PROVIDER` resolves. It does not.

The second lookup becomes the real double-underscore form — `:` replaced by `__`, uppercased — giving `SCRINIA__EMBEDDINGS__PROVIDER`. Single-underscore stays first and therefore keeps priority, so nothing existing changes behaviour.

This closes a real cross-surface gap: the server builds configuration through `WebApplication.CreateBuilder`, whose environment-variable provider maps `Scrinia__Embeddings__Provider` to `Scrinia:Embeddings:Provider` natively. Before this change the same key took one form on the server and a different one on the CLI.

`GetPluginName` (`:620-629`) carries the same single lookup and gains the same second form, so `plugins:embeddings` behaves like every other key.

The doc comment is corrected to state both forms and their precedence. `docs/cli-reference.md:551` gains the double-underscore form alongside the single. Pinned by a test setting each form in turn and asserting resolution, plus one asserting single wins when both are set.

### Exit codes

Per C-1, the mechanical part is rule 3 of the conformance test. The prose part is one comment at the final `return 0` in `ScriniaCommands.HintCommand.cs`, stating that a `UserPromptSubmit` hook must not fail the prompt and that the zero is therefore deliberate.

If the task-level audit finds an error path returning zero that this design's inspection missed, it is fixed and pinned with a test rather than deferred; the enumeration above is a claim about the tree, and a claim can be wrong.

## Section 3 — say what actually ships (L1a)

`MemoryEndpoints.IsWritable` (`src/Scrinia.Server/Endpoints/MemoryEndpoints.cs:133-136`) and `RequestContext.StoreAccessLevels` (`src/Scrinia.Server/Auth/RequestContext.cs:27`) read an SSO `store_access` claim. `Program.cs:513` populates the array from `context.User.FindAll("store_access")`. No `IClaimsTransformation` exists outside tests — the only implementation is `MarkTargetUserReadOnlyTransform` in `tests/Scrinia.Server.Tests/PatchEndpointTests.cs:836`, used there and in `CopyFromClientEndpointTests.cs:651` — and no authentication handler emits the claim. Every shipped configuration therefore evaluates as writable, at all eight `IsWritable` call sites.

This is forward-looking scaffolding, correctly built and honestly tested. The defect is that a reader of `MemoryEndpoints` concludes read-only stores are enforced today.

The fix is comments at the two declarations stating that nothing populates the claim in any shipped configuration, and naming what must be added for the gate to bite: an `IClaimsTransformation` registered outside tests, emitting `store_access` claims of the form `{store}:read-only`. No behaviour change, no test.

## Section 4 — the two carried items

**The plugin double-registration guardrail.** `EmbeddingsPlugin.ConfigureServices` registers `this` as `IMemoryOperationHook` (`src/Scrinia.Plugin.Embeddings/EmbeddingsPlugin.cs:119`); `Program.cs:114-115` registers the same instance again for any plugin implementing the interface. `AddSingleton` appends rather than replaces, so `GetServices<IMemoryOperationHook>()` returns the instance twice, and `PluginPipeline`'s constructor deduplicates with `Distinct()` on reference equality (`src/Scrinia.Server/Services/PluginPipeline.cs:16-25`). The comment explains this; no test holds it. A test builds a service collection with the same hook instance registered twice, constructs a `PluginPipeline`, drives one operation and asserts the hook fired once.

**Ephemeral coverage for `ReindexStoreAsync`.** Scrinia has four full-corpus reindex loops. `EmbeddingReindexer.ReindexAsync` and `ReindexViaSinkAsync` filter the ephemeral scope out; `BuiltInEmbeddingsService.ReindexStoreAsync` and `EmbeddingsPlugin.ReindexStoreAsync` do not, because they iterate `store.ListScoped()`, which includes ephemeral memories by design. A literal scope of `"ephemeral"` therefore reaches `MemoryArtifactSource` only from those two, and only they exercise its scope-keyed `IsEphemeral(scope)` check.

On 2026-08-17 a review residual asserted that check was provably dead and instructed its deletion; the claim came from inspecting only `EmbeddingReindexer`'s two loops. The implementer verified the other two and refused. No test drives `ReindexStoreAsync` against an ephemeral memory, so the deletion would have shipped green.

A test drives `BuiltInEmbeddingsService.ReindexStoreAsync` over a store containing an ephemeral memory and asserts the ephemeral memory is reached with scope `"ephemeral"`. The test's doc comment names what it protects, so the next person told the check is dead has something to run.

## Testing

Every change above is pinned except the L1a comments, which have no behaviour to pin. Three of the pins are the branch's real product — the manifest coverage rule, the CLI conformance rules, and the two carried guardrails — because each fails when a *future* instance diverges, which is the thing the review says nothing currently does.

The publish scripts themselves are not executed by any test. Asserting their behaviour would mean running `dotnet publish` for three RIDs. The pin therefore asserts their *inputs* — the manifest, the absence of hardcoded project paths, the version literal — and not their output.

## Non-goals

- The NMP/2 hardening backlog (negative pad-footer parse, unbounded per-chunk decode). Six-plus call sites share that decoder; changing its acceptance needs its own review.
- The pin residual parked on branch 7a (the round-trip guard's `MatchesBaseline` reference-equality gap).
- L1's genuinely dead code — `HnswIndex`, the unreachable RRF fusion path, `EntityGraph`'s instance side, `MergeConfig.Resolver`. That is L1b and a branch of its own.
- Deriving the LLamaSharp version from the project file rather than pinning the literal. Bash XML parsing to avoid a test that already catches the drift.
- Collapsing `publish.sh` into `publish.ps1`. Considered and rejected by the user; recorded above so a later reader knows it was a decision.
