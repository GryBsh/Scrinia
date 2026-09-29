# Published Surface Coherence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make two published surfaces — the REST authorization policy and the MCP tool family — state each of their rules exactly once, in a place a new call site cannot silently bypass.

**Architecture:** Two independent parts in disjoint projects, executed A → B. Part A moves route→permission from 19 hand-written handler guards into endpoint metadata enforced by one filter, with a startup validator that refuses to boot a route declaring nothing. Part B collapses the MCP facade's seven duplicated address-canonicalization calls into one helper, pins all five address-taking surfaces with a behavioural test net, and fixes six places where sibling MCP tools answer the same question differently.

**Scope note:** M3 (plugin contract inversion) was originally Part C of this plan and has been **split into its own branch**. A pre-implementation falsification pass found it structurally blocked — see the final section for what was established, so the next branch starts from evidence rather than from the review finding. Nothing in Parts A or B depends on it.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, ModelContextProtocol SDK, xUnit + FluentAssertions.

**Spec:** `docs/superpowers/specs/2026-08-18-published-surface-coherence-design.md` (commit `eac3f7c`)

## Global Constraints

- **Breaking changes are permitted and must be recorded.** Product version is `0.5.0`, pre-1.0. No compatibility shims, no dual-accept parameter parsing. Every wire-visible or schema-visible break gets a `CHANGELOG.md` entry under Unreleased → Changed (breaking).
- **Enumerate from the authoritative list, never from an existing guard's call sites.** The authoritative lists are: the route table (`MapGet`/`MapPost`/`MapPut`/`MapPatch`/`MapDelete` calls), the `[McpServerTool]` attribute set, the `[Argument]`-taking CLI command set, and `git grep` over an interface name. Scoping work by "where the existing check already appears" has produced a Critical on three consecutive branches.
- **Do not unify the three ephemeral-alias policies.** The MCP facade canonicalizes and proceeds; the CLI canonicalizes then refuses (its process exits, so an ephemeral memory could never be read back); REST refuses the alias outright (it would otherwise store permanent bytes under `local:temp`). All three are correct for their surface. Part B collapses duplication *within* the facade only.
- **Verify counts by enumeration, never by citing a recorded figure.** This plan's first draft asserted "19 hand-placed call sites" from a project memory; the real number of direct callers is 10, and the difference changed the whole shape of Part B. It also asserted two `ctx.Store` measurements from a grep whose pattern could not match the usage shape that actually exists. Re-derive, and prefer reading the call site over pattern-matching it.
- **Run the full suite, not a filtered subset, before reporting a task DONE.** Command: `dotnet test Scrinia.sln`. The solution builds clean without `-p:NuGetAudit=false`; do not add that flag.
- **`ImplicitUsings` is enabled** in all projects and test projects carry `global using Xunit;`. A per-file `using Xunit;` is CS8933 and fails the build.

## File Structure

**Part A — `Scrinia.Server`**

| File | Responsibility |
|---|---|
| `src/Scrinia.Server/Auth/Permissions.cs` (new) | The ten permission-name constants. Single vocabulary for both surfaces. |
| `src/Scrinia.Server/Auth/PermissionMetadata.cs` (new) | The metadata record, the `RequirePermission` / `PublicEndpoint` builder extensions, and the enforcing `IEndpointFilter`. |
| `src/Scrinia.Server/Auth/PermissionCoverageValidator.cs` (new) | Startup walk over `EndpointDataSource`; throws on an authenticated endpoint that declares neither. |
| `src/Scrinia.Server/Endpoints/*.cs` (modify) | Add `.RequirePermission(...)` to each route; delete the 19 handler guards. |
| `src/Scrinia.Server/Auth/McpPermissionFilter.cs` (modify) | Use the constants; make the unknown-tool arm fail closed. |
| `src/Scrinia.Server/Program.cs` (modify) | Invoke the validator after `app` is built. |

**Part B — `Scrinia.Core`, `Scrinia.Mcp`, `Scrinia`**

| File | Responsibility |
|---|---|
| `src/Scrinia.Mcp/MemoryTools.cs` (modify) | One `Address(...)` helper replacing seven direct calls; skill-path routing; the `guide` envelope. |
| `src/Scrinia.Mcp/MemoryTools.Core.cs` (modify) | `list` mode validation. |
| `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs` (modify) | `reconcile` argument validation; the `restore` envelope. |
| `src/Scrinia.Mcp/MemoryTools.Skills.cs` (modify) | `SkillDelete`. |
| `tests/Scrinia.Tests/Naming/AddressSurfaceCoverageTests.cs` (new) | The net over all five address-taking surfaces. |

**Read-only in Part B** — these surfaces are covered by tests but not modified, because each already applies its own deliberate policy: `src/Scrinia/Commands/ScriniaCommands.cs` (CLI, refuses), `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs` (REST, refuses), `src/Scrinia.Mcp/ScriniaMcpResources.cs` and `src/Scrinia.Server/Chat/AgentLoop.cs` (one call each — a helper wrapping one call at one site is indirection without consolidation).

---

# Part A — Server authorization (M5)

### Task 1: One permission vocabulary

**Files:**
- Create: `src/Scrinia.Server/Auth/Permissions.cs`
- Modify: `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs`, `EmbeddingsEndpoints.cs`, `ChatEndpoints.cs`, `KeyEndpoints.cs`, `src/Scrinia.Server/Auth/McpPermissionFilter.cs`
- Test: `tests/Scrinia.Server.Tests/Auth/PermissionVocabularyTests.cs`

**Interfaces:**
- Produces: `Scrinia.Server.Auth.Permissions` — ten `public const string` fields, and `Permissions.All` as `IReadOnlyList<string>`. Later tasks reference these constants by name.

This task is a pure rename with no behaviour change. It exists separately so that the behavioural change in Task 2 has a diff containing only behaviour.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Scrinia.Server.Tests/Auth/PermissionVocabularyTests.cs
namespace Scrinia.Server.Tests.Auth;

public class PermissionVocabularyTests
{
    [Fact]
    public void All_ContainsEveryDeclaredConstant()
    {
        var declared = typeof(Permissions)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        // Guards the hazard this class exists to remove: a constant added without
        // being added to All would be invisible to every coverage check built on All.
        Permissions.All.Should().BeEquivalentTo(declared);
    }

    [Fact]
    public void NoServerSourceFileUsesABarePermissionLiteral()
    {
        // Enumerates from the source tree, not from a list of known call sites.
        string serverRoot = TestPaths.RepoRoot("src", "Scrinia.Server");
        var offenders = new List<string>();

        foreach (string file in Directory.EnumerateFiles(serverRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") ||
                file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
                file.EndsWith("Permissions.cs", StringComparison.Ordinal))
                continue;

            string text = File.ReadAllText(file);
            foreach (string perm in Permissions.All)
                if (text.Contains($"\"{perm}\"", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)}: \"{perm}\"");
        }

        offenders.Should().BeEmpty(
            "permission names must come from the Permissions constants so a rename is a compile error");
    }
}
```

`TestPaths.RepoRoot` may not exist. If it does not, add it to the existing test-helper class in `tests/Scrinia.Server.Tests/`, resolving upward from `AppContext.BaseDirectory` until a directory containing `Scrinia.sln` is found, and throwing a clear message if none is.

- [ ] **Step 2: Run the tests and verify they fail**

Run: `dotnet test tests/Scrinia.Server.Tests --filter FullyQualifiedName~PermissionVocabularyTests`
Expected: FAIL — `Permissions` does not exist (CS0103).

- [ ] **Step 3: Create the constants**

```csharp
// src/Scrinia.Server/Auth/Permissions.cs
namespace Scrinia.Server.Auth;

/// <summary>
/// The permission vocabulary, stated once. Both authorization surfaces — the REST
/// endpoint filter and <see cref="McpPermissionFilter"/> — reference these constants,
/// so renaming a permission is a compile error rather than a silent divergence
/// between two independently maintained mappings.
/// </summary>
public static class Permissions
{
    public const string Append = "append";
    public const string Chat = "chat";
    public const string Copy = "copy";
    public const string Export = "export";
    public const string Forget = "forget";
    public const string Import = "import";
    public const string Read = "read";
    public const string Reindex = "reindex";
    public const string Search = "search";
    public const string Store = "store";

    /// <summary>Every declared permission. Pinned against the declared constants by test.</summary>
    public static readonly IReadOnlyList<string> All =
    [
        Append, Chat, Copy, Export, Forget, Import, Read, Reindex, Search, Store,
    ];
}
```

- [ ] **Step 4: Replace every bare literal**

In the four endpoint files and `McpPermissionFilter.cs`, replace each string literal with its constant. Example transformations:

```csharp
// MemoryEndpoints.cs:144
if (!ctx.HasPermission(Permissions.Store))

