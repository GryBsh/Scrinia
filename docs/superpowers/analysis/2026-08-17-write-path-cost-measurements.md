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
Allocation makes the shape unambiguous even without the timing: before, N=100 → 1,000 → 5,000 (a
50× growth in N) drives allocation from 45 MB → 4.31 GB → **107.65 GB** — over 2,000× growth for a
50× input increase, the signature of quadratic blowup from re-reading and re-JSON-parsing every
sidecar in the scope on every single write. After, the same 50× growth in N drives allocation from
553 KB → 5.23 MB → 26.17 MB, roughly 48× — close to the linear growth O(N) predicts.
`MarginalUpsert` tells the same story from the other side — the cost a user actually feels on one
more write into an already-large scope grows with N *before* (14.8 ms → 145 ms → 684 ms as N goes
100 → 1,000 → 5,000, consistent with "one write costs O(N)") and stays flat *after* (10.3 ms →
6.2 ms → 5.0 ms, consistent with O(1)). The N=100 `MarginalUpsert` row is the one place in this
table where the number should be read qualitatively rather than trusted for a precise ratio: the
after-side error bar (±27.9 ms) is larger than its own mean, because at N=100 the true per-write
cost is only a few milliseconds either way and JIT/GC/process noise dominates a signal that small.
The direction is still correct (10.3 ms < 14.8 ms); the *magnitude* of that particular ratio is not
load-bearing evidence — N=1,000 and N=5,000, where the signal is orders of magnitude larger than
the noise floor, are.

**Vector write path.** K=10/50/200 are as clean as the store-write numbers: 6.8×, 25.4×, 100×
faster, with the before-side mean scaling in rough proportion to K (4.8 ms → 24.8 ms → 94.2 ms →
384.3 ms), consistent with "1 + K lock acquisitions" collapsing to a flat ~3.6-5.5 ms regardless of
K after. **K=1 is the one number that does not move in the predicted direction, and it is not a
contradiction of the premise — it is exactly what the code predicts once worked through.** At K=1
the pre-fix code runs `RemoveAsync` (an early no-op the very first time a memory is indexed, since
there is nothing to remove) followed by exactly one `UpsertAsync`: one lock, one file open. The
post-fix code runs exactly one `ReplaceMemoryVectorsAsync`: also one lock, one file open. There is
no operation-count difference to exploit at K=1, and the file-operation-count table confirms this
precisely — Read/Write/Other ops are **identical**, 1/1/26, in both before and after at K=1. The
~5 ms mean in both cases is fixed per-call overhead (embedding call, lock acquisition, file
open/close), not the thing this branch touched, and the timing "regression" (5.548 ms after vs.
4.821 ms before) sits well inside both rows' own confidence intervals — noise, not a real
regression. The file-operation-count table is the most decisive evidence in this whole document
for the "K locked writes collapsed to one" claim, specifically because it has none of the timing
noise: `OtherOps` (which is where file-lock/open/close syscalls land) is exactly `26 × K` before,
for every K tested, and exactly `26` after, for every K tested — a syscall count does not have a
confidence interval to hide behind.

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

**One honest caveat about methodology, not results.** The "after" runs for both benchmark classes
used BenchmarkDotNet's in-process toolchain; "before" runs used the default out-of-process
toolchain. This was not a choice made for either side's benefit — see Methodology below for why it
was forced, and why it does not threaten any of the conclusions above given the effect sizes
involved (a few milliseconds of process/JIT overhead against ratios in the tens to hundreds, and
against a 25-minute single sample).

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
in-process toolchain turned out to have its *own* limit: `InProcessEmitExecutor` refuses to run a
benchmark it judges "takes too long" (observed on the very first attempt at N=5,000
`SequentialUpserts`, which is genuinely long-running pre-fix). The "before" runs therefore used the
default out-of-process toolchain instead, which has no such ceiling. Both toolchains measure the
same managed code with the same JIT; the difference is a few milliseconds of process startup and
warm-up overhead, immaterial against effect sizes of 2× to 2,000×+ and against a 25-minute single
sample. This is recorded as a Scrinia finding
(`/findings/benchmarkdotnet-stray-worktree-project-collision`) for whoever next runs a benchmark in
this workspace.

**Reduced sample count at N=5,000 (store write path).** `SequentialUpserts` at N=5,000 pre-fix
allocates 107.65 GB and takes 25.49 minutes for a *single* iteration; `MarginalUpsert` at the same N
re-seeds that same 5,000-entry scope, unmeasured, before every iteration, so its single sample took
59.8 minutes wall-clock. Running BenchmarkDotNet's default 10-iteration Monitoring job at this N
would have cost multiple hours per benchmark with no proportionate gain in confidence given how
large the effect already is. A single real sample was taken instead
(`--iterationCount 1 --warmupCount 0`) for both benchmarks at N=5,000, pre-fix only — post-fix
N=5,000 runs in ~17 s per iteration and used the full default 10-iteration job like every other
cell in this document. `RunStrategy.Monitoring` still makes the N=5,000 pre-fix numbers a genuine,
non-amortized measurement of one real invocation each; they simply carry no confidence interval.
Every other cell in Part 1 and all of Part 2 used BenchmarkDotNet's default Monitoring settings
(`WarmupCount=1, IterationCount=10`) unmodified.

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
