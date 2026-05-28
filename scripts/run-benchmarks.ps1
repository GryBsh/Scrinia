<#
.SYNOPSIS
    Runs scrinia-bench across all four (or selected) retrieval benchmarks.

.DESCRIPTION
    Builds the harness once and then invokes each subcommand with the right
    --data path under $DataRoot. Pass-through args after `--` go straight to
    scrinia-bench so ablation flags work (`-- --alpha-recency 1.0 --tau-days 90`).

    Headline runs use --mode bm25_embeddings. Add -Mode bm25 for the BM25-only
    baseline. Add -Split held_out to run LongMemEval on the 450-question
    held-out split.

.PARAMETER DataRoot
    Where prepare-datasets.ps1 dropped the datasets. Default: ./datasets/.

.PARAMETER Benchmark
    Which benchmarks to run. Default: all four.

.PARAMETER Mode
    Retriever mode: bm25 | bm25_embeddings (default).

.PARAMETER Split
    LongMemEval split: full | dev | held_out (default: full).

.PARAMETER TopK
    Top-K candidates per query. Default 50; gives us R@5/10/50 for free.

.PARAMETER Configuration
    Build config. Default Release.

.PARAMETER ExtraArgs
    Pass-through args to every scrinia-bench invocation. Useful for ablations.
    Both forms work; the script splits any space-containing tokens:
        -ExtraArgs "--alpha-recency","1.0","--tau-days","90"
        -ExtraArgs "--embed-provider vulkan"
        -ExtraArgs "--alpha-recency 1.0","--tau-days 90"

