# Review remediation — design

**Date:** 2026-08-05
**Source:** the whole-codebase review of 2026-08-05, recorded at Scrinia
`/findings/whole-codebase-review-2026-08-05`. Six confirmed findings, C1–C6.
**Shape:** two branches. `merge-driver-correctness` (C2, C1, C3), then
`internal-consistency` (C4, C5, C6).

## Why two branches

The findings split along one line: is the feature broken, or is it merely untidy. C2 is
a driver that crashes on every real conflict and leaves no resolvable trail; C1 silently
serves stale results. Both need real tests and deserve review in isolation. C4–C6 cannot
break a merge.

C3 is merge documentation, and branch 1 changes what merges do, so C3 travels with
branch 1 — docs land with the behaviour they document.

## Established facts

Everything below was reproduced, not reasoned about. Re-verify before contradicting.

Against real git (2.54.0.windows.1), scratch repos, probe merge drivers:

1. Git invokes a merge driver with **extensionless temp files in the repo root**:
   `O=.merge_file_QkZbrI A=.merge_file_wyknvk B=.merge_file_d0zpzN`.
2. Git **does** expose the artifact path via `%P`:
   `P=.scrinia/memories/local/auth-flow.nmp2`. The current driver never asks for it.
3. When one side is unchanged, git resolves by blob OID at the tree level and **never
   invokes the driver** — for a two-branch merge. Not established for rebase,
   cherry-pick, stash-pop, or `git merge-file`.
4. When a driver exits non-zero, git reports `CONFLICT`, sets index state `UU`, and
   leaves the working-tree file **exactly as the driver left `%A`**. Git adds **no
   conflict markers of its own** for custom drivers. If markers are wanted, the driver
   writes them.

Against the **shipped `scri-merge` binary**, not a probe driver:

5. Every production invocation of the `nmp2` driver **crashes**. `%A` is
   `.merge_file_X`, so `Path.GetDirectoryName` returns `""` and
   `Path.GetFullPath("")` throws at `Nmp2ConflictHandler.cs:25`:

   ```
   System.ArgumentException: The path is empty. (Parameter 'path')
      at Scrinia.Merge.Nmp2ConflictHandler.Handle(...) Nmp2ConflictHandler.cs:line 25
   ```

   Execution never reaches the `return 0` at `:54`. The merge halts with a .NET stack
   trace and `CONFLICT`; git leaves the file `UU`, the working tree keeps ours, no
   markers are written, no sidecar is marked, and no `conflict/` directory is created.
   **This is not silent data loss** — an earlier characterisation of it as such came
   from reading `:54` and testing with probe drivers that returned 0 by construction.

6. The `meta` driver **works correctly**. Verified end-to-end: exit 0, keywords unioned,
   latest `updatedAt` wins. A conflict return on dissimilar keywords is the
   `JaccardThreshold` gate at `MetaJsonMerger.cs:23-25` behaving as designed, not a bug.

In .NET:

7. `Path.ChangeExtension(".merge_file_wyknvk", ".meta.json")` returns `".meta.json"` —
   a dotfile's whole name counts as its extension, so the artifact identity is lost.

In the tree:

8. `MetaJsonMerger` writes only on the resolved path (`MetaJsonMerger.cs:60`); its
   conflict path leaves `%A` untouched.
9. `.nmp2` is ASCII text — an `NMP/2 <bytes>B CRC32:<hex> BR+B64 C:<n>` header followed
   by base64 chunks. Markers can be inserted, at the cost of an invalid header CRC.
10. `HttpMemoryStore.GetStoreDirForScope` throws `NotSupportedException`
    (`HttpMemoryStore.cs:459`).

## Branch 1 — `merge-driver-correctness`

### C2. The `nmp2` driver crashes on every real conflict

Per fact 5, when both sides change a memory the driver throws an unhandled
`ArgumentException` and dies. Git reports `CONFLICT`, leaves the file `UU`, and the user
sees a .NET stack trace. None of the conflict-as-data payload is produced: no copies, no
`"conflicted": true`, no markers. `reconcile()` detects conflicts only by scanning for
`<<<<<<<` (`MemoryTools.Lifecycle.cs:99, :211, :592`), so it reports zero conflicts and
the user is left to resolve a binary-ish artifact by hand.

Even had the crash not occurred, the sidecar and copy work would have no-opped on
fact 7, and `return 0` at `:54` would have discarded theirs.

**Driver signature.** Both commands take the pathname:
`scri-merge <meta|nmp2> %O %A %B %P`.

Every path derivation moves from `%A` to `%P`:

| Derivation | Today | After |
|---|---|---|
| Sidecar | `ChangeExtension(%A)` → `.meta.json` | `ChangeExtension(%P)` → `auth-flow.meta.json` |
| Conflict dir | `GetDirectoryName(%A)` → `""` | `GetDirectoryName(%P)` |
| Copy filenames | `GetFileName(%A)` → `.merge_file_X` | `GetFileName(%P)` → `auth-flow.nmp2` |
| `MergeConfig.Load` | walks up from a temp file, never finds `.scrinia`, survives on the `GetCurrentDirectory()` fallback (`Program.cs:38-49`) | resolves from `%P` |

