using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using ModelContextProtocol.Server;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using YamlDotNet.Serialization;

namespace Scrinia.Mcp;

public sealed partial class ScriniaProjectTools
{
    /// <summary>
    /// Resolves a workflow by name with override precedence:
    /// 1. Disk file (.scrinia/workflows/{name}.json)
    /// 2. NMP/2 memory (workflow:{name}) — legacy fallback
    /// 3. Built-in default (QuickFixWorkflow or DefaultGoalWorkflow)
    /// Corrupted overrides fall back with a warning.
    /// </summary>
    private static async Task<(WorkflowDefinition Workflow, string? Warning)> ResolveWorkflowAsync(
        IMemoryStore store, string workflowName, CancellationToken ct)
    {
        // 1a. Disk file — YAML
        string baseDir = GetScriniaBaseDir(store);
        foreach (var ext in new[] { ".yaml", ".yml" })
        {
            string yamlPath = Path.Combine(baseDir, "workflows", $"{workflowName}{ext}");
            if (File.Exists(yamlPath))
            {
                try
                {
                    string yamlContent = await File.ReadAllTextAsync(yamlPath, ct);
                    // AOT pipeline: YAML → object → JSON string → source-gen deserialize
                    var yamlDeserializer = new DeserializerBuilder().Build();
                    var yamlObj = yamlDeserializer.Deserialize<object>(yamlContent);
                    var jsonSerializer = new SerializerBuilder()
                        .JsonCompatible()
                        .Build();
                    string jsonString = jsonSerializer.Serialize(yamlObj);
                    var parsed = JsonSerializer.Deserialize(jsonString,
                        PlanningJsonContext.Default.WorkflowDefinition);
                    if (parsed is not null) return (parsed, null);
                }
                catch (Exception ex)
                {
                    return (WorkflowDefinition.DefaultGoalWorkflow,
                        $"\u26a0 ACTION NEEDED: YAML workflow '{workflowName}{ext}' could not be parsed: {ex.Message}");
                }
            }
        }

        // 1b. Disk file — JSON
        try
        {
            string filePath = Path.Combine(baseDir, "workflows", $"{workflowName}.json");
            if (File.Exists(filePath))
            {
                string json = await File.ReadAllTextAsync(filePath, ct);
                var parsed = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.WorkflowDefinition);
                if (parsed is not null) return (parsed, null);
            }
        }
        catch (JsonException)
        {
            var fallback = workflowName.Equals("quick-fix", StringComparison.OrdinalIgnoreCase)
                ? WorkflowDefinition.QuickFixWorkflow
                : WorkflowDefinition.DefaultGoalWorkflow;
            return (fallback,
                "\u26a0 ACTION NEEDED: workflow file could not be parsed \u2014 using built-in default.");
        }
        catch { /* file I/O error — fall through to NMP/2 */ }

        // 2. NMP/2 fallback (legacy)
        try
        {
            string content = await ReadMemoryAsync(store, $"workflow:{workflowName}", ct);
            var parsed = JsonSerializer.Deserialize(content, PlanningJsonContext.Default.WorkflowDefinition);
            if (parsed is not null) return (parsed, null);
        }
        catch (FileNotFoundException) { /* no override stored — fall through to built-ins */ }
        catch
        {
            var fallback = workflowName.Equals("quick-fix", StringComparison.OrdinalIgnoreCase)
                ? WorkflowDefinition.QuickFixWorkflow
                : WorkflowDefinition.DefaultGoalWorkflow;
            return (fallback,
                "\u26a0 ACTION NEEDED: stored workflow override could not be parsed \u2014 using built-in default.");
        }

