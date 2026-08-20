---
name: feedback-query-param-type-changes
description: Converting a query parameter or wire field from a delimited string to an array silently changes empty-value, null-element AND multi-element semantics — probe ?p=, [null], and a multi-element list whose elements contain the old delimiter
metadata:
  type: feedback
---

When a review covers a change from a delimited `string? p` to `string[]? p` (or
`IReadOnlyList<string>?`) on an ASP.NET Core route or a store contract, probe three inputs before
accepting any "this layer is a pass-through" argument:

1. `?p=` (empty value)
2. a JSON body containing `[null]`
3. **a MULTI-element list whose elements contain the old delimiter**, e.g. `["odd,name", "api"]`

**Why:** ASP.NET Core binds `?p=` to `[""]`, not to an empty array — so a downstream
`x is { Count: > 0 }` guard that was meant to mean "no filter" now sees one blank element and
takes the filter branch. `string[]` happily deserializes a JSON `null` element despite the
non-nullable element annotation, so any unguarded `s.Trim()` downstream becomes an unhandled
`NullReferenceException` on a schema-valid request. And a **single**-element round-trip test does
not pin the client's repeated-parameter emission at all: once the server stops splitting on the
delimiter, a client that re-joins `["a,b"]` to `"a,b"` still produces exactly the right request,
so the test stays green while `["a,b","c"]` collapses to one bogus name. Measured on
`fix/published-surface-coherence` Task 9 fix round 1: mutating `HttpMemoryStore.FetchAllPages`
back to a comma-join left every committed round-trip test passing.

**How to apply:** measure, do not reason. Integration probes against the real route settle all
three in a minute; the equivalent chain of reasoning across binder, DTO, pipeline and store has
been wrong every time it has been tried here. When mutation-testing the fix, mutate the CLIENT
and the SERVER halves separately — a mutation that only removes one half can be masked by the
other. See [[feedback-review-method]] and [[project-store-parity-scope-filters]].
