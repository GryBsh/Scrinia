<#
.SYNOPSIS
    Wall-clock timing for scri's short-lived verbs, against a real workspace.

.DESCRIPTION
    BenchmarkDotNet cannot see the cost this script exists to measure. Roughly half of a
    `scri hint` is spent before any Scrinia code runs — host startup, assembly load, JIT — and an
    in-process harness has already paid all of it before its first iteration. So this drives the
    PUBLISHED binary as a child process and times the whole thing, which is what the hook that
    fires on every user prompt actually costs.

    Reports median and p90 rather than mean. Process-start timings have a long right tail from
    scheduling and disk, and a mean over 30 runs is mostly a report on the worst two.

    Discards the first -Warmup runs. A cold first launch pays page-in for a 19 MB single-file
    binary and is not what a hook fires into on the tenth prompt of a session.

.PARAMETER Exe
    Path to the published scri.exe. Publish one with:
      dotnet publish src/Scrinia/Scrinia.csproj --runtime win-x64 --self-contained `
        --configuration Release -p:PublishSingleFile=true -p:PublishTrimmed=true --output <dir>

.PARAMETER WorkspaceRoot
    Workspace to measure against. Defaults to this repository, which carries a real corpus.

.PARAMETER Runs
    Timed runs per verb (default 20).

.PARAMETER Warmup
    Discarded runs per verb before timing starts (default 3).

.PARAMETER Label
    Written into the output so two runs can be told apart (e.g. 'baseline', 'r2r').

.PARAMETER Json
    Emit the results as JSON for diffing two runs, instead of a table.

.EXAMPLE
    ./measure-startup.ps1 -Exe ./dist/scri.exe -Label baseline
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Exe,

    [string]$WorkspaceRoot = $PSScriptRoot,

    [int]$Runs = 20,

    [int]$Warmup = 3,

    [string]$Label = 'run',

    [switch]$Json
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Exe)) { throw "No such executable: $Exe" }
$Exe = (Resolve-Path $Exe).Path
$WorkspaceRoot = (Resolve-Path $WorkspaceRoot).Path

# Each case is (name, args). `--workspace-root` is passed explicitly everywhere: without it the
# verb resolves the workspace from the current directory, and that resolution walks up the tree
# looking for .git — a cost that belongs to the measurement only if the caller would pay it.
# `hint` takes its prompt as --prompt, not positionally. Passing it positionally is an argument
# error that exits 1 in about the time `--version` takes, which looks exactly like a fast hint —
# hence Assert-Case below, which fails the run rather than reporting the error path as a timing.
$cases = @(
    @{ Name = 'version'; Args = @('--version') }
    @{ Name = 'hint (match)'; Args = @('hint', '--prompt', 'how does the merge driver resolve sidecar conflicts', '--workspace-root', $WorkspaceRoot, '--plain') }
    @{ Name = 'hint (no match)'; Args = @('hint', '--prompt', 'zzzzqqq unrelated gibberish token', '--workspace-root', $WorkspaceRoot, '--plain') }
    @{ Name = 'restore'; Args = @('restore', '--workspace-root', $WorkspaceRoot) }
    @{ Name = 'search'; Args = @('search', 'merge driver', '--workspace-root', $WorkspaceRoot) }
    @{ Name = 'list'; Args = @('list', '--workspace-root', $WorkspaceRoot) }
)

function Assert-Case {
    param([string]$Name, [string[]]$Arguments)

    $sink = Join-Path ([System.IO.Path]::GetTempPath()) "scri-measure-assert.txt"
    & $Exe @Arguments *> $sink
    if ($LASTEXITCODE -ne 0) {
        $out = (Get-Content $sink -Raw -ErrorAction SilentlyContinue)
        Remove-Item $sink -ErrorAction SilentlyContinue
        throw "Case '$Name' exited $LASTEXITCODE, so its timing would measure a failure path.`n$out"
    }
    Remove-Item $sink -ErrorAction SilentlyContinue
}

function Measure-Case {
    param([string]$Name, [string[]]$Arguments)

    # Redirected to a file rather than captured into the pipeline: PowerShell's own object
    # pipeline adds measurable overhead per line of output, and `search` prints a table.
    $sink = [System.IO.Path]::GetTempFileName()
    try {
        for ($i = 0; $i -lt $Warmup; $i++) {
            & $Exe @Arguments *> $sink
        }

        $samples = [double[]]::new($Runs)
        for ($i = 0; $i -lt $Runs; $i++) {
            $sw = [System.Diagnostics.Stopwatch]::StartNew()
            & $Exe @Arguments *> $sink
            $sw.Stop()
            $samples[$i] = $sw.Elapsed.TotalMilliseconds
        }
    }
    finally {
        Remove-Item $sink -ErrorAction SilentlyContinue
    }

    $sorted = $samples | Sort-Object

    [pscustomobject]@{
        Case   = $Name
        Median = [math]::Round($sorted[[int]($Runs / 2)], 1)
        P90    = [math]::Round($sorted[[int][math]::Floor(($Runs - 1) * 0.9)], 1)
        Min    = [math]::Round($sorted[0], 1)
        Max    = [math]::Round($sorted[-1], 1)
    }
}

# Every case is checked for a zero exit BEFORE any timing, so a mistyped argument fails the run
# instead of being reported as a suspiciously fast verb.
foreach ($c in $cases) { Assert-Case -Name $c.Name -Arguments $c.Args }

$results = foreach ($c in $cases) {
    Write-Host "  measuring $($c.Name) ..." -ForegroundColor DarkGray
    Measure-Case -Name $c.Name -Arguments $c.Args
}

if ($Json) {
    [pscustomobject]@{
        label         = $Label
        exe           = $Exe
        workspaceRoot = $WorkspaceRoot
        runs          = $Runs
        warmup        = $Warmup
        results       = $results
    } | ConvertTo-Json -Depth 5
}
else {
    Write-Host ''
    Write-Host "$Label  ($Runs runs, $Warmup warmup, ms)" -ForegroundColor Cyan
    $results | Format-Table -AutoSize
}