`meta` needs `%P` for the config row alone; its merge is content-only and otherwise
correct.

**Conflict behaviour.** On both-sides-changed, in order:

1. Write whole valid copies of ours and theirs to `conflict/current/<name>.nmp2` and
   `conflict/incoming/<name>.nmp2` beside the real artifact.
2. Set `"conflicted": true` on the real sidecar.
3. Write marker-wrapped content into `%A`, so `reconcile()`'s existing scan fires. The
   shape is the standard one `reconcile()` already parses
   (`MemoryTools.Lifecycle.cs:378-388`): the ours body, `<<<<<<<`/`=======`/`>>>>>>>`
   delimiters, the theirs body. Marker length honours `%L` if it is added later; until
   then the default seven characters is correct.
4. Return 1, so git halts the merge.

Markers are a **detection signal only**. The content `reconcile()` decodes comes from
the two whole copies, so an invalidated header CRC in the working-tree file costs
nothing. Per fact 4, step 3 is the driver's job — git will not do it.

**`reconcile()` gains one behaviour:** when resolving a conflicted `.nmp2`, prefer
`conflict/current` and `conflict/incoming` beside it over parsing the marker body.
Reassembling two half-streams of base64 into decodable artifacts is the failure mode
this design avoids.

**The trivial-merge fast path stays** (`Nmp2ConflictHandler.cs:8-20`). Fact 3 covers a
two-branch merge only. Twelve correct lines are not worth deleting on one experiment;
its comment is corrected to claim only what was established.

**Legacy invocation (auto-repair).** On a 4-argument call the driver reads
`merge.scrinia-<kind>.driver`, appends ` %P` if absent, writes it back, prints what
changed, and exits 1. Both keys, not just `nmp2`. Chosen in preference to refusing with
an instruction, accepting that a merge driver mutating git config is surprising; the
printed line is what makes it debuggable.

Two implementation constraints. The write targets **local repository config** — plain
`git config <key> <value>`, no `--global`; a driver must never alter a user's global git
state. And `Scrinia.Merge` is a standalone AOT/trimmed executable with no `Scrinia.Core`
dependency, so it shells out to `git` itself rather than reusing `RunGit` from
`ScriniaCommands.Setup.cs`. If the rewrite fails for any reason, exit 1 with the reason
printed — never proceed on the guessed path.

### C1. Bundle import leaves the entity-fact cache stale

`SaveIndexUnsafe` (`FileMemoryStore.cs:885-908`) is the only index-mutation path that
omits the `_factStore` invalidation its three siblings perform (`:934`, `:965`,
`:1004`). It is reachable from bundle import — `BundleFormatService.cs:258` →
`ImportTopicEntries:1658` → `SaveIndex:1691` — carrying entries whose `Attributes` are
exactly what the fact store is built from.

Consumers then serve stale data off the fast path at `:1126`: the `facts()` tool
(`MemoryTools.Facts.cs:65`), search (`MemoryTools.Core.cs:891`,
`PluginPipeline.cs:179`), and the typing sink (`EntityTypingEventHandler.cs:47`).

**Fix:** add the invalidation, matching the siblings.

### C3. Merge docs and the shipped skill describe files that do not exist

No `scrinia-merge-meta.sh`, no `.ps1`, no `post-merge` hook, no tracked
`.gitattributes` exists anywhere in the repo.

`docs/multi-user-setup.md` and `src/Scrinia.Mcp/skills/merge-safety.md` both point at
`scri setup --multi-user` instead of hand-run `git config` blocks. Copy-pasteable
commands are how these drifted from `ScriniaCommands.Setup.cs:334-337` in the first
place.

Specific corrections:

- Drop the `jq` and PowerShell prerequisites — neither driver exists.
- Drop the `*.nmp2 binary` claim; the generated file is
  `*.meta.json merge=scrinia-meta` + `*.nmp2 merge=scrinia-nmp2`, with no binary marking.
- `.gitattributes` is **generated by setup** (`Setup.cs:341-344`), not tracked.
- Update the `local/skills/qa.nmp2` example; skills are no longer store entries.
- **Delete** the post-merge hook section rather than implement it. Nothing has ever
  written that file. A now-failing merge plus `reconcile()` cover what it claimed.
- Describe the new behaviour: a conflicted memory stops the merge.

### Testing branch 1

**A real-git integration harness** in `tests/Scrinia.Merge.Tests`. Each case builds a
scratch repo in a temp directory, registers the driver as
`dotnet <Scrinia.Merge.dll> <kind> %O %A %B %P` — resolved from
`typeof(Nmp2ConflictHandler).Assembly.Location`, so no AOT publish is needed — writes
`.gitattributes`, manufactures a divergence, and shells out to `git merge`. Assertions
run against the working tree and index, never a direct `Handle()` call.

Non-parallel collection (each case spawns processes). Skips cleanly when `git` is absent
from `PATH`. Living in the existing project means CI needs no pipeline change.

