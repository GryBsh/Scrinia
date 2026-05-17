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
    Pass-through args to every scrinia-bench invocation. Useful for ablations:
        -ExtraArgs "--alpha-recency", "1.0", "--tau-days", "90"

.EXAMPLE
    ./scripts/run-benchmarks.ps1
    All four benchmarks, BM25+embeddings, default top-K.

.EXAMPLE
    ./scripts/run-benchmarks.ps1 -Benchmark longmemeval -Split held_out
    The 450-question held-out comparison number.

.EXAMPLE
    ./scripts/run-benchmarks.ps1 -ExtraArgs "--alpha-recency","1.0","--tau-days","90"
    Recency ablation sweep across all four.
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
    [string[]]$ExtraArgs = @()
)

$ErrorActionPreference = "Stop"

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
    param([string]$Subcommand, [string[]]$Args)
    $allArgs = @($Subcommand, "--mode", $Mode, "--top-k", $TopK) + $Args + $ExtraArgs
    Write-Host ""
    Write-Host "=== scrinia-bench $($allArgs -join ' ') ==="
    dotnet run --project $BenchProject -c $Configuration --no-build -- @allArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "scrinia-bench $Subcommand exited $LASTEXITCODE"
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
            $args = @("--data", $data, "--split", $Split)
            Invoke-Bench "longmemeval" $args
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
