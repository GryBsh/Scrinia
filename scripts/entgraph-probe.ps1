<#
.SYNOPSIS
    Entity-graph (PPR) recall probe: baseline vs +entity-graph on the MemBench
    multi-hop categories, with a per-category decision table.

.DESCRIPTION
    For each multi-hop category, runs two scrinia-bench cells that are identical except
    for the entity-graph signal:

        baseline : --agent typed --rerank none
        treatment: --agent typed --rerank none --entity-graph on

    Both carry the typed-keyword TF seeding, so the only difference is the PPR
    supplemental score — the diff isolates the entity graph's recall contribution.

    Runs at --top-k 200 so each result JSON captures gold ranks well past the top of the
    pool, letting the analysis report how far gold lifted. Then loads each JSON pair and
    prints, per category, the within-pool ranking effect.

    METRIC NOTE: MemBench per-question haystacks are tiny (~5–30 turns), so R@50 is
    saturated (≈100%) and useless here — the gold is always in the pool. The discriminating
    axis is *within-pool rank*: R@5 / R@10 / NDCG and the gold-rank lift. The table leads
    with those; R@50 is shown only to confirm saturation.

    SECURITY: the agent API key is read from $env:LLMACTL_KEY and never printed or written.

.PARAMETER AnalyzeOnly
    Skip building and running cells; just re-analyze the result JSONs already in the
    output dir. Read-only, no LLM calls.

.PARAMETER DataRoot
    Where prepare-datasets.ps1 dropped the datasets. Default: <repo>/datasets.

.PARAMETER AgentBaseUrl
    OpenAI-compatible base URL for the entity/keyword-extraction agent.

.PARAMETER AgentModel
    Agent model slug.

.PARAMETER Provider
    Agent provider id (openai-compatible).

.PARAMETER TopK
    Retrieval depth. Default 200 (captures gold ranks well past the R@10 cutoff).

.PARAMETER PerCatLimit
    Optional per-category question cap for a fast smoke run. Omit for the full categories.

.PARAMETER Categories
    Multi-hop categories to probe. Default: RecMultiSession, aggregative, comparative.

.EXAMPLE
    $env:LLMACTL_KEY = '...'; ./scripts/entgraph-probe.ps1
    Full probe across the three multi-hop categories.

.EXAMPLE
    ./scripts/entgraph-probe.ps1 -AnalyzeOnly
    Re-print the decision table from existing result JSONs without rerunning anything.
#>
[CmdletBinding()]
param(
    [switch]$AnalyzeOnly,
    [string]$DataRoot = $null,
    [string]$AgentBaseUrl = "http://localhost:8080/v1",
    [string]$AgentModel = "qwen3",
    [string]$Provider = "openai",
    [int]$TopK = 200,
    [int]$PerCatLimit = 0,
    [string[]]$Categories = @("RecMultiSession", "aggregative", "comparative"),
    [string]$Configuration = "Release",
    [int]$IngestConcurrency = $null,
    [switch]$Rerank
)

$ErrorActionPreference = "Stop"
# Don't let a cell's stderr/non-zero exit throw — we check $LASTEXITCODE per cell and warn.
$PSNativeCommandUseErrorActionPreference = $false

$RepoRoot = Split-Path $PSScriptRoot -Parent
$BenchProject = Join-Path $RepoRoot "benchmarks\Scrinia.Retrieval.Benchmarks\Scrinia.Retrieval.Benchmarks.csproj"
$outDir = Join-Path $RepoRoot "benchmarks\Scrinia.Retrieval.Benchmarks\results\entgraph-probe"
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

if (-not $AnalyzeOnly) {
    if (-not $env:LLMACTL_KEY) {
        Write-Error "LLMACTL_KEY env var is not set. The agent extraction needs it. Set it with: `$env:LLMACTL_KEY = '<key>'"
        exit 1
    }
    if (-not $DataRoot) { $DataRoot = Join-Path $RepoRoot "datasets" }
    $data = Join-Path $DataRoot "membench\MemData\FirstAgent"
    if (-not (Test-Path $data)) {
        Write-Error "membench data not found at $data — run ./scripts/prepare-datasets.ps1 first."
        exit 1
    }

    Write-Host "building scrinia-bench ($Configuration)..."
    dotnet build $BenchProject -c $Configuration --nologo -v quiet
    if ($LASTEXITCODE -ne 0) { Write-Error "build failed"; exit 1 }
}

