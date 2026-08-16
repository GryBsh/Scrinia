# Store Contract & Remote Parity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `HttpMemoryStore.ListScoped` return the complete listing, so the five members built on it stop answering confidently and wrongly past 200 entries.

**Architecture:** One private paging helper on `HttpMemoryStore` replaces the two divergent request-builders (`LoadIndex` pages; `ListScoped` does not). The completeness invariant those five members silently depend on is written onto `IMemoryStore`. A second conformance fixture seeds past the 200-row boundary and asserts local/remote parity across the read surface.

**Tech Stack:** .NET 10.0 preview, xUnit 2.9.3, FluentAssertions, ASP.NET Core minimal APIs, Kestrel loopback for remote-store tests.

**Spec:** `docs/superpowers/specs/2026-08-16-store-contract-remote-parity-design.md` (commit `c36893f`)

## Global Constraints

- **No member is deleted from `IMemoryStore`.** H1 as written in the review is explicitly NOT implemented — see spec D-2. `FakeMemoryStore` (`tests/Scrinia.TestDoubles/FakeMemoryStore.cs`) must be **unchanged** by this branch.
- **Page size stays `1000`**, matching `MemoryEndpoints.MaxListLimit`. The defect is the absence of an explicit limit, not the value of one.
- **The non-2xx throw is preserved.** Neither caller may return `[]` on a failed request. `HttpMemoryStore.cs:469-478` records why: an empty list is indistinguishable from "this store has nothing in it", and a 403 from the `search` permission gate made `list()` answer "No memories stored." for a full store.
- **`global using Xunit;` is already in `GlobalUsings.cs`.** Adding `using Xunit;` to a test file triggers **CS8933**. Do not add it.
- **Kestrel, never `TestServer`.** `HttpMemoryStore` sends synchronously and `Microsoft.AspNetCore.TestHost.ClientHandler.Send` throws `NotSupportedException` unconditionally.
- **Build with `dotnet build Scrinia.sln`.** Do NOT pass `-p:NuGetAudit=false`; that workaround was retired on 2026-07-30 and restore is clean without it.
- Assertions use FluentAssertions with a `because:` message stating what a failure would mean.
- Do not modify any pre-existing test file. If one appears to need a change, STOP and report it — that is a signal the store boundary moved, which this branch does not intend.

---

### Task 1: One paging loop, proven by a listing-parity test

**Files:**
- Create: `tests/Scrinia.Server.Tests/Conformance/PagedListingConformanceFixture.cs`
- Create: `tests/Scrinia.Server.Tests/Conformance/PagedListingConformanceTests.cs`
- Modify: `src/Scrinia/HttpMemoryStore.cs:449-499` (`LoadIndex`), `:616-675` (`ListScoped`)

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces: `PagedListingConformanceFixture` with public `IMemoryStore Local`, `IMemoryStore Remote`, `const int TopicCount = 10`, `const int PerTopic = 25`, `const int TotalEntries = 250`; and `[CollectionDefinition(nameof(PagedListingConformanceCollection))]`. Task 2 adds tests against this same fixture.

**Background the implementer needs:**

`ParseQualifiedName` (`FileMemoryStore.cs:237-270`) turns a `/`-prefixed name into a scope plus subject: `/t00/entry-000` becomes scope `local:t00`, subject `entry-000`. So seeding names of the form `/tNN/entry-MMM` creates `NN` distinct topic scopes. `FormatQualifiedName` reverses it.

`FileMemoryStore.ListScoped` enumerates `ResolveReadScopes` then `LoadIndex(scope)` per scope, so entries arrive **scope-by-scope and contiguous**. Truncating at 200 of 250 therefore makes whole topics vanish rather than thinning each evenly.

- [ ] **Step 1: Write the fixture**

Create `tests/Scrinia.Server.Tests/Conformance/PagedListingConformanceFixture.cs`:

