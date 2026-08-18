<#
.SYNOPSIS
    Runs the write-path cost benchmarks (task 7 of the 2026-08-17 write-path-cost plan) and
    writes results under artifacts/benchmarks/.

.DESCRIPTION
    Builds tests/Scrinia.Benchmarks in the given configuration (Release by default — a Debug
    build's numbers are meaningless for BenchmarkDotNet) and runs WritePathBenchmarks and/or
    VectorWriteBenchmarks via BenchmarkSwitcher's --filter, --exporters and --artifacts flags.

    This is the reproduction path for the tables in
    docs/superpowers/analysis/2026-08-17-write-path-cost-measurements.md. To reproduce the
    "before" half of that document, run this script once from a worktree checked out at the
    branch's merge-base commit (f89f204) with WritePathBenchmarks.cs/VectorWriteBenchmarks.cs
    copied in — those files do not exist at that commit — then again from this branch for
    "after". See the doc's environment block for the exact commits compared.

.PARAMETER Benchmark
    Which benchmark class(es) to run. Default: both.

.PARAMETER Configuration
    Build config. Default Release. BenchmarkDotNet numbers from a Debug build are not meaningful.

.PARAMETER ArtifactsPath
    Where BenchmarkDotNet writes its exported reports (CSV/HTML/GitHub-markdown/JSON). Default:
    artifacts/benchmarks/<timestamp>/ at the repo root.

.PARAMETER ExtraArgs
    Additional pass-through args appended to the dotnet run invocation, e.g. -ExtraArgs
    "--exporters","json" to also emit machine-readable output alongside the default reports.

.EXAMPLE
    ./scripts/run-write-path-benchmarks.ps1
    Builds Release and runs both benchmark classes.

.EXAMPLE
    ./scripts/run-write-path-benchmarks.ps1 -Benchmark WritePathBenchmarks
    Store write path only (task 7, part D.1 — the O(N^2) -> O(N) shape and the marginal-write cost).

.EXAMPLE
    ./scripts/run-write-path-benchmarks.ps1 -Benchmark VectorWriteBenchmarks -ExtraArgs "--exporters","json"
    Vector write path only, plus a JSON export for downstream tooling.
#>
[CmdletBinding()]
param(
    [ValidateSet("WritePathBenchmarks", "VectorWriteBenchmarks")]
    [string[]]$Benchmark = @("WritePathBenchmarks", "VectorWriteBenchmarks"),
    [string]$Configuration = "Release",
    [string]$ArtifactsPath = $null,
    [string[]]$ExtraArgs = @()
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path $PSScriptRoot -Parent
$BenchProject = Join-Path $RepoRoot "tests\Scrinia.Benchmarks\Scrinia.Benchmarks.csproj"

if (-not $ArtifactsPath) {
    $stamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $ArtifactsPath = Join-Path $RepoRoot "artifacts\benchmarks\$stamp"
}
New-Item -ItemType Directory -Force -Path $ArtifactsPath | Out-Null

Write-Host "building Scrinia.Benchmarks ($Configuration)..."
dotnet build $BenchProject -c $Configuration --nologo -v quiet
if ($LASTEXITCODE -ne 0) {
    Write-Error "build failed"
    exit 1
}

foreach ($b in $Benchmark) {
    $filter = "*$b*"
    Write-Host ""
    Write-Host "=== $b (filter: $filter) ==="
    $runArgs = @(
        "run", "--project", $BenchProject, "-c", $Configuration, "--no-build", "--",
        "--filter", $filter,
        "--artifacts", $ArtifactsPath
    ) + $ExtraArgs
    & dotnet @runArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "$b exited $LASTEXITCODE"
    }
}

Write-Host ""
Write-Host "Results written to $ArtifactsPath"
