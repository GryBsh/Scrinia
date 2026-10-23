# Requires a completed solution build in the selected configuration.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Root,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Root = (Resolve-Path -LiteralPath $Root).Path
$expectedNames = @('auditor','chaos-engineer','debugger','evolutionary','onboarder','qa','self-reflector') | Sort-Object

function Assert-ExactNames([string[]]$Actual, [string]$Source) {
    $sorted = @($Actual | Sort-Object)
    if ($sorted.Count -ne $expectedNames.Count -or
        @(Compare-Object $expectedNames $sorted -CaseSensitive).Count -ne 0) {
        throw "Built-in skill names differ in $Source"
    }
}
function Read-RepoText([string]$Relative) {
    return [IO.File]::ReadAllText((Join-Path $Root $Relative))
}
function Get-TableNames([string]$Relative, [string]$Heading) {
    $pattern = '(?ms)^' + [regex]::Escape($Heading) + '\r?\n(?<section>.*?)(?=^#{1,6} |\z)'
    $sections = [regex]::Matches((Read-RepoText $Relative), $pattern)
    if ($sections.Count -ne 1) { throw "Expected one named skill section in $Relative" }
    return @([regex]::Matches($sections[0].Groups['section'].Value, '(?m)^\| `([^`|]+)` \|') |
        ForEach-Object { $_.Groups[1].Value })
}
function Invoke-Dotnet([string[]]$Arguments) {
    $output = & dotnet @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments -join ' ') failed: $output" }
    return ($output -join "`n").Trim()
}
function New-FixtureStartInfo([string]$Assembly, [string[]]$Arguments, [string]$Scratch, [string]$Tracking) {
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.WorkingDirectory = Join-Path $Scratch 'workspace'
    $start.ArgumentList.Add($Assembly)
    foreach ($argument in $Arguments) {
        $start.ArgumentList.Add($argument.Replace('{scratch}', $Scratch).Replace('{workspace}', $start.WorkingDirectory))
    }
    foreach ($key in @($start.Environment.Keys)) {
        if ($key -match '^(SCRINIA_|PLUGINS_)') { $start.Environment.Remove($key) | Out-Null }
    }
    $start.Environment['SCRINIA_HOME'] = Join-Path $Scratch 'home'
    $start.Environment['SCRINIA_WORKSPACE_TRACKING'] = $Tracking
    return $start
}
function Stop-FixtureProcess([Diagnostics.Process]$Process) {
    if (-not $Process.HasExited) { $Process.Kill($true) }
    if (-not $Process.WaitForExit(10000)) { throw 'Fixture process did not exit within the cleanup deadline' }
}
function Read-FixtureOutput($Task, [string]$Source) {
    if (-not $Task.Wait(10000)) { throw "Timed out draining $Source" }
    return $Task.GetAwaiter().GetResult()
}
function Initialize-SharedFixture([string]$Assembly, [string]$Scratch) {
    $base = Join-Path $Scratch 'base'
    [IO.Directory]::CreateDirectory((Join-Path $base '.git')) | Out-Null
    foreach ($arguments in @(
        ,@('shared', 'base', 'add', 'contracts', $base)
        ,@('shared', 'enable', 'contracts')
    )) {
        $process = [Diagnostics.Process]::new()
        $started = $false
        $stdout = $null
        $stderr = $null
        $primaryError = $null
        try {
            $process.StartInfo = New-FixtureStartInfo $Assembly $arguments $Scratch 'project-git'
            $started = $process.Start()
            $process.StandardInput.Close()
            $stdout = $process.StandardOutput.ReadToEndAsync()
            $stderr = $process.StandardError.ReadToEndAsync()
            if (-not $process.WaitForExit(120000)) { throw "Timed out setting up fixture: $($arguments -join ' ')" }
            $output = Read-FixtureOutput $stdout 'shared setup stdout'
            $errorOutput = Read-FixtureOutput $stderr 'shared setup stderr'
            if ($process.ExitCode -ne 0) {
                throw "Shared fixture setup exited $($process.ExitCode): $($arguments -join ' ')`nSTDOUT: $output`nSTDERR: $errorOutput"
            }
        }
        catch { $primaryError = $_; throw }
        finally {
            $cleanupError = $null
            try {
                if ($started) {
                    Stop-FixtureProcess $process
                    if ($null -ne $stdout) {
                        $output = Read-FixtureOutput $stdout 'shared setup stdout'
                        if ($null -ne $primaryError) { Write-Warning "Shared setup STDOUT: $output" -WarningAction Continue }
                    }
                    if ($null -ne $stderr) {
                        $errorOutput = Read-FixtureOutput $stderr 'shared setup stderr'
                        if ($null -ne $primaryError) { Write-Warning "Shared setup STDERR: $errorOutput" -WarningAction Continue }
                    }
                }
            }
            catch { $cleanupError = $_ }
            try { $process.Dispose() }
            catch {
                if ($null -eq $cleanupError) { $cleanupError = $_ }
                else { Write-Warning "Additional shared setup disposal failure: $_" -WarningAction Continue }
            }
            if ($null -ne $cleanupError) {
                Write-Warning "Shared setup cleanup failed; retained path ${Scratch}: $cleanupError" -WarningAction Continue
                # A cleanup-only failure fails the gate; otherwise the pending rethrow stays authoritative.
                if ($null -eq $primaryError) { throw $cleanupError }
            }
        }
    }
}
function Assert-ServerVersion([string]$Assembly, [string[]]$Arguments, [string]$Name, [string]$Version, [switch]$Shared) {
    $scratch = Join-Path ([IO.Path]::GetTempPath()) ('scrinia-contract-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory((Join-Path $scratch 'workspace')) | Out-Null
    $process = [Diagnostics.Process]::new()
    $started = $false
    $stderr = $null
    $lineTask = $null
    $setupComplete = -not $Shared
    $primaryError = $null
    $diagnostics = [Text.StringBuilder]::new()
    try {
        if ($Shared) { Initialize-SharedFixture $Assembly $scratch; $setupComplete = $true }
        $tracking = if ($Shared) { 'project-git' } else { 'local' }
        $process.StartInfo = New-FixtureStartInfo $Assembly $Arguments $scratch $tracking
        $started = $process.Start()
        # Concurrent stderr drain prevents diagnostics from blocking the stdio response.
        $stderr = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.WriteLine('{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"release-contracts","version":"1"}}}')
        $process.StandardInput.Flush()
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        $response = $null
        while ([DateTime]::UtcNow -lt $deadline) {
            $lineTask = $process.StandardOutput.ReadLineAsync()
            $remaining = [int][Math]::Max(1, ($deadline - [DateTime]::UtcNow).TotalMilliseconds)
            if (-not $lineTask.Wait($remaining)) { throw "Timed out initializing $Name" }
            $line = $lineTask.Result
            $lineTask = $null
            if ($null -eq $line) { throw "$Name exited before initialization" }
            [void]$diagnostics.AppendLine($line)
            if (-not $line.StartsWith('{', [StringComparison]::Ordinal)) { continue }
            $message = ConvertFrom-Json -InputObject $line
            if ($message.PSObject.Properties.Name -contains 'id' -and $message.id -eq 1) {
                $response = $message
                break
            }
        }
        if ($null -eq $response -or -not ($response.PSObject.Properties.Name -contains 'result')) {
            throw "Invalid initialize response from $Name"
        }
        if ($response.result.serverInfo.name -cne $Name -or $response.result.serverInfo.version -cne $Version) {
            throw "ServerInfo differs for ${Name}: actual name=$($response.result.serverInfo.name), version=$($response.result.serverInfo.version); expected version=$Version"
        }
        Write-Output "$Name ServerInfo: $Version (model-free stdio initialize)"
        if ($Name -ceq 'scri') {
            $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
            $process.StandardInput.WriteLine('{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}')
            $process.StandardInput.Flush()
            $toolResponse = $null
            $deadline = [DateTime]::UtcNow.AddSeconds(30)
            while ([DateTime]::UtcNow -lt $deadline) {
                $lineTask = $process.StandardOutput.ReadLineAsync()
                $remaining = [int][Math]::Max(1, ($deadline - [DateTime]::UtcNow).TotalMilliseconds)
                if (-not $lineTask.Wait($remaining)) { throw 'Timed out listing host tools' }
                $line = $lineTask.Result
                $lineTask = $null
                if ($null -eq $line) { throw 'Host exited before tools/list' }
                [void]$diagnostics.AppendLine($line)
                if (-not $line.StartsWith('{', [StringComparison]::Ordinal)) { continue }
                $message = ConvertFrom-Json -InputObject $line
                if ($message.PSObject.Properties.Name -contains 'id' -and $message.id -eq 2) { $toolResponse = $message; break }
            }
            $expectedTools = if ($Shared) {
                @('shared_bases','shared_search','shared_list','shared_recall','shared_facts','shared_remember',
                    'shared_forget','shared_compact','shared_link','shared_refresh')
            } else { @('guide','remember','recall','search','list','forget','compact','link','restore','facts') }
            if ($null -eq $toolResponse -or -not ($toolResponse.PSObject.Properties.Name -contains 'result')) {
                throw 'Invalid host tools/list response'
            }
            $actualTools = @($toolResponse.result.tools | ForEach-Object { $_.name })
            if ($actualTools.Count -ne $expectedTools.Count -or
                @(Compare-Object $expectedTools $actualTools -CaseSensitive).Count -ne 0) { throw 'Host tool set differs' }
            Write-Output "Host tools verified: $($actualTools -join ', ')"
        }
    }
    catch {
        $primaryError = $_
        Write-Warning "$Name contract failure: $_`nSTDOUT: $diagnostics" -WarningAction Continue
        throw
    }
    finally {
        $cleanupError = $null
        try {
            if ($started) {
                Stop-FixtureProcess $process
                # Finish an outstanding line read before starting the final stdout drain.
                if ($null -ne $lineTask) {
                    $lastLine = Read-FixtureOutput $lineTask "$Name stdout line"
                    if ($lastLine) { Write-Warning "$Name STDOUT: $lastLine" -WarningAction Continue }
                }
                $remainingOutput = Read-FixtureOutput ($process.StandardOutput.ReadToEndAsync()) "$Name stdout"
                if ($remainingOutput) { Write-Warning "$Name STDOUT: $remainingOutput" -WarningAction Continue }
                $errorOutput = Read-FixtureOutput $stderr "$Name stderr"
                if ($errorOutput) { Write-Warning "$Name STDERR: $errorOutput" -WarningAction Continue }
            }
        }
        catch { $cleanupError = $_ }
        try { $process.Dispose() }
        catch {
            if ($null -eq $cleanupError) { $cleanupError = $_ }
            else { Write-Warning "Additional fixture disposal failure: $_" -WarningAction Continue }
        }
        if (-not $setupComplete) {
            Write-Warning "Shared setup did not complete; preserving fixture at $scratch" -WarningAction Continue
        }
        elseif ($null -eq $cleanupError) {
            try {
                # Delete only this newly created absolute temp child after bounded process/output cleanup.
                $temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar)
                if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($scratch)) -cne $temp) {
                    throw "Refusing fixture cleanup outside temp directory: $scratch"
                }
                if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
            }
            catch { $cleanupError = $_ }
        }
        if ($null -ne $cleanupError) {
            Write-Warning "Fixture cleanup failed; retained path ${scratch}: $cleanupError" -WarningAction Continue
            if ($null -eq $primaryError) { throw $cleanupError }
        }
    }
}