```csharp
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Server.Models;
using Scrinia.Server.Services;

namespace Scrinia.Server.Tests.Conformance;

/// <summary>
/// <see cref="StoreConformanceFixture"/>'s construction, over a corpus that crosses the
/// server's 200-row default page size. That fixture is the artifact forcing the two stores
/// to agree, but it seeds 27 entries and asserts only on search — structurally below the
/// boundary where C2 begins, so it could never have caught it.
///
/// <para>A separate fixture rather than a larger shared one: StoreConformanceTests pins exact
/// counts to its 27-entry corpus ("25 of the fixture's 27 seeded entries contain 'widget'",
/// "an unfiltered top-20 of 27 candidates"), and growing that corpus would break assertions
/// whose subject is something else entirely.</para>
///
/// <para>250 entries across 10 topic scopes. The count is chosen against the SERVER's default
/// page size (200, MemoryEndpoints.DefaultListLimit) — the boundary the defect actually sits
/// on — not the client's own 1000-entry page size. See the plan's "Known limitation".</para>
/// </summary>
public sealed class PagedListingConformanceFixture : IAsyncLifetime
{
    public const int TopicCount = 10;
    public const int PerTopic = 25;
    public const int TotalEntries = TopicCount * PerTopic;   // 250, above the server's 200 default

    private readonly ScriniaServerFactory _factory = new();
    private HttpClient _client = null!;

    /// <summary>The server's OWN store instance, resolved from StoreManager — not a second
    /// FileMemoryStore opened on the same directory, which would fight over the per-scope index
    /// cache and produce spurious differences.</summary>
    public IMemoryStore Local { get; private set; } = null!;
    public IMemoryStore Remote { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Order is load-bearing: UseKestrel must run before the host is materialized, which the
        // first touch of Services or CreateClient would do.
        _factory.UseKestrel(0);
        _factory.StartServer();

        _client = _factory.CreateAuthenticatedClient();
        Local = _factory.Services.GetRequiredService<StoreManager>().GetStore(_factory.PrimaryStore);
        // HttpMemoryStore's constructor assigns _http.Timeout, which throws if the HttpClient has
        // already issued a request — so construct it before the first SeedAsync.
        Remote = new HttpMemoryStore(_client, _factory.PrimaryStore);

        // Every topic gets the same number of entries, and every topic is non-empty. The second
        // part matters: FileMemoryStore.DiscoverTopics re-derives scopes from on-disk directory
        // names while HttpMemoryStore.DiscoverTopics derives them from the listing, so a topic
        // directory with zero entries would legitimately differ between the two and is not this
        // branch's subject.
        for (int t = 0; t < TopicCount; t++)
            for (int i = 0; i < PerTopic; i++)
                await SeedAsync($"/t{t:D2}/entry-{i:D3}", $"paged listing probe topic {t} number {i}");
    }

    private async Task SeedAsync(string name, string body)
    {
        var resp = await _client.PostAsJsonAsync(
            $"/api/v1/stores/{_factory.PrimaryStore}/memories",
            new StoreRequest([body], name, body));
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Order-insensitive comparison key for a listing: the qualified label of each
    /// entry and nothing else.</summary>
    public static string[] QualifiedNames(IMemoryStore store, IEnumerable<ScopedArtifact> items) =>
        items.Select(i => store.FormatQualifiedName(i.Scope, i.Entry.Name))
             .OrderBy(s => s, StringComparer.Ordinal).ToArray();

    public Task DisposeAsync()
    {
        // HttpMemoryStore owns _client's disposal (its Dispose() disposes the HttpClient it was
        // constructed with), so no separate _client.Dispose() here.
        (Remote as IDisposable)?.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }
}

[CollectionDefinition(nameof(PagedListingConformanceCollection))]
public sealed class PagedListingConformanceCollection
    : ICollectionFixture<PagedListingConformanceFixture>;
```

- [ ] **Step 2: Write the failing test**

Create `tests/Scrinia.Server.Tests/Conformance/PagedListingConformanceTests.cs`:

