param(
    [Parameter(Mandatory)]
    [string]$OutputDir,

    [switch]$WithEmbeddings,

    [ValidateSet('win-x64', 'linux-x64', 'osx-arm64')]
    [string]$Platform
)

$ErrorActionPreference = 'Stop'

$AllRids = @('win-x64', 'linux-x64', 'osx-arm64')
$Rids = if ($Platform) { @($Platform) } else { $AllRids }
# When a single platform is specified, output directly into OutputDir (no RID subdirectory).
$SinglePlatform = [bool]$Platform

# RuntimeInformation rather than $IsWindows/$IsLinux/$IsMacOS: those automatic variables do not
# exist in Windows PowerShell 5.1. OS and architecture are resolved separately and joined so a
# linux-arm64 or win-arm64 host is not mistaken for its x64 sibling.
$HostOs =
    if ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Windows)) { 'win' }
    elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::Linux)) { 'linux' }
    elseif ([System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform([System.Runtime.InteropServices.OSPlatform]::OSX)) { 'osx' }
    else { '' }
$HostArch = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
    'X64'   { 'x64' }
    'Arm64' { 'arm64' }
    default { '' }
}
$HostRid = if ($HostOs -and $HostArch) { "$HostOs-$HostArch" } else { '' }
# Whether the host's own RID is one this script can actually publish for (and so a safe thing
# to suggest in the [skip] message below). osx-x64 and an unrecognised host are examples of a
# real, buildable machine whose RID is not in $AllRids.
$HostRidSupported = $AllRids -contains $HostRid

# The manifest is the one source of truth for what ships. publish.sh reads the same file.
$Manifest = Join-Path $PSScriptRoot 'publish.projects.tsv'
if (-not (Test-Path $Manifest)) { throw "Manifest not found at $Manifest" }

$ManifestLines = @(Get-Content -Path $Manifest)
if ($ManifestLines.Count -eq 0) { throw "Manifest $Manifest is empty" }

# Pinned exactly, in order: publish.sh reads columns POSITIONALLY, so a reordered or renamed
# header would silently misassign every field there without either script raising an error.
# -cne is a case-sensitive comparison — plain -ne is case-insensitive in PowerShell and would
# accept a header bash rejects. publish.sh pins the identical line — the two readers of one
# source of truth must agree about what the file means.
$ExpectedHeader = "id`tproject`tcondition`tprofile`toutput`trids"
if ($ManifestLines[0] -cne $ExpectedHeader) {
    throw "Manifest header must be exactly 'id<TAB>project<TAB>condition<TAB>profile<TAB>output<TAB>rids' — found '$($ManifestLines[0])'"
}

# Import-Csv cannot see a field beyond the header's column count — it silently drops extras
# rather than erroring the way publish.sh's positional `read` does. Validated against the raw
# line, before Import-Csv ever runs, so a 7-column row is rejected here instead of quietly
# publishing with its last field dropped.
foreach ($line in $ManifestLines | Select-Object -Skip 1) {
    if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#')) { continue }
    $fieldCount = @($line -split "`t").Count
    if ($fieldCount -ne 6) {
        throw "Manifest row has $fieldCount tab-separated columns, expected 6: '$line'"
    }
}

# Filters blank and #-prefixed rows exactly as publish.sh does. The two readers of one source
# of truth must agree about what the file means.
$Projects = @(Import-Csv -Path $Manifest -Delimiter "`t" |
    Where-Object { $_.id -and -not $_.id.StartsWith('#') })
if ($Projects.Count -eq 0) { throw "Manifest $Manifest lists no projects" }
# Validated here, once, at read time — not deep inside the publish loop — so a bad row is
# caught before any artifact has been published for any RID, rather than mid-run.
#
# -cnotin, not -notin: PowerShell's membership operators are case-insensitive by default, so
# plain -notin would silently ACCEPT 'output=Rid' where publish.sh's `case` rejects it. Every
# comparison against a manifest value here and in the publish loop below uses the
# case-sensitive form for the same reason — condition and rids gate which projects/RIDs
# actually build, so a value degrading past validation used to ship the embeddings plugin in
# EVERY build (a `with_embeddings` typo) or silently fall back to `all` RIDs (`rids=hostt`).
foreach ($p in $Projects) {
    if ($p.output -cnotin @('rid', 'plugin')) { throw "Unknown output kind '$($p.output)' for $($p.id)" }
    if ($p.profile -cnotin @('trimmed', 'plain')) { throw "Unknown profile '$($p.profile)' for $($p.id)" }
    if ($p.condition -cnotin @('always', 'with-embeddings')) { throw "Unknown condition '$($p.condition)' for $($p.id)" }
    if ($p.rids -cnotin @('all', 'host')) { throw "Unknown rids scope '$($p.rids)' for $($p.id)" }
}