// ChatEndpoints.cs:20
if (!ctx.HasPermission(Permissions.Chat) || !ctx.HasPermission(Permissions.Read) || !ctx.HasPermission(Permissions.Search))

// McpPermissionFilter.cs:73-77
"search" or "list" or "facts" => Permissions.Search,
"recall" => Permissions.Read,
"remember" or "compact" or "link" or "reconcile" => Permissions.Store,
"append" => Permissions.Append,
"forget" => Permissions.Forget,
```

The `switch` arms' *left-hand* side are MCP tool names, not permission names — leave those as literals. Only the values change.

- [ ] **Step 5: Run the tests and verify they pass**

Run: `dotnet test tests/Scrinia.Server.Tests --filter FullyQualifiedName~PermissionVocabularyTests`
Expected: PASS.

- [ ] **Step 6: Run the full suite**

Run: `dotnet test Scrinia.sln`
Expected: all green. This task changes no behaviour, so any failure is a genuine mistake in the rename.

- [ ] **Step 7: Commit**

```bash
git add src/Scrinia.Server/Auth/Permissions.cs src/Scrinia.Server/Endpoints src/Scrinia.Server/Auth/McpPermissionFilter.cs tests/Scrinia.Server.Tests/Auth/PermissionVocabularyTests.cs
git commit -m "refactor(auth): one permission vocabulary shared by both surfaces"
```

---

### Task 2: Declare permissions as endpoint metadata, enforce in one filter

**Files:**
- Create: `src/Scrinia.Server/Auth/PermissionMetadata.cs`
- Modify: `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs`, `EmbeddingsEndpoints.cs`, `ChatEndpoints.cs`, `HealthEndpoints.cs`, `KeyEndpoints.cs`
- Test: `tests/Scrinia.Server.Tests/Auth/EndpointPermissionTests.cs`

**Interfaces:**
- Consumes: `Permissions` (Task 1).
- Produces: `RouteHandlerBuilder.RequirePermission(params string[])`, `RouteHandlerBuilder.PublicEndpoint()`, and `RequiredPermissionsMetadata(IReadOnlyList<string> Permissions)`. Task 3's validator reads this metadata.

**Context:** All 27 routes are currently guarded correctly. This task must not change which caller gets a 403 for which route — only where that decision is expressed. The response body must stay byte-identical, because clients parse it.

- [ ] **Step 1: Record the current behaviour as a characterization test**

Before changing anything, pin what exists. This is the safety net for the whole task.

```csharp
// tests/Scrinia.Server.Tests/Auth/EndpointPermissionTests.cs
namespace Scrinia.Server.Tests.Auth;

public class EndpointPermissionTests
{
    // Enumerated from the route table in the five *Endpoints.cs files, not from
    // the set of handlers that happen to call HasPermission today.
    public static TheoryData<string, string, string> GatedRoutes() => new()
    {
        { "POST",   "/api/v1/stores/local/memories",                     Permissions.Store },
        { "GET",    "/api/v1/stores/local/memories",                     Permissions.Search },
        { "GET",    "/api/v1/stores/local/memories/x",                   Permissions.Read },
        { "GET",    "/api/v1/stores/local/memories/x/entry",             Permissions.Read },
        { "PATCH",  "/api/v1/stores/local/memories/x",                   Permissions.Store },
        { "DELETE", "/api/v1/stores/local/memories/x",                   Permissions.Forget },
        { "POST",   "/api/v1/stores/local/memories/x/append",            Permissions.Append },
        { "POST",   "/api/v1/stores/local/memories/x/copy",              Permissions.Copy },
        { "POST",   "/api/v1/stores/local/memories/x/copy-from-client",  Permissions.Copy },
        { "GET",    "/api/v1/stores/local/memories/x/chunks/0",          Permissions.Read },
        { "GET",    "/api/v1/stores/local/search",                       Permissions.Search },
        { "POST",   "/api/v1/stores/local/search",                       Permissions.Search },
        { "POST",   "/api/v1/stores/local/export",                       Permissions.Export },
        { "POST",   "/api/v1/stores/local/import",                       Permissions.Import },
        { "GET",    "/api/v1/stores/local/embeddings/status",            Permissions.Read },
        { "GET",    "/api/v1/stores/local/embeddings/settings",          Permissions.Read },
        { "POST",   "/api/v1/stores/local/embeddings/reindex",           Permissions.Reindex },
        { "GET",    "/api/v1/stores/local/chat/providers",               Permissions.Chat },
        // The conjunctive case. ChatEndpoints.cs:20 requires chat AND read AND search,
        // so it needs a row per permission — this is the only route in the table whose
        // 403 can be triggered three different ways, and the riskiest transformation
        // in this task. It must not ship without coverage.
        { "POST",   "/api/v1/stores/local/chat/",                        Permissions.Chat },
        { "POST",   "/api/v1/stores/local/chat/",                        Permissions.Read },
        { "POST",   "/api/v1/stores/local/chat/",                        Permissions.Search },
    };

    [Theory]
    [MemberData(nameof(GatedRoutes))]
    public async Task Route_Returns403_WhenCallerLacksItsPermission(string method, string path, string required)
    {
        // A key holding every permission EXCEPT the one under test. This is the probe
        // that makes the assertion sharp: a route that ignored its declaration entirely
        // would answer 200/400/404 here, not 403.
        var granted = Permissions.All.Where(p => p != required).ToArray();
        using var app = await TestServerFactory.CreateAsync(permissions: granted);

        var response = await app.SendAsync(method, path, body: "{}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            $"{method} {path} declares {required}");
    }

    [Theory]
    [MemberData(nameof(GatedRoutes))]
    public async Task Route_DoesNotReturn403_WhenCallerHoldsItsPermission(string method, string path, string required)
    {
        // The inverse probe. Without it, a filter that returned 403 unconditionally
        // would pass the test above for every row.
        using var app = await TestServerFactory.CreateAsync(permissions: Permissions.All.ToArray());

        var response = await app.SendAsync(method, path, body: "{}");

        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden,
            $"{method} {path} should admit a caller holding {required}");
    }

    [Fact]
    public async Task ForbiddenBody_IsUnchanged()
    {
        using var app = await TestServerFactory.CreateAsync(permissions: []);
        var response = await app.SendAsync("POST", "/api/v1/stores/local/memories", body: "{}");

        string json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("Permission 'store' required.",
            "clients parse this body; moving where the check lives must not change what it says");
    }
}
```

**Neither helper exists — you are building both.** Verified: there is no `TestServerFactory` and no `TestPaths` anywhere in `tests/Scrinia.Server.Tests`. What exists is `ScriniaServerFactory : WebApplicationFactory<Program>` (`tests/Scrinia.Server.Tests/ScriniaServerFactory.cs:12`), whose surface is:

- `CreateAuthenticatedClient()` — one baked-in key holding **every** permission, so it cannot express "all but one"
- `CreateClientWithKey(string rawKey)`
- `CreateRestrictedStoreKey(string storeName, string? userId = null)` — store access, zero permissions
- `TestApiKey`, `TestKeyId`, `TestUserId`, `PrimaryStore`, `SecondaryStore`
- `Services.GetRequiredService<ApiKeyStore>()`, whose `CreateKey(userId, stores, permissions, label)` takes an arbitrary permission array

Add **one** method to `ScriniaServerFactory` — do not create a second factory:

```csharp
    /// <summary>A client whose key holds exactly <paramref name="permissions"/>. The
    /// baked-in test key holds everything, which cannot express the all-but-one probe
    /// the permission tests need.</summary>
    public HttpClient CreateClientWithPermissions(params string[] permissions)
    {
        var keyStore = Services.GetRequiredService<ApiKeyStore>();
        var (rawKey, _, _) = keyStore.CreateKey(
            TestUserId, [PrimaryStore], permissions, label: "scoped");
        return CreateClientWithKey(rawKey);
    }
```

Then write the `SendAsync(method, path, body)` shape in the test file itself as a small local helper over `HttpClient` — it is test-file scaffolding, not factory surface. Substitute `PrimaryStore` for the literal `local` in the route table's paths.

- [ ] **Step 2: Run and verify the characterization tests pass against the CURRENT code**

Run: `dotnet test tests/Scrinia.Server.Tests --filter FullyQualifiedName~EndpointPermissionTests`
Expected: PASS. These describe behaviour that already exists. If any row fails now, stop and report it — that is a real pre-existing defect and it changes the task.

- [ ] **Step 3: Write the metadata, extensions and filter**

```csharp
// src/Scrinia.Server/Auth/PermissionMetadata.cs
namespace Scrinia.Server.Auth;

/// <summary>
/// Endpoint metadata naming the permissions a caller must hold. Conjunctive: the
/// caller needs all of them.
/// </summary>
public sealed record RequiredPermissionsMetadata(IReadOnlyList<string> Permissions);

