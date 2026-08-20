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

    WHERE THE WORKSPACE LIVES DOMINATES EVERYTHING ELSE. A workspace on a synced or network
    path (Dropbox, OneDrive, Synology Drive, a mapped share) was measured here at ~270ms per
    invocation more than the same files copied to a local disk — larger than any code change in
    this repository has been worth, and worse, it is not stable: it changes when the sync client
    decides to scan. Pass -WorkspaceRoot a LOCAL path when comparing two builds, or the answer is
    a report on the sync client. The script warns when the workspace does not look local.

.PARAMETER Exe
    Path to a published scri.exe. Publish one with:
      dotnet publish src/Scrinia/Scrinia.csproj --runtime win-x64 --self-contained `
        --configuration Release -p:PublishSingleFile=true -p:PublishTrimmed=true --output <dir>

.PARAMETER Against
    A second scri.exe to compare against. When given, the two are timed INTERLEAVED — one run of
    each, alternating, per iteration — so machine state that drifts mid-run hits both equally.
    Measuring one build to completion and then the other is how a sequential comparison turns a
    background sync into a performance result; this script did exactly that once.

.PARAMETER WorkspaceRoot
    Workspace to measure against. Defaults to this repository. See the warning above about synced
    paths — use -CopyWorkspaceTo to get a local copy automatically.

.PARAMETER CopyWorkspaceTo
    Copy -WorkspaceRoot's .scrinia/ to this directory first and measure against the copy. The way
    to get a trustworthy number when the repository itself lives on a synced path.

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

# A drive that is not a plain local fixed disk puts a filter driver between every read and the
# result. Cheap to detect, and worth shouting about: it is the largest single term in these
# timings and it is not constant, so it silently invalidates a comparison run across it.
$driveRoot = [System.IO.Path]::GetPathRoot($WorkspaceRoot)
$drive = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$($driveRoot.TrimEnd('\'))'" -ErrorAction SilentlyContinue
if ($drive -and $drive.DriveType -ne 3) {
    Write-Warning "$driveRoot is DriveType $($drive.DriveType), not a local fixed disk. Timings will measure it."
}
else {
    # Only the first two segments below the drive root. Matching the whole path produces false
    # positives whenever a scratch directory is NAMED after a synced source tree, which is exactly
    # what happened the first time this warning fired.
    $head = ($WorkspaceRoot.Substring($driveRoot.Length) -split '[\\/]' | Select-Object -First 2) -join '\'
    if ($head -match 'SynologyDrive|OneDrive|Dropbox|Google ?Drive|iCloud') {
        Write-Warning "$WorkspaceRoot sits under a synced folder ('$head'). A sync client adds a large, VARIABLE per-read cost — measured here at ~270ms per invocation, enough to swamp the difference between two builds. Use -CopyWorkspaceTo for a comparison you can trust."
    }
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
