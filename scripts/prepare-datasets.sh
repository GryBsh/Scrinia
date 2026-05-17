#!/usr/bin/env bash
# Downloads the four retrieval-quality benchmark datasets used by scrinia-bench.
#
# Idempotent: only downloads what isn't already present. Targets:
#   - ConvoMem     (HuggingFace, git clone of Salesforce/ConvoMem)
#   - LongMemEval  (HuggingFace, single JSON file)
#   - LoCoMo       (GitHub, git clone of snap-research/locomo)
#   - MemBench     (GitHub, git clone of import-myself/Membench)
#
# Also fetches MemPalace's lme_split_50_450.json so `scrinia-bench longmemeval
# --split held_out` works out of the box.
#
# Usage:
#   ./scripts/prepare-datasets.sh                    # all four
#   ./scripts/prepare-datasets.sh longmemeval        # just one
#   DATA_ROOT=/tmp/scrinia-data ./scripts/prepare-datasets.sh
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DATA_ROOT="${DATA_ROOT:-$REPO_ROOT/datasets}"
mkdir -p "$DATA_ROOT"

# Argument list = benchmark allowlist. No args = all four.
if [ $# -eq 0 ]; then
    set -- convomem longmemeval locomo membench
fi

command -v git >/dev/null 2>&1 || { echo "git not found in PATH (needed for ConvoMem/LoCoMo/MemBench)"; exit 1; }
command -v curl >/dev/null 2>&1 || command -v wget >/dev/null 2>&1 || { echo "neither curl nor wget found in PATH"; exit 1; }

# Cross-tool fetch — uses curl if available, falls back to wget.
fetch() {
    local url="$1" out="$2"
    if command -v curl >/dev/null 2>&1; then
        curl -fsSL -o "$out" "$url"
    else
        wget -q -O "$out" "$url"
    fi
}

get_convomem() {
    local target="$DATA_ROOT/ConvoMem"
    if [ -d "$target/core_benchmark" ]; then
        echo "[convomem] already present at $target — skipping"
        return
    fi
    echo "[convomem] cloning Salesforce/ConvoMem from HuggingFace..."
    git clone --depth 1 "https://huggingface.co/datasets/Salesforce/ConvoMem" "$target"
    echo "[convomem] done. Point --data at $target/core_benchmark/evidence_questions"
}

get_longmemeval() {
    local target="$DATA_ROOT/longmemeval"
    mkdir -p "$target"
    local data_file="$target/longmemeval_s_cleaned.json"
    if [ -f "$data_file" ]; then
        echo "[longmemeval] $data_file already present — skipping data download"
    else
        echo "[longmemeval] downloading longmemeval_s_cleaned.json..."
        fetch "https://huggingface.co/datasets/xiaowu0162/longmemeval-cleaned/resolve/main/longmemeval_s_cleaned.json" "$data_file"
    fi
    local split_file="$target/lme_split_50_450.json"
    if [ -f "$split_file" ]; then
        echo "[longmemeval] $split_file already present — skipping split download"
    else
        echo "[longmemeval] downloading MemPalace's lme_split_50_450.json..."
        fetch "https://raw.githubusercontent.com/MemPalace/mempalace/main/benchmarks/lme_split_50_450.json" "$split_file"
    fi
}

get_locomo() {
    local target="$DATA_ROOT/locomo"
    if [ -f "$target/data/locomo10.json" ]; then
        echo "[locomo] already present at $target — skipping"
        return
    fi
    echo "[locomo] cloning snap-research/locomo..."
    git clone --depth 1 "https://github.com/snap-research/locomo" "$target"
    echo "[locomo] done. Point --data at $target/data/locomo10.json"
}

get_membench() {
    local target="$DATA_ROOT/membench"
    if [ -d "$target/MemData" ]; then
        echo "[membench] already present at $target — skipping"
        return
    fi
    echo "[membench] cloning import-myself/Membench..."
    git clone --depth 1 "https://github.com/import-myself/Membench" "$target"
    echo "[membench] done. Point --data at $target/MemData/FirstAgent"
}

for b in "$@"; do
    case "$b" in
        convomem)    get_convomem ;;
        longmemeval) get_longmemeval ;;
        locomo)      get_locomo ;;
        membench)    get_membench ;;
        *) echo "unknown benchmark: $b (expected: convomem|longmemeval|locomo|membench)"; exit 1 ;;
    esac
done

echo ""
echo "All requested datasets present under $DATA_ROOT"
echo "Next: ./scripts/run-benchmarks.sh"