/// <summary>
/// Marks an endpoint as deliberately ungated. Its presence is what distinguishes
/// "no permission needed" from "someone forgot" — see PermissionCoverageValidator.
/// </summary>
public sealed record PublicEndpointMetadata;

/// <summary>
/// Marks an endpoint whose permission check genuinely happens somewhere the route
/// table cannot see. <paramref name="Where"/> names that place and is required.
///
/// This exists for exactly one shape of endpoint: one HTTP route carrying many
/// operations with different permissions. `/mcp` is the case — a single route over
/// JSON-RPC, where McpPermissionFilter resolves the permission per call. Declaring
/// a single RequirePermission there would be a lie, and PublicEndpoint() would be a
/// worse one.
///
/// It is also an escape hatch, so it is pinned: a test asserts the exact set of
/// endpoints using it. Adding a new one fails that test and forces the question.
/// </summary>
public sealed record PermissionCheckedElsewhereMetadata(string Where);

public static class PermissionEndpointExtensions
{
    /// <summary>Declares the permissions this route requires. Conjunctive.</summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, params string[] permissions)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(permissions);
        if (permissions.Length == 0)
            throw new ArgumentException(
                "RequirePermission needs at least one permission. For a deliberately ungated "
                + "route use PublicEndpoint() — an empty list would read as 'gated' to the "
                + "coverage validator while gating nothing.",
                nameof(permissions));

        builder.WithMetadata(new RequiredPermissionsMetadata(permissions));
        builder.AddEndpointFilter(new PermissionEndpointFilter(permissions));
        return builder;
    }

    /// <summary>Declares this route as deliberately reachable without a permission.</summary>
    public static TBuilder PublicEndpoint<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithMetadata(new PublicEndpointMetadata());
        return builder;
    }

    /// <summary>
    /// Declares that this route's permission check happens outside the route table,
    /// naming where. See <see cref="PermissionCheckedElsewhereMetadata"/> — its use
    /// is pinned by test.
    /// </summary>
    public static TBuilder PermissionCheckedElsewhere<TBuilder>(this TBuilder builder, string where)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(where);
        builder.WithMetadata(new PermissionCheckedElsewhereMetadata(where));
        return builder;
    }
}

/// <summary>
/// The single enforcement point. Replaces the per-handler guards; the 403 body is
/// reproduced exactly as those guards wrote it, because clients parse it.
/// </summary>
internal sealed class PermissionEndpointFilter(IReadOnlyList<string> required) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var requestContext = ctx.HttpContext.RequestServices.GetService<RequestContext>()
            ?? ctx.Arguments.OfType<RequestContext>().FirstOrDefault();

        // Fail closed: a gated route with no resolvable request context is denied,
        // matching McpPermissionFilter's stance on the same question.
        if (requestContext is null)
            return Results.Json(new ErrorResponse("Permission required."), statusCode: 403);

        foreach (string permission in required)
            if (!requestContext.HasPermission(permission))
                return Results.Json(
                    new ErrorResponse($"Permission '{permission}' required."), statusCode: 403);

        return await next(ctx);
    }
}
```

How `RequestContext` reaches the filter depends on how it is currently supplied to handlers — it appears as a handler parameter, so it is either DI-registered or bound by a custom binder. Read `src/Scrinia.Server/Auth/RequestContext.cs` and the DI registration in `Program.cs` before finalizing the resolution above, and use whichever single mechanism is already in place rather than both.

- [ ] **Step 4: Declare permissions on every route and delete the guards**

Add `.RequirePermission(...)` to each of the 19 gated route registrations, matching the table in Step 1. Then delete the corresponding `if (!ctx.HasPermission(...)) return Results.Json(...)` block from each handler. `ChatEndpoints`' three-permission guard becomes one declaration:

```csharp
group.MapPost("/", ...).RequirePermission(Permissions.Chat, Permissions.Read, Permissions.Search);
```

**`KeyEndpoints` and `HealthEndpoints` need no changes.** Both are gated by *named* authorization policies — `RequireAuthorization("ManageKeys")` and `RequireAuthorization("Health")` — which is already an explicit, deliberate gate. Task 3's validator treats a named policy as a declaration (see its rule), so these routes are covered without a `RequirePermission` call.

`KeyEndpoints:52`'s `HasPermission` loop stays. It is privilege-escalation prevention on the request *body* — the caller cannot grant a permission they do not themselves hold — not route gating, so it is not one of the 19 guards being removed. Leave it and leave its comment.

`HealthEndpoints`' `/health/live`, `/health/ready` and `/health` carry no `RequireAuthorization` at all, so they are out of the validator's scope entirely and need no marker.

`PublicEndpoint()` therefore has no caller in this task. It exists for the case the validator must be able to express — a route that authenticates but deliberately requires no permission — and Task 3 tests it directly.

- [ ] **Step 5: Run the characterization tests again**

Run: `dotnet test tests/Scrinia.Server.Tests --filter FullyQualifiedName~EndpointPermissionTests`
Expected: PASS, unchanged from Step 2. Same behaviour, different expression.

- [ ] **Step 6: Verify no handler guard survives**

Run: `git grep -n "HasPermission" -- src/Scrinia.Server/Endpoints/`
Expected: no output. If a line remains, either it is a genuine non-route check (like `KeyEndpoints:52`) — leave it and note why — or a guard was missed.

- [ ] **Step 7: Run the full suite and commit**

```bash
dotnet test Scrinia.sln
git add -A src/Scrinia.Server tests/Scrinia.Server.Tests
git commit -m "refactor(auth): declare route permissions as metadata, enforce in one filter"
```

---

### Task 3: Fail closed at startup

**Files:**
- Create: `src/Scrinia.Server/Auth/PermissionCoverageValidator.cs`
- Modify: `src/Scrinia.Server/Program.cs`
- Test: `tests/Scrinia.Server.Tests/Auth/PermissionCoverageValidatorTests.cs`

**Interfaces:**
- Consumes: `RequiredPermissionsMetadata`, `PublicEndpointMetadata` (Task 2).
- Produces: `PermissionCoverageValidator.Validate(EndpointDataSource)`, throwing `InvalidOperationException`.

**Context:** This is the highest-blast-radius change in the branch. A false positive is a server that will not start. It therefore gets tested in both directions before being wired into `Program.cs`.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Scrinia.Server.Tests/Auth/PermissionCoverageValidatorTests.cs
namespace Scrinia.Server.Tests.Auth;

public class PermissionCoverageValidatorTests
{
    private static EndpointDataSource SourceOf(params Endpoint[] endpoints) =>
        new TestEndpointDataSource(endpoints);

    private static Endpoint Endpoint(string displayName, params object[] metadata) =>
        new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/x"),
            0,
            new EndpointMetadataCollection(metadata),
            displayName);

    [Fact]
    public void Accepts_AnAuthenticatedEndpointDeclaringAPermission()
    {
        var source = SourceOf(Endpoint("GET /gated",
            new AuthorizeAttribute(),
            new RequiredPermissionsMetadata([Permissions.Read])));

        var act = () => PermissionCoverageValidator.Validate(source);

        act.Should().NotThrow();
    }

    [Fact]
    public void Accepts_AnAuthenticatedEndpointMarkedPublic()
    {
        var source = SourceOf(Endpoint("GET /health",
            new AuthorizeAttribute(),
            new PublicEndpointMetadata()));

        var act = () => PermissionCoverageValidator.Validate(source);

        act.Should().NotThrow();
    }

    [Fact]
    public void Accepts_AnEndpointWithNoAuthorizationAtAll()
    {
        // Not every endpoint is in scope. An unauthenticated endpoint is a different
        // decision, made elsewhere, and this validator must not second-guess it.
        var source = SourceOf(Endpoint("GET /open"));

        var act = () => PermissionCoverageValidator.Validate(source);

        act.Should().NotThrow();
    }

    [Fact]
    public void Accepts_AnEndpointGatedByANamedPolicy()
    {
        // A named policy is already an explicit, deliberate gate — this is how
        // KeyEndpoints ("ManageKeys") and HealthEndpoints ("Health") are protected.
        // Requiring RequirePermission on top would be ceremony, and forcing them to
        // claim PublicEndpoint() would be a false statement about routes that are gated.
        var source = SourceOf(Endpoint("POST /keys", new AuthorizeAttribute("ManageKeys")));

        var act = () => PermissionCoverageValidator.Validate(source);

        act.Should().NotThrow();
    }

    [Fact]
    public void Rejects_BareAuthenticationWithNoPolicyAndNoPermission()
    {
        // The actual hole: RequireAuthorization() with no policy proves only that the
        // caller is someone, not that they may do this.
        var source = SourceOf(Endpoint("POST /gap", new AuthorizeAttribute()));

        var act = () => PermissionCoverageValidator.Validate(source);

        act.Should().Throw<InvalidOperationException>().WithMessage("*POST /gap*");
    }

    [Fact]
    public void Rejects_AnAuthenticatedEndpointDeclaringNeither()
    {
        var source = SourceOf(Endpoint("GET /forgotten", new AuthorizeAttribute()));

        var act = () => PermissionCoverageValidator.Validate(source);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*GET /forgotten*",
                "the message must name the offending route — a validator that fails "
                + "without saying which endpoint is worse than no validator");
    }

    [Fact]
    public void Rejects_AnEndpointDeclaringBoth()
    {
        // Both markers is a contradiction, not a belt-and-braces. Reading it as
        // "public wins" would let a stray PublicEndpoint() silently open a gated route.
        var source = SourceOf(Endpoint("GET /confused",
            new AuthorizeAttribute(),
            new RequiredPermissionsMetadata([Permissions.Read]),
            new PublicEndpointMetadata()));

        var act = () => PermissionCoverageValidator.Validate(source);

        act.Should().Throw<InvalidOperationException>().WithMessage("*GET /confused*");
    }

    [Fact]
    public void Accepts_AnEndpointCheckedBelowTheRouteTable()
    {
        var source = SourceOf(Endpoint("POST /mcp",
            new AuthorizeAttribute(),
            new PermissionCheckedElsewhereMetadata("McpPermissionFilter")));

        var act = () => PermissionCoverageValidator.Validate(source);

        act.Should().NotThrow();
    }

    [Fact]
    public void Rejects_APermissionNameOutsideTheVocabulary()
    {
        var source = SourceOf(Endpoint("GET /typo",
            new AuthorizeAttribute(),
            new RequiredPermissionsMetadata(["raed"])));

        var act = () => PermissionCoverageValidator.Validate(source);

        act.Should().Throw<InvalidOperationException>().WithMessage("*raed*");
    }
}
```

