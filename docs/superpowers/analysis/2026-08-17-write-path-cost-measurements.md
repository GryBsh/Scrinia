# Write-path cost — measurements

**Task 7 of the 2026-08-17 write-path-cost plan.** Tasks 1-6 removed the write-path costs this
document measures: `FileMemoryStore.Upsert` no longer re-reads and re-parses every sidecar in a
scope on each write, and `MemoryVectorIndexer.IndexAsync` writes a memory's K vectors as one
locked file operation instead of K. This document is the evidence the branch rests on —
benchmarks are evidence, never the design target, and nothing in production code was tuned to
move a number here. Where a number was surprising or didn't move the way the premise predicts,
that is called out explicitly below rather than smoothed over.

Harnesses: `tests/Scrinia.Benchmarks/WritePathBenchmarks.cs`,
`tests/Scrinia.Benchmarks/VectorWriteBenchmarks.cs`. Reproduce with
`scripts/run-write-path-benchmarks.ps1` (see that script's header for what it does and does not
cover — it reproduces "after" numbers against whatever commit you run it from; "before" numbers
require the worktree procedure described in the Methodology section below).

---

## Environment

```
BenchmarkDotNet v0.15.4, Windows 11 (10.0.26300.9032)
AMD Ryzen 5 9600X 3.90GHz, 1 CPU, 12 logical and 6 physical cores
.NET SDK 10.0.400-preview.0.26322.102
  [Host] : .NET 10.0.9 (10.0.9, 10.0.926.27113), X64 RyuJIT x86-64-v4
```

**"Before"** = `f89f204` (the branch's merge-base — the write path as H5 found it, before any of
tasks 1-6). **"After"** = `58639b8` (this branch's `HEAD` at the time of this measurement, tasks
1-6 complete). Both benchmark files were copied unchanged into a temporary worktree at `f89f204`
to produce the "before" numbers; the worktree was discarded afterward, and nothing on
`fix/write-path-cost` was checked out, reset, or reverted to get them — see Methodology.

---

## Part 1 — Store write path (`FileMemoryStore.Upsert`)

### 1a. `SequentialUpserts` — N sequential `Upsert` calls into a scope that starts empty

This is the benchmark that demonstrates the O(N²) → O(N) shape rather than asserting it.

| Entries | Before mean (N samples) | After mean (N samples) | Speedup | Alloc before | Alloc after |
|---:|---:|---:|---:|---:|---:|
| 100 | 924.4 ms ± 89.6 ms (99.9% CI, n=10) | 497.7 ms ± 135.4 ms (99.9% CI, n=10) | 1.9× | 45.02 MB | 553.5 KB |
| 1,000 | 61.10 s ± 1.02 s (99.9% CI, n=10) | 4.159 s ± 1.13 s (99.9% CI, n=10) | 14.7× | 4.31 GB | 5.23 MB |
| 5,000 | 25.49 min (n=1, single sample) | 16.99 s ± 1.50 s (99.9% CI, n=10) | ~90× | **107.65 GB** | 26.17 MB |

### 1b. `MarginalUpsert` — one `Upsert` into a scope already holding N entries

The cost a user with an established workspace actually feels on the next write.

| Entries | Before mean (N samples) | After mean (N samples) | Speedup | Alloc before | Alloc after |
|---:|---:|---:|---:|---:|---:|
| 100 | 14.78 ms ± 1.21 ms (n=10) | 10.27 ms ± 27.91 ms (n=10) | ~1.4× (noise-dominated, see Reading) | 932.2 KB | 38.56 KB |
| 1,000 | 145.4 ms ± 38.9 ms (n=10) | 6.231 ms ± 12.36 ms (n=10) | ~23× | 8.83 MB | 157.8 KB |
| 5,000 | 683.6 ms (n=1, single sample) | 4.996 ms ± 1.15 ms (n=10) | ~137× | 44.16 MB | 690.9 KB |

---

## Part 2 — Vector write path (`MemoryVectorIndexer.IndexAsync`)

