param(
    [Parameter(Mandatory)]
    [string]$OutputDir,

    [switch]$WithVulkan,

    [ValidateSet('win-x64', 'linux-x64', 'osx-arm64')]
    [string]$Platform
)

$ErrorActionPreference = 'Stop'

$Rids = if ($Platform) { @($Platform) } else { @('win-x64', 'linux-x64', 'osx-arm64') }
$Project = 'src/Scrinia/Scrinia.csproj'
$VulkanProject = 'src/Scrinia.Plugin.Embeddings.Cli/Scrinia.Plugin.Embeddings.Cli.csproj'
# When a single platform is specified, output directly into OutputDir (no RID subdirectory).
$SinglePlatform = [bool]$Platform

New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

foreach ($rid in $Rids) {
    $ridDir = if ($SinglePlatform) { $OutputDir } else { "$OutputDir/$rid" }

    Write-Host "Publishing $rid ..."
    dotnet publish $Project `
        --runtime $rid `
        --self-contained `
        --configuration Release `
        -p:PublishSingleFile=true `
        -p:PublishTrimmed=true `
        --output $ridDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid" }
    Write-Host "  -> $ridDir"

    if ($WithVulkan) {
        Write-Host "  Publishing Vulkan embeddings plugin for $rid ..."
        $pluginsDir = "$ridDir/plugins/scri-plugin-embeddings"
        # Single-file + self-contained (not trimmed). Native DLLs extract alongside the exe.
        dotnet publish $VulkanProject `
            --runtime $rid `
            --configuration Release `
            --output $pluginsDir
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish (vulkan plugin) failed for $rid" }

        # Single-file publish flattens all native DLLs to the root. The CPU backend's
        # DLLs (ggml-base, ggml, llama, mtmd) overwrite the Vulkan ones since they share
        # filenames. Overwrite the root copies with the Vulkan-compiled versions so
        # LLamaSharp loads Vulkan by default.
        $nugetBase = "$env:USERPROFILE/.nuget/packages"
        if ($rid -like 'win-*') {
            $vulkanSrc = "$nugetBase/llamasharp.backend.vulkan.windows/0.25.0/runtimes/$rid/native/vulkan"
        } else {
            $vulkanSrc = "$nugetBase/llamasharp.backend.vulkan.linux/0.25.0/runtimes/$rid/native/vulkan"
        }
        if (Test-Path $vulkanSrc) {
            Copy-Item "$vulkanSrc/*" $pluginsDir -Force
            Write-Host "    Overwrote root native DLLs with Vulkan variants"
        }
        Write-Host "  -> $pluginsDir"
    }
}

Write-Host ''
Write-Host 'Done. Builds:'
foreach ($rid in $Rids) {
    $ridDir = if ($SinglePlatform) { $OutputDir } else { "$OutputDir/$rid" }
    Get-ChildItem "$ridDir/scri*" -ErrorAction SilentlyContinue |
        ForEach-Object { '{0}  {1}' -f $_.Length.ToString('N0').PadLeft(12), $_.FullName } |
        Write-Host
    if ($WithVulkan) {
        $ext = if ($rid -like 'win-*') { '.exe' } else { '' }
        Get-ChildItem "$ridDir/plugins/scri-plugin-embeddings$ext" -ErrorAction SilentlyContinue |
            ForEach-Object { '{0}  {1}' -f $_.Length.ToString('N0').PadLeft(12), $_.FullName } |
            Write-Host
    }
}
