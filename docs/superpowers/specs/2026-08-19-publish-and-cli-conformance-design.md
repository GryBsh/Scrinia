# Publish and CLI Conformance — Design

**Date:** 2026-08-19
**Findings:** M2, L1a, L2 of the 2026-08-16 whole-codebase design review, plus two items carried from branch 4 (embeddings-search-consolidation).
**Branch:** `fix/publish-and-cli-conformance`, based on `main` after the branch 7a merge.

## The shape

Five surfaces grew instance by instance with nothing forcing them to agree:

- three project lists — two developer publish scripts and the release workflow — sharing no source of truth,
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

The review's findings were re-verified against the tree at `main`. Seven statements need amending, and the design depends on the amended versions. C-5 and C-6 were found by a falsification pass over an earlier draft of this spec, and each overturned something the draft asserted.

**C-1. L2's exit-code claim is overstated.** The review says "several commands return 0 unconditionally regardless of outcome." All 23 command entrypoints were enumerated and inspected. The only genuine always-zero command is `hint`, and it is *correct*: a `UserPromptSubmit` hook that exits non-zero can fail the user's prompt. `migrate` and `migrate_attributes` also end in an unconditional `return Task.FromResult(0)`, but each has a real failure path above it (`Migrate` returns 1 for a missing `.scrinia/`; `MigrateAttributes` throws for a non-file-backed store). `facts` routes through `EmitMcpResult`, which returns non-zero on error. `BundleCommands` is a pure forwarder to implementations that do return non-zero. So this item becomes: document `hint`'s deliberate always-zero at the return, and pin *the ability to fail* mechanically rather than auditing prose.

**C-2. The two plugin output layouts are both supported; neither script is broken on that axis.** `WorkspaceSetup.ResolveEmbeddingsPluginExe` (`src/Scrinia/Commands/WorkspaceSetup.cs:492-494`) tries `plugins/{name}/{name}{ext}` first and `plugins/{name}{ext}` second. `publish.ps1` produces the first, `publish.sh` the second. The layout is therefore a free choice, and this design picks the subdirectory form for a reason unrelated to discovery: it removes the need for the destructive cleanup step (below).

**C-3. The review names `publish.sh:41-42, :85` and the missing merge driver. It does not name three further divergences that the same fix must cover**, or the manifest will be declared a success while the scripts still disagree:

- the switch is `--with-embeddings` in `publish.sh` and `-WithVulkan` in `publish.ps1`;
- `publish.ps1` overwrites the published native DLLs with the Vulkan-compiled variants (`publish.ps1:60-73`); `publish.sh` does not, so the same switch ships Vulkan on Windows and CPU-only on Linux;
- both scripts silently skip that overwrite when the source directory is absent, and on `osx-arm64` the path always resolves to the *linux* package (`publish.ps1:65-69`), so macOS silently never gets it.

**C-4. The `find` is destructive for the reason the review gives, and the fix it suggests is not the one this design takes.** `find "$plugins_dir" -maxdepth 1 ! -name "scri-plugin-*" ! -name "." -exec rm -rf {} +` emits the start path itself, whose basename is `plugins`, matching neither guard. The review suggests `-mindepth 1`. This design instead deletes the cleanup step entirely by switching to the subdirectory layout, where no stray files land beside the plugin. A deleted step cannot be got wrong later; a corrected one can.

**C-5. There is a third project list, and it is the one that ships.** `.github/workflows/release.yml` hardcodes four project paths (`:73`, `:83`, `:102`, `:162`) and is what produces the archives users download. Its matrix pairs each RID with a matching OS runner — `win-x64`/`windows-latest`, `linux-x64`/`ubuntu-latest`, `osx-arm64`/`macos-latest` — so each platform is built natively.

Two consequences, both of which change this design:

- **The review's M2 cost statement is wrong.** It says the missing merge driver means "silent data-corruption risk on non-Windows installs". It does not: `release.yml:83` publishes `Scrinia.Merge` for all three RIDs, so a user installing from a release artifact gets `scri-merge` on Linux and macOS. The real exposure is narrower and worth stating accurately — a developer who builds locally with `publish.sh` gets a tree whose merge driver is missing. That is a genuine defect; it is not the shipped product's.
- **The pin's guarantee has to be stated against three files, not two,** or "the next project added cannot land in only one list" is false: it could land in the manifest and never reach the release.

