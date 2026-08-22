<#
.SYNOPSIS
    Runs the test suite repeatedly and reports any test that fails, with its full message.

.DESCRIPTION
    Intermittent failures in this repo have twice been seen once and then lost, because the
    run that caught them was summarised rather than recorded: a `dotnet test | grep` pipeline
    that keeps only "Failed: 1" throws away the one thing needed to act — the test's name.

    This script exists so that never costs a second sighting. Every run writes a TRX file;
    runs that pass are deleted, runs that fail are kept and their failures printed with the
    assertion message and stack trace.

    Rates observed here are low: the event-sink tripwire failed roughly 2 runs in 12, and an
    intermittent failure seen once did not recur in 83 runs. Choose -Times accordingly — a clean
     10-run sweep is weak evidence, and 30+ is a reasonable overnight setting.

.PARAMETER Times
    How many times to run. Default 20.

.PARAMETER Project
    Test project or solution to run. Default: the whole solution, which is the only condition
    some of these failures have ever appeared under — assemblies run as concurrent processes
    and contend for CPU and disk in a way a single project never reproduces.

.PARAMETER ResultsDirectory
    Where TRX files for failing runs are kept. Default: a timestamped folder under $env:TEMP.

.PARAMETER Build
    Build before running. Off by default so repeated runs are fast; pass it on the first run
    of a session or after changing code.

.EXAMPLE
    ./scripts/find-flaky-test.ps1 -Times 30
    Runs the whole solution 30 times and prints every failure it catches.

.EXAMPLE
    ./scripts/find-flaky-test.ps1 -Times 40 -Project tests/Scrinia.Tests
    Narrows to one assembly. Faster per run, so more samples per hour — but it cannot
    reproduce anything caused by cross-assembly contention.
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 1000)]
    [int]$Times = 20,

    [string]$Project = 'Scrinia.sln',

    [string]$ResultsDirectory,

    [switch]$Build
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $ResultsDirectory) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $ResultsDirectory = Join-Path $env:TEMP "scrinia-flake-$stamp"
}
New-Item -ItemType Directory -Path $ResultsDirectory -Force | Out-Null

if ($Build) {
    Write-Host "Building $Project ..." -ForegroundColor Cyan
    dotnet build (Join-Path $repoRoot $Project) | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Build failed; not running." }
}

Write-Host "Running $Project x$Times. TRX for failing runs kept in:" -ForegroundColor Cyan
Write-Host "  $ResultsDirectory"
Write-Host ''

$failedRuns = 0
$seen = @{}

for ($i = 1; $i -le $Times; $i++) {
    $trxName = "run$i.trx"
    $trxPath = Join-Path $ResultsDirectory $trxName

    # --no-build by default: the caller opts into building once via -Build. Rebuilding every
    # iteration would dominate the wall clock and reduce the number of samples per hour, and
    # sample count is the whole point.
    #
    # Output is captured rather than discarded. Sending it to $null would throw away build and
    # restore errors too, so a run that never started would report "no TRX produced" and nothing
    # else — the same losing-the-evidence failure this script exists to prevent.
    $runLog = & dotnet test (Join-Path $repoRoot $Project) --no-build `
        --logger "trx;LogFileName=$trxName" `
        --results-directory $ResultsDirectory 2>&1

    if (-not (Test-Path $trxPath)) {
        Write-Host ("run {0,3}: no TRX produced — the run itself failed to start" -f $i) -ForegroundColor Yellow
        $runLog | Select-Object -Last 20 | ForEach-Object { Write-Host "      $_" -ForegroundColor DarkYellow }
        continue
    }

    [xml]$trx = Get-Content $trxPath
    # `| Where-Object { $_ }` is load-bearing, not tidiness. PowerShell unwraps a single XML child
    # to a scalar and a missing one to $null — and `@($null).Count` is 1, not 0 (verified). So
    # `@($trx.TestRun.Results.UnitTestResult).Count` reports 1 for a TRX containing NO results,
    # which would defeat the zero-result guard below and count an empty run as a pass. Filtering
    # the nulls out first is what makes the count mean what it reads as.
    $results = @($trx.TestRun.Results.UnitTestResult | Where-Object { $_ })
    $failures = @($results | Where-Object { $_.outcome -eq 'Failed' })

    if ($results.Count -eq 0) {
        # A TRX with no results is not a pass. It means the filter matched nothing or the host
        # died before running anything — silently counting that "ok" would let a sweep report
        # thirty clean runs having executed no tests at all.
        Write-Host ("run {0,3}: TRX contains NO test results — nothing was executed" -f $i) -ForegroundColor Yellow
        Write-Host "      kept: $trxPath" -ForegroundColor DarkYellow
        continue
    }

    if ($failures.Count -eq 0) {
        # Passing runs are deleted: 30 full-solution TRX files is ~100MB of noise, and a
        # passing run carries nothing worth reading.
        Remove-Item $trxPath -Force
        Write-Host ("run {0,3}: ok" -f $i) -ForegroundColor DarkGray
        continue
    }

    $failedRuns++
    Write-Host ("run {0,3}: {1} FAILED  -> {2}" -f $i, $failures.Count, $trxPath) -ForegroundColor Red
    foreach ($f in $failures) {
        $seen[$f.testName] = ($seen[$f.testName] + 1)
        Write-Host "    $($f.testName)" -ForegroundColor Red
        $msg = $f.Output.ErrorInfo.Message
        if ($msg) { Write-Host "      $($msg -replace "`r?`n", "`n      ")" -ForegroundColor DarkRed }
    }
}

Write-Host ''
Write-Host ("Runs: $Times   Runs with a failure: $failedRuns") -ForegroundColor Cyan

if ($seen.Count -eq 0) {
    # State this precisely. A clean sweep bounds the rate, it does not prove the suite sound —
    # the original intermittent failure this script was written for survived 83 clean runs.
    Write-Host "No failures caught. That bounds the rate below roughly 1 in $Times; it is not proof there is none." -ForegroundColor Green
    exit 0
}

Write-Host 'Caught:' -ForegroundColor Yellow
$seen.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object {
    Write-Host ("  {0}  x{1}" -f $_.Key, $_.Value)
}
Write-Host ''
Write-Host "TRX files for the failing runs are in $ResultsDirectory" -ForegroundColor Yellow
exit 1