```csharp
using FluentAssertions;

namespace Scrinia.Server.Tests.Conformance;

/// <summary>
/// The test that would have caught C2: the same corpus, both store implementations, identical
/// listings — over a corpus large enough for the server's default page size to matter.
/// </summary>
[Collection(nameof(PagedListingConformanceCollection))]
public sealed class PagedListingConformanceTests(PagedListingConformanceFixture fx)
{
    [Fact]
    public void ListScoped_Unfiltered_BothStores_ReturnEveryEntry()
    {
        var local = fx.Local.ListScoped(null);
        var remote = fx.Remote.ListScoped(null);

        local.Should().HaveCount(PagedListingConformanceFixture.TotalEntries,
            because: "the fixture seeded exactly that many through the real REST write path");

        remote.Should().HaveCount(PagedListingConformanceFixture.TotalEntries,
            because: "a remote listing that stops at the server's 200-row default answers "
                   + "confidently and wrongly, with no error and no truncation marker");

        PagedListingConformanceFixture.QualifiedNames(fx.Remote, remote).Should()
            .BeEquivalentTo(PagedListingConformanceFixture.QualifiedNames(fx.Local, local),
                because: "the same corpus read through either store is the same set of memories");
    }
}
```

- [ ] **Step 3: Run it and confirm it fails for the right reason**

Run: `dotnet test tests/Scrinia.Server.Tests --filter "FullyQualifiedName~PagedListingConformance"`

Expected: FAIL. `remote` has **200** entries, not 250. Record the observed count in your report — a failure with any other count or an exception means the fixture is wrong, not the production code, and you should fix the fixture before touching `HttpMemoryStore`.

- [ ] **Step 4: Extract the shared paging helper**

In `src/Scrinia/HttpMemoryStore.cs`, add this private method next to `LoadIndex`:

```csharp
/// <summary>
/// Every entry the server holds for <paramref name="scopes"/> — or across every scope when it
/// is null — collected page by page until the server's own reported <c>Total</c> is reached.
///
/// <para>ONE paging loop for this type. <see cref="LoadIndex"/> and
/// <see cref="ListScoped(string?)"/> read the same listing endpoint, and while each owned its
/// own request-building the two drifted: LoadIndex paged to exhaustion, ListScoped sent no
/// limit at all and took the server's 200-row default as though it were the whole store. The
/// comment on LoadIndex's loop described, precisely, the defect its sibling still had.</para>
/// </summary>
private List<MemoryEntry> FetchAllPages(string? scopes)
{
    var collected = new List<MemoryEntry>();
    int offset = 0;
    // 1000 is MaxListLimit server-side (MemoryEndpoints.cs) — the largest page the server will
    // honour, so paging at this size minimizes round trips. The server's default page size
    // (200) is what silently truncated a caller who sent no limit: they got the first page and
    // no signal that more existed.
    const int pageSize = 1000;

    while (true)
    {
        string url = scopes is not null
            ? $"{BaseUrl}/memories?scopes={Uri.EscapeDataString(scopes)}&offset={offset}&limit={pageSize}"
            : $"{BaseUrl}/memories?offset={offset}&limit={pageSize}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = SendSync(req);

        // Returning [] here would look exactly like "this store has nothing in it" — the same
        // lie ListScoped told past 200 entries, just gated behind a failed request instead of a
        // silent truncation. A caller must not conclude "empty" from a server that is
        // unreachable or erroring. The status code is included specifically so a 403 reads as a
        // permission problem (this endpoint requires "search", distinct from "store") rather
        // than an opaque server fault.
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Remote store listing failed{(scopes is null ? "" : $" for scopes '{scopes}'")}: " +
                $"{(int)resp.StatusCode} {resp.StatusCode}.",
                inner: null,
                statusCode: resp.StatusCode);

        using var stream = resp.Content.ReadAsStream();
        var list = JsonSerializer.Deserialize(stream, HttpStoreJsonContext.Default.ListApiResponse);
        if (list?.Memories is null || list.Memories.Length == 0)
            break;

        collected.AddRange(list.Memories);

        offset += list.Memories.Length;
        if (offset >= list.Total)
            break;
    }

    return collected;
}
```

Note on the unified message: `LoadIndex`'s throw previously read `for scope '{scope}'` and `ListScoped`'s read `for scopes '{scopes}'`. Nothing asserts on either string (verified by grep across `src/` and `tests/`), so they become one wording.

