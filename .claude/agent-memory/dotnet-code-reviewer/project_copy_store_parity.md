---
name: project-scrinia-copy-rulings
description: Store-parity is the governing constraint for CopyMemory — FileMemoryStore and HttpMemoryStore must not diverge, and divergences are tracked as numbered "rulings"
metadata:
  type: project
---

`IMemoryStore.CopyMemory` has two implementations (`FileMemoryStore`, local; `HttpMemoryStore`,
remote) and the governing design constraint is that they must not diverge in observable
behaviour — same refusal messages, same ordering of guards, same carried metadata. Known
divergences are adjudicated as numbered "rulings" recorded in the method's doc comment and in
`.superpowers/sdd/**/task-*-report.md`.

**Why:** a caller cannot tell which store it is talking to. A guard ordered differently between
the two (e.g. same-name-check before vs. after source-existence) produces a different error
message for identical input, which is the defect class the rulings exist to close.

**How to apply:** when reviewing anything in either `CopyMemory`, diff the two implementations
side by side and flag any field, guard order, or message text that differs — even when the
remote version looks *more* correct. "Remote is better here" is still a divergence and needs an
explicit ruling, not a silent improvement.

Review method for these: [[feedback-review-method]].
