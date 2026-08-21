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
    scheduling and disk, and a mean over 20 runs is mostly a report on the worst two.

    Discards the first -Warmup runs. A cold first launch pays page-in for a large single-file
    binary and is not what a hook fires into on the tenth prompt of a session.

    ONLY TRUST THE DELTA. Absolute wall-clock here is bimodal on at least one machine: the same
    binary, against the same workspace, measures roughly 120ms at some times and 400ms at others,
    and flips between the two without warning. That was chased and NOT explained. What it is not,
    each ruled out by measurement:

      - the workspace's location    a bare .scrinia measured 120ms under C:\SynologyDrive and
                                    123ms under a local temp path — no difference
      - the repository's contents   adding .git, a parent .scrinia, or a 3,472-file source tree
                                    to a bare workspace changed nothing
      - the corpus                  an EMPTY workspace costs the same as a 69-memory one, which
                                    HintReadPathBenchmarks independently predicts (the search is
                                    microseconds)
      - a machine-wide slowdown     `cmd /c exit` moved 26ms to 35ms across the same window,
                                    nowhere near 3x

    So: run -Against, always, when the question is "did this change help". Interleaving is what
    makes a comparison survive a drift nobody has identified. A single-binary run tells you what
    the machine felt like at that moment and very little else.

.PARAMETER Exe
    Path to a published scri.exe. Publish one with:
      dotnet publish src/Scrinia/Scrinia.csproj --runtime win-x64 --self-contained `
        --configuration Release -p:PublishSingleFile=true -p:PublishTrimmed=true --output <dir>

.PARAMETER Against
    A second scri.exe to compare against. When given, the two are timed INTERLEAVED — one run of
    each, alternating, per iteration — so machine state that drifts mid-run hits both equally.
    Measuring one build to completion and then the other is how a sequential comparison reports
    whatever drifted in between as a performance result; this script did exactly that once, and
    the drift got written up as a finding before anyone checked.

.PARAMETER WorkspaceRoot
    Workspace to measure against. Defaults to this repository.

.PARAMETER CopyWorkspaceTo
    Copy -WorkspaceRoot's .scrinia/ to this directory first and measure against the copy. Useful
    for measuring against a corpus without writing into the real one — not, as this parameter was
    originally added to do, for escaping a filesystem penalty that turned out not to exist.

.PARAMETER Runs
    Timed runs per verb per binary (default 20).

.PARAMETER Warmup
    Discarded runs per verb per binary before timing starts (default 3).

.PARAMETER Label
    Written into the output so two runs can be told apart (e.g. 'baseline', 'r2r').

.PARAMETER AgainstLabel
    Label for the -Against binary.

.PARAMETER Json
    Emit the results as JSON instead of a table.

.EXAMPLE
    ./measure-startup.ps1 -Exe ./dist/scri.exe -Label baseline

.EXAMPLE
    ./measure-startup.ps1 -Exe ./out-base/scri.exe -Against ./out-r2r/scri.exe `
        -Label base -AgainstLabel r2r -CopyWorkspaceTo $env:TEMP/scri-ws
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Exe,

    [string]$Against,

    [string]$WorkspaceRoot = $PSScriptRoot,

    [string]$CopyWorkspaceTo,

    [int]$Runs = 20,

    [int]$Warmup = 3,

    [string]$Label = 'A',

    [string]$AgainstLabel = 'B',

    [switch]$Json
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Exe)) { throw "No such executable: $Exe" }
$Exe = (Resolve-Path $Exe).Path
if ($Against) {
    if (-not (Test-Path $Against)) { throw "No such executable: $Against" }
    $Against = (Resolve-Path $Against).Path
}
$WorkspaceRoot = (Resolve-Path $WorkspaceRoot).Path

if ($CopyWorkspaceTo) {
    $src = Join-Path $WorkspaceRoot '.scrinia'
    if (-not (Test-Path $src)) { throw "No .scrinia/ under $WorkspaceRoot to copy." }
    if (Test-Path $CopyWorkspaceTo) { Remove-Item $CopyWorkspaceTo -Recurse -Force }
    New-Item -ItemType Directory $CopyWorkspaceTo | Out-Null
    Copy-Item $src (Join-Path $CopyWorkspaceTo '.scrinia') -Recurse -Force
    $WorkspaceRoot = (Resolve-Path $CopyWorkspaceTo).Path
    Write-Host "workspace copied to $WorkspaceRoot" -ForegroundColor DarkGray
}