`TestEndpointDataSource` is a two-line subclass returning a fixed list; add it as a private nested class or a small test helper.

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test tests/Scrinia.Server.Tests --filter FullyQualifiedName~PermissionCoverageValidatorTests`
Expected: FAIL — `PermissionCoverageValidator` does not exist.

- [ ] **Step 3: Implement the validator**

```csharp
// src/Scrinia.Server/Auth/PermissionCoverageValidator.cs
namespace Scrinia.Server.Auth;

/// <summary>
/// Startup gate making a missing permission declaration impossible to ship. Every
/// endpoint that requires authentication must declare either the permissions it
/// needs or that it is deliberately ungated; anything else refuses to boot.
/// </summary>
public static class PermissionCoverageValidator
{
    public static void Validate(EndpointDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var problems = new List<string>();

        foreach (var endpoint in source.Endpoints)
        {
            // Out of scope: an endpoint with no authorization requirement is a
            // separate decision made elsewhere.
            if (endpoint.Metadata.GetMetadata<IAuthorizeData>() is null)
                continue;

            // A named authorization policy is itself an explicit gate — KeyEndpoints
            // and HealthEndpoints are protected this way. Only bare authentication,
            // which proves identity and nothing else, needs a permission declaration.
            if (endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                    .Any(a => !string.IsNullOrWhiteSpace(a.Policy)))
                continue;

            // Checked below the route table — /mcp resolves its permission per
            // JSON-RPC call. The marker's uses are pinned by a separate test.
            if (endpoint.Metadata.GetMetadata<PermissionCheckedElsewhereMetadata>() is not null)
                continue;

            var declared = endpoint.Metadata.GetMetadata<RequiredPermissionsMetadata>();
            bool isPublic = endpoint.Metadata.GetMetadata<PublicEndpointMetadata>() is not null;
            string name = endpoint.DisplayName ?? endpoint.ToString() ?? "<unnamed endpoint>";

            if (declared is not null && isPublic)
            {
                problems.Add($"{name}: declares both RequirePermission and PublicEndpoint.");
                continue;
            }

            if (declared is null && !isPublic)
            {
                problems.Add(
                    $"{name}: requires authentication but declares neither RequirePermission(...), "
                    + "PublicEndpoint(), nor PermissionCheckedElsewhere(...).");
                continue;
            }

            foreach (string permission in declared?.Permissions ?? [])
                if (!Permissions.All.Contains(permission, StringComparer.Ordinal))
                    problems.Add($"{name}: '{permission}' is not a declared permission.");
        }

        if (problems.Count > 0)
            throw new InvalidOperationException(
                "Endpoint permission coverage check failed. Every authenticated endpoint must "
                + "declare its permissions or be explicitly marked public:"
                + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }
}
```

- [ ] **Step 4: Run and verify the tests pass**

Run: `dotnet test tests/Scrinia.Server.Tests --filter FullyQualifiedName~PermissionCoverageValidatorTests`
Expected: PASS, all six.

- [ ] **Step 5: Declare the two endpoints the route table cannot describe**

Verified: two endpoints carry bare `RequireAuthorization()` with no named policy, and neither is reachable from the four `*Endpoints.cs` files Task 2 touched. **Without this step the validator refuses to boot and nearly every `Scrinia.Server.Tests` test fails**, because they all boot the app through `ScriniaServerFactory : WebApplicationFactory<Program>`.

```csharp
// Program.cs:583 — one HTTP route carrying every JSON-RPC operation. The permission
// varies per call and is resolved by McpPermissionFilter, which EndpointDataSource
// cannot see. RequirePermission would name a permission that isn't the real one.
app.MapMcp("/mcp")
   .RequireAuthorization()
   .RequireRateLimiting("api")
   .PermissionCheckedElsewhere("McpPermissionFilter (per JSON-RPC method/tool)");

// Program.cs:571 — plugins map their own routes into this group, so the permissions
// are not knowable here. Empty in the default configuration (plugins load only when
// Vulkan is explicitly enabled, Program.cs:82), which is why no test catches it.
var pluginGroup = app.MapGroup("/api/v1/plugins")
    .RequireAuthorization()
    .RequireRateLimiting("api")
    .PermissionCheckedElsewhere("the plugin's own endpoint declarations");
```

The plugin group is the weaker of the two — "the plugin declares it" is a promise nothing enforces. Record that in the ledger as a follow-up rather than solving it here: making plugin-mapped routes declare permissions is a plugin-contract change, and the plugin contract is Part C's subject, not Part A's.

- [ ] **Step 6: Pin the escape hatch**

`PermissionCheckedElsewhere` can silence the validator, so its uses are enumerated and asserted. A new use fails this test.

```csharp
    [Fact]
    public void OnlyTheKnownEndpoints_BypassTheRouteTableCheck()
    {
        using var factory = new ScriniaServerFactory();
        _ = factory.CreateAuthenticatedClient(); // force the app to build

        var bypassing = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Where(e => e.Metadata.GetMetadata<PermissionCheckedElsewhereMetadata>() is not null)
            .Select(e => e.DisplayName ?? "<unnamed>")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        bypassing.Should().HaveCount(2,
            "this marker suppresses the coverage check — a third use must be argued for, "
            + "not merely added");
    }
```

Assert on the count rather than exact display names: minimal-API display names are framework-generated and brittle across SDK updates, while the count is the property that actually matters.

- [ ] **Step 7: Wire it into startup**

In `Program.cs`, after the app is built and all endpoints are mapped — the validator needs the fully populated `EndpointDataSource`, so this must come after every `Map*` call and before `await app.RunAsync()` (Program.cs:596):

```csharp
PermissionCoverageValidator.Validate(app.Services.GetRequiredService<EndpointDataSource>());
```

- [ ] **Step 6: Verify the real application passes its own gate**

Run: `dotnet test Scrinia.sln`
Expected: all green. Every server test boots the app, so a validator that rejects the real route table will fail loudly and immediately here. If it does, the failure message names the route — fix the declaration, do not weaken the validator.

- [ ] **Step 7: Commit**

```bash
git add src/Scrinia.Server/Auth/PermissionCoverageValidator.cs src/Scrinia.Server/Program.cs tests/Scrinia.Server.Tests/Auth/PermissionCoverageValidatorTests.cs
git commit -m "feat(auth): refuse to boot when a route declares no permission"
```

---

### Task 4: Close the MCP unknown-tool fallback

**Files:**
- Modify: `src/Scrinia.Server/Auth/McpPermissionFilter.cs`
- Test: `tests/Scrinia.Server.Tests/Auth/McpToolCoverageTests.cs`

**Interfaces:**
- Consumes: `Permissions` (Task 1).

**Context:** `RequiredPermission`'s `_ => null` arm currently means an unrecognised `tools/call` target is ungated — the same hole as a missing REST guard. The tools that legitimately need no permission (`guide`, `restore`) must become explicit rather than falling into the same default as a typo.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Scrinia.Server.Tests/Auth/McpToolCoverageTests.cs
namespace Scrinia.Server.Tests.Auth;

public class McpToolCoverageTests
{
    // Enumerates from the [McpServerTool] attribute set — the authoritative list —
    // so a tool added later cannot land ungated without failing here.
    public static TheoryData<string> DeclaredToolNames()
    {
        var data = new TheoryData<string>();
        foreach (var method in typeof(ScriniaMcpTools).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            var attr = method.GetCustomAttributes()
                .FirstOrDefault(a => a.GetType().Name == "McpServerToolAttribute");
            if (attr is null) continue;
            string? name = attr.GetType().GetProperty("Name")?.GetValue(attr) as string;
            if (!string.IsNullOrWhiteSpace(name)) data.Add(name);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(DeclaredToolNames))]
    public void EveryDeclaredTool_HasAnExplicitPermissionDecision(string toolName)
    {
        McpPermissionFilter.HasExplicitDecision("tools/call", toolName)
            .Should().BeTrue($"tool '{toolName}' must declare a permission or be explicitly ungated");
    }

    [Fact]
    public void AnUnknownToolName_HasNoExplicitDecision()
    {
        McpPermissionFilter.HasExplicitDecision("tools/call", "nonexistent_tool")
            .Should().BeFalse("an unrecognised tool must fail closed, not fall through ungated");
    }

    [Fact]
    public void AnUngatedTool_IsDistinguishableFromAnUnmappedOne()
    {
        // Both yield a null permission; only the explicitness query separates them.
        // Without this test the two cases could quietly collapse back into one.
        McpPermissionFilter.RequiredPermission("tools/call", "guide").Should().BeNull();
        McpPermissionFilter.RequiredPermission("tools/call", "nonexistent_tool").Should().BeNull();

        McpPermissionFilter.HasExplicitDecision("tools/call", "guide").Should().BeTrue();
        McpPermissionFilter.HasExplicitDecision("tools/call", "nonexistent_tool").Should().BeFalse();
    }

    [Fact]
    public void DeclaredToolNames_IsNotEmpty()
    {
        // Without this, a reflection query that silently matched nothing would make
        // the theory above vacuously green.
        DeclaredToolNames().Should().NotBeEmpty();
    }
}
```

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test tests/Scrinia.Server.Tests --filter FullyQualifiedName~McpToolCoverageTests`
Expected: FAIL — `HasExplicitDecision` does not exist.

- [ ] **Step 3: Make the mapping total and fail closed**

Replace the tool switch's default arm and add the explicitness query:

```csharp
    /// <summary>
    /// Tools that are deliberately reachable without a permission. Listing them
    /// explicitly is what lets an unrecognised name fail closed instead of sharing
    /// a default arm with them.
    /// </summary>
    private static readonly string[] UngatedTools = ["guide", "restore"];

