# scrinia — the `scri` global tool

`scrinia` is the immutable NuGet **global tool** that installs the single user-facing command
`scri`. The same packaged command implements both server modes (`scri serve` and `scri shared
serve`) plus the configuration, tracking, and shared-base commands.

## Immutable package

This package is immutable once installed: after `dotnet tool install` the tool directory is never
written at runtime. All mutable data — configuration, models, plugins, plugin state, and workspace
memory — lives below [`SCRINIA_HOME`](#where-data-lives), never inside the package. The package ships
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

```console
$ dotnet tool update --global scrinia
```

## Uninstall

```console
$ dotnet tool uninstall --global scrinia
```

## Where data lives

All writable state goes below `SCRINIA_HOME` (the per-user home resolved deterministically; unset
defaults to `<LocalApplicationData>\Scrinia`). The package directory itself stays immutable.

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

## Vulkan plugin (separate install)

The optional Vulkan GPU plugin is **not** part of this package; it is a separately installed,
RID-specific asset. Install it into the mandatory plugin hierarchy under `SCRINIA_HOME`:

```console
$ dotnet tool install --global --add-source <local-feed> scri-plugin-embeddings
```

After installation its executable, managed assemblies, and native libraries live only at
`SCRINIA_HOME/plugins/scri-plugin-embeddings/<version>/<rid>/`, and any plugin-downloaded model lives
at `SCRINIA_HOME/models`.