# A genuinely remote volume would put a network round trip in front of every read, which is worth
# knowing about. This check used to be paired with a second one that guessed at sync clients from
# the directory NAME; that guess was wrong — a bare workspace measured the same under
# C:\SynologyDrive as under a local temp path — and it is gone.
$driveRoot = [System.IO.Path]::GetPathRoot($WorkspaceRoot)
$drive = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$($driveRoot.TrimEnd('\'))'" -ErrorAction SilentlyContinue
if ($drive -and $drive.DriveType -ne 3) {
    Write-Warning "$driveRoot is DriveType $($drive.DriveType), not a local fixed disk. Timings will include whatever that costs."
}

if (-not $Against) {
    Write-Warning "Single-binary run. Absolute numbers here drift by 3x on at least one machine for reasons nobody has pinned down — see the notes at the top of this script. To answer 'did this change help', pass -Against and compare the interleaved delta."
}

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

$sink = Join-Path ([System.IO.Path]::GetTempPath()) 'scri-measure-sink.txt'

function Assert-Case {
    param([string]$Which, [string]$Name, [string[]]$Arguments)

    & $Which @Arguments *> $sink
    if ($LASTEXITCODE -ne 0) {
        $out = Get-Content $sink -Raw -ErrorAction SilentlyContinue
        throw "Case '$Name' exited $LASTEXITCODE on $Which, so its timing would measure a failure path.`n$out"
    }
}

function Get-Median { param([double[]]$Sorted) $Sorted[[int]($Sorted.Count / 2)] }
function Get-P90 { param([double[]]$Sorted) $Sorted[[int][math]::Floor(($Sorted.Count - 1) * 0.9)] }

function Measure-Case {
    param([string]$Name, [string[]]$Arguments)

    $sw = [System.Diagnostics.Stopwatch]::new()
    $a = [System.Collections.Generic.List[double]]::new()
    $b = [System.Collections.Generic.List[double]]::new()

    for ($i = 0; $i -lt $Warmup; $i++) {
        & $Exe @Arguments *> $sink
        if ($Against) { & $Against @Arguments *> $sink }
    }

    for ($i = 0; $i -lt $Runs; $i++) {
        $sw.Restart(); & $Exe @Arguments *> $sink; $sw.Stop()
        $a.Add($sw.Elapsed.TotalMilliseconds)

        if ($Against) {
            $sw.Restart(); & $Against @Arguments *> $sink; $sw.Stop()
            $b.Add($sw.Elapsed.TotalMilliseconds)
        }
    }

    $sa = $a.ToArray() | Sort-Object
    $row = [ordered]@{
        Case = $Name
        "$Label" = [math]::Round((Get-Median $sa), 1)
        "$Label p90" = [math]::Round((Get-P90 $sa), 1)
    }
    if ($Against) {
        $sb = $b.ToArray() | Sort-Object
        $mA = Get-Median $sa
        $mB = Get-Median $sb
        $row["$AgainstLabel"] = [math]::Round($mB, 1)
        $row["$AgainstLabel p90"] = [math]::Round((Get-P90 $sb), 1)
        $row['Delta'] = "{0:+0.0;-0.0;0}" -f ($mB - $mA)
        $row['Pct'] = if ($mA -gt 0) { "{0:+0;-0;0}%" -f ((($mB - $mA) / $mA) * 100) } else { 'n/a' }
    }
    [pscustomobject]$row
}

try {
    foreach ($c in $cases) {
        Assert-Case -Which $Exe -Name $c.Name -Arguments $c.Args
        if ($Against) { Assert-Case -Which $Against -Name $c.Name -Arguments $c.Args }
    }

    $results = foreach ($c in $cases) {
        Write-Host "  measuring $($c.Name) ..." -ForegroundColor DarkGray
        Measure-Case -Name $c.Name -Arguments $c.Args
    }
}
finally {
    Remove-Item $sink -ErrorAction SilentlyContinue
}

if ($Json) {
    [pscustomobject]@{
        exe           = $Exe
        against       = $Against
        workspaceRoot = $WorkspaceRoot
        runs          = $Runs
        warmup        = $Warmup
        interleaved   = [bool]$Against
        results       = $results
    } | ConvertTo-Json -Depth 5
}
else {
    Write-Host ''
    $mode = if ($Against) { "$Label vs $AgainstLabel, interleaved" } else { $Label }
    Write-Host "$mode  ($Runs runs, $Warmup warmup, ms, workspace: $WorkspaceRoot)" -ForegroundColor Cyan
    $results | Format-Table -AutoSize
}
