<#
.SYNOPSIS
    Downloads the four retrieval-quality benchmark datasets used by scrinia-bench.

.DESCRIPTION
    Idempotent: only downloads what isn't already present. Targets:

    - ConvoMem     (HuggingFace, git clone of Salesforce/ConvoMem)
    - LongMemEval  (HuggingFace, single JSON file)
    - LoCoMo       (GitHub, git clone of snap-research/locomo)
    - MemBench     (GitHub, git clone of import-myself/Membench)

    Also fetches MemPalace's lme_split_50_450.json so `scrinia-bench longmemeval
    --split held_out` works out of the box.

.PARAMETER DataRoot
    Where datasets are downloaded. Default: ./datasets/ at the repo root.

.PARAMETER Benchmark
    Which datasets to fetch. Default: all four. Accepts: convomem, longmemeval,
    locomo, membench.

.EXAMPLE
    ./scripts/prepare-datasets.ps1
    Downloads all four.

.EXAMPLE
    ./scripts/prepare-datasets.ps1 -Benchmark longmemeval,convomem
    Only those two.
#>
[CmdletBinding()]
param(
    [string]$DataRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\datasets") -ErrorAction SilentlyContinue),
    [ValidateSet("convomem", "longmemeval", "locomo", "membench")]
    [string[]]$Benchmark = @("convomem", "longmemeval", "locomo", "membench")
)

$ErrorActionPreference = "Stop"

if (-not $DataRoot) {
    $DataRoot = Join-Path (Split-Path $PSScriptRoot -Parent) "datasets"
}
New-Item -ItemType Directory -Force -Path $DataRoot | Out-Null

function Test-Command($name) {
    return [bool](Get-Command $name -ErrorAction SilentlyContinue)
}

if (-not (Test-Command "git")) {
    Write-Error "git not found in PATH. ConvoMem/LoCoMo/MemBench need git clone."
    exit 1
}

function Get-ConvoMem {
    $target = Join-Path $DataRoot "ConvoMem"
    if (Test-Path (Join-Path $target "core_benchmark")) {
        Write-Host "[convomem] already present at $target — skipping"
        return
    }
    Write-Host "[convomem] cloning Salesforce/ConvoMem from HuggingFace..."
    git clone --depth 1 "https://huggingface.co/datasets/Salesforce/ConvoMem" $target
    Write-Host "[convomem] done. Point --data at $target/core_benchmark/evidence_questions"
}

function Get-LongMemEval {
    $target = Join-Path $DataRoot "longmemeval"
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    $dataFile = Join-Path $target "longmemeval_s_cleaned.json"
    if (Test-Path $dataFile) {
        Write-Host "[longmemeval] $dataFile already present — skipping data download"
    }
    else {
        Write-Host "[longmemeval] downloading longmemeval_s_cleaned.json..."
        Invoke-WebRequest `
            -Uri "https://huggingface.co/datasets/xiaowu0162/longmemeval-cleaned/resolve/main/longmemeval_s_cleaned.json" `
            -OutFile $dataFile `
            -UseBasicParsing
    }
    $splitFile = Join-Path $target "lme_split_50_450.json"
    if (Test-Path $splitFile) {
        Write-Host "[longmemeval] $splitFile already present — skipping split download"
    }
    else {
        Write-Host "[longmemeval] downloading MemPalace's lme_split_50_450.json..."
        Invoke-WebRequest `
            -Uri "https://raw.githubusercontent.com/MemPalace/mempalace/main/benchmarks/lme_split_50_450.json" `
            -OutFile $splitFile `
            -UseBasicParsing
    }
}

function Get-LoCoMo {
    $target = Join-Path $DataRoot "locomo"
    if (Test-Path (Join-Path $target "data" "locomo10.json")) {
        Write-Host "[locomo] already present at $target — skipping"
        return
    }
    Write-Host "[locomo] cloning snap-research/locomo..."
    git clone --depth 1 "https://github.com/snap-research/locomo" $target
    Write-Host "[locomo] done. Point --data at $target/data/locomo10.json"
}

function Get-MemBench {
    $target = Join-Path $DataRoot "membench"
    if (Test-Path (Join-Path $target "MemData")) {
        Write-Host "[membench] already present at $target — skipping"
        return
    }
    Write-Host "[membench] cloning import-myself/Membench..."
    git clone --depth 1 "https://github.com/import-myself/Membench" $target
    Write-Host "[membench] done. Point --data at $target/MemData/FirstAgent"
}

foreach ($b in $Benchmark) {
    switch ($b) {
        "convomem" { Get-ConvoMem }
        "longmemeval" { Get-LongMemEval }
        "locomo" { Get-LoCoMo }
        "membench" { Get-MemBench }
    }
}

Write-Host ""
Write-Host "All requested datasets present under $DataRoot"
Write-Host "Next: ./scripts/run-benchmarks.ps1"