**C-6. The two-character escaper in `hint --json` is correct for the two characters it handles.** `ScriniaCommands.HintCommand.cs:149-150` is `s.Replace("\\", "\\\\").Replace("\"", "\\\"")` — backslash first, then quote, which is the right order. A name containing `"` or `\` round-trips correctly today. The actual holes are narrower and must be named accurately, because a test written against the wrong one passes before and after the fix and proves nothing:

- **control characters** U+0000–U+001F, which RFC 8259 forbids raw inside a string and which `JsonEscape` passes through untouched;
- **`m.Scope`**, interpolated at `:96` with no escaping at all.

The consolidation argument is unchanged — a hand-rolled escaper beside a source-generated context is the defect shape — but the spec must not claim the escaper is broken for cases where it is not.

**C-7. `scri migrate`'s backup flag was not merely inverted — it was unreachable.** The review calls this an inconsistency ("inverts the backup flag"). Measured against the built CLI, ConsoleAppFramework binds a `bool` parameter as a bare presence flag and accepts no value syntax for it at all:

```
scri migrate --skip-backup            -> accepted
scri migrate --skip-backup=false      -> Argument '--skip-backup=false' is not recognized.
scri migrate --skip-backup false      -> Argument 'false' is not recognized.
```

A parameter declared `bool backup = true` therefore has no off switch in any syntax. Before this branch, `docs/cli-reference.md` documented `scri migrate [--backup=true|false]` — a form the parser rejects — so every `scri migrate` run wrote a backup and no user could decline it.

Two consequences. The CHANGELOG entry is a bug fix, not a rename: `--skip-backup` is the first way to actually skip the backup, and the previously documented `--backup=false` never worked. And the conformance rule "every bool option defaults to false" is a correctness rule rather than a style preference — a default-`true` bool is an option that cannot be exercised — which is how its failure message is now worded.

## Section 1 — the publish manifest (M2)

### The manifest

A new tab-separated file at the repository root, `publish.projects.tsv`, with a header row and one row per shipped project:

```
id	project	condition	profile	output	rids
scri	src/Scrinia/Scrinia.csproj	always	trimmed	rid	all
scri-merge	src/Scrinia.Merge/Scrinia.Merge.csproj	always	plain	rid	host
scri-plugin-embeddings	src/Scrinia.Plugin.Embeddings.Cli/Scrinia.Plugin.Embeddings.Cli.csproj	with-embeddings	plain	plugin	all
```

Columns:

- `id` — the published binary's name; also the plugin subdirectory name for `output=plugin`.
- `project` — path relative to the repository root, forward slashes.
- `condition` — `always`, or `with-embeddings` to publish only when the switch is given.
- `profile` — `trimmed` (self-contained, single-file, `PublishTrimmed`, trim warnings as errors) or `plain` (`--runtime` and `--configuration` only; the project file configures the rest).
- `output` — `rid` for the per-RID directory, `plugin` for `{rid_dir}/plugins/{id}`.
- `rids` — `all`, or `host` for a project that can only be built for the machine doing the building.

`condition` and `rids` are separate columns on purpose. They are orthogonal — one is about a user's switch, the other about what the toolchain can do — and folding them into one column is how a later row that needs both ends up unexpressible.

### Why `scri-merge` is `rids=host`

`Scrinia.Merge` sets `PublishAot=true` (`src/Scrinia.Merge/Scrinia.Merge.csproj:7`), and NativeAOT cannot cross-compile between operating systems. Measured rather than assumed, on Windows:

```
dotnet publish src/Scrinia.Merge/Scrinia.Merge.csproj --runtime linux-x64 --configuration Release --output <tmp>
→ error : Cross-OS native compilation is not supported.
→ exit code 1, output directory empty
```

The exit code matters: under `set -euo pipefail` a foreign-RID merge publish would abort the whole `publish.sh` run, taking the `scri` builds that succeed today with it. Adding `scri-merge` to the default all-RID loop unconditionally would therefore be a *regression*, not the fix the review asks for.

`publish.ps1` has this latent bug today — it publishes `Scrinia.Merge` for every RID in the loop and would throw on the first foreign one. Every invocation documented in `README.md:118`, `docs/getting-started.md:51` and `docs/cli-reference.md:589` passes `-Platform win-x64`, which is why nobody has hit it.

So both scripts skip a `host` row for any non-host RID, printing a warning that names what was skipped and why. A developer building for their own platform gets the merge driver; a developer building all three gets it for their own and a clear line about the other two. `release.yml` is unaffected: it builds each RID on that RID's own runner, which is why the shipped artifacts have always had `scri-merge` for all three platforms.

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
2. Neither `publish.sh` nor `publish.ps1` contains the literal `.csproj` — the project list can only come from the manifest. A plain substring check, including inside comments: a looser path-shaped regex would let a comment reintroduce a project path, and the mutation that proves this rule works is exactly such a comment.
3. Every project under `src/` that produces an executable — `<OutputType>Exe</OutputType>` or `Sdk="Microsoft.NET.Sdk.Web"` — is either in the manifest or in a **named exclusion set**, each entry carrying its reason:
   - `Scrinia.AppHost` — Aspire development orchestrator, never shipped to users.
   - `Scrinia.Server` — shipped by `release.yml`'s `build-server` job (`:162`) and as a container image (`src/Scrinia.Server/Dockerfile`, `docker-compose.yml`), never by the developer publish scripts, which build the CLI and what it loads.
   - The benchmark harnesses `Scrinia.Retrieval.Benchmarks` and `Scrinia.Benchmarks` live outside `src/` and so are outside the enumeration's root; they are named here so their absence is deliberate rather than incidental.
4. The LLamaSharp Vulkan backend version literal appearing in both scripts equals the `LLamaSharp.Backend.Vulkan` `PackageReference` version in `src/Scrinia.Plugin.Embeddings/Scrinia.Plugin.Embeddings.csproj` (`0.25.0` at the time of writing). A drift here makes the overwrite path not exist, which is exactly the silent skip fixed above.
5. Every manifest project appears in a `dotnet publish` line in `.github/workflows/release.yml`, and every project `release.yml` publishes is either in the manifest or in the same named exclusion set. This is what extends the guarantee to the list that actually ships (C-5).

Rules 3 and 5 together are what matter: a new executable fails rule 3 until someone puts it in the manifest or names why not, and fails rule 5 until the release workflow ships it too. Rule 3 alone would let a project be added to the manifest and never reach a user.

`release.yml` keeps its hand-written publish lines. Driving a GitHub Actions matrix from the manifest means restructuring a workflow that cannot be exercised without cutting a release, and the pin already makes a divergence fail in CI. That trade is recorded in Non-goals.

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

`ScriniaCommands.HintCommand.cs:92-97` builds JSON by string interpolation. Replaced with two records — `CliHintOutput(int Count, CliHintMatch[] Matches)` and `CliHintMatch(string Scope, string Name, double Score)` — serialized through the existing `CliJsonContext` and `WriteJson`. `JsonEscape` (`:149`) is deleted; it has no other caller.

The hook envelope is *not* affected: `Hint.BuildHookEnvelope` (`src/Scrinia/Commands/Hint.cs:215-228`) already uses `Utf8JsonWriter` correctly. Only `--json` hand-rolls.

Per C-6, the escaper is correct for `"` and `\`; the reachable defect is a **control character** in a name, which it passes through raw and no JSON parser accepts. That is what the pin uses: a seeded name carrying a newline as well as a quote and a backslash, run through the `--json` path and parsed with `JsonDocument`. The current implementation fails it; a name carrying only the quote and backslash would pass before and after and prove nothing.

