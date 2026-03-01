param(
    [Parameter(Mandatory)]
    [string]$OutputDir,

    [switch]$WithEmbeddings,

    [ValidateSet('win-x64', 'linux-x64', 'osx-arm64')]
    [string]$Platform
)

$ErrorActionPreference = 'Stop'

$Rids = if ($Platform) { @($Platform) } else { @('win-x64', 'linux-x64', 'osx-arm64') }
$Project = 'src/Scrinia/Scrinia.csproj'
$EmbeddingsProject = 'src/Scrinia.Plugin.Embeddings.Cli/Scrinia.Plugin.Embeddings.Cli.csproj'
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

    if ($WithEmbeddings) {
        Write-Host "  Publishing embeddings plugin for $rid ..."
        $pluginsDir = "$ridDir/plugins"
        # Single-file + self-contained + native bundling is configured in the .csproj.
        # Only --runtime is needed here for RID selection.
        dotnet publish $EmbeddingsProject `
            --runtime $rid `
            --configuration Release `
            --output $pluginsDir
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish (embeddings) failed for $rid" }

        # Clean stray build artifacts that don't go into the single-file bundle
        Get-ChildItem "$pluginsDir" -Exclude "scri-plugin-*" -ErrorAction SilentlyContinue |
            Remove-Item -Force -Recurse
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
    if ($WithEmbeddings) {
        $ext = if ($rid -like 'win-*') { '.exe' } else { '' }
        Get-ChildItem "$ridDir/plugins/scri-plugin-embeddings$ext" -ErrorAction SilentlyContinue |
            ForEach-Object { '{0}  {1}' -f $_.Length.ToString('N0').PadLeft(12), $_.FullName } |
            Write-Host
    }
}
