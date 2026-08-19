#!/usr/bin/env bash
set -euo pipefail

WITH_EMBEDDINGS=false
OUTPUT_DIR=""
PLATFORM=""
ALL_RIDS=("win-x64" "linux-x64" "osx-arm64")
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MANIFEST="$SCRIPT_DIR/publish.projects.tsv"
# Kept in step with the LLamaSharp.Backend.Vulkan PackageReference in the
# Scrinia.Plugin.Embeddings project file by a test — a drift here makes the overwrite path
# below not exist, which used to be a silent skip.
VULKAN_BACKEND_VERSION="0.25.0"

# Projects marked rids=host in the manifest are published only for this machine's own RID.
# Scrinia.Merge is NativeAOT, and cross-OS native compilation is unsupported: a foreign-RID
# publish exits 1 and writes nothing, which under `set -e` would abort this whole script.
case "$(uname -s)" in
    Linux*)               HOST_RID="linux-x64" ;;
    Darwin*)              if [ "$(uname -m)" = "arm64" ]; then HOST_RID="osx-arm64"; else HOST_RID="osx-x64"; fi ;;
    MINGW*|MSYS*|CYGWIN*) HOST_RID="win-x64" ;;
    *)                    HOST_RID="" ;;
esac

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

# The manifest is the one source of truth for what ships. publish.ps1 reads the same file.
# The two lists used to be maintained by hand and had already diverged: scri-merge — the
# .nmp2 merge driver — shipped only from PowerShell, so a Linux or macOS build produced a
# tree whose configured merge driver pointed at a binary that did not exist.
if [ ! -f "$MANIFEST" ]; then
    echo "Error: manifest not found at $MANIFEST" >&2
    exit 1
fi

IDS=(); PROJECTS=(); CONDITIONS=(); PROFILES=(); OUTPUTS=(); RIDSCOPES=()
# The `|| [ -n "$m_id" ]` keeps the last row when the file has no trailing newline: `read`
# returns non-zero at EOF-without-newline having already filled the variables, so the plain
# form would silently drop it.
while IFS=$'\t' read -r m_id m_project m_condition m_profile m_output m_rids || [ -n "$m_id" ]; do
    # Strip a trailing CR: .gitattributes pins this file to LF, but a user whose git
    # configuration ignores it would otherwise get a corrupted final column.
    m_rids="${m_rids%$'\r'}"
    if [ -z "$m_id" ]; then continue; fi
    case "$m_id" in \#*) continue ;; esac
    IDS+=("$m_id"); PROJECTS+=("$m_project"); CONDITIONS+=("$m_condition")
    PROFILES+=("$m_profile"); OUTPUTS+=("$m_output"); RIDSCOPES+=("$m_rids")
done < <(tail -n +2 "$MANIFEST")

if [ "${#IDS[@]}" -eq 0 ]; then
    echo "Error: manifest $MANIFEST lists no projects" >&2
    exit 1
fi

# Overwrites the published native DLLs with the Vulkan-compiled variants. Single-file publish
# flattens all native DLLs to the root, and the CPU backend's (ggml-base, ggml, llama, mtmd)
# overwrite the Vulkan ones since they share filenames — so LLamaSharp would load CPU.
overwrite_vulkan_natives() {
    local rid="$1" dest="$2" pkg src
    case "$rid" in
        win-*)   pkg="llamasharp.backend.vulkan.windows" ;;
        linux-*) pkg="llamasharp.backend.vulkan.linux" ;;
        *)
            echo "  [warn] no Vulkan backend package exists for $rid; shipping the CPU backend." >&2
            return 0 ;;
    esac
    src="$HOME/.nuget/packages/$pkg/$VULKAN_BACKEND_VERSION/runtimes/$rid/native/vulkan"
    if [ -d "$src" ]; then
        cp -f "$src"/* "$dest"/
        echo "    Overwrote root native DLLs with Vulkan variants"
    else
        echo "  [warn] Vulkan natives not found at $src; shipping the CPU backend." >&2
    fi
}

mkdir -p "$OUTPUT_DIR"

for rid in "${RIDS[@]}"; do
    if [ "$SINGLE_PLATFORM" = true ]; then
        rid_dir="$OUTPUT_DIR"
    else
        rid_dir="$OUTPUT_DIR/$rid"
    fi

    for i in "${!IDS[@]}"; do
        if [ "${CONDITIONS[$i]}" = "with-embeddings" ] && [ "$WITH_EMBEDDINGS" != true ]; then
            continue
        fi

        if [ "${RIDSCOPES[$i]}" = "host" ] && [ "$rid" != "$HOST_RID" ]; then
            echo "  [skip] ${IDS[$i]} for $rid: it is NativeAOT and cross-OS native compilation" >&2
            echo "         is unsupported. Build on $rid, or use --platform=$HOST_RID." >&2
            continue
        fi

        case "${OUTPUTS[$i]}" in
            rid)    out_dir="$rid_dir" ;;
            plugin) out_dir="$rid_dir/plugins/${IDS[$i]}" ;;
            *)
                echo "Error: unknown output kind '${OUTPUTS[$i]}' for ${IDS[$i]}" >&2
                exit 1 ;;
        esac

        echo "Publishing ${IDS[$i]} for $rid ..."
        case "${PROFILES[$i]}" in
            trimmed)
                # Trim-analysis warnings are gated as errors so a regression silently dropping a
                # member via reflection cannot ship. If a trim warning fires, fix the root cause —
                # either move to a JsonSerializerContext, annotate with [DynamicallyAccessedMembers],
                # or rewrite the reflection-heavy call to a trim-safe shape.
                dotnet publish "${PROJECTS[$i]}" \
                    --runtime "$rid" \
                    --self-contained \
                    --configuration Release \
                    -p:PublishSingleFile=true \
                    -p:PublishTrimmed=true \
                    -p:SuppressTrimAnalysisWarnings=false \
                    -p:TreatWarningsAsErrors=true \
                    --output "$out_dir"
                ;;
            plain)
                # Single-file + self-contained + native bundling is configured in the project
                # file. Only --runtime is needed here for RID selection.
                dotnet publish "${PROJECTS[$i]}" \
                    --runtime "$rid" \
                    --configuration Release \
                    --output "$out_dir"
                ;;
            *)
                echo "Error: unknown profile '${PROFILES[$i]}' for ${IDS[$i]}" >&2
                exit 1 ;;
        esac

        if [ "${OUTPUTS[$i]}" = "plugin" ]; then
            overwrite_vulkan_natives "$rid" "$out_dir"
        fi
        echo "  -> $out_dir"
    done
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
        ls -lh "$rid_dir"/plugins/scri-plugin-embeddings/scri-plugin-embeddings* 2>/dev/null || true
    fi
done