    public static string? RequiredPermission(string method, string? toolName) => method switch
    {
        "tools/call" => toolName switch
        {
            "search" or "list" or "facts" => Permissions.Search,
            "recall" => Permissions.Read,
            "remember" or "compact" or "link" or "reconcile" => Permissions.Store,
            "append" => Permissions.Append,
            "forget" => Permissions.Forget,
            _ => null,
        },
        "resources/read" => Permissions.Read,
        _ => null, // initialize, tools/list, prompts/*, resources/list, notifications, …
    };

    /// <summary>
    /// True when this method/tool pair has a deliberate permission decision — either a
    /// mapped permission or membership of <see cref="UngatedTools"/>.
    ///
    /// Deliberately a separate question from <see cref="RequiredPermission"/> returning
    /// null. Null means "no permission needed", which is the right answer for `guide`
    /// and the wrong answer for a tool nobody mapped; one value cannot carry both. The
    /// filter denies when this returns false, so an unmapped tool fails closed without
    /// inventing a sentinel permission string that a key might somehow be granted.
    /// </summary>
    public static bool HasExplicitDecision(string method, string? toolName)
    {
        if (method != "tools/call")
            return true; // non-tool methods are decided by the outer switch

        if (toolName is null)
            return false;

        return RequiredPermission(method, toolName) is not null
            || UngatedTools.Contains(toolName, StringComparer.Ordinal);
    }
```

- [ ] **Step 4: Deny in the filter**

`RequiredPermission` alone no longer distinguishes ungated from unmapped, so the filter must ask the new question. In `Filter`, before the existing `required is not null` check:

```csharp
            if (!HasExplicitDecision(req.Method, toolName))
                // Fail closed: an unrecognised tool is indistinguishable from one
                // someone forgot to map, so it is denied rather than admitted.
                throw new McpException("This operation is not available.");
```

Keep the message generic — the SDK sanitizes filter-thrown exceptions at this layer anyway, and naming the mapping gap tells an unauthenticated caller about internals.

- [ ] **Step 5: Run and verify the tests pass**

Run: `dotnet test tests/Scrinia.Server.Tests --filter FullyQualifiedName~McpToolCoverageTests`
Expected: PASS. If a real tool name shows up as having no explicit decision, add it to the switch or to `UngatedTools` — do not widen the default.

- [ ] **Step 6: Run the full suite and commit**

```bash
dotnet test Scrinia.sln
git add src/Scrinia.Server/Auth/McpPermissionFilter.cs tests/Scrinia.Server.Tests/Auth/McpToolCoverageTests.cs
git commit -m "fix(auth): deny unmapped MCP tools instead of admitting them"
```

---

# Part B — Tool family coherence (M1)

### Task 5: One helper per surface, and a net over all five

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.cs` (collapse seven direct calls into one helper)
- Test: `tests/Scrinia.Tests/Naming/AddressSurfaceCoverageTests.cs` (new)

**Interfaces:**
- Produces: a private static helper in `ScriniaMcpTools` — name it `Address(string raw)` — returning the canonical form. Nothing public changes.

**Context — read this before writing code, because the obvious version of this task is wrong.**

An earlier draft of this plan proposed a `MemoryAddress` value type on the internal `MemoryTools.Core` methods, on the theory that a new surface would then fail to compile without converting. That was verified and **rejected**. Two reasons, both load-bearing:

1. **Two of the four surfaces never call those methods.** `ScriniaMcpResources.cs:47` calls `store.ResolveArtifactAsync` directly and `AgentLoop.ExecuteRecallAsync` calls `MemoryOrchestrator.ShowAsync` directly. A type enforces exactly the boundary it occupies, so it would have guaranteed nothing for either.
2. **Two of the parameters are not addresses.** `Recall`'s `artifactOrName` also accepts inline NMP/2 artifact text (`MemoryTools.Core.cs:22-24`, `:50`); `Forget`'s `nameOrUri` also accepts a `file://` URI (`:908`, `:930`). Wrapping them uniformly would make the type misrepresent its contents.

**The surfaces implement three deliberate policies and must keep doing so. Do not unify them.**

| Surface | Current handling | Policy |
|---|---|---|
| MCP facade, `MemoryTools.cs` | seven direct `CanonicalizeEphemeralAlias` calls (`:181, :216, :323, :348, :369, :396, :397`) | canonicalize, proceed |
| CLI, `ScriniaCommands.cs:50` inside `RefuseEphemeral` | one shared helper, seven callers | canonicalize, then **refuse** — the process exits, so an ephemeral memory could never be read back |
| REST, `MemoryEndpoints.cs:1011-1020` `NameRefusal` | `MemoryNaming.IsEphemeralAlias` | **refuse** — it would otherwise resolve to persistent scope `local:temp` and store permanent bytes under a misleading name |
| MCP resource, `ScriniaMcpResources.cs:39` | one direct call | canonicalize, proceed |
| Agent loop, `AgentLoop.cs:207` | one direct call | canonicalize, proceed |

The CLI and REST already have their one helper each. Only the facade's seven calls are genuinely duplicated. So this task does two small things: collapse those seven, and add the test net that covers all five surfaces including the ones no helper can reach.

- [ ] **Step 1: Write the failing test**

The net is a behavioural round-trip, not a reflection query that merely proves it matched something. Each surface is driven with `/temp/probe` and must treat it as `~probe`.

```csharp
// tests/Scrinia.Tests/Naming/AddressSurfaceCoverageTests.cs
namespace Scrinia.Tests.Naming;

/// <summary>
/// Pins the ephemeral-alias policy at every surface that accepts an address as text.
/// Three of the five canonicalize and proceed; two deliberately refuse. Both answers
/// are correct for their surface — what this fixture catches is a surface that does
/// NEITHER, which is how three sites were missed on the ephemeral-addressing branch.
/// </summary>
public class AddressSurfaceCoverageTests
{
    private static ScriniaMcpTools Tools() => new();

    [Fact]
    public async Task McpFacade_TreatsTempPathAsTheEphemeralMemory()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().MemoryRemember("/temp/probe", ["body"]);

        // Addressable under the canonical spelling, which is the whole rule.
        var recalled = ResponseParser.Parse(await Tools().MemoryRecall("~probe"));
        recalled.Status.Should().NotBe("error");
    }

    [Fact]
    public async Task McpResource_TreatsTempPathAsTheEphemeralMemory()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().MemoryRemember("~probe", ["body"]);

        // Reached through the resource template, which bypasses the tool methods
        // entirely — this is one of the two surfaces a type on the internal
        // boundary could never have covered.
        string content = await ScriniaMcpResources.ReadMemoryForTest("/temp/probe");

        content.Should().Contain("body");
    }

    [Fact]
    public void EveryMcpToolTakingAnAddress_IsCoveredByThisFixture()
    {
        // Enumerates from the [McpServerTool] attribute set so a tool added later
        // that takes an address forces someone to extend this fixture.
        var addressTools = typeof(ScriniaMcpTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.GetCustomAttributes().Any(a => a.GetType().Name == "McpServerToolAttribute"))
            .Where(m => m.GetParameters().Any(p =>
                p.ParameterType == typeof(string) &&
                p.Name is "path" or "artifactOrName" or "nameOrUri" or "destination" or "source"))
            .Select(m => m.Name)
            .ToList();

        addressTools.Should().NotBeEmpty("the reflection query must actually match something");
        addressTools.Should().BeEquivalentTo(KnownAddressTools,
            "a tool taking an address must be added to this fixture's coverage");
    }

    // Update deliberately when a tool gains or loses an address parameter.
    private static readonly string[] KnownAddressTools = [/* fill from the first run */];
}
```

The `KnownAddressTools` list is filled from the first run — run the test, read the actual list out of the failure message, and paste it in. Do not guess it.

`ScriniaMcpResources.ReadMemoryForTest` does not exist. The resource method is `[McpServerResource(UriTemplate = "scrinia://memory/{name}")]` in `ScriniaMcpResources.cs`; expose whatever minimal internal seam lets a test drive it, or drive it through the registered resource handler if the SDK makes that reachable. Do not skip this case — it is one of the two surfaces the rejected value type could not have protected, which is precisely why it needs a test.

Add equivalent cases for the CLI (`RefuseEphemeral` refuses and names the canonical form) and REST (`NameRefusal` 400s on `/temp/`) **only if** no such test already exists — check first: `EphemeralAliasRefusalTests.cs` exists in `tests/Scrinia.Server.Tests/` and may already cover REST. Do not duplicate it; reference it from this fixture's doc comment instead.

- [ ] **Step 2: Run and verify each test fails for the right reason**

Run: `dotnet test tests/Scrinia.Tests --filter FullyQualifiedName~AddressSurfaceCoverageTests`

Expected: compile failure on `ReadMemoryForTest` and on the empty `KnownAddressTools`. Fix those, then confirm the round-trip tests **pass** — the behaviour they assert already exists today. This fixture is a net, not a red-green cycle; its value is that it fails when someone later adds a surface and forgets.

- [ ] **Step 3: Prove the net can actually fail**

Temporarily delete the `CanonicalizeEphemeralAlias` call at `MemoryTools.cs:181` and re-run. `McpFacade_TreatsTempPathAsTheEphemeralMemory` must fail. Restore it.

Do the same for `ScriniaMcpResources.cs:39` against the resource test. Restore.

A net that cannot be shown to fail is not a net. Record both observed failures in the task report.

- [ ] **Step 4: Collapse the facade's seven calls**

Add to `ScriniaMcpTools`:

```csharp
    /// <summary>
    /// Canonicalizes an address arriving at the MCP facade. One helper rather than
    /// seven repetitions of the same call — the CLI (RefuseEphemeral) and REST
    /// (NameRefusal) already have theirs.
    ///
    /// The facade canonicalizes and proceeds; the CLI and REST deliberately REFUSE
    /// instead, because their processes cannot serve an ephemeral memory back. Those
    /// are three considered policies, not an inconsistency to unify.
    /// </summary>
    private static string Address(string raw) => MemoryNaming.CanonicalizeEphemeralAlias(raw);
```

Replace all seven call sites with `Address(...)`. This is a rename, not a behaviour change.

- [ ] **Step 5: Verify the count**

Run: `git grep -c "CanonicalizeEphemeralAlias" -- src/`

Expected: `MemoryNaming.cs` (the definition), `PathRouter.cs` (a comment), `ScriniaCommands.cs` (the CLI helper), `ScriniaMcpResources.cs`, `AgentLoop.cs`, and `MemoryTools.cs` at **1** rather than 7.

The two single-call surfaces keep their direct call — a one-line helper wrapping a one-line call at one site is indirection without consolidation.

- [ ] **Step 6: Run the full suite and commit**

```bash
dotnet test Scrinia.sln
git add src/Scrinia.Mcp tests/Scrinia.Tests/Naming
git commit -m "refactor(mcp): one address helper at the facade, and a net over all five surfaces"
```


### Task 6: Unknown enum values and ignored arguments become errors

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs` (`List`), `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs` (`Reconcile`)
- Test: `tests/Scrinia.Tests/Mcp/ToolFamilyConsistencyTests.cs`

**Context:** Two of the six contradictions, grouped because both are "an argument the tool accepted did nothing, and it reported success."

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Scrinia.Tests/Mcp/ToolFamilyConsistencyTests.cs
namespace Scrinia.Tests.Mcp;

public class ToolFamilyConsistencyTests
{
    [Theory]
    [InlineData("ful")]      // misspelling of "full"
    [InlineData("summry")]   // misspelling of "summary"
    [InlineData("verbose")]  // plausible but unsupported
    public async Task List_RejectsAnUnrecognisedMode(string mode)
    {
        using var scope = new TestHelpers.StoreScope();
        var response = await Tools().List(scopes: null, mode: mode, offset: 0, limit: 50,
            excludeTopics: null, CancellationToken.None);

        response.Status.Should().Be("error",
            "falling through to summary reports success for a call that did something else");
        response.Error.Should().Contain(mode);
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("full")]
    [InlineData("drift")]
    public async Task List_StillAcceptsEveryDocumentedMode(string mode)
    {
        using var scope = new TestHelpers.StoreScope();
        var response = await Tools().List(scopes: null, mode: mode, offset: 0, limit: 50,
            excludeTopics: null, CancellationToken.None);

        response.Status.Should().NotBe("error");
    }

    [Fact]
    public async Task Reconcile_RejectsAChoiceWithNoConflictId()
    {
        using var scope = new TestHelpers.StoreScope();
        var response = await Tools().Reconcile(conflictId: null, choice: "ours",
            content: null, CancellationToken.None);

        response.Status.Should().Be("error",
            "silently performing a read-only scan while reporting action: reconciled "
            + "tells the caller their choice was applied");
    }

    [Fact]
    public async Task Reconcile_StillScansWhenNeitherArgumentIsGiven()
    {
        using var scope = new TestHelpers.StoreScope();
        var response = await Tools().Reconcile(conflictId: null, choice: null,
            content: null, CancellationToken.None);

        response.Status.Should().NotBe("error");
    }
}
```

**The `Scrinia.Tests` fixture pattern, verified — use exactly this, do not invent a wrapper.** Two cooperating pieces, used throughout `ScriniaMcpToolsTests.cs` and its siblings:

```csharp
    // Declared once per test class.
    private static ScriniaMcpTools Tools() => new();

    // Per test, wiring MemoryStoreContext.Current to a throwaway store.
    using var scope = new TestHelpers.StoreScope();
```

`TestHelpers.StoreScope` is a nested `internal sealed class ... : IDisposable` (`tests/Scrinia.Tests/TestHelpers.cs:96`), so it is constructed with `new`, not called as a method.

The **internal** methods (`List`, `Recall`, `Forget`, `Reconcile`, …) return `Task<McpResponse>`, so assert on `.Status` / `.Error` directly. The **facade** methods (`MemoryRemember`, `MemoryRecall`, …) return `Task<string>` of rendered YAML; parse those with `ResponseParser.Parse(yaml)` (`TestHelpers.cs:184`), which yields an `McpResponseAssert` exposing `.Status`, `.Content`, `.Info`, `.Error`, `.ActionNeeded` and `.FollowUp`.

Note `McpResponse` has **no** `Message` property — the field carrying an error string is `Error` (`src/Scrinia.Mcp/McpResponse.cs:64`).

- [ ] **Step 2: Run and verify the two rejection tests fail**

Run: `dotnet test tests/Scrinia.Tests --filter FullyQualifiedName~ToolFamilyConsistencyTests`
Expected: the two `Rejects` tests FAIL (they currently return success); the two `Still` tests PASS.

- [ ] **Step 3: Reject the unrecognised mode**

In `List` (`MemoryTools.Core.cs:603`), before the drift check at `:618`, validate:

```csharp
        if (!string.Equals(mode, "summary", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mode, "full", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(mode, "drift", StringComparison.OrdinalIgnoreCase))
        {
            return ResponseBuilder.Error(
                $"Unknown mode '{mode}'. Use 'summary', 'full', or 'drift'.",
                ErrorCodes.InvalidParameter,
                "list({ mode: 'full' })");
        }
```

Match the message shape `facts` already uses for the same class of error — read it first so the family agrees.

- [ ] **Step 4: Reject the orphaned choice**

In `Reconcile` (`MemoryTools.Lifecycle.cs:84`), before the `conflictId is not null` branch:

```csharp
        if (conflictId is null && !string.IsNullOrWhiteSpace(choice))
            return Task.FromResult(ResponseBuilder.Error(
                "'choice' requires 'conflictId' — without one there is no conflict to resolve, "
                + "and the call would silently perform a read-only scan instead.",
                ErrorCodes.InvalidParameter,
                "reconcile({ conflictId: '<path>', choice: 'ours' }) or reconcile({}) to scan"));
```

- [ ] **Step 5: Run and verify all four pass**

Run: `dotnet test tests/Scrinia.Tests --filter FullyQualifiedName~ToolFamilyConsistencyTests`
Expected: PASS.

- [ ] **Step 6: Run the full suite and commit**

```bash
dotnet test Scrinia.sln
git add src/Scrinia.Mcp tests/Scrinia.Tests/Mcp/ToolFamilyConsistencyTests.cs
git commit -m "fix(mcp): reject unknown list modes and orphaned reconcile choices"
```

---

### Task 7: The `/skill/` namespace stops being write-only

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Skills.cs` (add `SkillDelete`), `src/Scrinia.Mcp/MemoryTools.cs` (route `forget`, warn on ignored args)
- Test: `tests/Scrinia.Tests/Mcp/SkillPathRoutingTests.cs`

**Interfaces:**
- Consumes: `TryParseSkillPath` (existing, `MemoryTools.cs:479`).
- Produces: `internal static Task<McpResponse> SkillDelete(string name, CancellationToken ct)`.

**Context:** `remember` and `recall` both route `/skill/` paths; `forget` does not, so it answers `NOT_FOUND` for a skill `recall` just returned. Separately, `remember` on a `/skill/` path routes to `SkillCreate` and drops `attributes` and `keywords` without saying so.

Built-in skills live in `BuiltInSkills`; disk skills are `.md` files under the skills directory. Only disk skills can be deleted.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Scrinia.Tests/Mcp/SkillPathRoutingTests.cs
namespace Scrinia.Tests.Mcp;

public class SkillPathRoutingTests
{
    [Fact]
    public async Task Forget_DeletesASkillThatRecallReturns()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Remember(MemoryAddress.From("/skill/temp-helper"),
            content: ["do the thing"], ct: CancellationToken.None);

        var before = await Tools().Recall(MemoryAddress.From("/skill/temp-helper"), CancellationToken.None);
        before.Status.Should().NotBe("error");

        var forget = await Tools().Forget(MemoryAddress.From("/skill/temp-helper"), CancellationToken.None);
        forget.Status.Should().NotBe("error",
            "a namespace remember and recall both route must not answer NOT_FOUND on forget");

        var after = await Tools().Recall(MemoryAddress.From("/skill/temp-helper"), CancellationToken.None);
        after.Status.Should().Be("error");
    }

    [Fact]
    public async Task Forget_OnABuiltInSkill_SaysSoRatherThanNotFound()
    {
        using var scope = new TestHelpers.StoreScope();
        string builtIn = BuiltInSkills.Keys.First();

        var response = await Tools().Forget(MemoryAddress.From($"/skill/{builtIn}"), CancellationToken.None);

        response.Status.Should().Be("error");
        response.Error.Should().Contain("built-in",
            "NOT_FOUND is a false statement about a skill that demonstrably exists");
    }

    [Fact]
    public async Task Remember_OnASkillPath_WarnsThatAttributesAndKeywordsAreIgnored()
    {
        using var scope = new TestHelpers.StoreScope();

        var response = await Tools().Remember(
            MemoryAddress.From("/skill/noted"),
            content: ["instructions"],
            attributes: new Dictionary<string, string[]> { ["topic"] = ["x"] },
            keywords: ["alpha"],
            ct: CancellationToken.None);

        response.Status.Should().NotBe("error");
        string rendered = response.ToYaml();
        rendered.Should().Contain("attributes").And.Contain("keywords");
    }

    [Fact]
    public async Task Remember_OnASkillPath_WithoutThoseArguments_DoesNotWarn()
    {
        using var scope = new TestHelpers.StoreScope();

        var response = await Tools().Remember(MemoryAddress.From("/skill/quiet"),
            content: ["instructions"], ct: CancellationToken.None);

        response.ToYaml().Should().NotContain("ignored",
            "a warning on every skill write would train the caller to ignore warnings");
    }
}
```

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test tests/Scrinia.Tests --filter FullyQualifiedName~SkillPathRoutingTests`
Expected: FAIL on all but the last.

- [ ] **Step 3: Implement `SkillDelete`**

In `MemoryTools.Skills.cs`, beside `SkillCreate` and `SkillLoad`. Resolve the skills directory the same way `SkillLoad` does — read it rather than reconstructing the path logic. Behaviour:

- Disk file exists → delete it, return success with `action: "forgotten"`.
- No disk file but the name is in `BuiltInSkills` → error saying it is a built-in skill and cannot be forgotten.
- Neither → the ordinary `NOT_FOUND` error.

- [ ] **Step 4: Route `forget` through the dispatcher**

In `MemoryTools.cs`, add `TryRouteSkillForget` next to `TryRouteSkillRemember` and `TryRouteSkillRecall`, and call it at the top of `MemoryForget` in the same shape the other two use.

- [ ] **Step 5: Warn on the ignored arguments**

In `TryRouteSkillRemember`, collect the names of supplied-but-ignored arguments and attach them to `SkillCreate`'s response as info rather than dropping them silently:

```csharp
        var ignored = new List<string>();
        if (attributes is { Count: > 0 }) ignored.Add("attributes");
        if (keywords is { Length: > 0 }) ignored.Add("keywords");
```

`TryRouteSkillRemember` does not currently receive `attributes` or `keywords` — widen its parameter list and update its single call site at `MemoryTools.cs:154`. Emit the warning through the same `WithInfo` channel the rest of the family uses; the message should say the arguments do not apply to a skill, which is markdown instructions, rather than implying they were lost by accident.

- [ ] **Step 6: Run and verify the tests pass**

Run: `dotnet test tests/Scrinia.Tests --filter FullyQualifiedName~SkillPathRoutingTests`
Expected: PASS.

- [ ] **Step 7: Run the full suite and commit**

```bash
dotnet test Scrinia.sln
git add src/Scrinia.Mcp tests/Scrinia.Tests/Mcp/SkillPathRoutingTests.cs
git commit -m "fix(mcp): make /skill/ readable, writable and deletable through one dispatcher"
```

---

### Task 8: One envelope for the restore context

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.cs` (`Guide`), `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs` (`Restore`, `BuildRestoreContextAsync`)
- Modify: `CHANGELOG.md`
- Test: `tests/Scrinia.Tests/Mcp/RestoreEnvelopeTests.cs`

**Context:** `guide` concatenates `ctx.ContentSections` into `info:` (`MemoryTools.cs:79`); `restore` returns the same sections as the `content:` body. The skills list therefore arrives under a different field depending on which tool produced it. **This is a breaking change to `restore`'s response shape.**

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Scrinia.Tests/Mcp/RestoreEnvelopeTests.cs
namespace Scrinia.Tests.Mcp;

public class RestoreEnvelopeTests
{
    [Fact]
    public async Task GuideAndRestore_PutTheRestoreContextInTheSameField()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Remember(MemoryAddress.From("/skill/probe"),
            content: ["x"], ct: CancellationToken.None);

