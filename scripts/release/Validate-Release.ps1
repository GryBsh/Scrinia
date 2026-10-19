# Exact committed candidate only. Optional package inputs are both-or-neither and never trigger fallback packing.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('\A[0-9a-f]{40}\z')][string]$Revision,
    [Parameter(Mandatory)][ValidateSet('win-x64','linux-x64','osx-arm64')][string]$Rid,
    [Parameter(Mandatory)][string]$Output,
    [string]$CandidatePackage,
    [ValidatePattern('\A[0-9a-f]{64}\z')][string]$CandidatePackageSha256
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
Set-Location -LiteralPath $Root
$head = ([string](& git rev-parse HEAD)).Trim()
if ($LASTEXITCODE -ne 0 -or $head -cne $Revision) { throw 'Checkout does not match the exact requested revision.' }
$dirty = @(& git status --porcelain=v1 --untracked-files=all)
if ($LASTEXITCODE -ne 0 -or $dirty.Count -ne 0) { throw 'Exact-candidate validation requires a clean checkout, including untracked files.' }
$Output = [IO.Path]::GetFullPath($Output)
if (Test-Path -LiteralPath $Output) { throw 'Output must be a new directory.' }
if ($Output.StartsWith($Root.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Retained output must be outside the exact candidate checkout.'
}
if ([bool]$CandidatePackage -ne [bool]$CandidatePackageSha256) { throw 'Supply both CandidatePackage and CandidatePackageSha256, or neither.' }
if ($CandidatePackage) {
    $CandidatePackage = (Resolve-Path -LiteralPath $CandidatePackage).Path
    if ((Get-FileHash -LiteralPath $CandidatePackage -Algorithm SHA256).Hash.ToLowerInvariant() -cne $CandidatePackageSha256) { throw 'Supplied candidate package SHA256 differs.' }
    Add-Type -AssemblyName System.IO.Compression
    $zip = [IO.Compression.ZipFile]::OpenRead($CandidatePackage)
    try {
        $entry = @($zip.Entries | Where-Object { $_.FullName.EndsWith('.nuspec', [StringComparison]::Ordinal) })
        if ($entry.Count -ne 1) { throw 'Candidate must have one nuspec.' }
        $reader = [IO.StreamReader]::new($entry[0].Open())
        try { [xml]$spec = $reader.ReadToEnd() } finally { $reader.Dispose() }
        if ($spec.package.metadata.id -cne 'scrinia' -or $spec.package.metadata.version -cne '0.6.0') { throw 'Supplied package is not the stable scrinia 0.6.0 candidate.' }
    } finally { $zip.Dispose() }
}
$sdk = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0 -or $sdk -cne '10.0.203') { throw 'All validation lanes require SDK 10.0.203 from global.json.' }
$version = (& dotnet msbuild (Join-Path $Root 'src/Scrinia/Scrinia.csproj') -nologo -getProperty:Version).Trim()
if ($LASTEXITCODE -ne 0 -or $version -cne '0.6.0') { throw 'The exact candidate source must evaluate to stable version 0.6.0.' }
$arch = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
$os = if ($IsWindows) { 'win' } elseif ($IsLinux) { 'linux' } elseif ($IsMacOS) { 'osx' } else { throw 'Unsupported native OS.' }
$resolved = "$os-$($arch.ToLowerInvariant())"
if ($resolved -cne $Rid) { throw "Native lane identity mismatch: $resolved vs $Rid" }
New-Item -ItemType Directory -Path $Output | Out-Null
$Evidence = Join-Path $Output 'evidence'
$Artifacts = Join-Path $Output 'artifacts'
New-Item -ItemType Directory -Path $Evidence,$Artifacts | Out-Null
$script:sequence = 0
function Invoke-Stage([string]$Name, [string]$File, [string[]]$Arguments, [int]$TimeoutSeconds = 1800) {
    $script:sequence++
    $stem = Join-Path $Evidence ('{0:D2}-{1}' -f $script:sequence,$Name)
    $start = [Diagnostics.ProcessStartInfo]::new($File)
    $start.WorkingDirectory = $Root
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['MSBUILDDISABLENODEREUSE'] = '1'
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $start
    $timer = [Diagnostics.Stopwatch]::StartNew(); $exitCode = -1; $failure = $null; $stdout = $null; $stderr = $null
    $drainTimeout = [Threading.CancellationTokenSource]::new()
    $outFile = [IO.File]::Create("$stem.stdout.log"); $errFile = [IO.File]::Create("$stem.stderr.log")
    try {
        if (-not $process.Start()) { throw 'Process did not start.' }
        $stdout = $process.StandardOutput.BaseStream.CopyToAsync($outFile,$drainTimeout.Token)
        $stderr = $process.StandardError.BaseStream.CopyToAsync($errFile,$drainTimeout.Token)
        if (-not $process.WaitForExit($TimeoutSeconds * 1000)) { $process.Kill($true); [void]$process.WaitForExit(10000); throw "Stage timed out: $Name" }
        $exitCode = $process.ExitCode
        if ($exitCode -ne 0) { throw "Required stage $Name failed with exit $exitCode; retained logs: $stem" }
    } catch { $failure = $_.Exception.Message; throw }
    finally {
        try {
            foreach ($task in @($stdout,$stderr)) {
                if ($null -eq $task) { continue }
                if (-not $task.Wait(10000)) {
                    $drainTimeout.Cancel()
                    throw 'Output drain exceeded 10 seconds; partial output retained.'
                }
                [void]$task.GetAwaiter().GetResult()
            }
        } catch {
            [IO.File]::WriteAllText("$stem.cleanup.log",($_ | Out-String))
            if ($null -eq $failure) { $failure = $_.Exception.Message; $exitCode = -1 }
        }
        $drainTimeout.Cancel(); $outFile.Dispose(); $errFile.Dispose(); $drainTimeout.Dispose()
        @{ command = $File; arguments = $Arguments; exitCode = $exitCode; elapsedSeconds = $timer.Elapsed.TotalSeconds; error = $failure } |
            ConvertTo-Json -Depth 4 | Set-Content -LiteralPath "$stem.outcome.json" -Encoding utf8NoBOM
        $process.Dispose()
        if ($null -ne $failure -and $exitCode -eq -1) { throw $failure }
    }
}
function Get-Tree([string]$Directory) {
    $files = @(Get-ChildItem -LiteralPath $Directory -File -Recurse)
    return @($files | ForEach-Object {
        $mode = if ($IsWindows) { '' } else { [IO.File]::GetUnixFileMode($_.FullName).ToString() }
        '{0}|{1}|{2}|{3}' -f [IO.Path]::GetRelativePath($Directory,$_.FullName),$_.Length,(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash,$mode
    } | Sort-Object)
}
try {
    Invoke-Stage 'sdk-info' 'dotnet' @('--info')
    @{ commit=$head; sdkVersion=$sdk; os=[Runtime.InteropServices.RuntimeInformation]::OSDescription; architecture=$arch;
       rawRid=[Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier; resolvedRid=$Rid; runId=$(if ($env:GITHUB_RUN_ID) { $env:GITHUB_RUN_ID } else { 'local-' + [Guid]::NewGuid().ToString('N') }) } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Evidence 'lane.json') -Encoding utf8NoBOM
    Invoke-Stage 'build-tools' 'dotnet' @('build','tools/Scrinia.ReleaseTools','-c','Release')
    $tool = Join-Path $Root 'tools/Scrinia.ReleaseTools/bin/Release/net10.0/Scrinia.ReleaseTools.dll'
    $packageDir = Join-Path $Output 'package'
    New-Item -ItemType Directory -Path $packageDir | Out-Null
    if ($CandidatePackage) { Copy-Item -LiteralPath $CandidatePackage -Destination (Join-Path $packageDir 'scrinia.0.6.0.nupkg') }
    else { Invoke-Stage 'pack-candidate' 'dotnet' @('pack','src/Scrinia/Scrinia.csproj','-c','Release','--output',$packageDir) }
    $package = Join-Path $packageDir 'scrinia.0.6.0.nupkg'
    Copy-Item -LiteralPath $package -Destination $Artifacts
    $baselineDir = Join-Path $Output 'baseline-test-only'
    $baselineBuild = Join-Path ([IO.Path]::GetTempPath()) ('scri-baseline-' + [Guid]::NewGuid().ToString('N'))
    [IO.File]::WriteAllText((Join-Path $Evidence 'baseline-intermediates.txt'),$baselineBuild)
    Invoke-Stage 'pack-test-baseline' 'dotnet' @('pack','src/Scrinia/Scrinia.csproj','-c','Release','--disable-build-servers','-m:1','-p:Version=0.6.0-validation-baseline.1','--artifacts-path',$baselineBuild,'--output',$baselineDir)
    $records = @(@{ name='scrinia.0.6.0.nupkg'; length=(Get-Item -LiteralPath $package).Length; sha256=(Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash.ToLowerInvariant(); kind='nuget'; rid=$null })
    foreach ($kind in @('plain','embeddings')) {
        $tree = Join-Path $Output "$kind-tree"
        if ($IsWindows) {
            $args = @('-NoProfile','-File',(Join-Path $Root 'publish.ps1'),'-OutputDir',$tree,'-Platform',$Rid)
            if ($kind -ceq 'embeddings') { $args += '-WithEmbeddings' }
            Invoke-Stage "publish-$kind" 'pwsh' $args
        } else {
            $args = @((Join-Path $Root 'publish.sh'),$tree,"--platform=$Rid")
            if ($kind -ceq 'embeddings') { $args += '--with-embeddings' }
            Invoke-Stage "publish-$kind" 'bash' $args
        }
        $name = "scrinia-0.6.0-$Rid-$kind" + $(if ($IsWindows) { '.zip' } else { '.tar.gz' })
        $archive = Join-Path $Artifacts $name
        $extracted = Join-Path $Output "$kind-extracted"
        New-Item -ItemType Directory -Path $extracted | Out-Null
        if ($IsWindows) {
            # Windows tar emits ZIP by suffix and preserves the full subtree, including the root manifest.
            Invoke-Stage "archive-$kind" 'tar' @('-a','-cf',$archive,'-C',$tree,'.')
        } else { Invoke-Stage "archive-$kind" 'tar' @('-czf',$archive,'-C',$tree,'.') }
        Invoke-Stage "extract-$kind" 'tar' @('-xf',$archive,'-C',$extracted)
        $before = @(Get-Tree $tree); $after = @(Get-Tree $extracted)
        if ($before.Count -ne $after.Count -or @(Compare-Object $before $after -CaseSensitive).Count -ne 0) { throw "Archive members, bytes or executable modes differ for $kind." }
        [IO.File]::WriteAllLines((Join-Path $Evidence "$kind-members.txt"),$after)
        $records += @{ name=$name; length=(Get-Item -LiteralPath $archive).Length; sha256=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant(); kind="$kind-archive"; rid=$Rid }
        if ($kind -ceq 'embeddings') {
            Invoke-Stage 'package-and-extracted-lifecycle' 'dotnet' @($tool,'lifecycle','--package',$package,'--baseline-package',(Join-Path $baselineDir 'scrinia.0.6.0-validation-baseline.1.nupkg'),'--bundle-root',$extracted,'--output',(Join-Path $Evidence 'lifecycle')) 3600
        } else {
            Invoke-Stage 'plain-extracted-version' (Join-Path $extracted $(if ($IsWindows) { 'scri.exe' } else { 'scri' })) @('--version')
            $versionLog = Join-Path $Evidence ('{0:D2}-plain-extracted-version.stdout.log' -f $script:sequence)
            if ([IO.File]::ReadAllText($versionLog).Trim() -cne '0.6.0') { throw 'Plain extracted host version differs from candidate.' }
        }
    }
    @{ version=1; commit=$Revision; packageVersion='0.6.0'; sdkVersion=$sdk; runId=$env:GITHUB_RUN_ID; rid=$Rid; artifacts=$records } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Output 'lane-artifacts.json') -Encoding utf8NoBOM
    [IO.File]::WriteAllText((Join-Path $Evidence 'complete.txt'),'Required native, package and artifact stages passed.')
} catch {
    [IO.File]::WriteAllText((Join-Path $Evidence 'failure.txt'),($_ | Out-String))
    throw
}
