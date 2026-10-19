# UX Polish Review — Release documentation and embedded guide

**Date:** 2026-10-10
**Scope:** Task 13 changed living copy in `AGENTS.md`, `CHANGELOG.md` (Unreleased additions only), `NMP_SPEC.md`, `README.md`, `docs/{installation,plugin-authoring,getting-started,cli-reference,troubleshooting,security}.md`, `docs/architecture/{overview,cli,core,embeddings}.md`, `src/Scrinia/package-readme.md`, and `src/Scrinia.Mcp/prompts/guide.md`. Reviewed the focused living-copy diff and immediate surrounding copy; historical reports and dated material are excluded.
**Reviewer:** ux-polish-reviewer agent

The copy gives useful installation and recovery distinctions without hype; two correctness issues and three clarity refinements remain.

## Must fix

### 1. Make the extracted Windows command work in PowerShell

**Location:** `README.md:130`, `docs/installation.md:99`, `docs/getting-started.md:77`, `docs/cli-reference.md:436`, `docs/plugin-authoring.md:27`, `src/Scrinia/package-readme.md:73`.

**Issue:** The Windows instruction omits PowerShell's current-directory prefix. A freshly extracted executable is not normally on PATH, so `scri.exe setup` can fail in the user's PowerShell session. Specify the extracted directory and use the same executable form consistently across all six surfaces. The Task 13 brief's suggested wording contains the same omission.

**Before:**
> Extract the embeddings archive for your operating system, then run `./scri setup` (`scri.exe setup` on Windows).

**After:**
> Extract the embeddings archive for your operating system. From the extracted directory, run `./scri setup` on Linux or macOS, or `.\scri.exe setup` in PowerShell on Windows.

Keep the separate NuGet instruction, `scri setup --embeddings-source <extracted-release-directory>`.

### 2. Distinguish embedded built-ins from project override files

**Location:** `src/Scrinia.Mcp/prompts/guide.md:206`.

**Issue:** The edited built-in list retains a false location claim: "They live at `.scrinia/skills/{name}.md`." Built-ins load from embedded resources; that disk location holds project overrides. An agent following this guide can incorrectly look for or assume the existence of those files. Verified against `EmbeddedPrompts.LoadAllSkills()` in `src/Scrinia.Mcp/EmbeddedPrompts.cs` and the disk/built-in lookup in `src/Scrinia.Mcp/MemoryTools.Skills.cs:692`.

**Before:**
> They live at `.scrinia/skills/{name}.md`; a project override takes precedence. Merge one against an updated built-in with `recall({ path: '/skill/{name}', withBuiltin: true })`.

**After:**
> Built-ins are embedded in Scrinia. Project overrides live at `.scrinia/skills/{name}.md` and take precedence. To compare an override with the current built-in before merging, use `recall({ path: '/skill/{name}', withBuiltin: true })`.

## Should fix

### 3. Explain the recovery condition in terms of affected files

**Location:** `docs/troubleshooting.md:238` and `docs/troubleshooting.md:268`.

**Issue:** "Members," "proven member states," and "prepared decision" require knowledge of the journal implementation in a troubleshooting flow. Describe the same condition using files and recorded states. The technical terminology can remain in the architecture reference.

**Before:**
> A prepared decision accepts only current members proven to match its recorded states before restoring prior bytes.

**After:**
> For a prepared operation, recovery restores the previous file contents only when each affected file matches a state recorded in the journal.

**Before:**
> Prepared recovery accepts only proven member states.

**After:**
> Prepared recovery proceeds only when each affected file matches a state recorded in the journal.

### 4. Give the setup failure instructions natural, actionable phrasing

**Location:** `docs/troubleshooting.md:119`.

**Issue:** "Correct network/download access" and "its separately quoted directory" are compressed implementation phrasing. The user needs to resolve the reported error and supply two distinct paths. Preserve that distinction with direct instructions.

**Before:**
> For model failure, correct network/download access. Then rerun the printed `scri setup --embeddings-source <embeddings-source> --workspace-root <workspace-root>` command, replacing each placeholder with its separately quoted directory.

**After:**
> For a model preparation failure, restore network access or resolve the reported download error. Then rerun the printed `scri setup --embeddings-source <embeddings-source> --workspace-root <workspace-root>` command. Replace each placeholder with the corresponding directory path and quote each path separately.

## Consider

### 5. Replace "genuine legacy" with the location that defines the case

**Location:** `docs/installation.md:121`, `docs/cli-reference.md:447`, `docs/plugin-authoring.md:40`, `docs/troubleshooting.md:127`.

**Issue:** "Genuine" does not help the reader identify the older installation. The nearby architecture copy already uses "executable-relative," which names the distinguishing location. Prefer one plain explanation and shorten repeated reminders.

**Before:**
> Genuine executable-relative legacy asset migration is a separate setup step; it is not a way to install the immutable archive.

**After:**
> Setup migrates legacy assets stored beside the executable in a separate step. Release archives use the bundle installation described above.

For the shorter reminders:

**Before:**
> Genuine legacy asset migration is a separate step.

**After:**
> Migration of legacy assets stored beside the executable is a separate step.

## Consistency notes

- Use `Scrinia` for the product, `scrinia` for the NuGet package, and `scri` for the command. Retain canonical identifiers and error codes verbatim.
- Call the optional component the "embeddings plugin"; qualify Vulkan for Windows/Linux and CPU/Metal for macOS.
- Use `./scri` for an extracted Linux/macOS executable, `.\scri.exe` for an extracted Windows executable in PowerShell, and `scri` for the installed global tool.
- Keep confirmed installation, configuration failure, model preparation failure, and uncertain destination distinct. The revised copy already preserves those boundaries well.
- Preserve the explicit instructions to stop older writers, back up the workspace and journals, and retain ambiguous evidence. Avoid turning those instructions into claims of successful recovery.
- Built-in skill names are consistently reduced to the approved seven. Describe disk files as project overrides rather than the storage location of embedded built-ins.

**Finding counts:** 2 Must fix, 2 Should fix, 1 Consider. No copy was edited, tests run, or Git state changed by this review. Broader factual correctness is handled by the separate documentation review.