- [ ] **Step 5: Rewrite both callers to use it**

Replace `LoadIndex`'s body (keep its existing doc comment, including the paragraph about round-trip bounds, and delete only the sentence "This method's own internal paging loop is a different thing — one logical call, several pages." if it no longer reads correctly once the loop has moved):

```csharp
public IReadOnlyList<ArtifactEntry> LoadIndex(string scope = "local")
{
    var result = new List<ArtifactEntry>();
    foreach (var item in FetchAllPages(scope))
        result.Add(ToArtifactEntry(item));
    return result;
}
```

Replace `ListScoped`'s server-fetch half. **Keep untouched**: the `includeEphemeral` block (`:621-629`) and the long comment at `:632-639` explaining why the `scopes is not null` test legitimately differs from its sibling's whitespace test — that comment stays true and stays put, moved to sit above the `FetchAllPages(scopes)` call.

```csharp
public List<ScopedArtifact> ListScoped(string? scopes = null)
{
    var result = new List<ScopedArtifact>();

    // Include ephemeral  — unchanged
    bool includeEphemeral = string.IsNullOrWhiteSpace(scopes);
    if (!includeEphemeral && scopes is not null)
    {
        includeEphemeral = scopes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(s => s.Trim().Equals("ephemeral", StringComparison.OrdinalIgnoreCase));
    }
    if (includeEphemeral)
        result.AddRange(ListEphemeral());

    foreach (var item in FetchAllPages(scopes))
    {
        // Recover the scope from the label the server reported. Task 13: this was a
        // character-for-character re-implementation of MemoryNaming.ParseScopeLabel — the same
        // three cases in the same order — so it now calls it. Recovery, not construction: see
        // ParseScopeLabel's remarks for why it is not validated here.
        result.Add(new ScopedArtifact(
            MemoryNaming.ParseScopeLabel(item.Scope), ToArtifactEntry(item)));
    }

    return result;
}
```

- [ ] **Step 6: Run the test and confirm it passes**

Run: `dotnet test tests/Scrinia.Server.Tests --filter "FullyQualifiedName~PagedListingConformance"`
Expected: PASS, both stores reporting 250.

- [ ] **Step 7: Run the two suites this touches**

Run: `dotnet test tests/Scrinia.Server.Tests` and `dotnet test tests/Scrinia.Tests`
Expected: no new failures. `HttpMemoryStore` has existing coverage in `RemoteStoreToolTests`, `StoreBoundaryTests`, `LocalRemoteParityTests` and `Conformance/StoreConformanceTests` — all must stay green. Report the counts.

- [ ] **Step 8: Commit**

```bash
git add src/Scrinia/HttpMemoryStore.cs tests/Scrinia.Server.Tests/Conformance/
git commit -m "fix(remote): page ListScoped to exhaustion through one shared loop"
```

---

### Task 2: The rest of the read surface, each case proven able to fail

**Files:**
- Modify: `tests/Scrinia.Server.Tests/Conformance/PagedListingConformanceTests.cs`

**Interfaces:**
- Consumes: `PagedListingConformanceFixture` from Task 1 (`Local`, `Remote`, `TopicCount`, `PerTopic`, `TotalEntries`, `QualifiedNames`).
- Produces: nothing later tasks depend on.

**The hazard this task exists to avoid.** Task 1 already fixed the bug, so every test added here passes the moment it is written. A test that has never been observed to fail is not evidence. The previous branch shipped exactly that defect — a guard test that passed with the guard entirely removed — and it was caught only by deliberately breaking the production code. **Step 2 below is mandatory, not optional.**

- [ ] **Step 1: Add the four remaining parity cases**

Append to `PagedListingConformanceTests.cs`:

