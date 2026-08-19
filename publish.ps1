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

# Kept in step with the LLamaSharp.Backend.Vulkan PackageReference in the
# Scrinia.Plugin.Embeddings project file by a test — a drift here makes the overwrite path
# below not exist, which used to be a silent skip.
$VulkanBackendVersion = '0.25.0'

# Projects marked rids=host in the manifest are published only for this machine's own RID.
# Scrinia.Merge is NativeAOT, and cross-OS native compilation is unsupported: a foreign-RID
# publish exits 1 and writes nothing.
#
# RuntimeInformation rather than $IsWindows/$IsLinux/$IsMacOS: those automatic variables do not
# exist in Windows PowerShell 5.1, where they evaluate to $null — which would skip scri-merge for
# EVERY rid and reproduce the missing merge driver this branch fixes. OS and architecture are
# resolved separately and joined so a linux-arm64 or win-arm64 host is not mistaken for its x64
# sibling — the same distinction the old macOS-only branch made, now made for every OS.
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
# Filters blank and #-prefixed rows exactly as publish.sh does. The two readers of one source
# of truth must agree about what the file means.
$Projects = @(Import-Csv -Path $Manifest -Delimiter "`t" |
    Where-Object { $_.id -and -not $_.id.StartsWith('#') })
if ($Projects.Count -eq 0) { throw "Manifest $Manifest lists no projects" }
# Validated here, once, at read time — not deep inside the publish loop — so a bad row is
# caught before any artifact has been published for any RID, rather than mid-run.
foreach ($p in $Projects) {
    if ($p.output -notin @('rid', 'plugin')) { throw "Unknown output kind '$($p.output)' for $($p.id)" }
    if ($p.profile -notin @('trimmed', 'plain')) { throw "Unknown profile '$($p.profile)' for $($p.id)" }
}

# Overwrites the published native DLLs with the Vulkan-compiled variants. Single-file publish
# flattens all native DLLs to the root, and the CPU backend's (ggml-base, ggml, llama, mtmd)
# overwrite the Vulkan ones since they share filenames — so LLamaSharp would load CPU.
function Set-VulkanNatives {
    param([string]$Rid, [string]$Dest)

    $pkg = if ($Rid -like 'win-*') { 'llamasharp.backend.vulkan.windows' }
           elseif ($Rid -like 'linux-*') { 'llamasharp.backend.vulkan.linux' }
           else { $null }

    if (-not $pkg) {
        # [Console]::Error, not Write-Warning: publish.sh writes these to stderr, and the two
        # scripts are meant to be an equivalent pair. Write-Warning uses the warning stream.
        [Console]::Error.WriteLine("  [warn] no Vulkan backend package exists for $Rid; shipping the CPU backend.")
        return
    }

    # $HOME, not $env:USERPROFILE: the latter is Windows-only and unset on Linux/macOS, which
    # made this branch never find the Vulkan natives there and silently ship the CPU backend
    # every time. $HOME is a PowerShell automatic variable on every platform.
    $src = "$HOME/.nuget/packages/$pkg/$VulkanBackendVersion/runtimes/$Rid/native/vulkan"
    if (Test-Path $src) {
        Copy-Item "$src/*" $Dest -Force
        Write-Host '    Overwrote root native DLLs with Vulkan variants'
    } else {
        [Console]::Error.WriteLine("  [warn] Vulkan natives not found at $src; shipping the CPU backend.")
    }
}

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

foreach ($rid in $Rids) {
    $ridDir = if ($SinglePlatform) { $OutputDir } else { "$OutputDir/$rid" }

    foreach ($p in $Projects) {
        if ($p.condition -eq 'with-embeddings' -and -not $WithEmbeddings) { continue }

        if ($p.rids -eq 'host' -and $rid -ne $HostRid) {
            if ($HostRidSupported) {
                [Console]::Error.WriteLine(
                    "  [skip] $($p.id) for ${rid}: it is NativeAOT and cross-OS native compilation is " +
                    "unsupported. Build on $rid, or use -Platform $HostRid.")
            } else {
                [Console]::Error.WriteLine(
                    "  [skip] $($p.id) for ${rid}: it is NativeAOT and cross-OS native compilation is " +
                    "unsupported, and this host ($HostOs/$HostArch) is not one this script publishes " +
                    "for. Supported: $($AllRids -join ' ').")
            }
            continue
        }

        $outDir = switch ($p.output) {
            'rid'    { $ridDir }
            'plugin' { "$ridDir/plugins/$($p.id)" }
            default  { throw "Unknown output kind '$($p.output)' for $($p.id)" }
        }

        Write-Host "Publishing $($p.id) for $rid ..."
        switch ($p.profile) {
            'trimmed' {
                # Trim-analysis warnings are gated as errors so a regression silently dropping a
                # member via reflection cannot ship. If a trim warning fires, fix the root cause —
                # either move to a JsonSerializerContext, annotate with [DynamicallyAccessedMembers],
                # or rewrite the reflection-heavy call to a trim-safe shape.
                dotnet publish (Join-Path $PSScriptRoot $p.project) `
                    --runtime $rid `
                    --self-contained `
                    --configuration Release `
                    -p:PublishSingleFile=true `
                    -p:PublishTrimmed=true `
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

        if ($p.output -eq 'plugin') { Set-VulkanNatives -Rid $rid -Dest $outDir }
        Write-Host "  -> $outDir"
    }
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
        Get-ChildItem "$ridDir/plugins/scri-plugin-embeddings/scri-plugin-embeddings$ext" -ErrorAction SilentlyContinue |
            ForEach-Object { '{0}  {1}' -f $_.Length.ToString('N0').PadLeft(12), $_.FullName } |
            Write-Host
    }
}