.PARAMETER Parallel
    When set, MemBench fans out one subprocess per category (11 total), throttled
    by -MaxConcurrency. ~10x wall-time speedup on many-cored boxes. Partial result
    JSONs land alongside the merged final result. Other benchmarks ignore -Parallel
    (their question counts don't justify the orchestration overhead).

.PARAMETER MaxConcurrency
    Cap on concurrent subprocesses when -Parallel is set. Default = logical CPU count.

.EXAMPLE
    ./scripts/run-benchmarks.ps1
    All four benchmarks, BM25+embeddings, default top-K.

.EXAMPLE
    ./scripts/run-benchmarks.ps1 -Benchmark longmemeval -Split held_out
    The 450-question held-out comparison number.

.EXAMPLE
    ./scripts/run-benchmarks.ps1 -ExtraArgs "--alpha-recency","1.0","--tau-days","90"
    Recency ablation sweep across all four.

.EXAMPLE
    ./scripts/run-benchmarks.ps1 -ExtraArgs "--embed-provider vulkan"
    Switch from Model2Vec (default) to the GGUF/Vulkan sentence-transformer.
    Auto-downloads the GGUF to ./models/ on first run.

.EXAMPLE
    ./scripts/run-benchmarks.ps1 -Benchmark membench -Parallel
    MemBench's 12k questions run as 11 concurrent category subprocesses.

.EXAMPLE
    ./scripts/run-benchmarks.ps1 -Benchmark membench -Parallel -ExtraArgs "--chunk prod"
    Chunked MemBench run, parallel across categories.
#>
[CmdletBinding()]
param(
    [string]$DataRoot = $null,
    [ValidateSet("convomem", "longmemeval", "locomo", "membench")]
    [string[]]$Benchmark = @("convomem", "longmemeval", "locomo", "membench"),
    [ValidateSet("bm25", "bm25_embeddings")]
    [string]$Mode = "bm25_embeddings",
    [ValidateSet("full", "dev", "held_out")]
    [string]$Split = "full",
    [int]$TopK = 50,
    [string]$Configuration = "Release",
    [string[]]$ExtraArgs = @(),
    [switch]$Parallel = $false,
    [int]$MaxConcurrency = [Environment]::ProcessorCount
)

# Stable category list for MemBench. Pulled from the dataset's per-category groupings
# (matches what the bench reports under per-category R@K). Hardcoded rather than
# discovered at runtime to keep the parallel fanout deterministic — if MemBench grows
# a new category upstream, add it here.
$script:MemBenchCategories = @(
    "RecMultiSession", "aggregative", "comparative", "conditional",
    "highlevel", "highlevel_rec", "knowledge_update", "lowlevel_rec",
    "noisy", "post_processing", "simple"
)

$ErrorActionPreference = "Stop"

# Tolerate both -ExtraArgs "--foo","bar" (idiomatic [string[]]) and
# -ExtraArgs "--foo bar" (single space-separated string). Without this,
# the merged token "--foo bar" reaches scrinia-bench as a single argv
# entry and the parser silently ignores it.
# Split each ExtraArgs entry on whitespace, drop empties, then strip wrapping
# single/double quotes from each token. PowerShell does not treat inner single
# quotes as delimiters inside an outer double-quoted string, so a literal value
# like --enrich-model 'qwen2.5:1.5b' would otherwise reach scrinia-bench as
# "'qwen2.5:1.5b'" (quotes and all) and downstream APIs reject it as an invalid
# name. Stripping here is safe for typical CLI values, which don't embed quotes.
$ExtraArgs = $ExtraArgs `
    | ForEach-Object { $_ -split '\s+' } `
    | Where-Object { $_ } `
    | ForEach-Object { $_ -replace "^['""]+|['""]+$", "" }

$RepoRoot = Split-Path $PSScriptRoot -Parent
if (-not $DataRoot) {
    $DataRoot = Join-Path $RepoRoot "datasets"
}

if (-not (Test-Path $DataRoot)) {
    Write-Error "DataRoot '$DataRoot' not found. Run ./scripts/prepare-datasets.ps1 first."
    exit 1
}

$BenchProject = Join-Path $RepoRoot "benchmarks\Scrinia.Retrieval.Benchmarks\Scrinia.Retrieval.Benchmarks.csproj"

Write-Host "building scrinia-bench ($Configuration)..."
dotnet build $BenchProject -c $Configuration --nologo -v quiet
if ($LASTEXITCODE -ne 0) {
    Write-Error "build failed"
    exit 1
}

function Invoke-Bench {
    # Parameter named "BenchArgs" rather than "Args" — PowerShell's $Args is an
    # automatic variable that always exists in function scope; declaring a param
    # of that name doesn't override it and the caller-supplied values get
    # silently dropped (this script shipped broken the first time because of
    # exactly that).
    param([string]$Subcommand, [string[]]$BenchArgs)

    if ($Parallel -and $Subcommand -eq "membench") {
        Invoke-BenchParallelCategories -Subcommand $Subcommand -BenchArgs $BenchArgs -Categories $script:MemBenchCategories
        return
    }

    $allArgs = @($Subcommand, "--mode", $Mode, "--top-k", $TopK) + $BenchArgs + $ExtraArgs
    Write-Host ""
    Write-Host "=== scrinia-bench $($allArgs -join ' ') ==="
    dotnet run --project $BenchProject -c $Configuration --no-build -- @allArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "scrinia-bench $Subcommand exited $LASTEXITCODE"
    }
}

function Invoke-BenchParallelCategories {
    # Process-level fanout: one subprocess per category, throttled via
    # ForEach-Object -Parallel -ThrottleLimit (PS 7+). Each subprocess writes its
    # own partial JSON; we merge at the end. Per-category logs go to
    # results/parallel-logs/ so interleaved stdout doesn't make a mess.
    #
    # Earlier versions used Start-Process -PassThru + a hand-rolled
    # while($proc.HasExited) throttle. That combination is broken: with
    # -NoNewWindow + redirected output, Start-Process closes the process handle
    # before returning, so HasExited always returns $true and the gate never
    # blocks. All categories then spawned simultaneously regardless of
    # -MaxConcurrency. ForEach-Object -Parallel throttles in the runtime.
    param([string]$Subcommand, [string[]]$BenchArgs, [string[]]$Categories)

    $resultsDir = Join-Path $RepoRoot "benchmarks\Scrinia.Retrieval.Benchmarks\results"
    $logsDir = Join-Path $resultsDir "parallel-logs"
    New-Item -ItemType Directory -Path $logsDir -Force | Out-Null

    $stamp = Get-Date -Format "yyyyMMdd_HHmm"

    Write-Host ""
    Write-Host "=== scrinia-bench $Subcommand (parallel: $($Categories.Count) categories, max $MaxConcurrency concurrent) ==="

    $jobs = @()
    foreach ($cat in $Categories) {
        $jobs += [pscustomobject]@{
            Category = $cat
            Partial  = Join-Path $resultsDir "partial_${Subcommand}_${cat}_${stamp}.json"
            Log      = Join-Path $logsDir "${Subcommand}-${cat}.log"
            ErrLog   = Join-Path $logsDir "${Subcommand}-${cat}.err"
        }
    }

    $results = $jobs | ForEach-Object -ThrottleLimit $MaxConcurrency -Parallel {
        $job = $_
        $cat = $job.Category
        $partial = $job.Partial
        $log = $job.Log
        $errLog = $job.ErrLog

        $catArgs = @(
            "run", "--project", $using:BenchProject, "-c", $using:Configuration, "--no-build", "--",
            $using:Subcommand, "--mode", $using:Mode, "--top-k", $using:TopK,
            "--category", $cat, "--output", $partial
        ) + $using:BenchArgs + $using:ExtraArgs

        Write-Host "  starting [$cat]"
        $startedAt = Get-Date
        & dotnet @catArgs > $log 2> $errLog
        $exit = $LASTEXITCODE
        $elapsed = (Get-Date) - $startedAt
        Write-Host ("  done [{0}] exit={1} elapsed={2:mm\:ss}" -f $cat, $exit, $elapsed)

        [pscustomobject]@{ Category = $cat; Partial = $partial; ExitCode = $exit }
    }

    $failed = @($results | Where-Object { $_.ExitCode -ne 0 } |
        ForEach-Object { "$($_.Category) (exit $($_.ExitCode))" })
    if ($failed.Count -gt 0) {
        Write-Warning "subprocess failures: $($failed -join '; '); see $logsDir"
    }

    # Merge partials into one canonical result file. Skip missing partials (failed
    # subprocesses) — the merger will emit a warning per skip.
    $partials = @($results | ForEach-Object { $_.Partial })
    $merged = Join-Path $resultsDir "scrinia_${Subcommand}_${Mode}__parallel_${stamp}.json"
    Write-Host "  merging $($partials.Count) partials into $merged..."
    & (Join-Path $PSScriptRoot "merge-bench-results.ps1") -Inputs $partials -Output $merged
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "merge step exited $LASTEXITCODE"
    } else {
        Write-Host "  wrote $merged"
    }
}

foreach ($b in $Benchmark) {
    switch ($b) {
        "convomem" {
            $data = Join-Path $DataRoot "ConvoMem\core_benchmark\evidence_questions"
            if (-not (Test-Path $data)) {
                Write-Warning "[convomem] data not found at $data — run prepare-datasets.ps1"
                continue
            }
            Invoke-Bench "convomem" @("--data", $data)
        }
        "longmemeval" {
            $data = Join-Path $DataRoot "longmemeval\longmemeval_s_cleaned.json"
            if (-not (Test-Path $data)) {
                Write-Warning "[longmemeval] data not found at $data — run prepare-datasets.ps1"
                continue
            }
            # Local var deliberately NOT named $args — same automatic-variable
            # trap as in Invoke-Bench's param above.
            $lmeArgs = @("--data", $data, "--split", $Split)
            Invoke-Bench "longmemeval" $lmeArgs
        }
        "membench" {
            $data = Join-Path $DataRoot "membench\MemData\FirstAgent"
            if (-not (Test-Path $data)) {
                Write-Warning "[membench] data not found at $data — run prepare-datasets.ps1"
                continue
            }
            Invoke-Bench "membench" @("--data", $data)
        }
        "locomo" {
            $data = Join-Path $DataRoot "locomo\data\locomo10.json"
            if (-not (Test-Path $data)) {
                Write-Warning "[locomo] data not found at $data — run prepare-datasets.ps1"
                continue
            }
            Invoke-Bench "locomo" @("--data", $data)
        }
    }
}

Write-Host ""
Write-Host "Results written to benchmarks/Scrinia.Retrieval.Benchmarks/results/"
