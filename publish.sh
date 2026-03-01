#!/usr/bin/env bash
set -euo pipefail

WITH_EMBEDDINGS=false
OUTPUT_DIR=""
PLATFORM=""
ALL_RIDS=("win-x64" "linux-x64" "osx-arm64")

# Parse arguments
for arg in "$@"; do
    case "$arg" in
        --with-embeddings) WITH_EMBEDDINGS=true ;;
        --platform=*) PLATFORM="${arg#--platform=}" ;;
        -*) echo "Unknown flag: $arg" >&2; exit 1 ;;
        *) OUTPUT_DIR="$arg" ;;
    esac
done

if [ -z "$OUTPUT_DIR" ]; then
    echo "Usage: ./publish.sh <output-folder> [--platform=win-x64|linux-x64|osx-arm64] [--with-embeddings]" >&2
    exit 1
fi

# Validate platform if specified
if [ -n "$PLATFORM" ]; then
    valid=false
    for r in "${ALL_RIDS[@]}"; do
        if [ "$r" = "$PLATFORM" ]; then valid=true; break; fi
    done
    if [ "$valid" = false ]; then
        echo "Error: invalid platform '$PLATFORM'. Must be one of: ${ALL_RIDS[*]}" >&2
        exit 1
    fi
    RIDS=("$PLATFORM")
    SINGLE_PLATFORM=true
else
    RIDS=("${ALL_RIDS[@]}")
    SINGLE_PLATFORM=false
fi

PROJECT="src/Scrinia/Scrinia.csproj"
EMBEDDINGS_PROJECT="src/Scrinia.Plugin.Embeddings.Cli/Scrinia.Plugin.Embeddings.Cli.csproj"

mkdir -p "$OUTPUT_DIR"

for rid in "${RIDS[@]}"; do
    if [ "$SINGLE_PLATFORM" = true ]; then
        rid_dir="$OUTPUT_DIR"
    else
        rid_dir="$OUTPUT_DIR/$rid"
    fi

    echo "Publishing $rid ..."
    dotnet publish "$PROJECT" \
        --runtime "$rid" \
        --self-contained \
        --configuration Release \
        -p:PublishSingleFile=true \
        -p:PublishTrimmed=true \
        --output "$rid_dir"
    echo "  -> $rid_dir"

    if [ "$WITH_EMBEDDINGS" = true ]; then
        echo "  Publishing embeddings plugin for $rid ..."
        plugins_dir="$rid_dir/plugins"
        # Single-file + self-contained + native bundling is configured in the .csproj.
        # Only --runtime is needed here for RID selection.
        dotnet publish "$EMBEDDINGS_PROJECT" \
            --runtime "$rid" \
            --configuration Release \
            --output "$plugins_dir"

        # Clean stray build artifacts that don't go into the single-file bundle
        if [ "$rid" = "win-x64" ] || [[ "$rid" == win-* ]]; then
            ext=".exe"
        else
            ext=""
        fi
        find "$plugins_dir" -maxdepth 1 ! -name "scri-plugin-*" ! -name "." -exec rm -rf {} +
        echo "  -> $plugins_dir"
    fi
done

echo ""
echo "Done. Builds:"
for rid in "${RIDS[@]}"; do
    if [ "$SINGLE_PLATFORM" = true ]; then
        rid_dir="$OUTPUT_DIR"
    else
        rid_dir="$OUTPUT_DIR/$rid"
    fi
    ls -lh "$rid_dir"/scri* 2>/dev/null || true
    if [ "$WITH_EMBEDDINGS" = true ]; then
        ls -lh "$rid_dir"/plugins/scri-plugin-embeddings* 2>/dev/null || true
    fi
done