Reachable because a path segment and an entry name are different things: `MemoryNaming.TrySanitizeSegment` sanitizes the *filename*, while a store write keeps the entry's `Name` verbatim in the index, which is what `hint`'s matches read. Bundle import writes entry names straight from an external bundle, so the route is an untrusted one rather than only a theoretical one.

### The environment-variable convention

`WorkspaceSetup.GetConfigValue` (`src/Scrinia/Commands/WorkspaceSetup.cs:637-650`) performs two lookups. The first replaces `:` with `_`, collapses doubled underscores and uppercases, giving `SCRINIA_EMBEDDINGS_PROVIDER`. The second replaces `:` with `_` and `.` with `_` and uppercases — which, for any key containing no `.` and no doubled underscore, produces a byte-identical string to the first. Every key in the repository is such a key, so the second lookup can never find anything the first did not. The method's own doc comment claims `SCRINIA__EMBEDDINGS__PROVIDER` resolves. It does not.

The second lookup becomes the real double-underscore form — `:` replaced by `__`, uppercased — giving `SCRINIA__EMBEDDINGS__PROVIDER`. Single-underscore stays first and therefore keeps priority, so nothing existing changes behaviour.

This closes a real cross-surface gap: the server builds configuration through `WebApplication.CreateBuilder`, whose environment-variable provider maps `Scrinia__Embeddings__Provider` to `Scrinia:Embeddings:Provider` natively. Before this change the same key took one form on the server and a different one on the CLI.

