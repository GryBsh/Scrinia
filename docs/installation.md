# Installing the `scri` global tool

Scrinia ships as one immutable NuGet **global tool** named `scrinia`, whose single command is
`scri`. The same packaged command implements both server modes (`scri serve` and `scri shared
serve`) plus the configuration, tracking, and shared-base commands.

## Install

Install `scri` onto your global tool path:

```sh
dotnet tool install --global scrinia
```

Verify the install:

```sh
scri --version
```

## Update

Stop older `scri serve`, `scri shared serve`, and CLI writers before upgrading. Windows
configuration-lock filenames and workspace admission changed; mixed old/new writers cannot be
assumed to coordinate. Back up the entire workspace, including recovery journals, before reopening
it. See [recovery guidance](troubleshooting.md#bundle-recovery-required).

```sh
dotnet tool update --global scrinia
```

## Uninstall

```sh
dotnet tool uninstall --global scrinia
```

## Immutable tool package

The `scrinia` package is immutable once installed: after `dotnet tool install` the tool directory is
never written at runtime. The package contains only the managed `scri` command, its managed
dependencies, the offline-required embedded prompts/skills/schemas/setup templates, and the package
readme. It deliberately contains no Merge driver, no `scri-merge`, no Vulkan/LLamaSharp assembly, no
native RID asset, no downloaded model, no repository data, no `.scrinia`, and no mutable cache.

The per-user registry, models, installed plugin payloads, and plugin state live below
[`SCRINIA_HOME`](#where-data-lives). Workspace configuration, memories, and derived vectors/facts
live in `<workspace-root>/.scrinia/`.

## Where data lives

`SCRINIA_HOME` holds the per-user registry, models, installed plugin payloads, and plugin state. It
resolves deterministically: a non-empty absolute `SCRINIA_HOME` environment value is used exactly, an
empty or relative value fails closed, and when unset it defaults to
`<LocalApplicationData>\Scrinia`. Workspace memory stays in `<workspace-root>/.scrinia/`.

```
SCRINIA_HOME/
  config.json                 # per-user registry (shared bases, tracking)
  models/                     # Model2Vec and plugin-downloaded models
  plugins/
    <plugin>/
      <version>/
        <rid>/                # separately installed plugin executable, managed assemblies, native libraries
        state/                # mutable plugin runtime state
```

Models resolve to `SCRINIA_HOME/models`; separately installed plugins live only at the mandatory
`SCRINIA_HOME/plugins/<plugin>/<version>/<rid>/` hierarchy — there is no flat plugin fallback.

## Commands

Run `scri` for memory over local stdio, or `scri shared serve` for the disjoint shared-base MCP
surface:

```sh
scri serve        # serves <workspace-root>/.scrinia/ over local stdio
scri shared serve # serves the enabled shared bases over local stdio
```

## Models

Scrinia's default embedding provider is the in-process Model2Vec model (`m2v-MiniLM-L6-v2`), which
`scri serve` auto-downloads on first startup (or `scri setup` downloads explicitly) to
`SCRINIA_HOME/models/m2v-MiniLM-L6-v2/`. Plugin-downloaded models (for example the Vulkan GGUF model)
also live under `SCRINIA_HOME/models`. Models are mutable runtime state and never ship inside the
immutable package.

## Plugin location and installation

A plugin is a **separately installed**, RID-specific executable — it is not part of the immutable
`scri` global tool package. After installation its executable, managed assemblies, and native
libraries live only at the mandatory hierarchy `SCRINIA_HOME/plugins/<plugin>/<version>/<rid>/`, and
any mutable plugin state lives at `SCRINIA_HOME/plugins/<plugin>/<version>/<rid>/state/`. The `scri`
host discovers the plugin executable only through this hierarchy — there is no flat
`plugins/<plugin>` fallback and nothing is written into the package directory at runtime.

## Embeddings plugin installation

Both plain and embeddings self-contained Unix archives require the build-matched host companion beside `scri`. Keep these files together when moving the extracted CLI:

```text
<extracted-release-directory>/
  scri
  libSystem.Native.so       # Linux; macOS uses libSystem.Native.dylib
  embeddings-bundle.json   # embeddings archive only
  plugins/                 # embeddings archive only
    scri-plugin-embeddings/0.6.0/<rid>/
```

The host companion supports filesystem classification. It is separate from the plugin's native libraries. The managed NuGet tool uses the installed .NET runtime; its package does not contain this self-contained archive companion.

Extract the embeddings archive for your operating system. From the extracted directory, run
`./scri setup` on Linux/macOS or `.\scri.exe setup` in PowerShell on Windows. Setup copies the
verified plugin into
`SCRINIA_HOME/plugins/scri-plugin-embeddings/0.6.0/<rid>/`.

If scri is installed through NuGet, run:

```sh
scri setup --embeddings-source <extracted-release-directory>
```

The extracted files remain unchanged. A checksum detects changed bundle contents;
it does not establish who published them. macOS uses the CPU/Metal backend.

The source directory must contain `embeddings-bundle.json` and the archive's exact
`plugins/scri-plugin-embeddings/0.6.0/<rid>/` tree. Supported payload RIDs are `win-x64`,
`linux-x64`, and `osx-arm64`; Windows/Linux use Vulkan. Exactly `ubuntu.24.04-x64` also maps to
`linux-x64` when the actual process is Linux/X64 and GNU libc is positively detected. Other unknown
distro identifiers are unsupported. Setup and home discovery use the same runtime identity.

Setup verifies the manifest, copies the payload, and records its plugin name/version in workspace
configuration. Success confirms installation, configuration, and path selection. Start `scri serve`
to initialize the plugin; setup success alone does not prove native loading or model inference.
`serve` never installs a release bundle. Setup migrates legacy assets stored beside the executable
in a separate step. Release archives use the bundle installation described above.

An explicit source cannot be combined with `--hooks` or `--uninstall-hooks`. Conflicting
`plugins:embeddings` selections or plugin-version/provider environment overrides are refused.
Check `plugins:embeddings`, `plugins:embeddings:version`, `SCRINIA_EMBEDDINGS_PROVIDER`, and
`SCRINIA__EMBEDDINGS__PROVIDER` before retrying. Setup can offer Ollama after installation; accepting
it selects Ollama and skips local-model downloads.

`InstalledConfigurationFailed` confirms the payload is retained but configuration or selection
failed. Correct the workspace/provider settings or reported I/O problem, then retry the printed
`scri setup --embeddings-source ... --workspace-root ...` command. `InstalledModelPreparationFailed`
also retains the installed payload: correct the download/network problem and retry setup. Other
installation failures may report an uncertain destination; do not treat that path as a confirmed
installation. Retry placeholders identify the source directory and workspace separately.

Local preparation downloads missing Model2Vec files to
`SCRINIA_HOME/models/m2v-MiniLM-L6-v2/` and, when the plugin is selected, the GGUF file
`SCRINIA_HOME/models/all-MiniLM-L6-v2-Q8_0.gguf`. First-run downloads require network access.
Local inference uses those files; optional HTTP providers use their configured endpoint.