```csharp
    /// <summary>The sharpest case. FileMemoryStore.ListScoped enumerates ResolveReadScopes then
    /// LoadIndex(scope) per scope, so entries arrive scope-by-scope and contiguous — truncating
    /// at 200 of 250 makes whole topics VANISH rather than thinning each evenly. Set equality,
    /// so this holds however the server orders scopes; it does not depend on predicting which
    /// topic disappears.</summary>
    [Fact]
    public void DiscoverTopics_BothStores_SeeEveryTopic()
    {
        var local = fx.Local.DiscoverTopics();
        var remote = fx.Remote.DiscoverTopics();

        local.Should().HaveCount(PagedListingConformanceFixture.TopicCount,
            because: "the fixture seeded that many non-empty topic scopes");

        remote.Should().BeEquivalentTo(local,
            because: "a topic whose entries all fall past a truncated listing does not merely "
                   + "lose entries — it stops existing, and code that lists it reports it empty");
    }

    /// <summary>ResolveReadScopes(null) is built on DiscoverTopics, so it inherits the whole
    /// defect. This is the member that decides which scopes any later read even looks at.</summary>
    [Fact]
    public void ResolveReadScopes_Unfiltered_BothStores_Agree()
    {
        fx.Remote.ResolveReadScopes(null).Should()
            .BeEquivalentTo(fx.Local.ResolveReadScopes(null),
                because: "the set of readable scopes decides what every later read can see");
    }

    [Fact]
    public void GatherTopicInfos_BothStores_ReportSameEntryCounts()
    {
        var local = fx.Local.GatherTopicInfos(null)
            .ToDictionary(t => t.Scope, t => t.EntryNames.Length);
        var remote = fx.Remote.GatherTopicInfos(null)
            .ToDictionary(t => t.Scope, t => t.EntryNames.Length);

        remote.Should().BeEquivalentTo(local,
            because: "per-topic counts come straight off the listing, so a truncated one "
                   + "under-reports every topic past the cut");
    }

    /// <summary>Closes C2's stated harm. This compares two genuinely different implementations
    /// — FileMemoryStore's generation-invalidated facts.json cache against the IMemoryStore
    /// default's rebuild from ListScoped(null) — over a corpus large enough that truncation
    /// changes the answer. It is also what makes keeping the default (spec D-2) defensible
    /// rather than merely convenient.</summary>
    [Fact]
    public void GetEntityFactStore_BothStores_DeriveTheSameFacts()
    {
        var local = fx.Local.GetEntityFactStore();
        var remote = fx.Remote.GetEntityFactStore();

        foreach (string key in new[] { "topic", "mention", "thing" })
        {
            remote.Entities(key).Should().BeEquivalentTo(local.Entities(key),
                because: $"entity '{key}' facts derived over a truncated corpus are wrong, "
                       + "and the remote store reaches them through the interface default");
        }
    }
```

If `EntityFactStore.Entities(string)` has a different name or shape than assumed, read `src/Scrinia.Core/Search/EntityFactStore.cs` and use the real member — assert on whatever enumerates its derived entities. Do not weaken the assertion to something that cannot fail; report the change you made.

- [ ] **Step 2: Prove each new case can fail (MANDATORY)**

Temporarily revert the fix — in `FetchAllPages`, change the URL construction to omit `offset`/`limit`, exactly reproducing the old `ListScoped` behaviour:

```csharp
        string url = scopes is not null
            ? $"{BaseUrl}/memories?scopes={Uri.EscapeDataString(scopes)}"
            : $"{BaseUrl}/memories";
```

Run: `dotnet test tests/Scrinia.Server.Tests --filter "FullyQualifiedName~PagedListingConformance"`

**Every one of the five tests must fail.** Record each observed failure — the actual counts and set differences — in your report. Then restore `FetchAllPages` exactly as Task 1 left it and confirm all five pass again.

A test that still passes under the reverted code is asserting nothing. Fix it so it fails, or report it as unfixable and say why — do not leave it in the file as green decoration.

- [ ] **Step 3: Run the suite**

Run: `dotnet test tests/Scrinia.Server.Tests`
Expected: green. Report counts.

- [ ] **Step 4: Commit**

```bash
git add tests/Scrinia.Server.Tests/Conformance/PagedListingConformanceTests.cs
git commit -m "test(remote): topics, scopes, counts and facts across the page boundary"
```

---

### Task 3: Write the invariant down, and stop the file contradicting itself

