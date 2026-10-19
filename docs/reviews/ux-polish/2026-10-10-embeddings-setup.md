# UX Polish Review — Embeddings bundle setup

**Date:** 2026-10-10
**Scope:** Fresh setup copy in `src/Scrinia/Commands/ScriniaCommands.Setup.cs` and new `src/Scrinia/Commands/EmbeddingsBundleSetup.cs`, against base `bf414c2e77acb1cc6e8e149bea08192eec623191` and the Task 12 brief. Reviewed source-directory help, option conflicts, failure stages, retained-installation and retry guidance, installation/selection output, and model/Ollama completion. Immediate installer, provider-resolution, configuration-precedence, and quoting control flow was read only to verify these claims. Implementation was still being finalized; line numbers describe the reviewed snapshot.
**Reviewer:** ux-polish-reviewer agent

The new copy is calm and concise, but retention, activation, and retry claims need correction before shipping: **3 Must fix, 2 Should fix, 0 Consider**.

## Must fix

### 1. A destination does not prove that a payload was installed

**Location:** `EmbeddingsBundleSetup.cs:57–58`, `ReportFailure`.

**Issue:** The retained-installation line is emitted for every non-null destination. The installer also supplies a destination for `ExistingContentConflict` and for failures after the destination was calculated but before publication was established. These paths can contain conflicting content or no installed payload. Calling them an installed payload contradicts the installer's more cautious diagnostic.

**Before:** `Installed payload retained at {destination}`

**After, for confirmed post-install configuration/model failures:** `Installed embeddings payload retained at {destination}.`

**After, for an installation failure or content conflict:** `Installation destination: {destination}`

Keep the installer's specific explanation of uncertain final/staging paths. Gate the retained-installation claim on a confirmed completed installation, rather than the presence of a destination string.

### 2. “Activated” exceeds the state that setup verifies

**Location:** `ScriniaCommands.Setup.cs:95` and `:146`; provider/Ollama completion context at `:120–130`.

**Issue:** The installation message follows a configuration write and executable-path resolution. It does not establish that the plugin was initialized or used. The final local message repeats the activation claim, although an effective HTTP provider supplied through the environment can still take priority at runtime. The Ollama branch explicitly selects Ollama and bypasses the plugin. Task 12 requires success output to describe installation and selection without implying GPU inference ran.

**Before:** `Embeddings {version} ({rid}) installed at {destination}; workspace selection activated.`

**After:** `Embeddings plugin {version} ({rid}) installed at {destination}; selected in workspace configuration.`

**Before:** `Setup complete: embeddings payload activated and local models prepared.`

**After, when effective provider selection is compatible and verified:** `Setup complete: embeddings plugin selected and local models prepared.`

**Ollama completion in the bundle-install branch:** `Setup complete: embeddings plugin installed and Ollama configured. Local model downloads skipped. Run scri config to inspect or adjust settings.`

This also needs a behavior check: incompatible higher-priority provider settings must be rejected before installation as required by Task 12, or the completion message must accurately name the effective provider. Copy alone cannot establish activation.

### 3. Retry guidance can substitute the workspace placeholder for the source directory

**Location:** `EmbeddingsBundleSetup.cs:59`, `ReportFailure`.

**Issue:** `PlatformQuoting.Current(source)` uses the Windows renderer, which returns the literal `<workspace-root>` for paths containing shell-sensitive characters such as `%`, `!`, or `&`. Reusing that renderer for the embeddings source produces the wrong placeholder and can make both arguments appear to refer to the workspace. The line labels this as a retry command without explaining the required substitution.

**Before, for an affected source:** `Retry: scri setup --embeddings-source <workspace-root> --workspace-root {quoted-workspace}`

**After:** `Retry after correcting the error: scri setup --embeddings-source <embeddings-source> --workspace-root {quoted-workspace}`

**Additional guidance when a placeholder is required:** `Replace <embeddings-source> with the bundle directory, quoted for your shell.`

If the workspace also requires a placeholder, name it separately and include it in the substitution guidance. Preserve quoting for paths that can be rendered directly; do not suggest an unquoted shell-sensitive path.

## Should fix

### 1. Selection-conflict guidance hides the exact setting and its source

**Location:** `EmbeddingsBundleSetup.cs:30`, `ValidateEmbeddingsSelection`.

**Issue:** “Its version” leaves the user to infer `plugins:embeddings:version`. “Effective” also obscures the environment precedence: changing workspace configuration will not resolve a conflicting environment override.

**Before:** `The effective embeddings plugin selection conflicts with this bundle. Adjust plugins:embeddings and its version, then retry setup.`

**After:** `The embeddings plugin settings conflict with this bundle. Check plugins:embeddings and plugins:embeddings:version, including environment overrides, then retry setup.`

If available, include the conflicting key and observed value. Keep the typed `SelectionConflict` status.

### 2. The model-preparation error can describe a failure before model preparation starts

**Location:** `ScriniaCommands.Setup.cs:149–152`, the post-install catch.

**Issue:** This catch surrounds workspace configuration, selection verification, legacy migration, Ollama setup, model preparation, and provider settings. A failure in the earlier stages is reported as `InstalledModelPreparationFailed` with text centered on preparing models. That diagnosis can send users toward model troubleshooting for a configuration failure.

**Before:** `The installed embeddings payload is retained, but setup did not finish preparing models or provider settings: {error}`

**After, if retaining one broad catch:** `The embeddings payload is installed, but setup did not finish: {error}`

**After, for an actual model-preparation failure:** `The embeddings payload is installed, but local model preparation failed: {error}`

Prefer stage-specific reporting: configuration failures should retain the configuration-failure status and wording; model failures should use the model-preparation status. This is a diagnostic-state issue to route to implementation, rather than solve by silently inventing a new state in the copy.

## Consider

None. The directory description for `--embeddings-source` and the hook/source conflict message are already clear. The installation line includes the required version, RID, and destination, and the completion lines avoid hype and decorative punctuation.

## Consistency notes

- Use sentence case and calm, direct statements. Complete diagnostic sentences end with a period; `Retry:` and `Installation destination:` are labels.
- Use “embeddings plugin” for the executable/plugin selection and “embeddings payload” for retained installation files. Use “local models” for downloaded model assets.
- Reserve “installed” for a confirmed installation, “selected” for verified configuration/path resolution, and runtime readiness claims for verified initialization.
- Keep exact command options, configuration keys, and typed failure statuses. Name `plugins:embeddings:version` explicitly.
- Keep diagnostics on stderr and retain platform quoting. Distinguish `<embeddings-source>` from `<workspace-root>` whenever a command needs substitution.

No implementation files were edited and no tests were rerun for this copy review.
