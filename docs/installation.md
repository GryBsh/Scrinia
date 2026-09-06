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

All mutable state — configuration, models, plugins, and plugin state — lives only below
[`SCRINIA_HOME`](#where-data-lives).

## Where data lives

`SCRINIA_HOME` is the only mutable home for configuration, models, plugins, and plugin state. It
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

## Vulkan plugin (separate install)

The optional Vulkan GPU plugin is **not** part of the `scri` package; it is a separately installed,
RID-specific asset. Install it into the mandatory plugin hierarchy under `SCRINIA_HOME`:

```sh
dotnet tool install --global --add-source <local-feed> scri-plugin-embeddings
```

After installation its executable, managed assemblies, and native libraries live only at
`SCRINIA_HOME/plugins/scri-plugin-embeddings/<version>/<rid>/`, and any plugin-downloaded model lives
at `SCRINIA_HOME/models`.
