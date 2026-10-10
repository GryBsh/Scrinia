# scrinia — the `scri` global tool

`scrinia` is the immutable NuGet **global tool** that installs the single user-facing command
`scri`. The same packaged command implements both server modes (`scri serve` and `scri shared
serve`) plus the configuration, tracking, and shared-base commands.

## Immutable package

This package is immutable once installed: after `dotnet tool install` the tool directory is never
written at runtime. The per-user registry, models, installed plugin payloads, and plugin state live
below [`SCRINIA_HOME`](#where-data-lives). Workspace configuration, memories, and derived
vectors/facts stay in `<workspace-root>/.scrinia/`.
The package ships
only the managed `scri` command, its managed dependencies, the offline-required embedded prompts,
skills, schemas, and setup templates, and this readme. It deliberately contains no Merge driver, no
Vulkan/LLamaSharp assembly, no native RID asset, no downloaded model, no repository data, no
`.scrinia` workspace, and no mutable cache.

## Install

```console
$ dotnet tool install --global scrinia
```

This installs the `scri` command onto your global tool path. Verify with `scri --version`.

## Update

Stop older server and CLI writers before upgrading, and back up the complete workspace including
recovery journals. The repaired configuration locks and workspace admission require cooperating
writers from compatible builds.

```console
$ dotnet tool update --global scrinia
```

## Uninstall

```console
$ dotnet tool uninstall --global scrinia
```

## Where data lives

The per-user registry, models, installed plugin payloads, and plugin state live below `SCRINIA_HOME`
(unset defaults to `<LocalApplicationData>\Scrinia`). Workspace configuration, memories, and
derived vectors/facts live in `<workspace-root>/.scrinia/`. The package directory stays immutable.

```
SCRINIA_HOME/
  config.json                 # per-user registry (shared bases, tracking)
  models/                     # Model2Vec and plugin-downloaded models
  plugins/
    <plugin>/
      <version>/
        <rid>/                # immutable plugin executable, managed assemblies, native libraries
        state/                # mutable plugin runtime state
```

Models resolve to `SCRINIA_HOME/models`; separately installed plugins live only at the mandatory
`SCRINIA_HOME/plugins/<plugin>/<version>/<rid>/` hierarchy — there is no flat plugin fallback.

## Commands

Run `scri` for memory over local stdio, or `scri shared serve` for the disjoint shared-base MCP
surface:

```console
$ scri serve        # serves <workspace-root>/.scrinia/ over local stdio
$ scri shared serve # serves the enabled shared bases over local stdio
```

## Embeddings plugin installation

Extract the embeddings archive for your operating system. From the extracted directory, run
`./scri setup` on Linux/macOS or `.\scri.exe setup` in PowerShell on Windows.
With this NuGet tool, run:

```console
$ scri setup --embeddings-source <extracted-release-directory>
```

The source contains `embeddings-bundle.json` and its versioned plugin tree. Setup copies the verified
plugin into `SCRINIA_HOME/plugins/scri-plugin-embeddings/0.6.0/<rid>/` and selects it in workspace
configuration. The extracted files remain unchanged. A checksum detects changed bundle contents;
it does not establish who published them. Supported payloads are `win-x64`, `linux-x64`, and
`osx-arm64`; macOS uses the CPU/Metal backend and Windows/Linux use Vulkan.

`serve` does not install a release bundle. Setup success confirms installation, configuration, and
path selection; plugin initialization and inference occur later. If configuration or model
preparation fails after installation, the payload remains installed; correct the reported problem
and rerun the printed setup retry command. Conflicting plugin selections or provider environment
overrides must be corrected first. Legacy asset migration is separate from bundle installation.

First-run Model2Vec and GGUF downloads require network access and go under `SCRINIA_HOME/models`.
Model2Vec uses `models/m2v-MiniLM-L6-v2/`; the plugin uses `models/all-MiniLM-L6-v2-Q8_0.gguf`.
Optional HTTP providers send content to their configured endpoint.