`GetPluginName` (`:620-629`) carries the same single lookup and gains the same second form, so `plugins:embeddings` behaves like every other key.

The doc comment is corrected to state both forms and their precedence. `docs/cli-reference.md:555` — the settings-resolution bullet, not the `## Configuration` heading two lines above it — gains the double-underscore form alongside the single. Pinned by a test setting each form in turn and asserting resolution, plus one asserting single wins when both are set.

### Exit codes

Per C-1, the mechanical part is rule 3 of the conformance test. The prose part is one comment at the final `return 0` in `ScriniaCommands.HintCommand.cs`, stating that a `UserPromptSubmit` hook must not fail the prompt and that the zero is therefore deliberate.

If the task-level audit finds an error path returning zero that this design's inspection missed, it is fixed and pinned with a test rather than deferred; the enumeration above is a claim about the tree, and a claim can be wrong.

## Section 3 — say what actually ships (L1a)

`MemoryEndpoints.IsWritable` (`src/Scrinia.Server/Endpoints/MemoryEndpoints.cs:133-136`) and `RequestContext.StoreAccessLevels` (`src/Scrinia.Server/Auth/RequestContext.cs:27`) read an SSO `store_access` claim. `Program.cs:513` populates the array from `context.User.FindAll("store_access")`. No `IClaimsTransformation` exists outside tests — the only implementation is `MarkTargetUserReadOnlyTransform` in `tests/Scrinia.Server.Tests/PatchEndpointTests.cs:836`, used there and in `CopyFromClientEndpointTests.cs:651` — and no authentication handler emits the claim. Every shipped configuration therefore evaluates as writable, at every `IsWritable` call site — seven in `MemoryEndpoints`, one in `EmbeddingsEndpoints:57`, and `Program.cs:339`, which is the one gating the MCP surface.

This is forward-looking scaffolding, correctly built and honestly tested. The defect is that a reader of `MemoryEndpoints` concludes read-only stores are enforced today.

The fix is comments at the two declarations stating that nothing populates the claim in any shipped configuration, and naming what must be added for the gate to bite: an `IClaimsTransformation` registered outside tests, emitting `store_access` claims of the form `{store}:read-only`. No behaviour change, no test.

## Section 4 — the two carried items

**The plugin double-registration guardrail.** `EmbeddingsPlugin.ConfigureServices` registers `this` as `IMemoryOperationHook` (`src/Scrinia.Plugin.Embeddings/EmbeddingsPlugin.cs:118`); `Program.cs:114-115` registers the same instance again for any plugin implementing the interface. `AddSingleton` appends rather than replaces, so `GetServices<IMemoryOperationHook>()` returns the instance twice, and `PluginPipeline`'s constructor deduplicates with `Distinct()` on reference equality (`src/Scrinia.Server/Services/PluginPipeline.cs:16-25`). The comment explains this; no test holds it. A test builds a service collection with the same hook instance registered twice, constructs a `PluginPipeline`, drives one operation and asserts the hook fired once.

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
- Driving `.github/workflows/release.yml`'s matrix from the manifest. The workflow cannot be exercised without cutting a release, and rule 5 of the pin already fails when its list and the manifest disagree. Restructuring it is a separate change with its own risk.
- Fixing `publish.ps1`'s pre-existing inability to build every RID in one run for reasons other than `scri-merge`. The `rids=host` column addresses the AOT case this branch introduces into `publish.sh`; any other cross-RID limitation is out of scope.