# Contract assertion (Phase 5 Task 6): the local publish scripts emit single-file release trees;
# the single NuGet global tool is `scri`. The manifest must therefore declare exactly one single-
# file CLI command (output=rid), and it must be `scri`; the optional plugin row (output=plugin) is
# the separately-installed Vulkan command and is NOT part of the NuGet tool payload — Vulkan and
# Merge are never packaged into the tool.
$singleFileCommands = @($Projects | Where-Object { $_.output -ceq 'rid' })
if ($singleFileCommands.Count -ne 1) {
    throw "Publish manifest must declare exactly one single-file release-tree command (output=rid); found $($singleFileCommands.Count)."
}
if ($singleFileCommands[0].id -cne 'scri') {
    throw "The single-file release-tree command must be `scri` (the one NuGet global tool); found '$($singleFileCommands[0].id)'."
}

$VersionOutput = @(dotnet msbuild (Join-Path $PSScriptRoot $singleFileCommands[0].project) -getProperty:Version)
if ($LASTEXITCODE -ne 0) { throw 'Could not evaluate the host version.' }
$Version = ($VersionOutput -join "`n").Trim()
if ($Version -cnotmatch '\A[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?(?:\+[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?\z') {
    throw 'MSBuild must return one semantic version value.'
}
$Version = $Version.Split('+')[0]
$TempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$RawRoot = Join-Path $TempParent ('scrinia-publish-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $RawRoot | Out-Null

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

try {
foreach ($rid in $Rids) {
    $ridDir = if ($SinglePlatform) { $OutputDir } else { "$OutputDir/$rid" }

    foreach ($p in $Projects) {
        # -ceq: case-sensitive. Plain -eq is case-insensitive in PowerShell, so 'With-Embeddings'
        # or 'Host' would silently match here where publish.sh's bash `[ = ]` would not.
        if ($p.condition -ceq 'with-embeddings' -and -not $WithEmbeddings) { continue }

        if ($p.rids -ceq 'host' -and $rid -ne $HostRid) {
            if ($HostRidSupported) {
                [Console]::Error.WriteLine(
                    "  [skip] $($p.id) for ${rid}: it is NativeAOT and native compilation cannot cross " +
                    "OS or CPU architecture. Build on $rid, or use -Platform $HostRid.")
            } else {
                # The platform's own identification of itself, not $HostOs/$HostArch — those are
                # the NORMALIZED values, and on a host this script cannot map one of them is
                # empty, which is how this line came to print "(/x64)". publish.sh prints
                # `uname -s`/`uname -m` here for the same reason.
                $rawOs = [Runtime.InteropServices.RuntimeInformation]::OSDescription
                $rawArch = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture
                [Console]::Error.WriteLine(
                    "  [skip] $($p.id) for ${rid}: it is NativeAOT and native compilation cannot " +
                    "cross OS or CPU architecture, and this host ($rawOs/$rawArch) is not one this " +
                    "script publishes for. Supported: $($AllRids -join ' ').")
            }
            continue
        }

        # -CaseSensitive: a bare `switch` matches case-insensitively in PowerShell, so
        # 'output=Rid' would silently take the 'rid' branch instead of falling to default.
        $outDir = switch -CaseSensitive ($p.output) {
            'rid'    { Join-Path $RawRoot "$rid/host" }
            'plugin' { Join-Path $RawRoot "$rid/plugin" }
            default  { throw "Unknown output kind '$($p.output)' for $($p.id)" }
        }

        Write-Host "Publishing $($p.id) for $rid ..."
        switch -CaseSensitive ($p.profile) {
            'trimmed' {
                # Trim-analysis warnings are gated as errors so a regression silently dropping a
                # member via reflection cannot ship. If a trim warning fires, fix the root cause —
                # either move to a JsonSerializerContext, annotate with [DynamicallyAccessedMembers],
                # or rewrite the reflection-heavy call to a trim-safe shape.
                #
                # PublishReadyToRun precompiles the managed code, so a short-lived verb stops
                # paying JIT for a startup path it will never run twice. ./measure-startup.ps1,
                # median of 20 runs, the two builds INTERLEAVED, against a local-disk copy of this
                # repository's workspace with the Model2Vec model present:
                #
                #     verb              trimmed    +R2R
                #     hint                285ms   118ms   -58%
                #     restore             460ms   227ms   -51%
                #     search              631ms   266ms   -58%
                #     list                346ms   136ms   -61%
                #
                # Confirmed as JIT and not something correlated with it: re-running the SAME R2R
                # binary with DOTNET_ReadyToRun=0 puts hint back to 263ms.
                #
                # Read the DELTA, not the absolute numbers. Wall-clock for a scri invocation on
                # this machine is bimodal — the same binary against the same workspace measures
                # ~120ms at some times and ~400ms at others — and the cause is not identified. It
                # is not the workspace's location (a bare .scrinia measured 120ms under
                # C:\SynologyDrive and 123ms under a local temp path), not the repository's
                # contents (.git, a parent .scrinia and a 3,472-file source tree each changed
                # nothing), and not the corpus (an EMPTY workspace costs the same as a 69-memory
                # one, which the in-process benchmark independently predicts). Interleaving the two
                # builds is what makes the comparison survive it.
                #
                # Cost: 19.5MB to 42.9MB on win-x64 (linux-x64 47.5MB, osx-arm64 51.3MB). Taken
                # deliberately — size is paid once per install, the JIT was paid per prompt,
                # because `hint` fires on every user prompt through the agent hook.
                #
                # --version and --help go the OTHER way, 213ms to 336ms. Not JIT
                # (DOTNET_ReadyToRun=0 leaves them at 352ms) and not page-in of the bigger image
                # (hint runs at 118ms from that same image). Unexplained; neither is on a hot path.
                dotnet publish (Join-Path $PSScriptRoot $p.project) `
                    --runtime $rid `
                    --self-contained `
                    --configuration Release `
                    -p:PublishSingleFile=true `
                    -p:PublishTrimmed=true `
                    -p:PublishReadyToRun=true `
                    -p:SuppressTrimAnalysisWarnings=false `
                    -p:TreatWarningsAsErrors=true `
                    --output $outDir
            }
            'plain' {
                # Single-file + self-contained + native bundling is configured in the project
                # file. Only --runtime is needed here for RID selection.
                dotnet publish (Join-Path $PSScriptRoot $p.project) `
                    --runtime $rid `
                    --configuration Release `
                    --output $outDir
            }
            default { throw "Unknown profile '$($p.profile)' for $($p.id)" }
        }
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($p.id) / $rid" }

        New-Item -ItemType Directory -Path $ridDir -Force | Out-Null
        if ($p.output -ceq 'plugin') {
            $PackageRootOutput = @(dotnet msbuild (Join-Path $PSScriptRoot $p.project) -getProperty:NuGetPackageRoot)
            if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the restored NuGet package root.' }
            $PackageRoot = ($PackageRootOutput -join "`n").Trim()
            if (-not (Test-Path -LiteralPath $PackageRoot -PathType Container)) { throw 'NuGetPackageRoot must be one existing directory.' }
            dotnet run --project (Join-Path $PSScriptRoot 'tools/Scrinia.ReleaseTools') -c Release -- `
                stage-embeddings --raw $outDir --root $ridDir --rid $rid --version $Version `
                --nuget-root $PackageRoot --policy (Join-Path $PSScriptRoot 'release/embeddings-payloads.json')
            if ($LASTEXITCODE -ne 0) { throw "Embeddings staging failed for $rid" }
        } else {
            $ext = if ($rid -ceq 'win-x64') { '.exe' } else { '' }
            Copy-Item -LiteralPath (Join-Path $outDir ($p.id + $ext)) -Destination $ridDir -Force
        }
        Write-Host "  -> $ridDir"
    }
}
} finally {
    $ResolvedRawRoot = [IO.Path]::GetFullPath($RawRoot)
    if (-not $ResolvedRawRoot.StartsWith($TempParent, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($ResolvedRawRoot) -notlike 'scrinia-publish-*') { throw 'Unsafe raw publish cleanup path.' }
    Remove-Item -LiteralPath $ResolvedRawRoot -Recurse -Force
}

Write-Host ''
Write-Host 'Done. Builds:'
foreach ($rid in $Rids) {
    $ridDir = if ($SinglePlatform) { $OutputDir } else { "$OutputDir/$rid" }
    Get-ChildItem "$ridDir/scri*" -ErrorAction SilentlyContinue |
        ForEach-Object { '{0}  {1}' -f $_.Length.ToString('N0').PadLeft(12), $_.FullName } |
        Write-Host
    if ($WithEmbeddings) {
        $ext = if ($rid -like 'win-*') { '.exe' } else { '' }
        Get-ChildItem "$ridDir/plugins/scri-plugin-embeddings/$Version/$rid/scri-plugin-embeddings$ext" -ErrorAction SilentlyContinue |
            ForEach-Object { '{0}  {1}' -f $_.Length.ToString('N0').PadLeft(12), $_.FullName } |
            Write-Host
    }
}