        // 3. Built-in default
        return workflowName.Equals("quick-fix", StringComparison.OrdinalIgnoreCase)
            ? (WorkflowDefinition.QuickFixWorkflow, null)
            : (WorkflowDefinition.DefaultGoalWorkflow, null);
    }

    /// <summary>
    /// Creates or updates a workflow definition from JSON, with full validation.
    /// </summary>
    private static async Task<string> CreateOrUpdateWorkflow(
        string action, string? definition, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(definition))
            return ResponseBuilder.Error($"Workflow '{action}' requires 'definition' parameter with workflow JSON.").ToYaml();

        WorkflowDefinition parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(definition, PlanningJsonContext.Default.WorkflowDefinition)!;
            if (parsed is null)
                return ResponseBuilder.Error("Workflow definition deserialized to null.").ToYaml();
        }
        catch (JsonException)
        {
            // JSON parse failed — try YAML pipeline: YAML → object → JSON string → source-gen deserialize
            try
            {
                var yamlDeserializer = new DeserializerBuilder().Build();
                var yamlObj = yamlDeserializer.Deserialize<object>(definition);
                var jsonSerializer = new SerializerBuilder()
                    .JsonCompatible()
                    .Build();
                string jsonFromYaml = jsonSerializer.Serialize(yamlObj);
                parsed = JsonSerializer.Deserialize(jsonFromYaml, PlanningJsonContext.Default.WorkflowDefinition)!;
                if (parsed is null)
                    return ResponseBuilder.Error("Workflow definition deserialized to null (from YAML).").ToYaml();
            }
            catch (Exception ex)
            {
                return ResponseBuilder.Error($"Failed to parse workflow definition as JSON or YAML — {ex.Message}").ToYaml();
            }
        }

        var errors = WorkflowDefinition.Validate(parsed);
        if (errors.Count > 0)
            return ResponseBuilder.Error($"Workflow validation failed:\n- {string.Join("\n- ", errors)}").ToYaml();

        // Compute basedOn hash from the built-in default workflow
        string defaultJson = JsonSerializer.Serialize(
            WorkflowDefinition.DefaultGoalWorkflow, PlanningJsonContext.Default.WorkflowDefinition);
        string basedOnHash = Convert.ToHexStringLower(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(defaultJson)));

        var store = CurrentStore;

        // Write to disk (.scrinia/workflows/{name}.json)
        string baseDir = GetScriniaBaseDir(store);
        string workflowsDir = Path.Combine(baseDir, "workflows");
        string filePath = Path.Combine(workflowsDir, $"{parsed.Name}.json");
        Directory.CreateDirectory(workflowsDir);

        // Archive previous version if file exists
        ArchiveFileVersion(filePath, Path.Combine(workflowsDir, "versions"));

        // Serialize and write workflow JSON
        string content = JsonSerializer.Serialize(parsed, PlanningJsonContext.Default.WorkflowDefinition);
        await File.WriteAllTextAsync(filePath, content, cancellationToken);

        // Write sidecar metadata
        string now = DateTimeOffset.UtcNow.ToString("o");
        var existingMeta = ReadSidecarMeta(filePath, PlanningJsonContext.Default.WorkflowFileMeta);
        var meta = new WorkflowFileMeta(
            BasedOn: basedOnHash,
            CreatedAt: existingMeta?.CreatedAt ?? now,
            UpdatedAt: now);
        WriteSidecarMeta(filePath, meta, PlanningJsonContext.Default.WorkflowFileMeta);

        // Check for legacy NMP/2 entry (MF-C01 migration note)
        string migrationNote = "";
        try
        {
            await ReadMemoryAsync(store, $"workflow:{parsed.Name}", cancellationToken);
            migrationNote = " Note: a legacy NMP/2 entry for workflow:{parsed.Name} still exists — it will be used as fallback but the disk file takes precedence.";
        }
        catch { /* no legacy entry — nothing to note */ }

        int seedCount = parsed.SeedActivities?.Length ?? 0;
        int gateCount = parsed.PostPlanActivities?.Length ?? 0;
        string wfAction = action == "create" ? "created" : "updated";
        var wfResult = ResponseBuilder.Success($"Workflow '{parsed.Name}' {wfAction} — {seedCount} seed(s), {gateCount} gate(s). Stored at .scrinia/workflows/{parsed.Name}.json.")
            .WithFileChanges()
            .WithPath($"/workflow/{parsed.Name}")
            .WithAction(wfAction);
        if (!string.IsNullOrEmpty(migrationNote))
            wfResult = wfResult.WithInfo(migrationNote.TrimStart());
        return wfResult.ToYaml();
    }

    // ── Merge infrastructure scaffolding ─────────────────────────────────────

    private static void ScaffoldMergeInfrastructure(string scriniaDir)
    {
        // .gitattributes
        string gitattributesPath = Path.Combine(scriniaDir, ".gitattributes");
        if (!File.Exists(gitattributesPath))
        {
            File.WriteAllText(gitattributesPath,
                "# Scrinia memory merge configuration\n" +
                "*.nmp2 binary\n" +
                "*.meta.json merge=scrinia-meta\n");
        }

        // hooks directory
        string hooksDir = Path.Combine(scriniaDir, "hooks");
        Directory.CreateDirectory(hooksDir);

        // Merge driver - bash
        string bashDriver = Path.Combine(hooksDir, "scrinia-merge-meta.sh");
        if (!File.Exists(bashDriver))
        {
            File.WriteAllText(bashDriver, GetBashMergeDriverContent());
        }

        // Merge driver - PowerShell
        string psDriver = Path.Combine(hooksDir, "scrinia-merge-meta.ps1");
        if (!File.Exists(psDriver))
        {
            File.WriteAllText(psDriver, GetPowerShellMergeDriverContent());
        }

        // Post-merge hook
        string postMerge = Path.Combine(hooksDir, "post-merge");
        if (!File.Exists(postMerge))
        {
            File.WriteAllText(postMerge, GetPostMergeHookContent());
        }
    }

    private static string GetBashMergeDriverContent() =>
        """
        #!/usr/bin/env bash
        # scrinia .meta.json merge driver
        # Unions keywords, takes latest updatedAt, max termFrequencies
        # Usage: git config merge.scrinia-meta.driver ".scrinia/hooks/scrinia-merge-meta.sh %O %A %B"

        set -euo pipefail

        ANCESTOR="$1"  # %O — common ancestor
        OURS="$2"      # %A — our version (result written here)
        THEIRS="$3"    # %B — their version

        # Requires jq for JSON processing
        if ! command -v jq &>/dev/null; then
            echo "scrinia merge driver: jq not found, falling back to git merge" >&2
            exit 1
        fi

        # Union keywords from both sides (sorted, unique)
        OURS_KW=$(jq -r '.keywords // [] | .[]' "$OURS" 2>/dev/null | sort -fu)
        THEIRS_KW=$(jq -r '.keywords // [] | .[]' "$THEIRS" 2>/dev/null | sort -fu)
        MERGED_KW=$(echo -e "${OURS_KW}\n${THEIRS_KW}" | sort -fu | grep -v '^$')

        # Pick base: latest updatedAt wins
        OURS_TS=$(jq -r '.updatedAt // .createdAt // ""' "$OURS" 2>/dev/null)
        THEIRS_TS=$(jq -r '.updatedAt // .createdAt // ""' "$THEIRS" 2>/dev/null)

        if [[ "$THEIRS_TS" > "$OURS_TS" ]]; then
            BASE="$THEIRS"
        else
            BASE="$OURS"
        fi

        # Build merged keywords as JSON array
        KW_JSON=$(echo "$MERGED_KW" | jq -R -s 'split("\n") | map(select(length > 0))')

        # Merge termFrequencies: take max value for each key
        TF_MERGED=$(jq -s '
          .[0].termFrequencies // {} | to_entries | map({key: .key, value: .value}) as $a |
          .[1].termFrequencies // {} | to_entries | map({key: .key, value: .value}) as $b |
          ($a + $b) | group_by(.key) | map({key: .[0].key, value: ([.[].value] | max)}) |
          from_entries
        ' "$OURS" "$THEIRS" 2>/dev/null || echo '{}')

        # Write result to OURS path (git expects result there)
        jq --argjson kw "$KW_JSON" --argjson tf "$TF_MERGED" \
          '.keywords = $kw | .termFrequencies = $tf' "$BASE" > "${OURS}.tmp" && mv "${OURS}.tmp" "$OURS"

        exit 0
        """;

    private static string GetPowerShellMergeDriverContent() =>
        """
        #!/usr/bin/env pwsh
        # scrinia .meta.json merge driver (PowerShell)
        # Usage: git config merge.scrinia-meta.driver "pwsh .scrinia/hooks/scrinia-merge-meta.ps1 %O %A %B"

        param(
            [string]$Ancestor,  # %O
            [string]$Ours,      # %A — result written here
            [string]$Theirs     # %B
        )

        try {
            $oursJson = Get-Content $Ours -Raw | ConvertFrom-Json
            $theirsJson = Get-Content $Theirs -Raw | ConvertFrom-Json

            # Pick base: latest updatedAt
            $oursTs = if ($oursJson.updatedAt) { [DateTimeOffset]::Parse($oursJson.updatedAt) } else { [DateTimeOffset]::MinValue }
            $theirsTs = if ($theirsJson.updatedAt) { [DateTimeOffset]::Parse($theirsJson.updatedAt) } else { [DateTimeOffset]::MinValue }

            $base = if ($theirsTs -gt $oursTs) { $theirsJson } else { $oursJson }
            $other = if ($theirsTs -gt $oursTs) { $oursJson } else { $theirsJson }

            # Union keywords (sorted, case-insensitive unique)
            $allKw = @()
            if ($oursJson.keywords) { $allKw += $oursJson.keywords }
            if ($theirsJson.keywords) { $allKw += $theirsJson.keywords }
            $base.keywords = $allKw | Sort-Object -Unique

            # Merge termFrequencies (max for shared keys)
            if ($other.termFrequencies) {
                $baseTf = @{}
                if ($base.termFrequencies) {
                    $base.termFrequencies.PSObject.Properties | ForEach-Object { $baseTf[$_.Name] = $_.Value }
                }
                $other.termFrequencies.PSObject.Properties | ForEach-Object {
                    if ($baseTf.ContainsKey($_.Name)) {
                        $baseTf[$_.Name] = [Math]::Max($baseTf[$_.Name], $_.Value)
                    } else {
                        $baseTf[$_.Name] = $_.Value
                    }
                }
                $base.termFrequencies = [PSCustomObject]$baseTf
            }

            # Write result
            $base | ConvertTo-Json -Depth 10 | Set-Content $Ours -Encoding UTF8
            exit 0
        }
        catch {
            Write-Error "scrinia merge driver failed: $_"
            exit 1
        }
        """;

    private static string GetPostMergeHookContent() =>
        """
        #!/usr/bin/env bash
        # scrinia post-merge hook
        # Scans .scrinia/ for unresolved merge conflicts after git merge/pull.
        #
        # Installation:
        #   cp .scrinia/hooks/post-merge .git/hooks/post-merge
        #   chmod +x .git/hooks/post-merge
        #
        # Or with symlink (updates automatically):
        #   ln -s ../../.scrinia/hooks/post-merge .git/hooks/post-merge

        # Check for conflict markers in .scrinia/ files
        if grep -r -l "<<<<<<< " .scrinia/ 2>/dev/null | head -1 > /dev/null 2>&1; then
            CONFLICTED=$(grep -r -l "<<<<<<< " .scrinia/ 2>/dev/null | wc -l)
            echo ""
            echo "⚠  scrinia: $CONFLICTED file(s) in .scrinia/ have unresolved merge conflicts."
            echo "   Run memory('reconcile') in your next agent session to resolve them."
            echo "   Files with conflicts:"
            grep -r -l "<<<<<<< " .scrinia/ 2>/dev/null | sed 's/^/     /'
            echo ""
        fi
        """;
}