try {
    Assert-ExactNames @(Get-ChildItem -LiteralPath (Join-Path $Root 'src/Scrinia.Mcp/skills') -File -Filter '*.md' |
        ForEach-Object { $_.BaseName }) 'disk'
    Assert-ExactNames (Get-TableNames 'README.md' '### Built-in skills') 'README table'
    Assert-ExactNames (Get-TableNames 'docs/getting-started.md' '### Built-in Skills') 'getting-started table'
    $mcpPath = Join-Path $Root "src/Scrinia.Mcp/bin/$Configuration/net10.0/Scrinia.Mcp.dll"
    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($mcpPath))
    $resources = @($assembly.GetManifestResourceNames())
    $skills = @($resources | Where-Object { $_.StartsWith('Scrinia.Mcp.skills.', [StringComparison]::Ordinal) } |
        ForEach-Object {
            if (-not $_.EndsWith('.md', [StringComparison]::Ordinal)) { throw "Unexpected skill resource $_" }
            $_.Substring('Scrinia.Mcp.skills.'.Length, $_.Length - 'Scrinia.Mcp.skills.'.Length - 3)
        })
    Assert-ExactNames $skills 'built MCP resources'
    $reader = [IO.StreamReader]::new($assembly.GetManifestResourceStream('Scrinia.Mcp.prompts.guide.md'))
    try { $guide = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $builtIns = [regex]::Matches($guide, '(?m)^Built-ins: (?<names>[^.\r\n]+)\.')
    if ($builtIns.Count -ne 1) { throw 'Expected one embedded guide built-in list' }
    Assert-ExactNames @([regex]::Matches($builtIns[0].Groups['names'].Value, '`([^`]+)`') |
        ForEach-Object { $_.Groups[1].Value }) 'built guide'

    $version = Invoke-Dotnet @('msbuild', (Join-Path $Root 'src/Scrinia/Scrinia.csproj'),
        '-nologo', '-getProperty:Version', "-p:Configuration=$Configuration")
    [xml]$props = Read-RepoText 'Directory.Build.props'
    $declaredVersions = @($props.SelectNodes('/Project/PropertyGroup/Version'))
    if ($declaredVersions.Count -ne 1 -or $declaredVersions[0].InnerText -cne $version) {
        throw 'Evaluated Version differs from canonical Directory.Build.props'
    }
    $productionFiles = @(Get-ChildItem -LiteralPath (Join-Path $Root 'src') -Recurse -File |
        Where-Object { $_.Extension -in '.cs', '.csproj' -and $_.FullName -notmatch '[/\\](bin|obj)[/\\]' })
    # Release identities must come from assembly/MSBuild metadata even when stale.
    # Protocol, schema, plugin-contract and native dependency versions are separate contracts.
    $releaseIdentityLiteral = '(?is)\b(?:ProductVersion|ReleaseVersion|ApplicationVersion|AppVersion|ScriniaVersion|ScriVersion|Release)\b\s*(?:=|=>)\s*@?"[^"\r\n]*"|\[\s*assembly\s*:\s*(?:System\.Reflection\.)?Assembly(?:File|Informational)?Version(?:Attribute)?\s*\(\s*@?"[^"\r\n]*"'
    foreach ($file in $productionFiles) {
        $text = [IO.File]::ReadAllText($file.FullName)
        if ($file.Extension -eq '.csproj') {
            [xml]$project = $text
            if (@($project.SelectNodes('//Version')).Count -ne 0) { throw "Project Version override in $($file.FullName)" }
        }
        elseif ($text -match $releaseIdentityLiteral -or
            $text -match ('"' + [regex]::Escape($version) + '(?:\+[^"\r\n]*)?"')) {
            throw "Production release-version literal in $($file.FullName)"
        }
    }
    $workflow = Read-RepoText '.github/workflows/checks.yml'
    $runs = @([regex]::Matches($workflow, '(?m)^\s+(?:- )?run: (.+)$') | ForEach-Object { $_.Groups[1].Value.Trim() })
    $expectedRuns = @('dotnet build Scrinia.sln -c Release',
        './scripts/release/Check-ReleaseContracts.ps1 -Root . -Configuration Release',
        'dotnet test Scrinia.sln -c Release --no-build',
        'dotnet build Scrinia.sln -c Release -warnaserror:CS1591')
    if ($runs.Count -ne $expectedRuns.Count -or @(Compare-Object $expectedRuns $runs -CaseSensitive).Count -ne 0 -or
        $workflow -notmatch '(?m)^  xml-doc-build:$' -or $workflow -match '(?m)^  xml-doc-coverage:$') {
        throw 'checks.yml command/job targets differ from the current solution/default-suite contract'
    }
    $cli = Join-Path $Root "src/Scrinia/bin/$Configuration/net10.0/scri.dll"
    $plugin = Invoke-Dotnet @('msbuild', (Join-Path $Root 'src/Scrinia.Plugin.Embeddings.Cli/Scrinia.Plugin.Embeddings.Cli.csproj'),
        '-nologo', '-getProperty:TargetPath', "-p:Configuration=$Configuration")
    $cliVersion = Invoke-Dotnet @($cli, '--version')
    if ($cliVersion -cne $version) {
        throw 'CLI version differs from evaluated MSBuild metadata'
    }
    Assert-ServerVersion $cli @('serve', '--workspace-root', '{workspace}', '--no-auto-setup') 'scri' $version
    Assert-ServerVersion $cli @('shared', 'serve', '--workspace-root', '{workspace}') 'scri' $version -Shared
    Assert-ServerVersion $plugin @('--data-dir', '{scratch}/data', '--models-dir', '{scratch}/models') 'scrinia-plugin-embeddings' $version
    Write-Output "Release contracts passed: exact seven skills, canonical Version $version, CLI/MCP metadata, current workflow targets."
    Write-Output 'XML check is build-only: no configured documentation emission or comprehensive API coverage policy.'
}
catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