# One cell. Returns the output JSON path. The API key flows from the env var into the
# process args; we echo a redacted command so the key never lands in logs/scrollback.
function Invoke-Cell {
    param([string]$Category, [bool]$EntityGraph)

    $arm = if ($EntityGraph) { "entgraph" } else { "baseline" }
    $out = Join-Path $outDir "$Category-$arm.json"

    $benchArgs = @(
        "membench", "--data", $data, "--mode", "bm25_embeddings", "--top-k", $TopK,
        "--category", $Category, 
        "--agent", "typed", "--agent-provider", $Provider,
        "--agent-base-url", $AgentBaseUrl, "--agent-model", $AgentModel,
        "--agent-api-key", $env:LLMACTL_KEY,
        "--output", $out
    )
    if ($PerCatLimit -gt 0) { $benchArgs += @("--limit", $PerCatLimit) }
    if ($EntityGraph) { $benchArgs += @("--entity-graph", "on") }
    if ($IngestConcurrency) { $benchArgs += @("--ingest-concurrency", $IngestConcurrency) }
    if ($Rerank) {
        $benchArgs += @()
    } else {
        $benchArgs += @("--rerank", "none")
    }

    $redacted = ($benchArgs | ForEach-Object { if ($_ -eq $env:LLMACTL_KEY) { "***" } else { $_ } }) -join ' '
    Write-Host "  [$Category/$arm] dotnet run -- $redacted"

    # Route the bench's own stdout/stderr to the host so it doesn't pollute this
    # function's return value (the only thing returned must be $out).
    dotnet run --project $BenchProject -c $Configuration --no-build -- @benchArgs 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { Write-Warning "cell [$Category/$arm] exited $LASTEXITCODE" }
    return $out
}

# 1-based rank of the first retrieved hit whose Id is a gold id; $null if none in window.
function Get-GoldRank {
    param($Retrieved, [System.Collections.Generic.HashSet[string]]$Gold)
    for ($i = 0; $i -lt $Retrieved.Count; $i++) {
        if ($Gold.Contains([string]$Retrieved[$i].Id)) { return $i + 1 }
    }
    return $null
}

function Get-Mean { param([double[]]$Values) if ($Values.Count -eq 0) { return $null } ($Values | Measure-Object -Average).Average }

if (-not $AnalyzeOnly) {
    Write-Host ""
    Write-Host "=== running cells ($($Categories.Count) categories x {baseline, entgraph}) ==="
    foreach ($cat in $Categories) {
        [void](Invoke-Cell -Category $cat -EntityGraph $false)
        [void](Invoke-Cell -Category $cat -EntityGraph $true)
    }
}