| # | Case | Asserts |
|---|---|---|
| 1 | Both sides changed | `git merge` exits non-zero; index `UU`; `conflict/current/auth-flow.nmp2` and `conflict/incoming/auth-flow.nmp2` exist under the artifact's name; real sidecar has `"conflicted": true`; **driver stderr contains no `Unhandled exception`** |
| 2 | One side unchanged | Driver never invoked; theirs wins. Pins fact 3 so a future git change fails a test rather than a comment |
| 3 | Legacy 4-arg config | Both config keys rewritten; change printed; exit 1; a second `git merge` succeeds |
| 4 | Custom `conflictDir` in `merge.config` | Copies land in the configured directory — a regression guard on config loading. It does **not** distinguish the `GetCurrentDirectory()` fallback: git always runs drivers from the worktree root, so the fallback resolved to the same `.scrinia` the `%P` walk finds. The fix removes a coincidence, not an observable defect |
| 5 | `meta` driver | Still auto-resolves: union merge, exit 0. No regression from the added parameter |

Case 1 fails today on every assertion — the driver crashes before producing any of it.
Case 5 is a regression guard, not a fix: the `meta` driver already works (fact 6), and
must keep working once it takes `%P`.

**The existing 18 unit tests stay.** They pin the pure logic correctly; their only fault
was being mistaken for coverage of integrated behaviour (they call `Handle()` with
`<temp>/ours.nmp2` — a real extension in a real directory, the one shape production
never sends). They gain the new parameter and nothing else.

**C1 gets one test** in the store suite: build the fact store, import a bundle carrying
attributes, assert the imported entry's facts are present. Fails on today's code.

**CHANGELOG:** a Fixed entry, not a breaking change. Conflicted `.nmp2` merges already
stopped — with an unhandled `ArgumentException` and no way to resolve them through
`reconcile()`. They now stop with a usable conflict instead. The only behaviour a
multi-user workspace loses is the crash.

## Branch 2 — `internal-consistency`

### C4. The `WorkspaceRoot` unification is incomplete

`IMemoryStore.cs:220` says callers "previously reverse-engineered this". Seven still do:

- `MemoryTools.Core.cs:354-356`, `:729-731`, `:1631-1633` — `GetDirectoryName` twice
- `MemoryTools.Lifecycle.cs:67`, `:580-581` — one hop, wanting `.scrinia`
- `MemoryTools.Bundling.cs:82-83` — `GetDirectoryName` + `".."` + `GetFullPath`
- `ScriniaCommands.Bundle.cs:143` — `Combine(storeDir, "..", "exports")`, unnormalized

All are correct today, so this is not a live bug. It matters because the count went from
two hand-rolled algorithms to three; because `Bundling.cs:31` (Export) was converted
while `:82` (Import) three lines away was not; and because `Bundling.cs:91` feeds the
hand-derived value to `PathContainment.IsWithin`, the SEC-041 traversal guard.

**Fix:** all seven move to `store.WorkspaceRoot`, making the comment true.

Per fact 8 this is **behaviour-preserving in remote mode**: every one of those sites
already throws `NotSupportedException` there, so switching to a property that also
throws changes nothing.

### C5. Chat provider retry block duplicated verbatim

`OpenAiChatProvider`, `AnthropicChatProvider` and `GeminiChatProvider` each carry a
byte-identical ~48-line block — fields, constructor defaults, `EnsureClosed` +
`RetryPolicy.ExecuteAsync`, all four catch arms, the `circuitError` re-raise, and
`body.Length > 500 ? body[..500] + "..."`. Only the endpoint path and JSON context
differ. Confirmed not yet diverged.

**Fix:** extract to one helper returning `(HttpResponseMessage? Response, string? Error)`
— a return value, not a shared base class, because the current code sits inside
`yield return` iterators and the caller must do the yielding. The 500-char truncation
becomes one shared helper covering all four sites, including
`ResilientEmbeddingProvider.cs:70`.

### C6. `AgentLoop` truncation constant

`AgentLoop.cs:200-203` hardcodes `if (content.Length > 8000)`.

**Fix:** a named constant with a comment on why the limit exists, keeping the
`[... truncated]` marker. Deliberately **not** a rebuild of the `ContentTruncated` +
followUp contract — that value is LLM-facing, not part of the MCP response shape.

## Out of scope

Deliberately excluded, with reasons:

- **Implementing a post-merge hook.** Never existed; `reconcile()` and a failing merge
  cover it.
- **Replacing SHA-256 in `ApiKeyStore` with a KDF.** Reviewed and correct as-is: these
  are 32-byte CSPRNG server-generated secrets, not user passwords.
- **The `restore()` best-effort conflict scan** (`MemoryTools.Lifecycle.cs:594`, and its
  10240-char read window). Explicitly best-effort (`:600`); acceptable.
- **The `[Unreleased]` CHANGELOG sweep** — tracked separately at
  `/findings/file-entity-unification-deferred-followups`, triggered by cutting a release,
  not by this work.

## Verification

Both branches: `dotnet build Scrinia.sln -c Debug` clean, then `Scrinia.Tests`,
`Scrinia.Server.Tests` and `Scrinia.Merge.Tests` green. Never two `dotnet test`
invocations against one project concurrently, and never `dotnet build` during a test run.
