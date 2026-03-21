using System.ComponentModel;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol.Server;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Mcp;

[McpServerToolType]
public sealed class ScriniaMcpTools
{

    private static IMemoryStore CurrentStore =>
        MemoryStoreContext.Current ?? throw new InvalidOperationException(
            "No memory store configured. Call MemoryStoreContext.Current = ... before using MCP tools.");

    /// <summary>
    /// Resolves inline NMP/2 artifacts without requiring a configured store.
    /// Returns null if the input requires store-based resolution (memory name, file://, ephemeral, etc.).
    /// file:// URIs are deliberately NOT handled here — they require store-based resolution
    /// for workspace sandbox validation (see FileMemoryStore.ResolveArtifactAsync).
    /// </summary>
    private static Task<string?> TryResolveWithoutStore(string input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input))
            return Task.FromResult<string?>(null);

        // Inline NMP/2 artifact
        if (input.TrimStart().StartsWith("NMP/2 ", StringComparison.Ordinal))
            return Task.FromResult<string?>(input);

        return Task.FromResult<string?>(null);
    }

    [McpServerTool(Name = "guide"), Description(
        "Required reading — call at session start, then commit content to your project's agent file. " +
        "If a project exists (.scrinia/ directory), check plan_status for active goals before starting new work. " +
        "Covers memory patterns, the goal-driven planning workflow, and when to plan vs. just do.")]
    public Task<string> Guide(CancellationToken cancellationToken = default) =>
        Task.FromResult("""
            # scrinia guide — cognitive toolset for LLM agents

            Scrinia is built on three overlapping capabilities — use them fluidly, not just sequentially:

            **Research** — know before you act. Search existing memories and skills (internal) or investigate
            the codebase and domain (external). Form hypotheses. Build on what you already believe.

            **Plan & Do** — structure work around goals. Decompose into parallel tasks, execute, verify
            against your hypothesis. Get user input at judgment calls.

            **Learn** — get better every time. Update beliefs after each phase. Evolve skills from experience.
            Store what you figured out. The agent that finishes a goal should be smarter than the one that started it.

            These aren't sequential phases — you can research during execution, learn during research,
            and plan while doing. The tools support all three at any time.

            ## First session setup
            1. Commit this guide's content to your project's agent file (AGENTS.md, CLAUDE.md, or equivalent)
            2. If a `.scrinia/` directory exists, call `plan_status()` to check for active goals
            3. If a project exists but no active goal, ask the user what they'd like to work on → `goal_update(add)`
            4. If no project exists, call `project_init` when the user's request warrants structured planning

            ## When to plan vs. just do
            Not every request needs planning. Use this guide:
            - **Just do it**: single file change, clear fix, quick question, under ~3 focused edits
            - **Set a goal**: multiple files, unclear scope, research needed, multi-step work, or the user says "add", "build", "refactor", "audit"
            - **When in doubt**: ask the user — "This looks like it could be a few changes or a larger effort. Want me to plan this out?"
            The cost of planning a small task is low. The cost of not planning a large one is high.

            ## Memory habits — build knowledge every session
            Whether or not you're in a planning workflow, proactively persist what you learn:
            - **When you learn something** about the codebase, domain, or tooling → `store(content, "topic:subject")`
            - **When you fix a bug** → store the root cause and fix pattern so it's not re-investigated
            - **When you research something** → store the findings, even if the user didn't ask you to
            - **When the user corrects you** → store the correction as a pattern to follow next time
            - **When you discover a convention** → store it so future sessions follow it automatically
            - **When starting work** → `search()` first to check if prior sessions already covered this ground
            These aren't planning steps — they're habits that make every session smarter than the last.
            Use topic naming to organize: `patterns:auth`, `bugs:sqlite-locking`, `conventions:naming`, etc.

            **Scrinia is the single source of truth.** If your platform provides its own memory or note
            system (Claude auto-memory, Cursor notes, Windsurf memories, etc.), do not use it for project
            knowledge, behavioral feedback, or preferences. All persistence goes through scrinia so the
            memory base stays whole and unfractured — accessible to any agent on this project, not locked
            into one platform.

            ## Skill habits — learn how to do things better
            Skills capture *how you approach work* — methodology, not facts. Keep them lean:
            - **Skills are methodology**: what to check, what order, what patterns matter, how to verify
            - **Memories are knowledge**: what you found, what was fixed, what you believe about the domain
            - **Skills reference memories**: a skill says `search("applied-fixes")`, not a hardcoded list of fixes
            - **When you develop an effective approach** to a type of task → `skill_create` with the methodology
            - **When the user validates your approach** ("that was exactly right") → capture it as a skill
            - **When starting a familiar task** → `skill_load` to check if you've done this type of work before
            - **When a skill doesn't work well** → update the methodology, not the facts (facts go in memories)
            A skill that grows with every use is storing facts that belong in memories.
            A good skill stays lean and gets smarter because the memories it references grow.

            ## Ephemeral scrinia memories (~name)
            Use `~` prefix for in-session working state that shouldn't persist:
            - `store(["scratch data"], "~scratch")` — dies when process exits
            - Great for intermediate results, draft summaries, working context
            - Promote to persistent with `copy("~scratch", "topic:final-name")`

            ## Topic organization
            Use topic:subject naming to organize related scrinia memories:
            - `store(["content"], "api:auth-flow")` — stored in api/ topic
            - `store(["content"], "arch:decisions")` — stored in arch/ topic
            - Topics are auto-discovered — no setup needed

            ## Chunked retrieval
            For large scrinia memories, retrieve only what you need:
            1. `chunk_count("my-memory")` — see how many chunks
            2. `get_chunk("my-memory", 1)` — read just the first chunk
            3. Process chunk by chunk to stay within context limits

            ## Incremental capture with append
            Build up scrinia memories incrementally — each append adds a new independently retrievable chunk:
            - `append("New finding here", "session-notes")` — adds as a new chunk
            - Creates the scrinia memory if it doesn't exist yet
            - Great for session journals, running logs, and incremental notes
            - Each appended chunk is individually indexed for search

            ## Context compression
            When you gather large amounts of information during research:
            1. Summarize your findings into a concise document
            2. `store([summary], "topic:finding-name")` — persist for future sessions
            3. Later: `search("finding")` → `show("topic:finding-name")` to recall
            This lets you carry knowledge across sessions without re-researching.

            ## Version history
            When you overwrite an existing scrinia memory, the previous version is archived:
            - Stored in `versions/` subdirectory with timestamp suffix
            - No manual action needed — happens automatically on store/append

            ## Review conditions
            Flag scrinia memories that may become stale:
            - `store(["content"], "api:endpoints", reviewAfter="2026-06-01")` — date-based
            - `store(["content"], "auth:flow", reviewWhen="when auth system changes")` — condition-based
            - `list()` shows a summary with topics, keywords, and stats
            - `list(mode="full")` shows all entries with `[stale]` or `[review?]` markers

            ## Context preservation (~checkpoints)
            Long conversations get compressed by your host platform. Use ephemeral scrinia checkpoints to survive:
            - Before a large task or after a milestone, store your current state:
              `store(["Task: ...\nKey findings: ...\nNext steps: ..."], "~checkpoint")`
            - After context compaction, restore your bearings:
              `list(scopes="ephemeral")` then `show("~checkpoint")`
            - Update the checkpoint as you make progress — overwrite with fresh state
            - **When to checkpoint**: before large multi-step tasks, after completing milestones,
              when the conversation is getting long, or before operations that generate lots of output
            - If you feel disoriented or can't remember what you were doing, call `plan_resume()`
              — it rebuilds your full context from stored planning state

            ## Cross-project sharing
            Export topics as portable .scrinia-bundle files:
            1. `export(["api", "arch"])` — creates a .scrinia-bundle in .scrinia/exports/
            2. Copy the bundle to another project
            3. `import("path/to/bundle.scrinia-bundle")` — restores all topics
            Useful for sharing team conventions, API patterns, or onboarding knowledge.

            ## Workspace changes
            Scrinia tools (store, append, forget, import, copy) write to the `.scrinia/` directory
            in the workspace root. These file changes are a direct result of your tool calls —
            treat them as changes you made. Include `.scrinia/` changes in your commits alongside
            other project changes.

            ## When to store vs. not store
            **Store in scrinia:** anything you'd want to know if you started a fresh session tomorrow —
            patterns, decisions, bug fixes, conventions, API behaviors, domain knowledge, user corrections.
            **Don't store:** transient working state (use ~ephemeral instead).
            **Exception:** use `~checkpoint` to preserve working context across context compactions.
            **Rule of thumb:** if you had to figure it out, store it. Future sessions shouldn't re-derive what you already know.

            ## Memory granularity
            - **One concept per memory**: `security:applied-fixes` not `everything-about-security`
            - **Use append for accumulation**: `append(new_fix, "security:applied-fixes")` adds a chunk, keeps it searchable
            - **Use store for replacement**: `store(updated_content, "arch:overview")` when the whole picture changed
            - **Topics group related memories**: `security:applied-fixes`, `security:patterns`, `security:concerns` — not one giant `security` memory
            - **Name for searchability**: will `search("auth fix")` find this? Use descriptive names and keywords
            - **When in doubt, smaller is better**: two focused memories beat one sprawling one

            ## Project planning tools
            Scrinia includes 20 planning tools for structured project lifecycle management.
            Plans are stored as standard scrinia memories with reserved topic conventions.
            The workflow is goal-driven — you initialize once, then cycle through goals.

            ### One-time setup
            `project_init(context)` — call once per workspace. In an existing codebase, this triggers
            a pre-planning phase where you should scan for concerns and build knowledge before setting goals.

            ### The goal-driven cycle

            **1. Pre-plan** (existing codebase) — understand what you're working with:
            - Scan the codebase for risks, tech debt, issues → `concern_add(description, severity, phaseScope)`
            - Capture architecture patterns and conventions → `store(content, "topic:subject", keywords=[...])`
            - Concerns and knowledge persist across goals — they accumulate over the project's lifetime.
            Skip this step for greenfield projects or when you already have context.

            **2. Set a goal** — what are we working toward?
            - `goal_update(action:"add", description:"...")` — the goal drives everything that follows.
            - Goals are the top-level unit of work. Requirements, roadmap, and tasks all serve the goal.
            - **Before planning, clarify with the user**: scope (in/out), success criteria, constraints, priority.
            Ambiguous goals lead to wasted work — invest in clarity upfront.

            **3. Research & hypothesize** — investigate, then state what you believe will work:
            - `research_start(phaseId, topic, question)` → investigate → `research_complete(phaseId, topic, findings, hypothesis)`
            - The hypothesis states your proposed approach and what would invalidate it
            - Research findings + hypothesis inform task decomposition
            - **Produce a change manifest**: for modification tasks, identify exact change sites (file paths,
            function names, line ranges, the pattern to apply). This enables specific task descriptions
            and effective parallelism. For greenfield tasks, a spec is sufficient.
            - Discover new concerns during research? → `concern_add`
            - Skip research when the path is already clear.

            **4. Plan** — define requirements and roadmap:
            - `plan_requirements(requirements)` — REQ-IDs derived from the goal + research + concerns
            - `plan_roadmap(roadmap)` — phases mapping to REQ-IDs with success criteria

            **5. Decompose & execute** — break phases into tasks and work through them:
            - `plan_tasks(phaseId, tasks)` — tasks with dependencies (waves computed automatically)
            - **Tasks should be agent-executable**: specific enough that a focused agent can make the change
            without exploring the codebase. For modifications: include file path, function/block, and
            transformation. For new files: include spec and interfaces. Research found the details —
            carry them through to tasks.
            - `task_next(phaseId)` → spawn a parallel agent for each task → `task_complete(taskName, outcome)`
            - `plan_status()` — check progress (computed live from task data)
            - During execution: `concern_add` for new risks, `store()` for things you learn
            - **Parallelize aggressively**: when task_next returns multiple tasks, spawn one agent per task.
            Use worktree isolation for tasks that touch overlapping files. Use background execution
            for long-running work so you can continue with other tasks.
            Use `skill_load("planner")` for complex decompositions — it produces explicit agent specs
            with file conflict detection and wave sequencing.
            - **Agent SOS**: if a spawned agent hits a wall (needs expertise, needs a new skill, or
            discovers the task needs decomposition), it returns an SOS signal rather than a poor result.
            Use `skill_load("sos-handler")` to triage and replan.
            - **Interrogate the design at each task**: don't just execute — ask "what's missing?" and
            "what would a downstream consumer expect?" before marking complete. Surface gaps proactively.
            - **Goals serve the project, not the other way around.** Task acceptance criteria won't capture
            every project-level implication. During execution, maintain awareness: did this change introduce
            a new permission, config setting, endpoint, or API surface? Does documentation, security posture,
            or the project's public contract need updating? Don't leave work for a future audit to catch.

            **6. Verify** — did the phase achieve its goal? Did your hypothesis hold?
            - `plan_verify(phaseId)` — surfaces your hypothesis + criteria checklist, then record evidence
            - Evaluate: did criteria pass AND does the evidence support your hypothesis?
            - If gaps: `plan_gaps(phaseId, failedCriteria)` → fix tasks → re-verify
            - If the hypothesis was wrong (approach fundamentally flawed, not just gaps):
              stop, discuss with the user, revise the approach, and replan from step 3 (research).
              Don't keep executing a plan built on a wrong assumption.
            - `concern_resolve(concernName, resolution)` — close addressed concerns
            - **Track findings with sequential IDs** (e.g. SEC-001, QAL-001, DOC-001) stored in a findings
            registry (`audit:findings-registry`). Never reuse numbers. This enables regression tracking
            across goals and consistent reference numbers in release document sets.
            - **Remediate in parallel**: after validating findings, group by file and spawn one fix agent
            per file group. The audit already identified exact locations — carry them through to fix agents.
            This is not a judgment call; it is the procedure.

            **7. Learn & distill** — record what happened and update your understanding:
            - `plan_retrospective(phaseId, whatWorked, whatFailed, lessons, beliefsUpdated)` — accumulates across phases
            - `beliefsUpdated`: what do you now understand differently? (auto-stored as topical memories)
            - Update or create skills (`skill_create`) with lessons from this phase
            - `plan_profile(profile)` — store project-level agent behavioral norms

            **8. Complete the goal** — distill, report, then start the next one:
            - Distill valuable findings into topical memories (`store`) so future goals start smarter
            - Update skills with accumulated lessons — this is the learning loop
            - `goal_update(action:"complete", goalId, outcome)` — mark the goal done
            - **Offer a march report**: `skill_load("march-reporter")` — produce a human-readable goal
            summary document for audit trail. Always produce one at milestone boundaries; ask for smaller goals.
            - Planning artifacts (task:*, plan:*, research:*) can be cleaned up — the learnings live in memories and skills now
            - Set the next goal → back to step 2

            **Recovery** (at any point):
            - `plan_resume()` — restore full context after context loss (rebuilds state if needed)
            - `plan_status()` — quick progress check
            - `concern(phaseFilter?)` — list active concerns

            **Skills** (stored as skill:* memories, portable across sessions and projects):
            - `skill_create(skillName, scaffold, instructions?, tools?)` — create a specialist skill with project-specific context
            - `skill_load(skillName?)` — list available skills or load one for use as a subagent prompt
            Skills evolve from experience: retrospective lessons feed back into skill updates.
            Use skill_load before research to check for existing specialists.

            **Reserved planning topics** — avoid using these prefixes for general knowledge:
            - `project:*` — project context, requirements, state (e.g. `project:context`, `project:state`)
            - `plan:*` — roadmaps (e.g. `plan:roadmap`)
            - `task:*` — decomposed tasks with keyword metadata (e.g. `task:01-1-03`)
            - `learn:*` — retrospectives, execution outcomes, and updated beliefs
            - `agent:*` — project-level agent behavioral norms (e.g. `agent:profile`)
            - `research:*` — investigation findings and hypotheses
            - `concern:*` — tracked risks and issues
            - `skill:*` — reusable specialist prompts
            Use `excludeTopics="plan,task,project,learn"` on `list`/`search` to hide planning from knowledge queries.

            ## Agent learning
            Learning happens through the full cycle, not just retrospectives:
            - Phase retrospectives accumulate in `learn:execution-outcomes` (searchable, provenance:agent keyword)
            - Skills evolve with each goal — update them after retrospectives
            - Topical memories grow as you distill findings after each goal
            - Agent behavioral norms persist in `agent:profile` across sessions
            The result: each goal starts with better context than the last.
            Memories authored via plan_retrospective and plan_profile carry `provenance:agent` keyword.
            """);

    [McpServerTool(Name = "encode"), Description(
        "Compress text into a chunk-addressable NMP/2 artifact (brotli). " +
        "Returns the artifact inline. " +
        "Use chunk_count() and get_chunk() to access the content chunk-by-chunk.")]
    public Task<string> Encode(
        [Description("The text to compress. " +
                     "Pass a single element for a single-chunk artifact, or multiple elements to control " +
                     "chunk boundaries — each element becomes one independently decodable chunk.")] string[] content,
        CancellationToken cancellationToken = default)
    {
        string artifact = content.Length == 1
            ? Nmp2ChunkedEncoder.Encode(content[0])
            : Nmp2ChunkedEncoder.EncodeChunks(content);
        return Task.FromResult(artifact);
    }

    [McpServerTool(Name = "chunk_count"), Description(
        "Returns the number of independently decodable chunks in a compressed artifact. " +
        "Single-chunk artifacts return 1.")]
    public async Task<int> ChunkCount(
        [Description("The artifact text, memory name, or file:// URI returned by Encode().")]
        string artifactOrName,
        CancellationToken cancellationToken = default)
    {
        var resolved = await TryResolveWithoutStore(artifactOrName, cancellationToken);
        string artifact = resolved ?? await CurrentStore.ResolveArtifactAsync(artifactOrName, cancellationToken);
        return Nmp2ChunkedEncoder.GetChunkCount(artifact);
    }

    [McpServerTool(Name = "get_chunk"), Description(
        "Decodes and returns the text of one chunk from a compressed artifact. " +
        "Chunks are 1-based. Call chunk_count() first to know the upper bound. " +
        "Process chunks sequentially to reconstruct the full document.")]
    public async Task<string> GetChunk(
        [Description("The artifact text, memory name, or file:// URI returned by Encode().")]
        string artifactOrName,
        [Description("1-based chunk index.")] int chunkIndex,
        CancellationToken cancellationToken = default)
    {
        var resolved = await TryResolveWithoutStore(artifactOrName, cancellationToken);
        string artifact = resolved ?? await CurrentStore.ResolveArtifactAsync(artifactOrName, cancellationToken);
        string chunk = Nmp2ChunkedEncoder.DecodeChunk(artifact, chunkIndex);
        SessionBudget.RecordAccess(artifactOrName, chunk.Length);
        return chunk;
    }

    [McpServerTool(Name = "show"), Description(
        "Unpack an NMP/2 artifact back to its original text content. " +
        "Accepts either the artifact text inline or a memory name. " +
        "Only NMP/2 artifacts are supported; other formats return an error string.")]
    public async Task<string> Show(
        [Description("The NMP/2 artifact text, or a memory name to resolve. " +
                     "Use the exact name shown by list() (e.g. 'session-notes', 'api:auth-flow', '~scratch').")] string artifactOrName,
        CancellationToken cancellationToken = default)
    {
        string artifact;

        // Fast path: inline NMP/2 artifacts and file:// URIs don't need a store
        var resolved = await TryResolveWithoutStore(artifactOrName, cancellationToken);
        if (resolved != null)
        {
            artifact = resolved;
        }
        else
        {
            // Store-based resolution (memory name, ephemeral, etc.)
            var store = MemoryStoreContext.Current;
            if (store is null)
                return $"Error: memory '{artifactOrName}' not found. Use list() or search() to find available memories.";

            try
            {
                artifact = await store.ResolveArtifactAsync(artifactOrName, cancellationToken);
            }
            catch (FileNotFoundException)
            {
                return $"Error: memory '{artifactOrName}' not found. Use list() or search() to find available memories.";
            }
        }

        if (!artifact.TrimStart().StartsWith("NMP/2 ", StringComparison.Ordinal))
            return "Error: only NMP/2 artifacts are supported by this tool.";

        byte[] bytes = new Nmp2Strategy().Decode(artifact);
        string decoded = System.Text.Encoding.UTF8.GetString(bytes);
        SessionBudget.RecordAccess(artifactOrName, decoded.Length);
        return decoded;
    }

    // ── Persistent memory tools ───────────────────────────────────────────────

    [McpServerTool(Name = "store"), Description(
        "Compress text and persist it as a named artifact in a memory scope. " +
        "Use proactively to save important findings, decisions, patterns, and solutions as you work. " +
        "Knowledge saved here persists across sessions and travels with the code. " +
        "Use topic:subject naming to organize into local topics " +
        "(e.g. 'api:auth-flow', 'arch:decisions'). " +
        "Prefix with ~ for ephemeral in-memory storage (e.g. '~scratch'). " +
        "Flag memories that may become stale with optional review conditions. " +
        "Note: this writes to .scrinia/ in the workspace — treat those file changes as your own.")]
    public async Task<string> Store(
        [Description("The text content to compress and store. " +
                     "Pass a single element for a single-chunk artifact, or multiple elements to control " +
                     "chunk boundaries — each element becomes one independently retrievable chunk.")] string[] content,
        [Description("Human-readable name for this artifact (e.g. \"session-notes\", \"my-codebase\"). " +
                     "Invalid filename characters are replaced with '_'. " +
                     "Naming: 'subject' (local store), 'topic:subject' (local topic), '~subject' (ephemeral).")] string name,
        [Description("Optional description. If empty, the first 200 characters of content are used.")] string description = "",
        [Description("Optional tags for categorization.")] string[]? tags = null,
        [Description("Optional keywords for search. Merged with auto-extracted content terms.")] string[]? keywords = null,
        [Description("Optional ISO 8601 date after which this memory should be reviewed for staleness.")] string? reviewAfter = null,
        [Description("Optional free-text condition describing when this memory should be reviewed (e.g. 'when auth system changes').")] string? reviewWhen = null,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;
        string joined = string.Concat(content);

        // Compute text analysis: keywords + term frequencies (single-pass)
        var (autoKeywords, tf) = TextAnalysis.AnalyzeText(joined);
        var (mergedKeywords, agentKeywordSet) = TextAnalysis.MergeKeywordsWithSource(keywords, autoKeywords);

        // Boost keywords in TF: agent keywords +5, auto-extracted +2
        foreach (string kw in mergedKeywords)
        {
            tf.TryGetValue(kw, out int count);
            tf[kw] = count + (agentKeywordSet.Contains(kw) ? 5 : 2);
        }

        ChunkEntry[]? chunkEntries = content.Length > 1
            ? ComputeChunkEntries(store, content)
            : null;

        // ── Ephemeral path (~name) ───────────────────────────────────────
        if (store.IsEphemeral(name))
        {
            string key = MemoryNaming.StripEphemeralPrefix(name);
            string ephArtifact = content.Length == 1
                ? Nmp2ChunkedEncoder.Encode(content[0])
                : Nmp2ChunkedEncoder.EncodeChunks(content);
            int ephChunkCount = Nmp2ChunkedEncoder.GetChunkCount(ephArtifact);
            long ephBytes = System.Text.Encoding.UTF8.GetByteCount(joined);
            string ephPreview = store.GenerateContentPreview(joined);
            string ephDesc = string.IsNullOrWhiteSpace(description)
                ? joined[..Math.Min(200, joined.Length)]
                : description;

            // Check if updating existing ephemeral entry
            var existingEph = store.GetEphemeral(key);
            DateTimeOffset ephCreatedAt = existingEph?.CreatedAt ?? DateTimeOffset.UtcNow;
            DateTimeOffset? ephUpdatedAt = existingEph is not null ? DateTimeOffset.UtcNow : null;

            var ephEntry = new EphemeralEntry(
                Name: key,
                Artifact: ephArtifact,
                OriginalBytes: ephBytes,
                ChunkCount: ephChunkCount,
                CreatedAt: ephCreatedAt,
                Description: ephDesc,
                Tags: tags,
                ContentPreview: ephPreview,
                Keywords: mergedKeywords.Length > 0 ? mergedKeywords : null,
                TermFrequencies: tf.Count > 0 ? tf : null,
                UpdatedAt: ephUpdatedAt,
                ChunkEntries: chunkEntries);

            store.RememberEphemeral(key, ephEntry);

            // Fire event sink (embeddings, etc.) — never block the response
            var sink = MemoryEventSinkContext.Current;
            try { await (sink?.OnStoredAsync($"~{key}", content, store, cancellationToken) ?? Task.CompletedTask); }
            catch (Exception ex) { Console.Error.WriteLine($"[scrinia:warn] Event sink error: {ex.GetType().Name}: {ex.Message}"); }

            return $"Remembered: ~{key} ({ephChunkCount} {(ephChunkCount == 1 ? "chunk" : "chunks")}, {FormatBytes(ephBytes)}) [ephemeral]";
        }

        // ── Persistent path ──────────────────────────────────────────────
        var (scope, subject) = store.ParseQualifiedName(name);

        // Check if entry already exists (for versioning + UpdatedAt)
        var existingEntries = store.LoadIndex(scope);
        var existingEntry = existingEntries.FirstOrDefault(e => e.Name == subject);
        DateTimeOffset createdAt = existingEntry?.CreatedAt ?? DateTimeOffset.UtcNow;
        DateTimeOffset? updatedAt = existingEntry is not null ? DateTimeOffset.UtcNow : null;

        // Archive previous version before overwriting
        if (existingEntry is not null)
            store.ArchiveVersion(subject, scope);

        string artifact = content.Length == 1
            ? Nmp2ChunkedEncoder.Encode(content[0])
            : Nmp2ChunkedEncoder.EncodeChunks(content);

        await store.WriteArtifactAsync(subject, scope, artifact, cancellationToken);

        string uri = store.ArtifactUri(subject, scope);
        string desc = string.IsNullOrWhiteSpace(description)
            ? joined[..Math.Min(200, joined.Length)]
            : description;

        int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(artifact);
        long originalBytes = System.Text.Encoding.UTF8.GetByteCount(joined);
        string contentPreview = store.GenerateContentPreview(joined);
        string qualifiedName = store.FormatQualifiedName(scope, subject);

        // Parse reviewAfter
        DateTimeOffset? parsedReviewAfter = null;
        if (!string.IsNullOrWhiteSpace(reviewAfter) && DateTimeOffset.TryParse(reviewAfter, out var ra))
            parsedReviewAfter = ra;

        var entry = new ArtifactEntry(
            Name: subject,
            Uri: uri,
            OriginalBytes: originalBytes,
            ChunkCount: chunkCount,
            CreatedAt: createdAt,
            Description: desc,
            Tags: tags,
            ContentPreview: contentPreview,
            Keywords: mergedKeywords.Length > 0 ? mergedKeywords : null,
            TermFrequencies: tf.Count > 0 ? tf : null,
            UpdatedAt: updatedAt,
            ReviewAfter: parsedReviewAfter,
            ReviewWhen: string.IsNullOrWhiteSpace(reviewWhen) ? null : reviewWhen,
            ChunkEntries: chunkEntries);

        store.Upsert(entry, scope);

        // Fire event sink (embeddings, etc.) — never block the response
        try { await (MemoryEventSinkContext.Current?.OnStoredAsync(qualifiedName, content, store, cancellationToken) ?? Task.CompletedTask); }
        catch (Exception ex) { Console.Error.WriteLine($"[scrinia:warn] Event sink error: {ex.GetType().Name}: {ex.Message}"); }

        return $"Remembered: {qualifiedName} ({chunkCount} {(chunkCount == 1 ? "chunk" : "chunks")}, {FormatBytes(originalBytes)}). Files in .scrinia/ were updated — these are your changes.";
    }

    [McpServerTool(Name = "list"), Description(
        "Returns a summary or full listing of persisted memories. " +
        "Call this when starting a session to orient on available project knowledge. " +
        "Default mode is 'summary' — returns topics, top keywords, and stats without flooding context. " +
        "Use mode='full' with offset/limit to page through entries.")]
    public Task<string> List(
        [Description("Optional comma-separated scope order, e.g. local,api,ephemeral. " +
                     "Topic names filter to local topics (e.g. 'api' shows api topic entries).")] string? scopes = null,
        [Description("'summary' (default) returns topics, top keywords, and stats. " +
                     "'full' returns a paginated table of all entries.")] string mode = "summary",
        [Description("Starting index for full mode (0-based). Ignored in summary mode.")] int offset = 0,
        [Description("Maximum entries to return in full mode (default 50). Ignored in summary mode.")] int limit = 50,
        [Description("Optional comma-separated topic names to exclude from results. " +
                     "Use 'plan,task,project,learn' to hide planning namespaces from knowledge listings.")] string? excludeTopics = null,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;
        List<ScopedArtifact> entries = store.ListScoped(scopes, excludeTopics);
        if (entries.Count == 0)
            return Task.FromResult("No memories stored.");

        entries.Sort((a, b) => b.Entry.CreatedAt.CompareTo(a.Entry.CreatedAt));

        if (!string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(BuildSummary(entries, store));

        return Task.FromResult(BuildFullList(entries, store, offset, limit));
    }

    private static string BuildSummary(List<ScopedArtifact> entries, IMemoryStore store)
    {
        long totalBytes = entries.Sum(e => e.Entry.OriginalBytes);
        int totalTokens = (int)(totalBytes / 4);
        int staleCount = entries.Count(e => e.Entry.ReviewAfter.HasValue && e.Entry.ReviewAfter.Value <= DateTimeOffset.UtcNow);
        int reviewCount = entries.Count(e => !string.IsNullOrEmpty(e.Entry.ReviewWhen)
            && !(e.Entry.ReviewAfter.HasValue && e.Entry.ReviewAfter.Value <= DateTimeOffset.UtcNow));
        int ephemeralCount = entries.Count(e => e.Scope == "ephemeral");

        // Group by scope
        var grouped = entries
            .Where(e => e.Scope != "ephemeral")
            .GroupBy(e => MemoryNaming.FormatScopeLabel(e.Scope))
            .OrderBy(g => g.Key)
            .ToList();

        int topicCount = grouped.Count(g => g.Key != "local");

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"## Memory Summary");
        sb.AppendLine($"**{entries.Count} memories** — {FormatBytes(totalBytes)} (~{totalTokens:N0} tokens)");
        if (topicCount > 0 || ephemeralCount > 0 || staleCount > 0 || reviewCount > 0)
        {
            var parts = new List<string>();
            if (topicCount > 0) parts.Add($"{topicCount} topic{(topicCount == 1 ? "" : "s")}");
            if (ephemeralCount > 0) parts.Add($"{ephemeralCount} ephemeral");
            if (staleCount > 0) parts.Add($"{staleCount} stale");
            if (reviewCount > 0) parts.Add($"{reviewCount} need review");
            sb.AppendLine(string.Join(" · ", parts));
        }
        sb.AppendLine();

        // Topics with entry counts and total size
        sb.AppendLine("### Scopes");
        foreach (var group in grouped)
        {
            string label = group.Key == "local" ? "local" : $"topic:{group.Key}";
            long groupBytes = group.Sum(e => e.Entry.OriginalBytes);
            sb.AppendLine($"- **{label}** — {group.Count()} {(group.Count() == 1 ? "memory" : "memories")}, {FormatBytes(groupBytes)}");
        }
        if (ephemeralCount > 0)
            sb.AppendLine($"- **ephemeral** — {ephemeralCount} {(ephemeralCount == 1 ? "memory" : "memories")}");
        sb.AppendLine();

        // Top keywords — aggregate from Keywords and Tags across all entries
        var keywordCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in entries)
        {
            if (item.Entry.Keywords is { Length: > 0 })
                foreach (var kw in item.Entry.Keywords)
                    keywordCounts[kw] = keywordCounts.GetValueOrDefault(kw) + 1;
            if (item.Entry.Tags is { Length: > 0 })
                foreach (var tag in item.Entry.Tags)
                    keywordCounts[tag] = keywordCounts.GetValueOrDefault(tag) + 1;
        }
        if (keywordCounts.Count > 0)
        {
            var topKeywords = keywordCounts
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .Select(kv => kv.Key);
            sb.AppendLine($"### Top keywords");
            sb.AppendLine(string.Join(", ", topKeywords));
            sb.AppendLine();
        }

        sb.Append("Use `list(mode=\"full\")` to see all entries, or `search(\"query\")` to find specific memories.");
        return sb.ToString();
    }

    private static string BuildFullList(List<ScopedArtifact> entries, IMemoryStore store, int offset, int limit)
    {
        int total = entries.Count;
        if (offset < 0) offset = 0;
        if (limit < 1) limit = 50;
        var page = entries.Skip(offset).Take(limit).ToList();

        // Build qualified names first to compute dynamic column width (never truncate names)
        var rows = new List<(string Name, ArtifactEntry Entry)>(page.Count);
        int nameW = 4; // min width = "name".Length
        foreach (var item in page)
        {
            var e = item.Entry;
            string qualifiedName = item.Scope == "ephemeral"
                ? $"~{e.Name}"
                : store.FormatQualifiedName(item.Scope, e.Name);
            rows.Add((qualifiedName, e));
            if (qualifiedName.Length > nameW) nameW = qualifiedName.Length;
        }

        const int chunkW = 7;
        const int bytesW = 10;
        const int tokensW = 8;
        const int dateW = 17;

        var sb = new System.Text.StringBuilder();

        // Pagination header
        int showing = offset + 1;
        int showingEnd = offset + page.Count;
        sb.AppendLine($"Showing {showing}-{showingEnd} of {total} memories.");
        sb.AppendLine();

        sb.AppendLine(
            $"{"name".PadRight(nameW)}  {"chunks",chunkW}  {"bytes",bytesW}  {"~tokens",tokensW}  {"created",dateW}  description");
        sb.AppendLine(new string('-', nameW + chunkW + bytesW + tokensW + dateW + 18));

        foreach (var (qualifiedName, e) in rows)
        {
            string sizeStr = FormatBytes(e.OriginalBytes);
            int estTokens = (int)(e.OriginalBytes / 4);
            string dateStr = e.CreatedAt.ToString("yyyy-MM-dd HH:mm");

            // Review markers
            string reviewPrefix = "";
            if (e.ReviewAfter.HasValue && e.ReviewAfter.Value <= DateTimeOffset.UtcNow)
                reviewPrefix = "[stale] ";
            else if (!string.IsNullOrEmpty(e.ReviewWhen))
                reviewPrefix = "[review?] ";

            string desc = e.Description;
            desc = desc.Replace('\n', ' ').Replace('\r', ' ');
            string fullDesc = reviewPrefix + desc;
            if (fullDesc.Length > 60) fullDesc = fullDesc[..57] + "...";

            sb.AppendLine(
                $"{qualifiedName.PadRight(nameW)}  {e.ChunkCount,chunkW}  {sizeStr,bytesW}  {estTokens,tokensW}  {dateStr,-dateW}  {fullDesc}");
        }

        if (showingEnd < total)
            sb.AppendLine($"\nUse list(mode=\"full\", offset={showingEnd}) for more.");

        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "search"), Description(
        "Search this first before starting research or problem-solving — " +
        "relevant knowledge may already exist from prior sessions. " +
        "Finds memories across local and topic scopes using a name/description query. " +
        "Searches both entries and topics.")]
    public async Task<string> Search(
        [Description("Search term matched against memory names and descriptions.")] string query,
        [Description("Optional comma-separated scope order, e.g. local,api,ephemeral. " +
                     "Topic names filter to local topics (e.g. 'api' shows api topic entries).")] string? scopes = null,
        [Description("Maximum results to return.")] int limit = 20,
        [Description("Optional comma-separated topic names to exclude from results. " +
                     "Use 'plan,task,project,learn' to hide planning namespaces from knowledge searches.")] string? excludeTopics = null,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // Compute supplemental scores from plugin (e.g. embeddings) if available
        // Use excludeTopics-filtered candidates so excluded topics don't influence embeddings scoring
        var contributor = SearchContributorContext.Current;
        IReadOnlyDictionary<string, double>? supplemental = null;
        if (contributor is not null)
        {
            var candidates = store.ListScoped(scopes, excludeTopics);
            supplemental = await contributor.ComputeScoresAsync(query, candidates, store, cancellationToken);
        }

        IReadOnlyList<SearchResult> matches = supplemental is { Count: > 0 }
            ? store.SearchAll(query, scopes, limit, supplemental)
                .Where(r => !IMemoryStore.ShouldExcludeScope(IMemoryStore.GetResultScope(r), excludeTopics))
                .ToList()
            : store.SearchAll(query, scopes, limit, excludeTopics);
        if (matches.Count == 0)
            return "No matching memories found.";

        // Build qualified names first to compute dynamic column width (never truncate names)
        const int typeW = 6;
        const int scoreW = 6;
        const int tokensW = 8;
        var rows = new List<(string Type, string Name, double Score, string TokensStr, string Desc)>(matches.Count);
        int nameW = 4; // min width = "name".Length
        foreach (var match in matches)
        {
            if (match is ChunkEntryResult cr)
            {
                string qualifiedName = cr.ParentItem.Scope == "ephemeral"
                    ? $"~{cr.ParentItem.Entry.Name}"
                    : store.FormatQualifiedName(cr.ParentItem.Scope, cr.ParentItem.Entry.Name);
                string chunkLabel = $"{qualifiedName} [chunk {cr.Chunk.ChunkIndex}/{cr.TotalChunks}]";
                string desc = cr.Chunk.ContentPreview ?? cr.ParentItem.Entry.Description;
                desc = desc.Replace('\n', ' ').Replace('\r', ' ');
                if (desc.Length > 60) desc = desc[..57] + "...";
                int estTokens = (int)(cr.ParentItem.Entry.OriginalBytes / cr.TotalChunks / 4);
                rows.Add(("chunk", chunkLabel, cr.Score, estTokens.ToString(), desc));
                if (chunkLabel.Length > nameW) nameW = chunkLabel.Length;
            }
            else if (match is EntryResult er)
            {
                string qualifiedName = er.Item.Scope == "ephemeral"
                    ? $"~{er.Item.Entry.Name}"
                    : store.FormatQualifiedName(er.Item.Scope, er.Item.Entry.Name);
                string desc = er.Item.Entry.Description.Replace('\n', ' ').Replace('\r', ' ');
                if (desc.Length > 60) desc = desc[..57] + "...";
                int estTokens = (int)(er.Item.Entry.OriginalBytes / 4);
                rows.Add(("entry", qualifiedName, er.Score, estTokens.ToString(), desc));
                if (qualifiedName.Length > nameW) nameW = qualifiedName.Length;
            }
            else if (match is TopicResult tr)
            {
                string trLabel = MemoryNaming.FormatScopeLabel(tr.Scope);
                string desc = tr.Description.Replace('\n', ' ').Replace('\r', ' ');
                if (desc.Length > 60) desc = desc[..57] + "...";
                rows.Add(("topic", trLabel, tr.Score, "", desc));
                if (trLabel.Length > nameW) nameW = trLabel.Length;
            }
        }

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{"type",-typeW}  {"name".PadRight(nameW)}  {"score",scoreW}  {"~tokens",tokensW}  description");
        sb.AppendLine(new string('-', typeW + nameW + scoreW + tokensW + 17));

        foreach (var (type, name, score, tokensStr, desc) in rows)
        {
            sb.AppendLine($"{type,-typeW}  {name.PadRight(nameW)}  {score,scoreW:F0}  {tokensStr,tokensW}  {desc}");
        }

        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "copy"), Description(
        "Copies a memory artifact from one scope to another. " +
        "Use to move between topics, promote ephemeral to persistent, " +
        "or reorganize project knowledge.")]
    public Task<string> Copy(
        [Description("Memory name or file:// URI to copy.")] string nameOrUri,
        [Description("Destination as qualified name (e.g. 'api:auth-flow' or 'my-notes'). " +
                     "Use '~name' for ephemeral destination.")] string destination,
        [Description("When true, replaces destination memory if it already exists.")] bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        bool ok = CurrentStore.CopyMemory(nameOrUri, destination, overwrite, out string msg);
        if (!ok) return Task.FromResult(msg);
        return Task.FromResult(msg);
    }

    [McpServerTool(Name = "forget"), Description(
        "Removes a stored artifact and its index entry. " +
        "Use to clean up outdated or incorrect memories. " +
        "Accepts a qualified name (e.g. 'session-notes', 'api:auth-flow', '~scratch'). " +
        "Note: this modifies .scrinia/ in the workspace — treat those file changes as your own.")]
    public async Task<string> Forget(
        [Description("The artifact name (e.g. \"session-notes\", \"api:auth\", \"~scratch\") or its file:// URI.")] string nameOrUri,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // Ephemeral memory (~name)
        if (store.IsEphemeral(nameOrUri))
        {
            string key = MemoryNaming.StripEphemeralPrefix(nameOrUri);
            if (!store.ForgetEphemeral(key))
                return $"Error: no ephemeral memory found with name '~{key}'.";

            try { await (MemoryEventSinkContext.Current?.OnForgottenAsync($"~{key}", true, store, cancellationToken) ?? Task.CompletedTask); }
            catch { /* plugin errors must not block forget */ }

            return $"Forgot: ~{key}";
        }

        // Backward compat: resolve file:// URIs to their memory name, then delete by name
        if (nameOrUri.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            string name = FileMemoryStore.NameFromUri(nameOrUri);

            bool removedAny = false;
            foreach (string s in store.ResolveReadScopes())
            {
                store.DeleteArtifact(name, s);
                removedAny |= store.Remove(name, s);
            }

            if (!removedAny)
                return $"Error: no artifact found with name or URI '{nameOrUri}'.";

            try { await (MemoryEventSinkContext.Current?.OnForgottenAsync(name, removedAny, store, cancellationToken) ?? Task.CompletedTask); }
            catch { /* plugin errors must not block forget */ }

            return $"Forgot: {name}. Files in .scrinia/ were updated — these are your changes.";
        }

        var (scope, subject) = store.ParseQualifiedName(nameOrUri);
        string qualifiedName = store.FormatQualifiedName(scope, subject);

        // Delete the artifact file
        bool deleted = store.DeleteArtifact(subject, scope);

        // Remove index entry
        bool removed = store.Remove(subject, scope);
        if (!removed && !deleted)
            return $"Error: no artifact found with name '{nameOrUri}'.";

        try { await (MemoryEventSinkContext.Current?.OnForgottenAsync(qualifiedName, deleted || removed, store, cancellationToken) ?? Task.CompletedTask); }
        catch { /* plugin errors must not block forget */ }

        return $"Forgot: {qualifiedName}. Files in .scrinia/ were updated — these are your changes.";
    }

    // ── Export/Import tools ───────────────────────────────────────────────────

    [McpServerTool(Name = "export"), Description(
        "Export one or more local topics into a portable .scrinia-bundle file. " +
        "Use to share project knowledge across workspaces or with teammates. " +
        "The bundle contains all entries from the specified topics.")]
    public Task<string> Export(
        [Description("Topic names to export (e.g. [\"api\", \"arch\"]).")] string[] topics,
        [Description("Output filename (saved to .scrinia/exports/). Defaults to auto-generated name.")] string? filename = null,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;
        if (topics is null || topics.Length == 0)
            return Task.FromResult("Error: at least one topic name is required.");

        string exportsDir = Path.Combine(store.GetStoreDirForScope("local"), "..", "exports");
        exportsDir = Path.GetFullPath(exportsDir);
        Directory.CreateDirectory(exportsDir);

        string bundleName = string.IsNullOrWhiteSpace(filename)
            ? $"export-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}"
            : filename;
        if (!bundleName.EndsWith(".scrinia-bundle", StringComparison.OrdinalIgnoreCase))
            bundleName += ".scrinia-bundle";

        // Sanitize filename: strip control characters and path separators
        bundleName = new string(bundleName.Where(c => !char.IsControl(c) && c != '/' && c != '\\').ToArray());
        bundleName = Path.GetFileName(bundleName);

        string bundlePath = Path.Combine(exportsDir, bundleName);

        List<string> exportedTopics;
        int totalEntries;

        using (var stream = new FileStream(bundlePath, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            (exportedTopics, totalEntries) = Scrinia.Core.Bundles.BundleFormatService.ExportTopicsToZip(zip, store, topics);

            if (exportedTopics.Count == 0)
            {
                try { File.Delete(bundlePath); } catch { }
                return Task.FromResult("Error: no entries found in the specified topics.");
            }
        }

        long fileSize = new FileInfo(bundlePath).Length;
        return Task.FromResult(
            $"Exported {exportedTopics.Count} topic(s) ({totalEntries} entries, {FormatBytes(fileSize)}) to {bundlePath}");
    }

    [McpServerTool(Name = "import"), Description(
        "Import topics from a .scrinia-bundle file into the local workspace. " +
        "Use to bring in shared knowledge from other projects or teammates. " +
        "Optionally filter which topics to import.")]
    public Task<string> Import(
        [Description("Path to the .scrinia-bundle file (relative to workspace or absolute).")] string bundlePath,
        [Description("Optional topic names to import. If empty, imports all topics in the bundle.")] string[]? topics = null,
        [Description("When true, replaces existing entries if they conflict.")] bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        // Resolve path relative to workspace root if not absolute
        string resolvedPath = Path.IsPathRooted(bundlePath)
            ? bundlePath
            : Path.Combine(Path.GetDirectoryName(store.GetStoreDirForScope("local"))!, "..", bundlePath);
        resolvedPath = Path.GetFullPath(resolvedPath);

        if (!File.Exists(resolvedPath))
            return Task.FromResult($"Error: bundle file not found: {resolvedPath}");

        try
        {
            using var stream = new FileStream(resolvedPath, FileMode.Open, FileAccess.Read);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);

            var (topicCount, entryCount, names) =
                Scrinia.Core.Bundles.BundleFormatService.ImportTopicsFromZip(zip, store, topics, overwrite);

            if (topicCount == 0)
                return Task.FromResult("No topics were imported (empty bundle or all filtered out).");

            return Task.FromResult(
                $"Imported {topicCount} topic(s) ({entryCount} entries): {string.Join(", ", names)}");
        }
        catch (InvalidOperationException ex)
        {
            return Task.FromResult($"Error: {ex.Message}");
        }
    }

    // ── Append/Reflect/Budget tools ─────────────────────────────────────────

    [McpServerTool(Name = "append"), Description(
        "Append content as a new independently retrievable chunk to an existing memory, " +
        "or create it if it does not exist. " +
        "Useful for incremental capture — build up session journals entry by entry " +
        "without recomposing the full document each time. " +
        "Note: this writes to .scrinia/ in the workspace — treat those file changes as your own.")]
    public async Task<string> Append(
        [Description("The text content to append.")] string content,
        [Description("Memory name to append to (e.g. 'session-notes', 'api:auth-flow', '~scratch').")] string name,
        CancellationToken cancellationToken = default)
    {
        var store = CurrentStore;

        string? existingArtifact = null;
        try
        {
            existingArtifact = await store.ResolveArtifactAsync(name, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            // Will create new
        }

        if (existingArtifact is null)
        {
            // Non-existent → create as single-chunk (same as Store)
            return await this.Store([content], name, cancellationToken: cancellationToken);
        }

        // Append as new chunk
        string newArtifact = Nmp2ChunkedEncoder.AppendChunk(existingArtifact, content);

        // Decode full result for metadata
        byte[] fullBytes = new Nmp2Strategy().Decode(newArtifact);
        string fullText = System.Text.Encoding.UTF8.GetString(fullBytes);
        int chunkCount = Nmp2ChunkedEncoder.GetChunkCount(newArtifact);
        long originalBytes = fullBytes.LongLength;

        // Compute text analysis from full decoded content (single-pass)
        var (autoKeywords, tf) = TextAnalysis.AnalyzeText(fullText);
        var mergedKeywords = TextAnalysis.MergeKeywords(null, autoKeywords);
        foreach (string kw in mergedKeywords)
        {
            tf.TryGetValue(kw, out int count);
            tf[kw] = count + 2;
        }

        string contentPreview = store.GenerateContentPreview(fullText);

        // Build chunk entry for the newly appended content (single-pass)
        var (newKw, newTf) = TextAnalysis.AnalyzeText(content);
        foreach (string k in newKw) { newTf.TryGetValue(k, out int c); newTf[k] = c + 2; }
        var newChunkEntry = new ChunkEntry(
            ChunkIndex: chunkCount,
            ContentPreview: store.GenerateContentPreview(content),
            Keywords: newKw.Length > 0 ? newKw : null,
            TermFrequencies: newTf.Count > 0 ? newTf : null);

        string qualifiedName;

        if (store.IsEphemeral(name))
        {
            string key = MemoryNaming.StripEphemeralPrefix(name);
            var existingEph = store.GetEphemeral(key);
            DateTimeOffset createdAt = existingEph?.CreatedAt ?? DateTimeOffset.UtcNow;

            ChunkEntry[]? existingChunks = existingEph?.ChunkEntries;
            ChunkEntry[] updatedChunks = existingChunks is not null
                ? [.. existingChunks, newChunkEntry]
                : [newChunkEntry];

            var ephEntry = new EphemeralEntry(
                Name: key,
                Artifact: newArtifact,
                OriginalBytes: originalBytes,
                ChunkCount: chunkCount,
                CreatedAt: createdAt,
                Description: fullText[..Math.Min(200, fullText.Length)],
                Tags: null,
                ContentPreview: contentPreview,
                Keywords: mergedKeywords.Length > 0 ? mergedKeywords : null,
                TermFrequencies: tf.Count > 0 ? tf : null,
                UpdatedAt: DateTimeOffset.UtcNow,
                ChunkEntries: updatedChunks);

            store.RememberEphemeral(key, ephEntry);
            qualifiedName = $"~{key}";
        }
        else
        {
            var (scope, subject) = store.ParseQualifiedName(name);

            // Check existing entry for versioning + timestamps
            var existingEntries = store.LoadIndex(scope);
            var existingEntry = existingEntries.FirstOrDefault(e => e.Name == subject);
            DateTimeOffset createdAt = existingEntry?.CreatedAt ?? DateTimeOffset.UtcNow;

            ChunkEntry[]? existingChunks = existingEntry?.ChunkEntries;
            ChunkEntry[] updatedChunks = existingChunks is not null
                ? [.. existingChunks, newChunkEntry]
                : [newChunkEntry];

            // Archive previous version
            if (existingEntry is not null)
                store.ArchiveVersion(subject, scope);

            await store.WriteArtifactAsync(subject, scope, newArtifact, cancellationToken);

            string uri = store.ArtifactUri(subject, scope);
            qualifiedName = store.FormatQualifiedName(scope, subject);

            var entry = new ArtifactEntry(
                Name: subject,
                Uri: uri,
                OriginalBytes: originalBytes,
                ChunkCount: chunkCount,
                CreatedAt: createdAt,
                Description: fullText[..Math.Min(200, fullText.Length)],
                Tags: null,
                ContentPreview: contentPreview,
                Keywords: mergedKeywords.Length > 0 ? mergedKeywords : null,
                TermFrequencies: tf.Count > 0 ? tf : null,
                UpdatedAt: DateTimeOffset.UtcNow,
                ReviewAfter: existingEntry?.ReviewAfter,
                ReviewWhen: existingEntry?.ReviewWhen,
                ChunkEntries: updatedChunks);

            store.Upsert(entry, scope);
        }

        // Fire event sink (embeddings, etc.) — never block the response
        try { await (MemoryEventSinkContext.Current?.OnAppendedAsync(qualifiedName, content, store, cancellationToken) ?? Task.CompletedTask); }
        catch { /* plugin errors must not block append */ }

        return $"Appended chunk {chunkCount} to {qualifiedName} ({chunkCount} {(chunkCount == 1 ? "chunk" : "chunks")}, {FormatBytes(originalBytes)}). Files in .scrinia/ were updated — these are your changes.";
    }

    // kt removed — knowledge transfer is a learnable goal, not a fixed tool.
    // The agent should treat "produce KT documents" as a goal, execute it, retrospect, and save a skill.

    private static ChunkEntry[] ComputeChunkEntries(IMemoryStore store, string[] chunks)
    {
        var entries = new ChunkEntry[chunks.Length];
        for (int i = 0; i < chunks.Length; i++)
        {
            var (kw, tf) = TextAnalysis.AnalyzeText(chunks[i]);
            foreach (string k in kw) { tf.TryGetValue(k, out int c); tf[k] = c + 2; }
            string preview = store.GenerateContentPreview(chunks[i]);
            entries[i] = new ChunkEntry(
                ChunkIndex: i + 1,
                ContentPreview: string.IsNullOrEmpty(preview) ? null : preview,
                Keywords: kw.Length > 0 ? kw : null,
                TermFrequencies: tf.Count > 0 ? tf : null);
        }
        return entries;
    }

    public static string FormatBytes(long bytes) =>
        bytes switch
        {
            < 1_024 => $"{bytes} B",
            < 1_048_576 => $"{bytes / 1_024.0:F1} KB",
            < 1_073_741_824 => $"{bytes / 1_048_576.0:F1} MB",
            _ => $"{bytes / 1_073_741_824.0:F1} GB",
        };
}