### 2a. `IndexOneMemory` — indexing one memory of K vectors

| Vectors (K) | Before mean (n samples) | After mean (n samples) | Speedup | Alloc before | Alloc after |
|---:|---:|---:|---:|---:|---:|
| 1 | 4.821 ms ± 8.337 ms (n=10) | 5.548 ms ± 7.078 ms (n=10) | ~parity — see Reading | 12.3 KB | 12.72 KB |
| 10 | 24.83 ms ± 11.00 ms (n=10) | 3.639 ms ± 0.595 ms (n=10) | 6.8× | 135.9 KB | 40.26 KB |
| 50 | 94.25 ms ± 8.63 ms (n=10) | 3.717 ms ± 0.472 ms (n=10) | 25.4× | 681.5 KB | 147.4 KB |
| 200 | 384.3 ms ± 27.8 ms (n=10) | 3.826 ms ± 0.575 ms (n=10) | 100× | 2.73 MB | 556.8 KB |

### 2b. File-operation counts (ad hoc, see Methodology — not part of the committed harness)

Real Win32 I/O syscall counts (`GetProcessIoCounters` delta around one steady-state
`IndexAsync` call, after a warm-up call to exclude first-call JIT/probe costs):

| Vectors (K) | Before: Read / Write / Other ops | After: Read / Write / Other ops |
|---:|---:|---:|
| 1 | 1 / 1 / 26 | 1 / 1 / 26 |
| 10 | 10 / 10 / 260 | 1 / 1 / 26 |
| 50 | 50 / 50 / 1,300 | 1 / 1 / 26 |
| 200 | 200 / 200 / 5,200 | 1 / 2 / 26 |

`WriteBytes` transferred was identical before/after at every K (31 / 310 / 1,550 / 6,200 bytes) —
same payload, same on-disk format, only the number of open/lock/write syscalls differs.

---

## Part 3 — Vectors per memory (reporting only, no code changed as a result)

Measured across the 26 persistent memories under this workspace's own `.scrinia/memories/`
(`local:findings` and `local:patterns` scopes), using `MemoryArtifactSource.ReadContentElementsAsync`
to reconstruct each memory's real content array and `TextChunker.SliceWindows` at the default
chunk size/overlap (1200/200) — the exact windowing `MemoryVectorIndexer.IndexAsync` performs,
with no embedding call involved.

| | |
|---|---:|
| Memories measured | 26 |
| Mean vectors/memory | 7.73 |
| Median vectors/memory | 6.5 |
| Min | 2 |
| Max | 19 |

Top 3 by vector count: `local:findings/store-boundaries-deferred-followups` (19 vectors from 16
elements), `local:findings/codebase-review-2026-07-31-dry-complexity-methodology` (17 from 8),
`local:findings/search-contract-deferred-followups` (15 from 11).

This is the input to the **deferred** decision on reducing vector *count* (branch 4's per-element
windowing). It is reported and nothing about it changes in this branch.

---

## Reading

