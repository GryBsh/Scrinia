[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ValidationRunId,
    [Parameter(Mandatory)][string]$Revision,
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$ArtifactRoot,
    [Parameter(Mandatory)][string]$ProgressPath,
    [switch]$ValidateOnly,
    [Parameter(Mandatory)][string]$ValidatorPath,
    # Tests supply an isolated executable transport. It must never use a real GitHub credential.
    [Parameter(DontShow)][scriptblock]$TestProcessRunner
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$locks = @()
$temporary = $null

function Invoke-Transport([string]$Executable, [string[]]$Arguments, [string]$OutputPath = '', [long]$MaximumBytes = 2147483648) {
    if ($TestProcessRunner) { return & $TestProcessRunner $Executable $Arguments $OutputPath $MaximumBytes }
    $start = [Diagnostics.ProcessStartInfo]::new($Executable)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $start
    $file = $null; $started = $false
    $deadline = [Threading.CancellationTokenSource]::new(600000)
    try {
        if (!$process.Start()) { throw 'Transport did not start.' }
        $started = $true
        $errorTask = $process.StandardError.ReadToEndAsync()
        if ($OutputPath) {
            $file = [IO.File]::Create($OutputPath)
            $buffer = [byte[]]::new(65536); [long]$total = 0
            do {
                $count = $process.StandardOutput.BaseStream.ReadAsync($buffer,0,$buffer.Length,$deadline.Token).GetAwaiter().GetResult()
                $total += $count
                if ($total -gt $MaximumBytes) { throw 'Binary transport output exceeded its bounded capacity.' }
                if ($count) { $file.Write($buffer,0,$count) }
            } while ($count)
        } else { $outputTask = $process.StandardOutput.ReadToEndAsync() }
        if (!$process.WaitForExit(600000)) { $process.Kill($true); throw 'Transport deadline exceeded.' }
        if (!$OutputPath) { $outputTask.GetAwaiter().GetResult() | Out-Null }
        $errorTask.GetAwaiter().GetResult() | Out-Null
        if ($process.ExitCode -ne 0) { throw "Transport $Executable failed (exit $($process.ExitCode)); outcome requires reconciliation." }
        if (!$OutputPath) { return $outputTask.Result }
        return $errorTask.Result
    } finally {
        if ($started -and !$process.HasExited) { $process.Kill($true); $process.WaitForExit(10000) | Out-Null }
        if ($file) { $file.Dispose() }; $deadline.Dispose(); $process.Dispose()
    }
}
function Confirm-CurrentValidationAttempt {
    # The jobs endpoint is /attempts/{selectedAttempt}/jobs. The documented job DTO
    # does not guarantee run_attempt; current attempt identity is checked on the run itself.
    $current = Read-Api "actions/runs/$ValidationRunId"
    if ($current.id.ToString() -cne $ValidationRunId -or $current.run_attempt -ne $selectedAttempt -or
        $current.repository.full_name -cne $repository -or $current.head_repository.full_name -cne $repository -or
        $current.head_sha -cne $Revision -or $current.workflow_id -ne $run.workflow_id -or $current.event -cne $run.event -or
        $current.status -cne 'completed' -or $current.conclusion -cne 'success') {
        throw 'Current validation attempt or successful run identity changed; publication is refused.'
    }
}
function Invoke-PublicationWrite([string]$Executable, [string[]]$Arguments) {
    Confirm-CurrentValidationAttempt
    Invoke-Transport $Executable $Arguments | Out-Null
}
function Read-Api([string]$Path) {
    $text = Invoke-Transport 'gh' @('api','--method','GET',"repos/$repository/$Path",'-H','Accept: application/vnd.github+json')
    return $text | ConvertFrom-Json
}
function Read-Pages([string]$Path, [string]$Property = '') {
    $all = @()
    for ($page = 1; $page -le 100; $page++) {
        $separator = if ($Path.Contains('?')) { '&' } else { '?' }
        $response = Read-Api "$Path${separator}per_page=100&page=$page"
        $members = @(if ($Property) { $response.$Property } else { $response })
        $all += $members
        if ($members.Count -lt 100) { return $all }
    }
    throw 'Remote pagination limit exceeded; eligibility is unknown.'
}
function Invoke-Validator([string[]]$Arguments) {
    & dotnet $ValidatorPath @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Local publication validator refused candidate or progress.' }
}
function Save-Progress {
    $directory = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ProgressPath))
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $pending = Join-Path $directory ([IO.Path]::GetRandomFileName())
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes(($progress | ConvertTo-Json -Depth 10))
        $stream = [IO.FileStream]::new($pending,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try { $stream.Write($bytes); $stream.Flush($true) } finally { $stream.Dispose() }
        [IO.File]::Move($pending, [IO.Path]::GetFullPath($ProgressPath), $true)
    } finally { if (Test-Path -LiteralPath $pending) { Remove-Item -LiteralPath $pending } }
}
function Read-Release {
    $matches = @(Read-Pages 'releases' | Where-Object tag_name -CEQ $tag)
    if ($matches.Count -gt 1) { throw 'CONFLICT: duplicate tag releases.' }
    if ($matches.Count -eq 0) {
        if ($progress.draftReleaseId) { throw 'CONFLICT: previously confirmed draft release is missing.' }
        return $null
    }
    $release = $matches[0]
    if ($release.body -cne $metadata -or $release.target_commitish -cne $Revision -or $release.prerelease -ne $false) {
        throw 'CONFLICT: existing release metadata differs from this validated candidate.'
    }
    if ($progress.draftReleaseId -and $progress.draftReleaseId -ne $release.id) { throw 'CONFLICT: draft release ID changed.' }
    return $release
}
function Check-Assets($Release) {
    if (!$Release) { return @() }
    $assets = @(Read-Pages "releases/$($Release.id)/assets")
    if (@($assets | Group-Object name | Where-Object Count -GT 1).Count) { throw 'CONFLICT: duplicate remote asset names.' }
    $verified = @()
    foreach ($asset in $assets) {
        $record = @($manifest.artifacts | Where-Object name -CEQ $asset.name)
        if ($record.Count -ne 1 -or $asset.state -cne 'uploaded' -or $asset.size -ne $record[0].length -or $asset.id -le 0) {
            throw 'CONFLICT: remote release assets differ from the exact seven-artifact manifest.'
        }
        $remotePath = Join-Path $temporary $asset.name
        Invoke-Transport 'gh' @('api','--method','GET',"repos/$repository/releases/assets/$($asset.id)",'-H','Accept: application/octet-stream') $remotePath | Out-Null
        if ((Get-FileHash -LiteralPath $remotePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $record[0].sha256) {
            throw 'CONFLICT: downloaded release asset bytes differ.'
        }
        $verified += $asset.name
    }
    return $verified
}
function Check-Nuget {
    $remotePath = Join-Path $temporary 'remote.nupkg'
    # Bound the downloaded envelope independently of ZIP-entry limits and Content-Length.
    # curl streams only the body to stdout; the completed HTTP status goes to stderr.
    # A transport error (including overflow) never establishes package absence.
    $status = (Invoke-Transport 'curl' @('--silent','--show-error','--location','--write-out','%{stderr}%{http_code}',
        'https://api.nuget.org/v3-flatcontainer/scrinia/0.6.0/scrinia.0.6.0.nupkg') $remotePath 134217728).Trim()
    if ($status -ceq '404') { return $null }
    if ($status -cne '200') { throw 'UNKNOWN: NuGet service did not supply package identity proof.' }
    $proofPath = Join-Path $temporary 'package-proof.json'
    Invoke-Validator @('compare-package','--original',$packagePath,'--remote',$remotePath,'--output',$proofPath)
    return Get-Content -LiteralPath $proofPath -Raw | ConvertFrom-Json
}
function Verify-SelectedRunArtifact {
    $name = "validated-release-$ValidationRunId-$Revision"
    $artifacts = @(Read-Pages "actions/runs/$ValidationRunId/artifacts" 'artifacts' | Where-Object name -CEQ $name)
    if ($artifacts.Count -ne 1) { throw 'Selected validation run has no sole exact named immutable artifact.' }
    $artifact = $artifacts[0]
    if ($artifact.id -le 0 -or $artifact.expired -ne $false -or $artifact.size_in_bytes -le 0 -or $artifact.size_in_bytes -gt 2147483648 -or
        $artifact.digest -cnotmatch '\Asha256:[0-9a-f]{64}\z' -or $artifact.workflow_run.id.ToString() -cne $ValidationRunId -or
        $artifact.workflow_run.repository_id -ne $run.repository.id -or $artifact.workflow_run.head_repository_id -ne $run.head_repository.id -or
        $artifact.workflow_run.head_sha -cne $Revision) { throw 'Selected validation artifact identity, retention, capacity or service digest is unproven.' }
    $archive = Join-Path $temporary 'selected-validation.zip'
    # The documented repository-scoped download API follows its service redirect. Caller-origin URLs are never used.
    Invoke-Transport 'gh' @('api','--method','GET',"repos/$repository/actions/artifacts/$($artifact.id)/zip") $archive $artifact.size_in_bytes | Out-Null
    if ((Get-Item -LiteralPath $archive).Length -gt 2147483648) { throw 'Validation artifact download exceeded bounded capacity.' }
    Invoke-Validator @('verify-validation-archive','--archive',$archive,'--manifest',$manifestPath,'--digest',$artifact.digest.Substring(7))
}

try {
    if ($Revision -cnotmatch '\A[0-9a-f]{40}\z' -or $ValidationRunId -cnotmatch '\A[1-9][0-9]*\z' -or $Version -cne '0.6.0') { throw 'Exact stable candidate identity is required.' }
    $repository = $env:GITHUB_REPOSITORY
    if ($repository -cnotmatch '\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z') { throw 'Repository identity is unavailable.' }
    if ($TestProcessRunner -and ($env:GH_TOKEN -or $env:GITHUB_TOKEN)) { throw 'Test transport requires empty GitHub credentials.' }
    $tag = "v$Version"
    $ArtifactRoot = [IO.Path]::GetFullPath($ArtifactRoot)
    $manifestPath = Join-Path $ArtifactRoot 'release-artifacts.json'
    foreach ($path in Get-ChildItem -LiteralPath $ArtifactRoot -File) {
        $locks += [IO.File]::Open($path.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    }
    Invoke-Validator @('verify-artifacts','--manifest',$manifestPath,'--root',$ArtifactRoot,'--sha',$Revision,'--version',$Version)
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($manifest.runId -cne $ValidationRunId -or $manifest.artifacts[0].name -notmatch '\A[A-Za-z0-9._-]+\z') { throw 'Manifest validation run differs.' }
    $packageRecord = @($manifest.artifacts | Where-Object kind -CEQ 'nuget')[0]
    if ($packageRecord.name -cne 'scrinia.0.6.0.nupkg') { throw 'NuGet artifact destination identity differs.' }
    $packagePath = Join-Path $ArtifactRoot $packageRecord.name
    $manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $progress = [ordered]@{ version=1; commit=$Revision; packageVersion=$Version; validationRunId=$ValidationRunId; manifestSha256=$manifestHash; draftReleaseId=$null; assetsVerified=@(); nugetVerified=$null; releasePublished=$false }
    if (Test-Path -LiteralPath $ProgressPath) {
        Invoke-Validator @('verify-progress','--progress',$ProgressPath,'--manifest',$manifestPath,'--sha',$Revision,'--version',$Version,'--run',$ValidationRunId)
        $saved = Get-Content -LiteralPath $ProgressPath -Raw | ConvertFrom-Json
        $progress.draftReleaseId = $saved.draftReleaseId
    }
    $run = Read-Api "actions/runs/$ValidationRunId"
    if ($run.id.ToString() -cne $ValidationRunId -or $run.repository.full_name -cne $repository -or $run.head_repository.full_name -cne $repository -or
        $run.head_sha -cne $Revision -or $run.status -cne 'completed' -or $run.conclusion -cne 'success' -or $run.run_attempt -lt 1) { throw 'Validation run identity or successful outcome is missing.' }
    $workflow = Read-Api "actions/workflows/$($run.workflow_id)"
    $direct = $workflow.path -ceq '.github/workflows/release-validation.yml' -and $run.event -ceq 'workflow_dispatch'
    $reusable = $workflow.path -ceq '.github/workflows/ci.yml' -and $run.event -ceq 'push'
    if (!$direct -and !$reusable) { throw 'Validation workflow/event is ineligible.' }
    if ($reusable) {
        $references = @($run.referenced_workflows | Where-Object { $_.path.StartsWith("$repository/.github/workflows/release-validation.yml@",[StringComparison]::Ordinal) -and $_.sha -ceq $Revision })
        if ($references.Count -ne 1) { throw 'Reusable validation workflow identity is unproven.' }
    }
    $prefix = if ($reusable) { 'release-validation / ' } else { '' }
    $required = @("${prefix}build-check", "${prefix}native (win-x64)", "${prefix}native (linux-x64)", "${prefix}native (osx-arm64)", "${prefix}aggregate")
    $selectedAttempt = $run.run_attempt
    $jobs = @(Read-Pages "actions/runs/$ValidationRunId/attempts/$selectedAttempt/jobs" 'jobs')
    foreach ($name in $required) {
        $matching = @($jobs | Where-Object name -CEQ $name)
        if ($matching.Count -ne 1 -or $matching[0].status -cne 'completed' -or $matching[0].conclusion -cne 'success' -or
            $matching[0].head_sha -cne $Revision -or $matching[0].run_id.ToString() -cne $ValidationRunId -or
            ($matching[0].PSObject.Properties.Name -ccontains 'run_attempt' -and $matching[0].run_attempt -ne $selectedAttempt)) {
            throw 'A required current-attempt native/build/aggregate validation job is missing or unsuccessful.'
        }
    }
    Confirm-CurrentValidationAttempt
    $ref = Read-Api "git/ref/tags/$tag"
    $target = $ref.object
    for ($depth=0; $target.type -ceq 'tag' -and $depth -lt 8; $depth++) { $target = (Read-Api "git/tags/$($target.sha)").object }
    if ($target.type -cne 'commit' -or $target.sha -cne $Revision) { throw 'Release tag does not resolve to the validated SHA.' }
    $metadata = @("Scrinia $Version validated release", '', '```text', "commit: $Revision",
        "validationRunId: $ValidationRunId", "manifestSha256: $manifestHash", '```') -join "`n"
    $temporary = Join-Path ([IO.Path]::GetTempPath()) ('scrinia-publication-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($temporary) | Out-Null
    Verify-SelectedRunArtifact
    Confirm-CurrentValidationAttempt
    $release = Read-Release
    $verifiedAssets = @(Check-Assets $release)
    $nuget = Check-Nuget
    if ($release -and !$release.draft -and ($verifiedAssets.Count -ne 7 -or !$nuget)) { throw 'CONFLICT: published release lacks exact artifact proof.' }
    if ($ValidateOnly) { Write-Output 'Validation identity, successful native lanes, tag and available remote bytes verified; no publication performed.'; return }
    if ($env:SCRINIA_PUBLICATION_APPROVED -cne 'true' -or !$env:NUGET_API_KEY -or (!$TestProcessRunner -and !$env:GH_TOKEN) -or
        $env:SCRINIA_NUGET_OWNER -cnotmatch '\A[A-Za-z0-9_.-]+\z' -or $env:SCRINIA_NUGET_OWNERSHIP_CONFIRMED -cne "scrinia:$($env:SCRINIA_NUGET_OWNER)") {
        throw 'Approved release environment, NuGet ownership attestation and publication secrets are required.'
    }
    if (!$release) {
        $request = Join-Path $temporary 'create.json'
        @{ tag_name=$tag; target_commitish=$Revision; name="Scrinia $Version"; body=$metadata; draft=$true; prerelease=$false } | ConvertTo-Json | Set-Content -LiteralPath $request -Encoding utf8NoBOM
        Invoke-PublicationWrite 'gh' @('api','--method','POST',"repos/$repository/releases",'--input',$request)
        $release = Read-Release
        if (!$release -or !$release.draft) { throw 'UNKNOWN: draft creation was not confirmed.' }
    }
    $progress.draftReleaseId = $release.id; $progress.assetsVerified = $verifiedAssets; $progress.nugetVerified = $nuget
    $progress.releasePublished = !$release.draft
    Save-Progress
    foreach ($record in $manifest.artifacts) {
        if ($record.name -cin $progress.assetsVerified) { continue }
        if (!$release.draft) { throw 'CONFLICT: published release cannot receive missing assets.' }
        Invoke-PublicationWrite 'gh' @('release','upload',$tag,(Join-Path $ArtifactRoot $record.name),'--repo',$repository)
        $progress.assetsVerified = @(Check-Assets (Read-Release)); Save-Progress
        if ($record.name -cnotin $progress.assetsVerified) { throw 'UNKNOWN: uploaded asset was not confirmed.' }
    }
    if (!$progress.nugetVerified) {
        # Reconcile immediately before pushing; neither a 409 nor missing metadata proves identity/ownership.
        $progress.nugetVerified = Check-Nuget
        if (!$progress.nugetVerified) {
            Invoke-PublicationWrite 'dotnet' @('nuget','push',$packagePath,'--source','https://api.nuget.org/v3/index.json','--api-key',$env:NUGET_API_KEY)
            $progress.nugetVerified = Check-Nuget
            if (!$progress.nugetVerified) { throw 'UNKNOWN: NuGet package publication has not been confirmed.' }
        }
        Save-Progress
    }
    $release = Read-Release
    $progress.assetsVerified = @(Check-Assets $release); $progress.nugetVerified = Check-Nuget
    if ($progress.assetsVerified.Count -ne 7 -or !$progress.nugetVerified) { throw 'UNKNOWN: final remote byte proof is incomplete.' }
    if ($release.draft) {
        $request = Join-Path $temporary 'publish.json'
        @{ draft=$false } | ConvertTo-Json | Set-Content -LiteralPath $request -Encoding utf8NoBOM
        Invoke-PublicationWrite 'gh' @('api','--method','PATCH',"repos/$repository/releases/$($release.id)",'--input',$request)
        $release = Read-Release
        if (!$release -or $release.draft) { throw 'UNKNOWN: GitHub publication was not confirmed.' }
    }
    $progress.releasePublished = $true; Save-Progress
    Write-Output 'Exact validated package and seven GitHub assets are published and remotely verified.'
} catch {
    $message = $_.Exception.Message
    foreach ($secret in @($env:NUGET_API_KEY,$env:GH_TOKEN,$env:GITHUB_TOKEN)) { if ($secret) { $message=$message.Replace($secret,'[redacted]') } }
    Write-Error "Publication refused: $message" -ErrorAction Continue
    exit 1
} finally {
    foreach ($stream in $locks) { $stream.Dispose() }
    if ($temporary -and (Test-Path -LiteralPath $temporary)) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
