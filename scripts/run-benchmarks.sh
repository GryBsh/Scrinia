#!/usr/bin/env bash
# Runs scrinia-bench across all four (or selected) retrieval benchmarks.
#
# Builds the harness once and invokes each subcommand with the right --data
# path under $DATA_ROOT. Headline runs use --mode bm25_embeddings; set MODE=bm25
# for the BM25-only baseline. Set SPLIT=held_out for LongMemEval's 450q split.
#
# Usage:
#   ./scripts/run-benchmarks.sh                       # all four
#   ./scripts/run-benchmarks.sh longmemeval           # just one
#   MODE=bm25 ./scripts/run-benchmarks.sh             # BM25 baseline
#   SPLIT=held_out ./scripts/run-benchmarks.sh longmemeval
#
#   # Ablation sweep — anything after `--` goes through to scrinia-bench:
#   ./scripts/run-benchmarks.sh -- --alpha-recency 1.0 --tau-days 90
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DATA_ROOT="${DATA_ROOT:-$REPO_ROOT/datasets}"
MODE="${MODE:-bm25_embeddings}"
SPLIT="${SPLIT:-full}"
TOP_K="${TOP_K:-50}"
CONFIGURATION="${CONFIGURATION:-Release}"

if [ ! -d "$DATA_ROOT" ]; then
    echo "DATA_ROOT '$DATA_ROOT' not found. Run ./scripts/prepare-datasets.sh first."
    exit 1
fi

# Split positional args (benchmark names) from extra pass-through args (after `--`).
BENCHMARKS=()
EXTRA=()
saw_dashdash=false
for a in "$@"; do
    if [ "$saw_dashdash" = true ]; then
        EXTRA+=("$a")
    elif [ "$a" = "--" ]; then
        saw_dashdash=true
    else
        BENCHMARKS+=("$a")
    fi
done
if [ ${#BENCHMARKS[@]} -eq 0 ]; then
    BENCHMARKS=(convomem longmemeval locomo membench)
fi

BENCH_PROJECT="$REPO_ROOT/benchmarks/Scrinia.Retrieval.Benchmarks/Scrinia.Retrieval.Benchmarks.csproj"

echo "building scrinia-bench ($CONFIGURATION)..."
dotnet build "$BENCH_PROJECT" -c "$CONFIGURATION" --nologo -v quiet

invoke_bench() {
    local subcommand="$1"
    shift
    local args=("$subcommand" --mode "$MODE" --top-k "$TOP_K" "$@" "${EXTRA[@]}")
    echo ""
    echo "=== scrinia-bench ${args[*]} ==="
    dotnet run --project "$BENCH_PROJECT" -c "$CONFIGURATION" --no-build -- "${args[@]}" || \
        echo "warning: scrinia-bench $subcommand exited $?"
}

for b in "${BENCHMARKS[@]}"; do
    case "$b" in
        convomem)
            data="$DATA_ROOT/ConvoMem/core_benchmark/evidence_questions"
            if [ ! -d "$data" ]; then echo "[convomem] data not found at $data — run prepare-datasets.sh"; continue; fi
            invoke_bench convomem --data "$data"
            ;;
        longmemeval)
            data="$DATA_ROOT/longmemeval/longmemeval_s_cleaned.json"
            if [ ! -f "$data" ]; then echo "[longmemeval] data not found at $data — run prepare-datasets.sh"; continue; fi
            invoke_bench longmemeval --data "$data" --split "$SPLIT"
            ;;
        membench)
            data="$DATA_ROOT/membench/MemData/FirstAgent"
            if [ ! -d "$data" ]; then echo "[membench] data not found at $data — run prepare-datasets.sh"; continue; fi
            invoke_bench membench --data "$data"
            ;;
        locomo)
            data="$DATA_ROOT/locomo/data/locomo10.json"
            if [ ! -f "$data" ]; then echo "[locomo] data not found at $data — run prepare-datasets.sh"; continue; fi
            invoke_bench locomo --data "$data"
            ;;
        *)
            echo "unknown benchmark: $b (expected: convomem|longmemeval|locomo|membench)"
            exit 1
            ;;
    esac
done

echo ""
echo "Results written to benchmarks/Scrinia.Retrieval.Benchmarks/results/"