**Store write path.** `SequentialUpserts` moves in the predicted direction at every N, and the gap
widens exactly the way O(N²) → O(N) predicts: 1.9× at N=100, 14.7× at N=1,000, ~90× at N=5,000.
Allocation makes the N=1,000 and N=5,000 rows unambiguous even without the timing: before, N=1,000
→ 5,000 (a 5× growth in N) drives allocation from 4.31 GB → **107.65 GB**, ~25× — matching the ~25×
theoretical O(N²) step almost exactly, the signature of quadratic blowup from re-reading and
re-JSON-parsing every sidecar in the scope on every single write. After, the same step drives
allocation from 5.23 MB → 26.17 MB, ~5×, matching O(N)'s linear prediction. **The N=100 row is
weaker evidence than the other two and should be read as directional, not precise.** Allocation at
N=100 does move the same direction (45 MB before vs. 553 KB after, ~83×), but that ratio does not
track the 1.9× timing ratio the way allocation and timing track each other at N=1,000/5,000 — at
924 ms vs. 498 ms, the absolute gap (~426 ms) is the smallest in this table, and it is measured
under the toolchain asymmetry described in Methodology (in-process "after" vs. out-of-process
"before"). Fixed per-iteration overhead (process/JIT, `IterationSetup`'s directory creation) is a
much larger fraction of a sub-second measurement than of a 25-minute one, so the 1.9× figure should
not be treated as precisely as the 14.7× and ~90× figures next to it — only that "before" is worse,
not by how much. `MarginalUpsert` tells the same story from the other side — the cost a user
actually feels on one more write into an already-large scope grows with N *before* (14.8 ms →
145 ms → 684 ms as N goes 100 → 1,000 → 5,000, consistent with "one write costs O(N)") and stays
flat *after* (10.3 ms → 6.2 ms → 5.0 ms, consistent with O(1)). The N=100 `MarginalUpsert` row
carries the same weakness twice over: it is the smallest absolute effect in the table (its
after-side error bar, ±27.9 ms, is larger than its own mean — JIT/GC/process noise dominates a
signal that small) *and* it inherits the toolchain asymmetry with no allocation backstop of the
kind that corroborates N=1,000/5,000 elsewhere. The direction is still correct (10.3 ms <
14.8 ms); neither the magnitude of that ratio nor the N=100 `SequentialUpserts` ratio is load-bearing
evidence on its own — N=1,000 and N=5,000, where timing and allocation corroborate each other and
both dwarf any plausible toolchain artifact, are.

**Vector write path.** K=50 and K=200 are as clean as the store-write N=1,000/5,000 rows: 25.4× and
100× faster, with allocation corroborating direction and rough proportion (before: 681.5 KB →
2.73 MB as K goes 50 → 200, a 4× growth matching the 4× growth in K; after: 147.4 KB → 556.8 KB,
also ~4×) — both scale with K as expected (each vector needs its own storage regardless of write
strategy), but the *absolute* gap between before and after at fixed K is what the timing ratio
reports, and at K=50/200 that gap (90 ms and 380 ms respectively) is large enough that toolchain
noise cannot plausibly explain it. **K=10 (6.8×) is weaker evidence and should be read as
directional, not precise, for the same reason N=100 above is:** the absolute gap (24.8 ms vs.
3.6 ms, ~21 ms) is small enough that the toolchain asymmetry between in-process "after" and
out-of-process "before" is a non-negligible fraction of it, and allocation at K=10 does not track
the timing ratio as tightly as it does at K=50/200 (135.9 KB vs. 40.26 KB is ~3.4×, well under the
6.8× timing ratio). **K=1 is the one number that does not move in the predicted direction, and it
is not a contradiction of the premise — it is exactly what the code predicts once worked through,
and it is the most rigorously corroborated row in this whole section.** At K=1 the pre-fix code
runs `RemoveAsync` (an early no-op the very first time a memory is indexed, since there is nothing
to remove) followed by exactly one `UpsertAsync`: one lock, one file open. The post-fix code runs
exactly one `ReplaceMemoryVectorsAsync`: also one lock, one file open. There is no operation-count
difference to exploit at K=1, and the file-operation-count table confirms this precisely —
Read/Write/Other ops are **identical**, 1/1/26, in both before and after at K=1, a toolchain-agnostic
syscall count rather than a timing that could be skewed by process/JIT differences. The ~5 ms mean
in both cases is fixed per-call overhead (embedding call, lock acquisition, file open/close), not
the thing this branch touched, and the timing "regression" (5.548 ms after vs. 4.821 ms before)
sits well inside both rows' own confidence intervals — noise, not a real regression. The
file-operation-count table as a whole is the most decisive evidence in this document for the "K
locked writes collapsed to one" claim, specifically because it has none of the timing noise or
toolchain-asymmetry risk that qualifies K=1/K=10 and N=100 above: `OtherOps` (where
file-lock/open/close syscalls land) is exactly `26 × K` before, for every K tested, and exactly
`26` after, for every K tested — a syscall count does not have a confidence interval, or a
toolchain, to hide behind. It is also, as noted in Methodology, the one measurement in this
document that is not reproducible via the committed harness — the strongest evidence here is also
the least reproducible.

**Vectors per memory.** The real corpus's mean of 7.73 vectors/memory (median 6.5, range 2-19)
is a modest multiplier, not the "branch 4 multiplied vector count 4-5×" figure the design spec's
Part C cites for the windowing change in the abstract — the ceiling that matters in practice is
bounded by how long a single memory typically runs, and this workspace's memories are mostly a few
hundred to a couple thousand characters, well under the 1,200-char default window before overlap
math produces a second window. The 26-memory sample is small — this is a working repository's own
memory store, not a synthetic stress corpus — so the mean and median should be read as "this is
what a real, lived-in workspace looks like today," not as a statistically tight estimate. Reported
as directed and unacted on: any decision about reducing vector count is explicitly out of this
branch's scope.

**One honest caveat about methodology, not results — and which numbers it actually touches.** The
"after" runs for both benchmark classes used BenchmarkDotNet's in-process toolchain; "before" runs
used the default out-of-process toolchain (forced by an environmental collision unrelated to either
side — see Methodology). This asymmetry applies to every row in this document, but its practical
risk is not uniform, because the risk scales with how large the measured gap is relative to a
plausible few-millisecond toolchain artifact. **Unambiguous, corroborated independently of
toolchain by allocation:** `SequentialUpserts`/`MarginalUpsert` at N=1,000 and N=5,000, and
`IndexOneMemory` at K=50 and K=200 — in each case the allocation delta is orders of magnitude and
tracks the timing delta closely. **Corroborated by a toolchain-agnostic syscall count:**
`IndexOneMemory` at K=1, where the ~parity claim rests on the file-operation-count probe rather than
timing at all. **Timing-only, under the disclosed toolchain asymmetry, and should be read
directionally rather than precisely:** `SequentialUpserts`/`MarginalUpsert` at N=100, and
`IndexOneMemory` at K=10 — the direction (before slower) is still almost certainly correct, but the
specific ratio (1.9×, ~1.4×, 6.8×) should not be quoted as a precise figure.

---

## Methodology

**Toolchain asymmetry, and why.** BenchmarkDotNet's default out-of-process toolchain generates a
throwaway csproj and resolves it by searching the *whole* repository tree for a project file
matching the benchmark assembly's name. This workspace carries a stray, unrelated git worktree
under `.claude/worktrees/agent-a0edb32f3c9d18d20/` that happens to contain its own checked-out copy
of `tests/Scrinia.Benchmarks/Scrinia.Benchmarks.csproj`, so that search finds two matches and
BenchmarkDotNet throws `NotSupportedException: Found more than one matching project file` before a
single iteration runs. Running "after" from the main checkout therefore requires the in-process
toolchain (`tests/Scrinia.Benchmarks/MonitoringInProcessConfig.cs`, `[Config(typeof(...))]` on both
benchmark classes) to skip project generation entirely. The temporary `f89f204` worktree used for
"before" has no nested `.claude/worktrees/` directory of its own (that path is git-ignored, so
`git worktree add` never materializes it there), so the collision does not occur there — but the
in-process toolchain turned out to have its *own* limit: the very first attempt at N=5,000
`SequentialUpserts` under `InProcessEmitToolchain` threw
`System.InvalidOperationException: ... takes too long to run. Prefer to use out-of-process
toolchains for long-running benchmarks.` before completing a single iteration. This is reported as
*observed behavior*, not a claim verified against BenchmarkDotNet 0.15.4's source — only the
compiled DLLs were available locally, and it is plausible rather than confirmed that this is a
deliberate, generalizable guard rather than something specific to this run. Whatever its exact
trigger, it fired here, and it is consistent with BenchmarkDotNet's own documented caveat that the
in-process toolchain shares process/JIT/GC state with the host in a way an out-of-process run does
not — a real, if likely small, source of divergence between the two toolchains, and a different and
larger risk than the process-startup overhead this section otherwise leans on. The "before" runs
therefore used the default out-of-process toolchain instead, which hit no such ceiling at any N
tested. Both toolchains measure the same managed code with the same JIT, and the dominant
difference between them is a few milliseconds of process-startup/warm-up overhead plus the
shared-state risk noted above. Against the N=1,000/5,000 and K=50/200 effect sizes (seconds to
minutes, and orders of magnitude in allocation) that is immaterial; against the N=100 and K=10
effect sizes (tens to hundreds of milliseconds) it is not automatically immaterial, which is why
those specific rows are called out as directional rather than precise in Reading above rather than
folded into one blanket "toolchain doesn't matter" claim. This is recorded as a Scrinia finding
(`/findings/benchmarkdotnet-stray-worktree-project-collision`) for whoever next runs a benchmark in
this workspace.

**Reduced sample count at N=5,000 (store write path).** `SequentialUpserts` at N=5,000 pre-fix
allocates 107.65 GB and takes 25.49 minutes for a *single* iteration; `MarginalUpsert` at the same N
re-seeds that same 5,000-entry scope, unmeasured, before every iteration, so its single sample took
59.8 minutes wall-clock. Running BenchmarkDotNet's default 10-iteration Monitoring job at this N
would have cost multiple hours per benchmark with no proportionate gain in confidence given how
large the effect already is. A single real sample was taken instead
(`dotnet build ... -c Release` then `dotnet run -c Release --project ... --no-build --
--iterationCount 1 --warmupCount 0`) for both benchmarks at N=5,000, pre-fix only — the same
`-c Release` configuration `scripts/run-write-path-benchmarks.ps1` enforces, run by hand rather than
through the script because these two invocations needed the narrowed iteration/warmup counts and,
for `SequentialUpserts`, a temporarily narrowed `[Params]` list so the build wasn't locked by the
already-running N=5,000 process. Post-fix N=5,000 runs in ~17 s per iteration and used the full
default 10-iteration job like every other cell in this document, also via `-c Release`.
`RunStrategy.Monitoring` still makes the N=5,000 pre-fix numbers a genuine, non-amortized
measurement of one real invocation each; they simply carry no confidence interval. Every other cell
in Part 1 and all of Part 2 used BenchmarkDotNet's default Monitoring settings
(`WarmupCount=1, IterationCount=10`) unmodified, all under `-c Release`.

**Why `RunStrategy.Monitoring` at all.** Both benchmark classes mutate real on-disk state inside
`IterationSetup`/`IterationCleanup` — a fresh scope per iteration, or a scope pre-seeded to N
entries. BenchmarkDotNet's default Throughput strategy is free to unroll several invocations inside
one iteration for a workload it judges fast, which would silently let a "one Upsert" benchmark
perform several unmeasured-adjacent writes between a single Setup/Cleanup pair, growing the scope
past the labeled N mid-iteration. Monitoring guarantees exactly one invocation per iteration,
closing that hole — see the doc comments on `WritePathBenchmarks`/`VectorWriteBenchmarks` for the
concrete failure mode this avoids.

**File-operation counts are not part of the committed, reproducible harness.** They were gathered
with a small ad hoc console probe (P/Invoke `GetProcessIoCounters`, one warm-up `IndexAsync` call
then a before/after snapshot around one measured call), run once against each of the before and
after `Scrinia.Core` builds, then discarded — not checked in, since the brief's file list for this
task did not call for a third permanent harness and BenchmarkDotNet's own timing/allocation output
already satisfies the committed deliverable. The method is documented here in enough detail to
reproduce by hand if a future task wants it committed.

**Vectors-per-memory harness.** A throwaway xunit `[Fact]` in `tests/Scrinia.Tests/`, run once via
`dotnet test --filter`, then deleted per the brief ("measure it, report it, change nothing" — the
harness itself is not a deliverable, only the number is).