**Files:**
- Modify: `src/Scrinia.Core/IMemoryStore.cs:55-56`, `:125-127`, `:129`, `:131-137`, `:142-148`, `:156-160`

**Interfaces:**
- Consumes: nothing. This task changes documentation only — **no signature, no body, no member added or removed.**
- Produces: nothing.

**Why this is a task and not a footnote.** These comments are what made the review itself misdiagnose H1. `:55-56` states "Neither member has a default implementation, deliberately: every store implements both, so a store added later cannot silently inherit a derivation that is wrong for it" — true of `StoreAsync`/`AppendAsync`, but placed and phrased as the file's governing rule, three members before three defaults that contradict it. `:131-137` states "A store that cannot filter must now say so at compile time" — true of `SearchAll` specifically, false as file-wide policy.

- [ ] **Step 1: State the completeness invariant on `ListScoped`**

`IMemoryStore.cs:129` is currently a bare declaration:

```csharp
    // Listing & Search
    List<ScopedArtifact> ListScoped(string? scopes = null);
```

Give it the contract every default below depends on:

```csharp
    // Listing & Search

    /// <summary>
    /// Every entry in <paramref name="scopes"/>, or across every readable scope when null.
    ///
    /// <para><b>The listing is COMPLETE.</b> An implementation backed by a paged remote source
    /// must page to exhaustion before returning; returning one page is not a degraded answer,
    /// it is a wrong one, and it is silent — the caller sees a short list, not an error.</para>
    ///
    /// <para>This is the precondition every default implementation in this interface rests on:
    /// <see cref="GetEntityFactStore"/>, <see cref="ListScoped(string?, string?)"/> and (through
    /// <c>DiscoverTopics</c>) <see cref="ResolveReadScopes(string?, string?)"/> are all correct
    /// exactly insofar as this member is. <c>HttpMemoryStore.ListScoped</c> broke it by sending
    /// no <c>limit</c> against a server defaulting to 200 rows, and all three inherited the
    /// truncation — including the one that makes whole topic scopes disappear rather than merely
    /// lose entries. <c>PagedListingConformanceTests</c> is what holds this now.</para>
    /// </summary>
    List<ScopedArtifact> ListScoped(string? scopes = null);
```

- [ ] **Step 2: Cite the invariant from the three defaults**

Add one sentence to each of the three default-bodied members' doc comments, so the next implementer reads why inheriting is safe instead of guessing:

- `GetEntityFactStore` (`:113-126`) — append to the existing summary paragraph:
  `<para>Correct for any store whose <see cref="ListScoped(string?)"/> honours its completeness contract, and only for such a store: facts derived over a truncated listing are wrong, not partial.</para>`
- `ListScoped(string? scopes, string? excludeTopics)` (`:142-148`) — append:
  `<para>Post-filtering is correct here only because <see cref="ListScoped(string?)"/> is complete; see its contract.</para>`
- `ResolveReadScopes(string? scopes, string? excludeTopics)` (`:156-160`) — append:
  `<para>Correct for the same reason as its sibling, and with the same precondition: the underlying single-argument resolution must see every scope, which for a remote store means a complete listing.</para>`

- [ ] **Step 3: Narrow the two over-broad claims**

`:55-56` — change the comment so it governs only the two members above it:

```csharp
    // Neither StoreAsync nor AppendAsync has a default implementation, deliberately: every store
    // implements both, so a store added later cannot silently inherit a derivation that is wrong
    // for it. This is a rule about DERIVATION, not a policy for the whole interface — several
    // members below do carry defaults, and carry them correctly; see ListScoped's contract for
    // the invariant that makes them safe.
```

`:131-137` — in `SearchAll`'s summary, scope the compile-time claim to itself. Replace "A store that cannot filter must now say so at compile time." with:

```
    /// A store that cannot filter must say so at compile time — for THIS member. That is a
    /// consequence of the specific defect it fixes (four default bodies that post-filtered after
    /// the top-K cut), not a rule the rest of this interface follows.
```

- [ ] **Step 4: Verify nothing but comments changed**

