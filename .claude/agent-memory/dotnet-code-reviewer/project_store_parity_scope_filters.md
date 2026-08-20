---
name: project-store-parity-scope-filters
description: Store parity is not just CopyMemory — any IMemoryStore member with a File and an Http implementation must be diffed, and Http's /memories-backed reads are the recurring weak side
metadata:
  type: project
---

The store-parity constraint recorded for `CopyMemory` ([[project-scrinia-copy-rulings]]) applies
to **every** `IMemoryStore` member with both a `FileMemoryStore` and an `HttpMemoryStore`
implementation, not only to copy. `HttpMemoryStore`'s read members that are backed by the REST
`/memories` listing endpoint (`ListScoped`, `LoadIndex`, `GatherTopicInfos`, and the interface
defaults derived from them) are the recurring weak side: they fold their arguments into a single
query-string value, so any argument shape the query string cannot express is silently lost while
the local store honours it. `/search`-backed members do not share this weakness.

**Why:** a caller cannot tell which store it is talking to, and a contract that is honoured
locally but degraded remotely is worse than one that is uniformly limited — it passes local tests
and fails only in the remote deployment.

**How to apply:** when a review touches an `IMemoryStore` signature, do not accept "the Core
contract now takes X" as evidence the capability exists. Drive the assertion through
`HttpMemoryStore` over a real Kestrel socket (the pattern in
`tests/Scrinia.Server.Tests/SearchScopeCommaRoundTripTests.cs` and `RemoteLoadIndexTests`;
`TestServer`'s in-memory handler cannot be used because `HttpMemoryStore` sends via
`HttpClient.Send`). Ask specifically whether the remote path re-encodes the argument into a
string.

The repo's cross-store conformance suite (`tests/Scrinia.Server.Tests/Conformance/`) pins the
**unfiltered** case only — `ResolveReadScopes(null)`, `ListScoped(null)`. Every parity defect
found so far has been in the *filtered* case, which nothing there covers, so an all-green
conformance run is not evidence of filtered-argument parity. Check the filtered case by hand.