        var guide = await Tools().Guide(CancellationToken.None);
        var restore = await Tools().Restore(CancellationToken.None);

        // The skills list is the payload that visibly swapped fields.
        ExtractInfo(guide).Should().ContainMatch("*Skills available*");
        ExtractInfo(restore).Should().ContainMatch("*Skills available*",
            "the same payload from a sibling tool must arrive under the same field");
    }

    [Fact]
    public async Task Guide_KeepsTheGuideDocumentAsItsContent()
    {
        using var scope = new TestHelpers.StoreScope();
        var guide = await Tools().Guide(CancellationToken.None);

        // Guarded because the fix must not move the guide text out of content: in
        // the course of aligning the sections.
        ExtractContent(guide).Should().Contain("Scrinia");
    }

    [Fact]
    public async Task Restore_ContentIsASummaryLine_NotTheSections()
    {
        using var scope = new TestHelpers.StoreScope();
        var restore = await Tools().Restore(CancellationToken.None);

        ExtractContent(restore).Should().NotContain("Skills available");
    }
}
```

`ExtractInfo` / `ExtractContent` parse the YAML envelope; check whether an existing test helper already does this before writing new ones.

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test tests/Scrinia.Tests --filter FullyQualifiedName~RestoreEnvelopeTests`
Expected: the first and third FAIL.

