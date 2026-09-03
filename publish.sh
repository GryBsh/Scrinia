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

# OS and architecture are resolved separately and joined: a Linux or Windows arm64 host must not
# be mistaken for its x64 sibling.
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
if [ ! -f "$MANIFEST" ]; then
    echo "Error: manifest not found at $MANIFEST" >&2
    exit 1
fi

# Pinned exactly, in order: this script reads columns POSITIONALLY below, so a reordered or
# renamed header would silently misassign every field to the wrong variable without either
# script raising an error. publish.ps1 pins the identical line — the two readers of one
# source of truth must agree about what the file means.
EXPECTED_HEADER=$'id\tproject\tcondition\tprofile\toutput\trids'
ACTUAL_HEADER="$(head -n 1 "$MANIFEST")"
ACTUAL_HEADER="${ACTUAL_HEADER%$'\r'}"
if [ "$ACTUAL_HEADER" != "$EXPECTED_HEADER" ]; then
    echo "Error: manifest header must be exactly 'id<TAB>project<TAB>condition<TAB>profile<TAB>output<TAB>rids' — found '$ACTUAL_HEADER'" >&2
    exit 1
fi

IDS=(); PROJECTS=(); CONDITIONS=(); PROFILES=(); OUTPUTS=(); RIDSCOPES=()
# The `|| [ -n "$raw_line" ]` keeps the last row when the file has no trailing newline: `read`
# returns non-zero at EOF-without-newline having already filled the variable, so the plain form
# would silently drop it.
while IFS= read -r raw_line || [ -n "$raw_line" ]; do
    # Strip a trailing CR: .gitattributes pins this file to LF, but a user whose git
    # configuration ignores it would otherwise get a corrupted final column.
    raw_line="${raw_line%$'\r'}"
    if [ -z "$raw_line" ]; then continue; fi
    case "$raw_line" in \#*) continue ;; esac

    # Field count is computed on the RAW line, before `read` ever splits it. Tab is IFS
    # whitespace, so `IFS=$'\t' read` collapses a trailing run of it: a row ending in a stray
    # tab (a malformed, empty 7th column) parsed identically to a clean 6-column row here, and
    # publish.ps1's `-split "\t"` count of that same line is 7 — the same input was silently
    # accepted by this script and rejected by the other. Counting the tabs directly (not asking
    # `read` to split first) catches a genuine 7th column and a trailing tab with one check and
    # one message, matching publish.ps1's `$fieldCount` check below.
    tabs_only="${raw_line//[^$'\t']/}"
    field_count=$((${#tabs_only} + 1))
    if [ "$field_count" -ne 6 ]; then
        echo "Error: manifest row has $field_count tab-separated columns, expected 6: '$raw_line'" >&2
        exit 1
    fi

    IFS=$'\t' read -r m_id m_project m_condition m_profile m_output m_rids <<< "$raw_line"

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
    # condition and rids gate which projects/RIDs actually build — an unrecognised value here
    # used to be silently misread rather than rejected (a `with_embeddings` typo shipped the
    # embeddings plugin in EVERY build; `rids=hostt` silently degraded to `all`).
    case "$m_condition" in
        always|with-embeddings) ;;
        *) echo "Error: unknown condition '$m_condition' for $m_id" >&2; exit 1 ;;
    esac
    case "$m_rids" in
        all|host) ;;
        *) echo "Error: unknown rids scope '$m_rids' for $m_id" >&2; exit 1 ;;
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
    local rid="$1" dest="$2" pkg src expected file
    local -a expected_files
    case "$rid" in
        win-*)
            pkg="llamasharp.backend.vulkan.windows"
            expected_files=("mtmd.dll" "llama.dll" "ggml.dll" "ggml-vulkan.dll" "ggml-base.dll")
            ;;
        linux-*)
            pkg="llamasharp.backend.vulkan.linux"
            expected_files=("libmtmd.so" "libllama.so" "libggml.so" "libggml-vulkan.so" "libggml-base.so")
            ;;
        *)
            # macOS has no Vulkan package in the supported release contract. Keep the explicit,
            # intentional CPU fallback for that platform; Windows and Linux fail below instead.
            echo "  [info] no Vulkan backend package exists for $rid; using the configured macOS CPU fallback." >&2
            return 0 ;;
    esac
    src="$HOME/.nuget/packages/$pkg/$VULKAN_BACKEND_VERSION/runtimes/$rid/native/vulkan"
    if [ ! -d "$src" ]; then
        echo "Error: Vulkan natives not found at $src; refusing to ship a CPU fallback for $rid." >&2
        exit 1
    fi
    for expected in "${expected_files[@]}"; do
        file="$src/$expected"
        if [ ! -f "$file" ]; then
            echo "Error: expected Vulkan native is missing: $file" >&2
            exit 1
        fi
        cp -f "$file" "$dest/$expected"
        if [ ! -f "$dest/$expected" ] || ! cmp -s "$file" "$dest/$expected"; then
            echo "Error: copied Vulkan native does not match its package source: $dest/$expected" >&2
            exit 1
        fi
    done
    echo "    Overwrote and verified root native DLLs with Vulkan variants"
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
