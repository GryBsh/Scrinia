## Goal-Driven Planning

### 1. Set a goal
`memory('remember', { path: '/goal/...', description: '...' })`
**Confirm the goal with the user before proceeding.**

### 2. Execute
Loop: `task('next', { path: '/goal/G-X' })` → spawn agent → `task('complete', { path: '/task/...', outcome: '...' })`
Follow task instructions exactly. Primary agent orchestrates, spawned agents execute.

### 3. Complete
`memory('transition', { path: '/goal/G-X', to: 'complete', outcome: '...' })`

### When to plan vs. just do
- **Every goal** goes through the full workflow
- **Questions and quick lookups** don't need a goal — just answer using `memory('search')` first
- **When in doubt**: set a goal — the auditor will right-size the scope

## Workflow Customization

Custom workflows define alternative pipelines. JSON Schema at `.scrinia/workflows/schema.json`.
Use `workflowRef` on goal creation:
```
memory('remember', { path: '/goal/...', description: '...', workflowRef: 'quick-fix' })
```

Workflow activities have types (agent/spawner/system), roles (seed/post-plan), and tags.
Seed activities run before planning. Post-plan activities (gates) run after execution.

## Skills

- Skills are methodology (how to work), memories are knowledge (what you know)
- `memory('recall', { path: '/skill/' })` lists available skills
- `memory('recall', { path: '/skill/...' })` activates one (loads its prompt)
- `memory('remember', { path: '/skill/...' })` captures effective approaches as reusable methodology
- Built-in skills: agent-specialist, planner, auditor, researcher, debugger, chaos-engineer, onboarder, sos-handler, evolutionary, cartographer, march-reporter, merge-safety, qa, self-reflector
- **Precedence**: project memory overrides built-in

## Recovery

- `memory('restore')` — full context restoration (project state, agent profile, patterns, active goal)
  - After restore, call `memory('recall')` for each path in `followUp`
  - Priority: agent norms first, then patterns on-demand
- `memory('recall', { path: '/project/status' })` — quick progress check

## Multi-User Merge Safety
After merging branches: `memory('reconcile')` → resolve each conflict → verify clean.
