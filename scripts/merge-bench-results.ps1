<#
.SYNOPSIS
    Merges partial benchmark result JSONs (from parallel category fanout) into one
    canonical result file with recomputed aggregates and per-category metrics.

.DESCRIPTION
    Concatenates the `Questions` array from each partial, then recomputes `Aggregate`
    (mean recall, NDCG) and `PerCategory` (grouped by Category) so the merged file
    is indistinguishable from a single sequential run's output. RunMetadata is
    inherited from the first partial with QuestionCount, StartedAt, and CompletedAt
    updated to span the full set.

    Missing input files are warned-about and skipped — failed subprocesses don't
    abort the merge. If ALL inputs are missing, exits 1.

.PARAMETER Inputs
    Paths to the partial result JSONs. Order doesn't matter.

.PARAMETER Output
    Path for the merged result JSON.

.EXAMPLE
    ./scripts/merge-bench-results.ps1 -Inputs $partials -Output ./results/merged.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Inputs,
    [Parameter(Mandatory)][string]$Output
)

$ErrorActionPreference = "Stop"

# 1. Load partials, skipping any that don't exist.
$parts = @()
foreach ($p in $Inputs) {
    if (-not (Test-Path $p)) {
        Write-Warning "merge: missing partial $p (subprocess probably failed) — skipping"
        continue
    }
    $parts += (Get-Content $p -Raw | ConvertFrom-Json)
}
if ($parts.Count -eq 0) {
    Write-Error "no partials loaded — all inputs missing or unreadable"
    exit 1
}

# 2. Concatenate all per-question results. PSCustomObject access is case-insensitive
#    so the lowercase reads here work against the bench's PascalCase output.
$allQuestions = @()
foreach ($part in $parts) {
    if ($null -ne $part.Questions) {
        $allQuestions += $part.Questions
    }
}
$n = $allQuestions.Count
if ($n -eq 0) {
    Write-Error "merge: no questions across $($parts.Count) partials"
    exit 1
}

# 3. Recompute aggregate metrics. Match BenchmarkScorer.Aggregate semantics:
#    R@K is mean(HitAtK), NDCG@10 is mean(Ndcg10), QuestionCount is N.
$r5 = ($allQuestions | Where-Object { $_.HitAt5 } | Measure-Object).Count / $n
$r10 = ($allQuestions | Where-Object { $_.HitAt10 } | Measure-Object).Count / $n
$r50 = ($allQuestions | Where-Object { $_.HitAt50 } | Measure-Object).Count / $n
$ndcg = ($allQuestions | Measure-Object -Property Ndcg10 -Average).Average

# Emit PascalCase to match the bench's System.Text.Json output so downstream tooling
# sees a consistent shape whether the run was sequential or parallel-merged.
$aggregate = [pscustomobject]@{
    QuestionCount = $n
    RecallAt5     = $r5
    RecallAt10    = $r10
    RecallAt50    = $r50
    Ndcg10        = $ndcg
}

# 4. Recompute per-category. Group by Category, then aggregate inside each group.
$perCategory = [ordered]@{}
$grouped = $allQuestions | Group-Object -Property Category
foreach ($g in $grouped) {
    $cat = if ([string]::IsNullOrEmpty($g.Name)) { "uncategorized" } else { $g.Name }
    $cn = $g.Count
    $perCategory[$cat] = [pscustomobject]@{
        QuestionCount = $cn
        RecallAt5     = ($g.Group | Where-Object { $_.HitAt5 } | Measure-Object).Count / $cn
        RecallAt10    = ($g.Group | Where-Object { $_.HitAt10 } | Measure-Object).Count / $cn
        RecallAt50    = ($g.Group | Where-Object { $_.HitAt50 } | Measure-Object).Count / $cn
        Ndcg10        = ($g.Group | Measure-Object -Property Ndcg10 -Average).Average
    }
}

# 5. Build merged RunMetadata. Take Benchmark/Mode/TopK/ScriniaVersion from the
#    first partial; span StartedAt/CompletedAt across the full set; updated count.
$first = $parts[0]
$startedAt = ($parts | ForEach-Object { [datetime]$_.RunMetadata.StartedAt } | Sort-Object | Select-Object -First 1)
$completedAt = ($parts | ForEach-Object { [datetime]$_.RunMetadata.CompletedAt } | Sort-Object -Descending | Select-Object -First 1)

# Tier 2 enrichment fields: inherit identity from the first partial (every partial in
# a parallel sweep shares the same provider/model/ops). Sum the cache counters across
# partials so the merged file reflects the full run's cache effectiveness.
$enrichProvider = $first.RunMetadata.EnrichmentProvider
$enrichModel    = $first.RunMetadata.EnrichmentModel
$enrichOps      = $first.RunMetadata.EnrichmentOps
$enrichHits     = $null
$enrichMisses   = $null
$enrichInflight = $null
if ($null -ne $enrichProvider) {
    $enrichHits     = ($parts | ForEach-Object { [int]$_.RunMetadata.EnrichmentCacheHits     } | Measure-Object -Sum).Sum
    $enrichMisses   = ($parts | ForEach-Object { [int]$_.RunMetadata.EnrichmentCacheMisses   } | Measure-Object -Sum).Sum
    $enrichInflight = ($parts | ForEach-Object { [int]$_.RunMetadata.EnrichmentInflightHits } | Measure-Object -Sum).Sum
}

$merged = [pscustomobject]@{
    RunMetadata = [pscustomobject]@{
        Benchmark              = $first.RunMetadata.Benchmark
        Mode                   = $first.RunMetadata.Mode
        TopK                   = $first.RunMetadata.TopK
        StartedAt              = $startedAt.ToString("o")
        CompletedAt            = $completedAt.ToString("o")
        QuestionCount          = $n
        ScriniaVersion         = $first.RunMetadata.ScriniaVersion
        EnrichmentProvider      = $enrichProvider
        EnrichmentModel         = $enrichModel
        EnrichmentOps           = $enrichOps
        EnrichmentCacheHits     = $enrichHits
        EnrichmentCacheMisses   = $enrichMisses
        EnrichmentInflightHits  = $enrichInflight
    }
    Aggregate   = $aggregate
    PerCategory = $perCategory
    Questions   = $allQuestions
}

# 6. Write merged JSON. Deep nesting (questions[].retrieved[]) so push -Depth.
$outDir = Split-Path $Output -Parent
if ($outDir -and -not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Path $outDir -Force | Out-Null
}
$merged | ConvertTo-Json -Depth 32 | Set-Content -Path $Output -Encoding UTF8

# 7. Print a summary so the orchestrator's user gets the headline without opening the JSON.
Write-Host ""
Write-Host "=== merged: $($first.runMetadata.benchmark) / $($first.runMetadata.mode) ==="
Write-Host "questions: $n  (from $($parts.Count) partials)"
Write-Host ("R@5:   {0:P1}" -f $r5)
Write-Host ("R@10:  {0:P1}" -f $r10)
Write-Host ("R@50:  {0:P1}" -f $r50)
Write-Host ("NDCG@10: {0:F3}" -f $ndcg)