- [ ] **Step 3: Extract one emitter**

Add a method beside `BuildRestoreContextAsync` that applies the context to a response builder, so both tools inherit one placement decision:

```csharp
    /// <summary>
    /// Applies a restore context to a response so that guide and restore place the
    /// same payload in the same fields. The two tools previously differed here, which
    /// meant the skills list arrived under info: from one and content: from the other.
    /// </summary>
    private static McpResponse ApplyRestoreContext(McpResponse response, RestoreContext ctx) =>
        response
            .WithActionNeeded(ctx.Warnings.ToArray())
            .WithInfo([.. ctx.Info, .. ctx.ContentSections])
            .WithInstruction(ctx.Instruction)
            .WithFollowUp(ctx.FollowUpNames.ToArray());
```

- [ ] **Step 4: Use it from both tools**

`Guide` keeps `ResponseBuilder.Success(guideContent).WithAction("guide")` and pipes through `ApplyRestoreContext`. `Restore` becomes `ResponseBuilder.Success(<summary line>).WithAction("restored")` piped through the same method, where the summary line states what was restored (counts), not the sections themselves.

- [ ] **Step 5: Run and verify the tests pass**

Run: `dotnet test tests/Scrinia.Tests --filter FullyQualifiedName~RestoreEnvelopeTests`
Expected: PASS. Other tests asserting `restore`'s old `content:` body will fail — update them; that is the intended break.

