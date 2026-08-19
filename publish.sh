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
# OS and architecture are resolved separately and joined: a Linux or Windows arm64 host must
# not be mistaken for its x64 sibling, the same way the Darwin branch already distinguishes them.
case "$(uname -s)" in
    Linux*)               HOST_OS="linux" ;;
    Darwin*)              HOST_OS="osx" ;;
    MINGW*|MSYS*|CYGWIN*) HOST_OS="win" ;;
    *)                    HOST_OS="" ;;
esac
case "$(uname -m)" in
    x86_64|amd64)  HOST_ARCH="x64" ;;
    arm64|aarch64) HOST_ARCH="arm64" ;;
    *)             HOST_ARCH="" ;;
esac
if [ -n "$HOST_OS" ] && [ -n "$HOST_ARCH" ]; then HOST_RID="$HOST_OS-$HOST_ARCH"; else HOST_RID=""; fi

# Whether the host's own RID is one this script can actually publish for (and so a safe thing
# to suggest in the [skip] message below). osx-x64 and an unrecognised host are examples of a
# real, buildable machine whose RID is not in ALL_RIDS.
HOST_RID_SUPPORTED=false
for r in "${ALL_RIDS[@]}"; do
    if [ "$r" = "$HOST_RID" ]; then HOST_RID_SUPPORTED=true; break; fi
done

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
# A seventh field name (m_extra) catches a row with more than six tab-separated columns: `read`
# with only six names would otherwise silently fold an extra column onto the end of m_rids.
while IFS=$'\t' read -r m_id m_project m_condition m_profile m_output m_rids m_extra || [ -n "$m_id" ]; do
    # Strip a trailing CR: .gitattributes pins this file to LF, but a user whose git
    # configuration ignores it would otherwise get a corrupted final column.
    m_rids="${m_rids%$'\r'}"
    m_extra="${m_extra%$'\r'}"
    if [ -z "$m_id" ]; then continue; fi
    case "$m_id" in \#*) continue ;; esac
    if [ -n "$m_extra" ]; then
        echo "Error: manifest row for '$m_id' has more than six columns" >&2
        exit 1
    fi
    # Validated here, once, at read time — not deep inside the publish loop — so a bad row is
    # caught before any artifact has been published for any RID, rather than mid-run.
    case "$m_output" in
        rid|plugin) ;;
        *) echo "Error: unknown output kind '$m_output' for $m_id" >&2; exit 1 ;;
    esac
    case "$m_profile" in
        trimmed|plain) ;;
        *) echo "Error: unknown profile '$m_profile' for $m_id" >&2; exit 1 ;;
    esac
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
        # Not `cp -f "$src"/*`: under `set -e` that aborts the whole script if $src has no
        # files (the glob stays literal) or contains a subdirectory ("omitting directory").
        # -type f sidesteps both.
        find "$src" -maxdepth 1 -type f -exec cp -f {} "$dest"/ \;
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
            if [ "$HOST_RID_SUPPORTED" = true ]; then
                echo "  [skip] ${IDS[$i]} for $rid: it is NativeAOT and native compilation cannot" >&2
                echo "         cross OS or CPU architecture. Build on $rid, or use --platform=$HOST_RID." >&2
            else
                echo "  [skip] ${IDS[$i]} for $rid: it is NativeAOT and native compilation cannot" >&2
                echo "         cross OS or CPU architecture, and this host ($(uname -s)/$(uname -m)) is not one this" >&2
                echo "         script publishes for. Supported: ${ALL_RIDS[*]}." >&2
            fi
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
                dotnet publish "$SCRIPT_DIR/${PROJECTS[$i]}" \
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
                dotnet publish "$SCRIPT_DIR/${PROJECTS[$i]}" \
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
