# Sharing Memory Across a Team

Scrinia memories live in `.scrinia/`, so they travel with the code and are shared with everyone on
the repository. Sharing uses ordinary source-control workflows — there is no remote server, no
shared database, and nothing extra to install.

## Committing shared memory

`.scrinia/` is a normal directory in your repository. Commit it like any other source file:

```text
# Scrinia prints these exact commands; you run them in your shell.
git -C <root> add -A -- '.scrinia/memories/api/auth.nmp2' '.scrinia/memories/api/auth.meta.json'
git -C <root> commit -m "docs(memory): update shared Scrinia memory"
git -C <root> push
```

Every successful shared mutation returns an authoritative manifest of the exact files it created,
modified, or deleted. Staging guidance is exactly one `git -C <root> add -A --` command with each
manifest path individually shell-quoted — never the whole `.scrinia/` directory, never a wildcard,
and never a derived path. Scrinia never launches `git`: it prints the commands and never claims that
they ran, that content is tracked, committed, pushed, clean, or synchronized.

## Shared bases

A **shared base** lets several repositories — a monorepo and its worktrees, or two developers'
clones — read and write the same memories through one registry. Register a shared base once per
user with an absolute repository root:

```text
scri shared base add <name> <absolute-root> [--json]
scri shared base remove <name> [--json]
scri shared base list [--all] [--json]
scri shared enable <name> [--json]
scri shared disable <name> [--json]
```

Shared bases are always fixed to the `project-git` tracking policy; that policy cannot be
overridden per base. Canonical names match `^[a-z][a-z0-9-]{0,31}$` and must be an existing
repository or worktree root. Validation is filesystem-only — Scrinia never invokes Git or inspects
its index, and reported index state is always `gitIndexStatus: UNVERIFIED`.

Only the commands you run explicitly enumerate or enable a base. Disabled registry names and roots
stay private; only the explicit `scri shared base list --all` command enumerates the complete
registry, including disabled entries. The default list shows enabled entries only.

## Working with teammates

Shared MCP tools run through `scri shared serve`. Reads use the generation opened by that process;
after changing shared files outside it, call `shared_refresh` for the base. A mutation refused with
`SHARED_BASE_STALE` needs a refresh before retrying.

`shared_link` resolves both endpoints in the selected base. Ordinary memory links appear in
`ref` attributes. For skills and AI assistant personas, links are stored in the Markdown file's metadata
sidecar as a `ref` array of logical paths. Their Markdown body stays unchanged. These sidecar links
survive restart. For skills and AI assistant personas, `shared_recall` and `shared_list` return empty
attributes and keywords. `shared_search` and `shared_facts` do not index these links.
`shared_search` searches ordinary memories; it also excludes skill and agent Markdown bodies.
Use `shared_list` to discover those file entities and `shared_recall` to read them.
The optional link reason is returned to the caller and
is not saved.

If a shared mutation reports `mutation: unknown`, call `shared_refresh` for the base, then inspect
the affected entries before deciding whether to retry. A link may have updated only one endpoint.

Older installations may need the explicit [managed-block update](troubleshooting.md#shared-tracking-policy-drift)
before a shared base can open.

- **Commit `.scrinia/`.** Memories travel with the code, so a teammate's next pull sees your
  memories automatically.
- **Register shared bases.** When the same memories should be shared across repositories, each
  developer registers the shared root with `scri shared base add`.
- **Merge like any other file.** `.scrinia/` participates in your repository's normal Git workflows;
  ordinary version control handles sharing and history.

## Troubleshooting

**A shared base is not enabled.** `scri shared base list` shows enabled bases; `scri shared enable
<name>` turns one on. If the managed tracking block is missing or drifted, restore it manually
before the base becomes available.

**`gitIndexStatus: UNVERIFIED`.** This is expected: Scrinia validates a shared base by filesystem
evidence only and never inspects the Git index.