- [ ] **Step 6: Record the break**

Add to `CHANGELOG.md` under Unreleased → Changed, marked breaking: `restore` now returns the workspace context in `info:` with a summary line in `content:`, matching `guide`. Callers reading the sections out of `content:` must read `info:`.

- [ ] **Step 7: Run the full suite and commit**

```bash
dotnet test Scrinia.sln
git add src/Scrinia.Mcp CHANGELOG.md tests/Scrinia.Tests/Mcp/RestoreEnvelopeTests.cs
git commit -m "fix(mcp)!: guide and restore share one envelope for workspace context"
```

---

### Task 9: Lists on the wire become lists

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.cs` (`search` facade), `src/Scrinia.Mcp/MemoryTools.Core.cs` (`Search`), and any caller passing comma-strings
- Modify: `CHANGELOG.md`
- Test: `tests/Scrinia.Tests/Mcp/SearchParameterShapeTests.cs`

**Context:** `search` takes `attributeFilter` as a real map but `scopes` and `excludeTopics` as comma-separated strings, so the schema cannot express or validate a scope name containing a comma. **Breaking.**

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Scrinia.Tests/Mcp/SearchParameterShapeTests.cs
namespace Scrinia.Tests.Mcp;

public class SearchParameterShapeTests
{
    [Fact]
    public void SearchTool_TakesScopesAndExcludeTopicsAsArrays()
    {
        var search = typeof(ScriniaMcpTools).GetMethods()
            .Single(m => m.GetCustomAttributes()
                .Any(a => a.GetType().Name == "McpServerToolAttribute"
                          && (string?)a.GetType().GetProperty("Name")?.GetValue(a) == "search"));

        search.GetParameters().Single(p => p.Name == "scopes")
            .ParameterType.Should().Be(typeof(string[]));
        search.GetParameters().Single(p => p.Name == "excludeTopics")
            .ParameterType.Should().Be(typeof(string[]));
    }

    [Fact]
    public async Task AScopeNameContainingAComma_IsAddressable()
    {
        // The concrete capability the comma-string encoding cannot express.
        using var scope = new TestHelpers.StoreScope();
        var response = await Tools().Search(query: "x", scopes: ["odd,name"],
            excludeTopics: null, ct: CancellationToken.None);

        response.Status.Should().NotBe("error");
    }
}
```

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test tests/Scrinia.Tests --filter FullyQualifiedName~SearchParameterShapeTests`
Expected: FAIL — parameters are `string`.

- [ ] **Step 3: Change the parameter types**

Change `scopes` and `excludeTopics` to `string[]?` on the `search` facade and on any sibling tool that takes them the same way — `list` takes both, so check it too and change it for the same reason. Where the internal implementation still wants a comma-joined string for a downstream API, join at that point rather than pushing the encoding out to the caller.

Update the `[Description]` text: it currently describes comma-separated values and would otherwise contradict the schema.

- [ ] **Step 4: Run and verify the tests pass, then the full suite**

Run: `dotnet test Scrinia.sln`
Expected: callers passing comma-strings fail to compile or fail assertions; update them.

- [ ] **Step 5: Record the break and commit**

Add a `CHANGELOG.md` entry under Unreleased → Changed, marked breaking: `search` and `list` take `scopes` and `excludeTopics` as arrays; comma-separated strings are no longer accepted.

```bash
git add src/Scrinia.Mcp CHANGELOG.md tests/Scrinia.Tests/Mcp/SearchParameterShapeTests.cs
git commit -m "fix(mcp)!: scopes and excludeTopics are arrays, matching attributeFilter"
```

---

# Closing out

### Task 10: Close the branch

**Files:**
- Modify: `CHANGELOG.md`, `docs/architecture/overview.md`

- [ ] **Step 1: Verify every break is recorded**

Run: `git log --oneline main..HEAD`
Cross-check each `!`-marked commit against `CHANGELOG.md`. Expected breaking entries: `restore`'s envelope, and `search`/`list` taking arrays.

- [ ] **Step 2: Verify the two surface guarantees**

```bash
git grep -n "HasPermission" -- src/Scrinia.Server/Endpoints/
```
Expect only `KeyEndpoints.cs:52` — the privilege-escalation check on the request body, which is not route gating. Anything else is a guard the metadata migration missed.

```bash
git grep -c "CanonicalizeEphemeralAlias" -- src/
```
Expect `MemoryNaming.cs` (definition), `PathRouter.cs` (comment), `ScriniaCommands.cs` (CLI helper), `ScriniaMcpResources.cs`, `AgentLoop.cs`, and `MemoryTools.cs` at **1**.

- [ ] **Step 3: Full suite**

Run: `dotnet test Scrinia.sln`
Expected: green.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "docs: record branch 6's breaking changes and surface guarantees"
```

---

# Part C (M3) — split into its own branch

Part C originally lived here and was removed after the pre-implementation
falsification pass found it structurally blocked. **Do not implement M3 from this
plan.** What was learned is recorded in the Scrinia memory
`/findings/plugin-contract-inversion-blockers` and summarised here so the next
branch starts from evidence rather than from the original review finding:

1. **`IPluginStore` cannot be two members.** Both plugin-shaped sinks route append
   through `MemoryArtifactSource.ReadForAppendAsync` (`:93-101`), which needs
   `ParseQualifiedName`, `IsEphemeral`, `ResolveArtifactAsync` and
   `ReadArtifactAsync`. `ListScoped` is not used by the sink contract at all — only
   by `ReindexStoreAsync`, which is `IEmbeddingsAdmin`. A five-member interface
   weakens, though does not destroy, the rationale for pinning the assembly version.

2. **There are seven `IMemoryEventSink` implementations, not six.**
   `CoreEmbeddingEventHandler` (`src/Scrinia/Services/CoreEmbeddingEventHandler.cs:17`)
   was missed, and needs the same four members.

3. **The reversal drags ASP.NET Core into the trimmed CLI publish.**
   `Scrinia.Plugin.Abstractions` carries
   `<FrameworkReference Include="Microsoft.AspNetCore.App" />` because
   `IScriniaPlugin` takes `IApplicationBuilder` and `IEndpointRouteBuilder`.
   `FrameworkReference` is transitive across `ProjectReference`, so making
   `Scrinia.Core` depend on Abstractions puts the ASP.NET shared framework into
   `src/Scrinia`, which publishes `--self-contained -p:PublishTrimmed=true` with
   `IsTrimmable` (`publish.ps1:35`, `Scrinia.csproj:12`). This most likely means
   splitting Abstractions into an ASP.NET-free contract assembly and a hosting
   assembly — a design question, not a plan correction.

4. **The build-graph flip itself is clean.** Verified: no cycle is possible, the
   three DTOs are self-contained, no `[JsonSerializable]` or `Type.GetType` names
   them so the move is serialization-inert, and `_legacyFields` touches no
   `InternalsVisibleTo`. The blocker is the contract and the framework reference,
   not the graph.

5. **106 files outside `Scrinia.Core`** reference the three DTOs and would need
   either `<Using Include="Scrinia.Plugin.Abstractions" />` in ~11 csprojs or 106
   file edits. They all import `Scrinia.Core.Models`, which survives the move, so
   there is no compiler hint — just CS0246 on the type name.
