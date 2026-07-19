#requires -version 7
# Rank LoCoMo runs by judge metric. -Replay first re-judges runs that lack judge data (over persisted context).
param(
    [string]$ResultsDir = "benchmarks/Scrinia.Retrieval.Benchmarks/results",
    [switch]$Replay,
    [string]$ReplayGlob = "scrinia_locomo_bm25_embeddings__chunked_*.json"
)
$proj = "benchmarks/Scrinia.Retrieval.Benchmarks"
if ($Replay) {
    Get-ChildItem -Path $ResultsDir -Filter $ReplayGlob |
        Where-Object { $_.Name -notlike "*.judged.json" } |
        ForEach-Object { dotnet run --project $proj -c Release -- judge-replay --input $_.FullName --judge }
}
dotnet run --project $proj -c Release -- rank --dir $ResultsDir