Run: `git diff --stat src/Scrinia.Core/IMemoryStore.cs` then `dotnet build Scrinia.sln`
Expected: build succeeds, 0 errors. Then confirm by inspection that every changed line is inside a comment or doc comment. If the diff touches any executable line, you have exceeded this task — revert that hunk.

- [ ] **Step 5: Run the suites and commit**

Run: `dotnet test tests/Scrinia.Tests` and `dotnet test tests/Scrinia.Server.Tests`
Expected: green.

```bash
git add src/Scrinia.Core/IMemoryStore.cs
git commit -m "docs(store): the completeness contract, and two claims the file contradicted"
```

---

### Task 4: Changelog and the full-solution gate

**Files:**
- Modify: `CHANGELOG.md`

**Interfaces:**
- Consumes: the commits from Tasks 1-3.
- Produces: nothing.

- [ ] **Step 1: Add the changelog entries**

Under the unreleased heading, following the file's existing style. Two entries — one Fixed, one Added. There is **no** breaking change in this branch, and no entry should claim one.

```markdown
### Fixed

- Remote stores now return complete listings. `HttpMemoryStore.ListScoped` sent no `limit`
  against a server defaulting to 200 rows, so past 200 entries a remote store answered listing
  questions confidently and wrongly — with no error and no truncation marker. Entity facts,
  per-topic counts and `list` beyond the first page were all derived from the truncated set, and
  topic discovery was worse than incomplete: a topic whose entries all fell past the cut stopped
  existing, so code that listed it reported it empty. `ListScoped` and `LoadIndex` now share one
  paging loop that runs to the server's reported total.

### Added

- `PagedListingConformanceTests` — local/remote parity across the read surface (`ListScoped`,
  `DiscoverTopics`, `ResolveReadScopes`, `GatherTopicInfos`, `GetEntityFactStore`) over a corpus
  that crosses the server's default page size. The existing conformance harness covers search
  over 27 entries, structurally below the boundary where the defect began.
```

- [ ] **Step 2: Run the full solution suite**

Run: `dotnet test Scrinia.sln`

Expected: green across `Scrinia.Tests`, `Scrinia.Server.Tests`, `Scrinia.Merge.Tests`, `Scrinia.Retrieval.Benchmarks.Tests`, `Scrinia.Plugin.Embeddings.Tests`, with **one** known exception:

`MemoryEventSinkContextInstallDefaultTests.AParallelScopeLifecycleDoesNotDisturbDefault` is a **pre-existing flake on `main`**, established by running a control worktree: 2 failures in 16 runs with none of this work present. If it fails, note it and move on. **Any other failure is this branch's and must be reported, not explained away.**

Report the per-assembly counts.

- [ ] **Step 3: Commit**

```bash
git add CHANGELOG.md
git commit -m "docs(changelog): complete remote listings, and the harness that holds them"
```

---

## Known limitation, to be carried into the final review

The client pages at 1000 (`MaxListLimit`). The 250-entry fixture proves the loop is entered and runs past the server's 200-row default — the defect as reported — but does **not** exercise iteration across more than one client-side page. Closing that needs either a test-visible seam on the page size or a 1001-entry corpus; both were considered and rejected in the spec's "Rejected alternatives". Do not quietly widen the fixture to cover it, and do not claim coverage the tests do not have.

## Self-review notes

- **Spec coverage:** D-1 → Task 1. D-2 → Task 3. D-3 → Tasks 1 and 2. Out-of-scope items (`/facts` route, multi-page iteration) are carried as explicit non-goals, not dropped silently.
- **Ordering:** Task 1 must precede Task 2 — Task 2's cases are verified by reverting Task 1's fix, which requires it to exist. Task 3 is documentation-only and independent, but is placed after the behaviour it describes is real. No intermediate commit leaves the tree worse than `HEAD`: Task 1 is test-then-fix within one commit, so no commit carries a red suite.
- **Type consistency:** `PagedListingConformanceFixture`'s public surface (`Local`, `Remote`, `TopicCount`, `PerTopic`, `TotalEntries`, `QualifiedNames`) is defined in Task 1 and used unchanged in Task 2.