# ── Analysis ────────────────────────────────────────────────────────────────
$rows = @()
foreach ($cat in $Categories) {
    $basePath = Join-Path $outDir "$cat-baseline.json"
    $egPath   = Join-Path $outDir "$cat-entgraph.json"
    if (-not (Test-Path $basePath) -or -not (Test-Path $egPath)) {
        Write-Warning "missing result JSON for '$cat' (looked for $basePath / $egPath) — skipping."
        continue
    }

    $base = (Get-Content $basePath -Raw | ConvertFrom-Json)
    $eg   = (Get-Content $egPath   -Raw | ConvertFrom-Json)

    $egById = @{}
    foreach ($q in $eg.Questions) { $egById[[string]$q.QuestionId] = $q }

    $n = 0
    $bR5 = 0.0; $eR5 = 0.0; $bR10 = 0.0; $eR10 = 0.0; $bR50 = 0.0; $eR50 = 0.0
    $bN = 0.0; $eN = 0.0
    $rec10 = 0; $reg10 = 0; $rec5 = 0; $reg5 = 0
    $lifts = New-Object System.Collections.Generic.List[double]
    $improved = 0; $worsened = 0

    foreach ($bq in $base.Questions) {
        $tq = $egById[[string]$bq.QuestionId]
        if (-not $tq) { continue }
        $n++
        $bR5 += [double]$bq.RecallAt5;  $eR5 += [double]$tq.RecallAt5
        $bR10 += [double]$bq.RecallAt10; $eR10 += [double]$tq.RecallAt10
        $bR50 += [double]$bq.RecallAt50; $eR50 += [double]$tq.RecallAt50
        $bN += [double]$bq.Ndcg10; $eN += [double]$tq.Ndcg10
        if ([double]$bq.RecallAt10 -eq 0 -and [double]$tq.RecallAt10 -eq 1) { $rec10++ }
        if ([double]$bq.RecallAt10 -eq 1 -and [double]$tq.RecallAt10 -eq 0) { $reg10++ }
        if ([double]$bq.RecallAt5  -eq 0 -and [double]$tq.RecallAt5  -eq 1) { $rec5++ }
        if ([double]$bq.RecallAt5  -eq 1 -and [double]$tq.RecallAt5  -eq 0) { $reg5++ }

        $gold = [System.Collections.Generic.HashSet[string]]::new([string[]]$bq.GroundTruthIds, [System.StringComparer]::OrdinalIgnoreCase)
        $rb = Get-GoldRank -Retrieved $bq.Retrieved -Gold $gold
        $rt = Get-GoldRank -Retrieved $tq.Retrieved -Gold $gold
        if ($null -ne $rb -and $null -ne $rt) {
            $lift = [double]($rb - $rt)   # +ve = gold moved up
            $lifts.Add($lift)
            if ($lift -gt 0) { $improved++ } elseif ($lift -lt 0) { $worsened++ }
        }
    }

    if ($n -eq 0) { continue }
    $rows += [pscustomobject]@{
        Category = $cat; N = $n
        BaseR5 = $bR5/$n; EgR5 = $eR5/$n; BaseR10 = $bR10/$n; EgR10 = $eR10/$n
        BaseR50 = $bR50/$n; EgR50 = $eR50/$n; BaseN = $bN/$n; EgN = $eN/$n
        Rec10 = $rec10; Reg10 = $reg10; Rec5 = $rec5; Reg5 = $reg5
        MeanLift = Get-Mean -Values $lifts.ToArray(); Improved = $improved; Worsened = $worsened
    }
}

Write-Host ""
Write-Host "=== entity-graph recall probe (within-pool rank; R@50 saturated on MemBench) ==="
foreach ($r in $rows) {
    $lift = if ($null -eq $r.MeanLift) { "n/a" } else { "{0:+0.00;-0.00;0}" -f $r.MeanLift }
    Write-Host ""
    Write-Host ("{0} (n={1})" -f $r.Category, $r.N)
    Write-Host ("  R@5    {0,6:P1} -> {1,6:P1}  ({2,6:P1})   flip 5: +{3}/-{4}" -f $r.BaseR5, $r.EgR5, ($r.EgR5 - $r.BaseR5), $r.Rec5, $r.Reg5)
    Write-Host ("  R@10   {0,6:P1} -> {1,6:P1}  ({2,6:P1})   flip 10: +{3}/-{4}" -f $r.BaseR10, $r.EgR10, ($r.EgR10 - $r.BaseR10), $r.Rec10, $r.Reg10)
    Write-Host ("  NDCG   {0,6:F3} -> {1,6:F3}  ({2,6:F3})" -f $r.BaseN, $r.EgN, ($r.EgN - $r.BaseN))
    Write-Host ("  rank   meanLift={0}  (improved {1} / worsened {2})   [R@50 {3:P0}->{4:P0}, saturated]" -f $lift, $r.Improved, $r.Worsened, $r.BaseR50, $r.EgR50)
}

$tRec = ($rows | Measure-Object -Property Rec10 -Sum).Sum
$tReg = ($rows | Measure-Object -Property Reg10 -Sum).Sum
Write-Host ""
Write-Host ("totals (R@10 flips): recovered={0}  regressed={1}  net={2:+0;-0;0}" -f $tRec, $tReg, ($tRec - $tReg))
Write-Host "read: look for R@5/R@10/NDCG lift concentrated on the low-precision categories (weak pool),"
Write-Host "      with few/no threshold regressions. Saturated/high-precision categories should stay flat."
Write-Host "results: $outDir"
