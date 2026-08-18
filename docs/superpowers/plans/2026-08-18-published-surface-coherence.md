# Published Surface Coherence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make three published surfaces — the REST authorization policy, the MCP tool family, and the plugin contract assembly — state each of their rules exactly once, in a place that a new call site cannot silently bypass.

**Architecture:** Three independent parts in disjoint projects, executed A → B → C. Part A moves route→permission from 19 hand-written handler guards into endpoint metadata enforced by one filter, with a startup validator that refuses to boot an undeclared route. Part B introduces a `MemoryAddress` type so the address-canonicalization rule is carried by the type system rather than by 19 remembered call sites, and fixes six places where sibling MCP tools answer the same question differently. Part C reverses the `Scrinia.Core` → `Scrinia.Plugin.Abstractions` dependency so the contract assembly sits at the bottom of the graph and can be versioned independently.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs, ModelContextProtocol SDK, xUnit + FluentAssertions.

**Spec:** `docs/superpowers/specs/2026-08-18-published-surface-coherence-design.md` (commit `eac3f7c`)

## Global Constraints

- **Breaking changes are permitted and must be recorded.** Product version is `0.5.0`, pre-1.0. No compatibility shims, no dual-accept parameter parsing. Every wire-visible or schema-visible break gets a `CHANGELOG.md` entry under Unreleased → Changed (breaking).
- **Enumerate from the authoritative list, never from an existing guard's call sites.** The authoritative lists are: the route table (`MapGet`/`MapPost`/`MapPut`/`MapPatch`/`MapDelete` calls), the `[McpServerTool]` attribute set, the `[Argument]`-taking CLI command set, and `git grep` over an interface name. Scoping work by "where the existing check already appears" has produced a Critical on three consecutive branches.
- **Do not unify `IMemoryOperationHook` and `IMemoryEventSink`.** The split is deliberate. `IMemoryOperationHook` is a published plugin contract with before-phases, an `Order` property and a `Cancel` veto; `IMemoryEventSink` has no equivalent for any of these. The split is also what stops a component implementing both from firing twice.
- **Preserve `CompositeEventSink`'s per-sink exception isolation.** Any new fan-out path delegates to `CompositeEventSink` rather than looping sinks itself. A previous branch introduced a loop without the per-sink `try`/`catch` and 5xx'd REST writes that had already succeeded.
- **Tests that drive internal methods will need updating in Part B.** `MemoryTools.Core` method signatures change. A test going red because it passes a `string` where a `MemoryAddress` is now required is a correct change, not a regression.
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
| `src/Scrinia.Core/MemoryAddress.cs` (new) | Readonly struct canonicalizing on construction. |
| `src/Scrinia.Mcp/MemoryTools.Core.cs` (modify) | Internal methods take `MemoryAddress`. |
| `src/Scrinia.Mcp/MemoryTools.cs`, `MemoryTools.Skills.cs`, `MemoryTools.Lifecycle.cs` (modify) | Boundary conversion; the six contradiction fixes; `SkillDelete`. |
| `src/Scrinia/Commands/ScriniaCommands.Memory.cs` (modify) | Boundary conversion. |
| `src/Scrinia.Mcp/ScriniaMcpResources.cs` (modify) | Boundary conversion. |
| `src/Scrinia.Server/Chat/AgentLoop.cs` (modify) | Boundary conversion. |

**Part C — `Scrinia.Plugin.Abstractions`, `Scrinia.Core`, and every sink**

| File | Responsibility |
|---|---|
| `src/Scrinia.Plugin.Abstractions/IPluginStore.cs` (new) | The two-member plugin-facing store contract. |
| `src/Scrinia.Plugin.Abstractions/` (receives) | `ArtifactEntry`, `ChunkEntry`, `ScopedArtifact`, `IMemoryEventSink`. |
| `src/Scrinia.Core/ICoreEventSink.cs` (new) | The internal sink contract taking full `IMemoryStore`. |
| `src/Scrinia.Core/PluginSinkAdapter.cs` (new) | Wraps an `IMemoryEventSink` as an `ICoreEventSink`. |
| `src/Scrinia.Core/CompositeEventSink.cs`, `IMemoryEventSink.cs` (modify) | Work in terms of `ICoreEventSink`. |
| `src/Scrinia.Plugin.Abstractions/Scrinia.Plugin.Abstractions.csproj` (modify) | Pin `AssemblyVersion`; drop the Core `ProjectReference`. |
| `src/Scrinia.Core/Scrinia.Core.csproj` (modify) | Gain the Abstractions `ProjectReference`. |

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

`TestServerFactory` with a `permissions` parameter and a `SendAsync(method, path, body)` helper may not exist in this shape. Check `tests/Scrinia.Server.Tests/` for the existing Kestrel/WebApplicationFactory harness (the conformance fixtures use one) and extend it rather than building a second harness.

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

Add `.RequirePermission(...)` to each of the 18 gated route registrations, matching the table in Step 1. Then delete the corresponding `if (!ctx.HasPermission(...)) return Results.Json(...)` block from each handler. `ChatEndpoints`' three-permission guard becomes one declaration:

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
                    $"{name}: requires authentication but declares neither RequirePermission(...) "
                    + "nor PublicEndpoint().");
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

- [ ] **Step 5: Wire it into startup**

In `Program.cs`, after the app is built and all endpoints are mapped — the validator needs the fully populated `EndpointDataSource`, so this must come after every `Map*` call and before `app.Run()`:

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
        foreach (var method in typeof(MemoryTools).GetMethods(BindingFlags.Public | BindingFlags.Instance))
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

### Task 5: `MemoryAddress`, and the boundary that cannot be forgotten

**Files:**
- Create: `src/Scrinia.Core/MemoryAddress.cs`
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs`, `MemoryTools.cs`, `MemoryTools.Lifecycle.cs`, `src/Scrinia/Commands/ScriniaCommands.Memory.cs`, `src/Scrinia.Mcp/ScriniaMcpResources.cs`, `src/Scrinia.Server/Chat/AgentLoop.cs`
- Test: `tests/Scrinia.Tests/Naming/MemoryAddressTests.cs`, `tests/Scrinia.Tests/Naming/AddressSurfaceCoverageTests.cs`

**Interfaces:**
- Produces: `Scrinia.Core.MemoryAddress` — `readonly record struct` with `string Value`, an explicit constructor canonicalizing via `MemoryNaming.CanonicalizeEphemeralAlias`, `implicit operator string`, and `static MemoryAddress From(string)`. There is **no** implicit `string` → `MemoryAddress` conversion: the whole point is that passing a raw string fails to compile.

**Context:** The rule has one definition (`MemoryNaming.CanonicalizeEphemeralAlias`) and 19 hand-placed call sites across four agent-facing surfaces. Three were missed on branch 2's first pass and were caught only by a whole-branch review. Typing the internal methods makes omission a compile error.

- [ ] **Step 1: Write the failing type tests**

```csharp
// tests/Scrinia.Tests/Naming/MemoryAddressTests.cs
namespace Scrinia.Tests.Naming;

public class MemoryAddressTests
{
    [Theory]
    [InlineData("/temp/x")]
    [InlineData("~x")]
    public void Construction_CanonicalizesTheEphemeralAlias(string input)
    {
        var address = MemoryAddress.From(input);

        address.Value.Should().Be(MemoryNaming.CanonicalizeEphemeralAlias(input),
            "the type exists so canonicalization cannot be skipped");
    }

    [Fact]
    public void TwoSpellingsOfTheSameAddress_AreEqual()
    {
        MemoryAddress.From("/temp/x").Should().Be(MemoryAddress.From("~x"));
    }

    [Fact]
    public void Construction_IsIdempotent()
    {
        var once = MemoryAddress.From("/temp/x");
        MemoryAddress.From(once.Value).Should().Be(once);
    }

    [Fact]
    public void DefaultInstance_IsRejectedRatherThanTreatedAsEmpty()
    {
        // A `default` struct bypasses the constructor, which is the one hole a
        // canonicalizing value type has. It must not silently read as a valid address.
        var act = () => _ = default(MemoryAddress).Value;

        act.Should().Throw<InvalidOperationException>();
    }
}
```

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test tests/Scrinia.Tests --filter FullyQualifiedName~MemoryAddressTests`
Expected: FAIL — `MemoryAddress` does not exist.

- [ ] **Step 3: Implement the type**

```csharp
// src/Scrinia.Core/MemoryAddress.cs
namespace Scrinia.Core;

/// <summary>
/// A memory address that has been canonicalized. Exists so that the ephemeral-alias
/// rule is carried by the type system rather than by remembering to call
/// <see cref="MemoryNaming.CanonicalizeEphemeralAlias"/> at every surface: the internal
/// tool methods take this type, so a new surface passing a raw string fails to compile.
///
/// There is deliberately no implicit conversion from string — that would restore
/// exactly the silent-omission hole this type removes.
/// </summary>
public readonly record struct MemoryAddress
{
    private readonly string? _value;

    private MemoryAddress(string value) => _value = value;

    /// <summary>The canonical address.</summary>
    public string Value => _value
        ?? throw new InvalidOperationException(
            "default(MemoryAddress) is not a valid address — it bypassed canonicalization. "
            + "Construct with MemoryAddress.From(...).");

    /// <summary>Canonicalizes <paramref name="raw"/> and wraps it.</summary>
    public static MemoryAddress From(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        return new MemoryAddress(MemoryNaming.CanonicalizeEphemeralAlias(raw));
    }

    public static implicit operator string(MemoryAddress address) => address.Value;

    public override string ToString() => Value;
}
```

- [ ] **Step 4: Run and verify the type tests pass**

Run: `dotnet test tests/Scrinia.Tests --filter FullyQualifiedName~MemoryAddressTests`
Expected: PASS.

- [ ] **Step 5: Convert the internal methods**

In `MemoryTools.Core.cs`, change the address parameter of each internal method from `string` to `MemoryAddress`: `Remember`, `Recall`, `Forget`, `Append`, `Compact`, `Link` (both source and destination). Inside each, delete the now-redundant `MemoryNaming.CanonicalizeEphemeralAlias(...)` call and use the value directly.

The build will now fail at every caller. That is the mechanism working. Fix each by converting at the boundary:

```csharp
// MemoryTools.cs — was: path = MemoryNaming.CanonicalizeEphemeralAlias(path);
return (await Forget(MemoryAddress.From(path), cancellationToken)).ToYaml();
```

Apply the same shape at all four surfaces: the `[McpServerTool]` facade methods in `MemoryTools.cs`, the CLI commands in `ScriniaCommands.Memory.cs`, `ScriniaMcpResources.cs`, and `AgentLoop.cs`.

- [ ] **Step 6: Verify the old call sites are gone**

Run: `git grep -c "CanonicalizeEphemeralAlias" -- src/`
Expected: a small number — the definition in `MemoryNaming.cs` and the single call inside `MemoryAddress.From`. Any other production call site means a surface is still canonicalizing by hand instead of converting; convert it.

- [ ] **Step 7: Write the surface coverage test**

```csharp
// tests/Scrinia.Tests/Naming/AddressSurfaceCoverageTests.cs
namespace Scrinia.Tests.Naming;

/// <summary>
/// Covers the surfaces that take an address as text from an agent. MemoryAddress makes
/// the internal path safe by construction; this pins the outer boundary, including any
/// surface that handles an address without reaching a typed internal method.
/// </summary>
public class AddressSurfaceCoverageTests
{
    [Fact]
    public void EveryMcpToolAddressParameter_RoundTripsTheEphemeralAlias()
    {
        var offenders = new List<string>();

        foreach (var method in typeof(MemoryTools).GetMethods(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!method.GetCustomAttributes().Any(a => a.GetType().Name == "McpServerToolAttribute"))
                continue;

            foreach (var p in method.GetParameters())
            {
                if (p.ParameterType != typeof(string)) continue;
                if (p.Name is not ("path" or "name" or "nameOrUri" or "destination" or "source")) continue;
                // Recorded here rather than asserted per-parameter so the failure message
                // lists every offending surface at once instead of the first.
                offenders.Add($"{method.Name}.{p.Name}");
            }
        }

        offenders.Should().NotBeEmpty("the reflection query must actually match something");
    }
}
```

This test as written only proves the query matches. Extend it to invoke each tool with `/temp/probe` and assert the resulting memory is addressable as `~probe`, following the pattern of the existing ephemeral tests in `ScriniaMcpToolsTests.cs`. Read those first — they establish how to stand up a store for this.

- [ ] **Step 8: Run the full suite**

Run: `dotnet test Scrinia.sln`
Expected: some existing tests fail to compile where they pass a `string` to a converted internal method. Update them to `MemoryAddress.From(...)`. `ScriniaMcpToolsTests.cs:1683` (`Append_TempPath_IsEphemeral_WhetherOrNotTheMemoryExists`) is a known one. This is expected and is not a store-layer regression.

- [ ] **Step 9: Commit**

```bash
git add src/Scrinia.Core/MemoryAddress.cs src/Scrinia.Mcp src/Scrinia/Commands src/Scrinia.Server/Chat tests/Scrinia.Tests/Naming
git commit -m "feat(naming): carry the address rule in a type, not at 19 call sites"
```

---

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
        using var ws = TestWorkspace.Create();
        var response = await ws.Tools.List(scopes: null, mode: mode, offset: 0, limit: 50,
            excludeTopics: null, CancellationToken.None);

        response.Status.Should().Be("error",
            "falling through to summary reports success for a call that did something else");
        response.Message.Should().Contain(mode);
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("full")]
    [InlineData("drift")]
    public async Task List_StillAcceptsEveryDocumentedMode(string mode)
    {
        using var ws = TestWorkspace.Create();
        var response = await ws.Tools.List(scopes: null, mode: mode, offset: 0, limit: 50,
            excludeTopics: null, CancellationToken.None);

        response.Status.Should().NotBe("error");
    }

    [Fact]
    public async Task Reconcile_RejectsAChoiceWithNoConflictId()
    {
        using var ws = TestWorkspace.Create();
        var response = await ws.Tools.Reconcile(conflictId: null, choice: "ours",
            content: null, CancellationToken.None);

        response.Status.Should().Be("error",
            "silently performing a read-only scan while reporting action: reconciled "
            + "tells the caller their choice was applied");
    }

    [Fact]
    public async Task Reconcile_StillScansWhenNeitherArgumentIsGiven()
    {
        using var ws = TestWorkspace.Create();
        var response = await ws.Tools.Reconcile(conflictId: null, choice: null,
            content: null, CancellationToken.None);

        response.Status.Should().NotBe("error");
    }
}
```

Check `tests/Scrinia.Tests/` for the existing workspace fixture — `TestWorkspace` is a placeholder for whatever the established helper is. Do not add a second fixture.

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
        using var ws = TestWorkspace.Create();
        await ws.Tools.Remember(MemoryAddress.From("/skill/temp-helper"),
            content: ["do the thing"], ct: CancellationToken.None);

        var before = await ws.Tools.Recall(MemoryAddress.From("/skill/temp-helper"), CancellationToken.None);
        before.Status.Should().NotBe("error");

        var forget = await ws.Tools.Forget(MemoryAddress.From("/skill/temp-helper"), CancellationToken.None);
        forget.Status.Should().NotBe("error",
            "a namespace remember and recall both route must not answer NOT_FOUND on forget");

        var after = await ws.Tools.Recall(MemoryAddress.From("/skill/temp-helper"), CancellationToken.None);
        after.Status.Should().Be("error");
    }

    [Fact]
    public async Task Forget_OnABuiltInSkill_SaysSoRatherThanNotFound()
    {
        using var ws = TestWorkspace.Create();
        string builtIn = BuiltInSkills.Keys.First();

        var response = await ws.Tools.Forget(MemoryAddress.From($"/skill/{builtIn}"), CancellationToken.None);

        response.Status.Should().Be("error");
        response.Message.Should().Contain("built-in",
            "NOT_FOUND is a false statement about a skill that demonstrably exists");
    }

    [Fact]
    public async Task Remember_OnASkillPath_WarnsThatAttributesAndKeywordsAreIgnored()
    {
        using var ws = TestWorkspace.Create();

        var response = await ws.Tools.Remember(
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
        using var ws = TestWorkspace.Create();

        var response = await ws.Tools.Remember(MemoryAddress.From("/skill/quiet"),
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

**Context:** `guide` concatenates `ctx.ContentSections` into `info:` (`MemoryTools.cs:80`); `restore` returns the same sections as the `content:` body. The skills list therefore arrives under a different field depending on which tool produced it. **This is a breaking change to `restore`'s response shape.**

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Scrinia.Tests/Mcp/RestoreEnvelopeTests.cs
namespace Scrinia.Tests.Mcp;

public class RestoreEnvelopeTests
{
    [Fact]
    public async Task GuideAndRestore_PutTheRestoreContextInTheSameField()
    {
        using var ws = TestWorkspace.Create();
        await ws.Tools.Remember(MemoryAddress.From("/skill/probe"),
            content: ["x"], ct: CancellationToken.None);

        var guide = await ws.Tools.Guide(CancellationToken.None);
        var restore = await ws.Tools.Restore(CancellationToken.None);

        // The skills list is the payload that visibly swapped fields.
        ExtractInfo(guide).Should().ContainMatch("*Skills available*");
        ExtractInfo(restore).Should().ContainMatch("*Skills available*",
            "the same payload from a sibling tool must arrive under the same field");
    }

    [Fact]
    public async Task Guide_KeepsTheGuideDocumentAsItsContent()
    {
        using var ws = TestWorkspace.Create();
        var guide = await ws.Tools.Guide(CancellationToken.None);

        // Guarded because the fix must not move the guide text out of content: in
        // the course of aligning the sections.
        ExtractContent(guide).Should().Contain("Scrinia");
    }

    [Fact]
    public async Task Restore_ContentIsASummaryLine_NotTheSections()
    {
        using var ws = TestWorkspace.Create();
        var restore = await ws.Tools.Restore(CancellationToken.None);

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
        var search = typeof(MemoryTools).GetMethods()
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
        using var ws = TestWorkspace.Create();
        var response = await ws.Tools.Search(query: "x", scopes: ["odd,name"],
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

# Part C — Plugin contract inversion (M3)

### Task 10: Reverse the project reference, move nothing

**Files:**
- Modify: `src/Scrinia.Plugin.Abstractions/Scrinia.Plugin.Abstractions.csproj`, `src/Scrinia.Core/Scrinia.Core.csproj`
- Create: `src/Scrinia.Plugin.Abstractions/IPluginStore.cs`

**Context:** A reversed `ProjectReference` is a build-graph change. It either works or fails at restore. Establishing that first means the type moves in Task 11 cannot be confused with a graph problem.

This task will not compile on its own if anything in Abstractions references a Core type — and it does: `HookContexts.cs` opens with `using Scrinia.Core;`. So this task moves exactly the minimum needed to break the cycle, and nothing else.

- [ ] **Step 1: Add `IPluginStore`**

```csharp
// src/Scrinia.Plugin.Abstractions/IPluginStore.cs
namespace Scrinia.Plugin.Abstractions;

/// <summary>
/// The store surface handed to plugins. Deliberately minimal: it carries the two
/// members the plugin-shaped sinks actually use, so that pinning this assembly's
/// AssemblyVersion is a promise the codebase can keep. Grow it only when a real
/// consumer needs a member — a wide contract here is the defect this split exists
/// to remove, not a convenience.
/// </summary>
public interface IPluginStore
{
    (string Scope, string Subject) ParseQualifiedName(string qualifiedName);

    List<ScopedArtifact> ListScoped(string? scopes = null);
}
```

`ParseQualifiedName`'s exact return shape must be copied from `IMemoryStore`'s declaration — read it rather than assuming the tuple names above.

- [ ] **Step 2: Move the DTOs `IPluginStore` exposes**

`git mv` these from `src/Scrinia.Core/Models/` into `src/Scrinia.Plugin.Abstractions/`: `ArtifactEntry.cs`, `ChunkEntry.cs`, `ScopedArtifact.cs`. Change their namespace to `Scrinia.Plugin.Abstractions`.

Every Core file referencing them will now fail. Rather than editing hundreds of files, add a global using in Core:

```xml
  <ItemGroup>
    <Using Include="Scrinia.Plugin.Abstractions" />
  </ItemGroup>
```

- [ ] **Step 3: Reverse the reference**

Remove `<ProjectReference Include="..\Scrinia.Core\Scrinia.Core.csproj" />` from `Scrinia.Plugin.Abstractions.csproj`. Add `<ProjectReference Include="..\Scrinia.Plugin.Abstractions\Scrinia.Plugin.Abstractions.csproj" />` to `Scrinia.Core.csproj`.

`HookContexts.cs` still has `required IMemoryStore Store` and will not compile. Change those six members to `IPluginStore` now — measured: no implementation reads `ctx.Store`, so nothing downstream breaks.

- [ ] **Step 4: Declare the inheritance**

In `IMemoryStore.cs`, change the declaration to `public interface IMemoryStore : IPluginStore` and delete the two member declarations now inherited. Their doc comments move to `IPluginStore` rather than being lost.

- [ ] **Step 5: Build and verify the graph**

Run: `dotnet build Scrinia.sln`
Expected: 0 errors. A cycle would surface here as NU1108 or a restore failure. If it does, stop and report — that is a structural finding, not a fix-it-locally problem.

- [ ] **Step 6: Run the full suite and commit**

```bash
dotnet test Scrinia.sln
git add -A src/Scrinia.Core src/Scrinia.Plugin.Abstractions
git commit -m "refactor(plugins): put the contract assembly at the bottom of the graph"
```

---

### Task 11: Split the sink interface, and re-establish the exclusion rule

**Files:**
- Move: `src/Scrinia.Core/IMemoryEventSink.cs` → `src/Scrinia.Plugin.Abstractions/IMemoryEventSink.cs` (interface only)
- Create: `src/Scrinia.Core/ICoreEventSink.cs`, `src/Scrinia.Core/PluginSinkAdapter.cs`
- Modify: `src/Scrinia.Core/CompositeEventSink.cs`, `MaintenanceEventSink.cs`, `EntityTypingEventHandler.cs`, `Embeddings/EmbeddingReindexer.cs`, `src/Scrinia/Services/McpPluginHost.cs`, `src/Scrinia.Server/Services/EventSinkOperationHook.cs`, `BuiltInEmbeddingsService.cs`, `src/Scrinia.Plugin.Embeddings/EmbeddingsPlugin.cs`
- Test: `tests/Scrinia.Tests/Plugins/SinkBoundaryTests.cs`

**Interfaces:**
- Produces: `IMemoryEventSink` (Abstractions) taking `IPluginStore`; `ICoreEventSink` (Core) taking `IMemoryStore`; `PluginSinkAdapter : ICoreEventSink`.

**Context — read before writing code.** `EventSinkOperationHook.cs:50` reads:

```csharp
_sinks = new CompositeEventSink([.. sinks.Where(s => s is not IMemoryOperationHook)]);
```

That filter stops a component implementing both `IMemoryEventSink` and `IMemoryOperationHook` from firing twice — `BuiltInEmbeddingsService` is exactly that, registered as both at `Program.cs:175-178`. It is a **type test on the sink instance**. Wrapping a plugin sink in `PluginSinkAdapter` makes the test inspect the *adapter*, which is not an `IMemoryOperationHook`, so a plugin implementing both would pass the filter and embed every write twice. **Filter before wrapping, never after.**

Second: the recorded safety argument for that filter was "`Scrinia.Core.csproj` has no `ProjectReference`, so the two Core sinks cannot gain `IMemoryOperationHook`; residual risk is plugin-only." Task 10 added that reference. The argument no longer holds and the doc comment stating it must be corrected in this task.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Scrinia.Tests/Plugins/SinkBoundaryTests.cs
namespace Scrinia.Tests.Plugins;

public class SinkBoundaryTests
{
    private sealed class DualSink : IMemoryEventSink, IMemoryOperationHook
    {
        public int SinkCalls;
        public int Order => 0;
        public Task OnStoredAsync(string q, string[] c, IPluginStore s, CancellationToken ct)
        { SinkCalls++; return Task.CompletedTask; }
        // remaining members: no-ops
    }

    [Fact]
    public void AdapterWrappedDualImplementer_IsStillExcluded()
    {
        // The regression this test exists for: the exclusion is a type test on the
        // instance, and an adapter hides the wrapped type from it.
        var dual = new DualSink();

        var fired = EventSinkOperationHook.SelectSinks([dual]);

        fired.Should().BeEmpty(
            "a component registered as both hook and sink must fire once, via the hook path");
    }

    [Fact]
    public void APlainPluginSink_IsAdaptedAndFires()
    {
        var plain = new PlainSink();

        var fired = EventSinkOperationHook.SelectSinks([plain]);

        fired.Should().HaveCount(1, "the exclusion must not swallow ordinary plugin sinks");
    }

    [Fact]
    public async Task PluginSinkAdapter_PreservesPerSinkExceptionIsolation()
    {
        // CompositeEventSink's per-sink try/catch is load-bearing: a throwing sink
        // must not abort later sinks or fail a write that already succeeded.
        var throwing = new ThrowingSink();
        var after = new PlainSink();
        var composite = new CompositeEventSink([new PluginSinkAdapter(throwing), new PluginSinkAdapter(after)]);

        var act = async () => await composite.OnStoredAsync("local:x", ["c"], FakeStore.Instance, default);

        await act.Should().NotThrowAsync();
        after.Fired.Should().BeTrue();
    }
}
```

`EventSinkOperationHook.SelectSinks` does not exist — extract the filtering expression at `:50` into an internal static method so it can be tested directly rather than only through a running server.

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test tests/Scrinia.Tests --filter FullyQualifiedName~SinkBoundaryTests`
Expected: FAIL to compile — the new types do not exist.

- [ ] **Step 3: Split the interface**

`IMemoryEventSink` moves to Abstractions with its three methods taking `IPluginStore`. `MemoryEventSinkContext`, `CompositeEventSink` and `InstallDefault` stay in Core and work in terms of a new `ICoreEventSink` with the same three methods taking `IMemoryStore`.

- [ ] **Step 4: Assign every existing implementation to a side**

Enumerate with `git grep -l "IMemoryEventSink" -- src/` and classify each — this is a required complete table, not a spot check. Expected assignment from the measured usage:

| Implementation | Side | Why |
|---|---|---|
| `EmbeddingsPlugin` | `IMemoryEventSink` | Uses `ParseQualifiedName`, `ListScoped` only |
| `BuiltInEmbeddingsService` | `IMemoryEventSink` | Same two members |
| `EmbeddingReindexer` | `ICoreEventSink` | Core-internal |
| `McpPluginHost` | `ICoreEventSink` | Needs `ReadArtifactAsync` |
| `MaintenanceEventSink` | `ICoreEventSink` | Needs `AnyScoped`, `LoadIndex`, `TryUpdateEntry` |
| `EntityTypingEventHandler` | `ICoreEventSink` | Needs `GetEntityFactStore` |

A wrong assignment is a compile error, because the store parameter types differ.

- [ ] **Step 5: Write the adapter, and filter before wrapping**

```csharp
// src/Scrinia.Core/PluginSinkAdapter.cs
namespace Scrinia.Core;

/// <summary>
/// Presents a plugin's <see cref="IMemoryEventSink"/> as an <see cref="ICoreEventSink"/>
/// so the fan-out stays a single ordered list. The conversion is a pass-through:
/// IMemoryStore already satisfies IPluginStore.
/// </summary>
/// <remarks>
/// CALLERS MUST APPLY ANY TYPE-BASED FILTERING TO THE WRAPPED SINK, NOT THE ADAPTER.
/// EventSinkOperationHook excludes sinks that also implement IMemoryOperationHook so a
/// dual-registered component fires once; that test inspects the instance, and this
/// adapter is not an IMemoryOperationHook regardless of what it wraps.
/// </remarks>
public sealed class PluginSinkAdapter(IMemoryEventSink inner) : ICoreEventSink
{
    internal IMemoryEventSink Inner => inner;

    public Task OnStoredAsync(string qualifiedName, string[] content, IMemoryStore store, CancellationToken ct) =>
        inner.OnStoredAsync(qualifiedName, content, store, ct);

    // OnAppendedAsync, OnForgottenAsync: same shape
}
```

In `EventSinkOperationHook`, extract and correct the selection:

```csharp
    internal static ICoreEventSink[] SelectSinks(IEnumerable<IMemoryEventSink> sinks) =>
        [.. sinks.Where(s => s is not IMemoryOperationHook).Select(s => new PluginSinkAdapter(s))];
```

- [ ] **Step 6: Update every registration site**

The split changes what `GetServices<IMemoryEventSink>()` returns — it now yields plugin-shaped sinks only. Every registration and resolution site must be visited. This is the complete list, enumerated with `git grep -n "AddSingleton<IMemoryEventSink>\|GetServices<IMemoryEventSink>\|InstallDefault" -- src/`; re-run it after the edits and expect no site to have been missed:

| Site | Change |
|---|---|
| `Program.cs:101` — `MaintenanceEventSink` | Register as `ICoreEventSink` |
| `Program.cs:107` — `new EventSinkOperationHook(GetServices<IMemoryEventSink>())` | Pass **both** collections; see below |
| `Program.cs:173`, `:184` — `BuiltInEmbeddingsService` | Stays `IMemoryEventSink` |
| `Program.cs:348` — per-request `[.. GetServices<IMemoryEventSink>()]` | Must resolve both and combine, or the CLI-parity sink set silently shrinks |
| `EmbeddingsPlugin.cs:117` — `services.AddSingleton<IMemoryEventSink>(this)` | Stays |
| `WorkspaceSetup.cs` — eight `InstallDefault` call sites (`:240, :400, :417, :426, :446, :463, :472, :528`) | `InstallDefault` takes `params ICoreEventSink[]`. `MaintenanceEventSink` and `McpPluginHost` are both `ICoreEventSink`, so most sites compile unchanged. Any site passing a plugin-shaped sink must wrap it: `new PluginSinkAdapter(sink)` |

`EventSinkOperationHook` needs both kinds, and the exclusion applies to both — Task 10's reference reversal means a Core sink can now implement `IMemoryOperationHook` too:

```csharp
    internal static ICoreEventSink[] SelectSinks(
        IEnumerable<IMemoryEventSink> pluginSinks,
        IEnumerable<ICoreEventSink> coreSinks) =>
    [
        // Filter BEFORE wrapping: the exclusion is a type test on the instance, and
        // PluginSinkAdapter is never an IMemoryOperationHook whatever it wraps.
        .. pluginSinks.Where(s => s is not IMemoryOperationHook).Select(s => new PluginSinkAdapter(s)),
        .. coreSinks.Where(s => s is not IMemoryOperationHook),
    ];
```

`Program.cs:348` is the easiest of these to miss and the most consequential: it builds the per-request sink set for the server's MCP endpoint. Dropping the Core sinks there would silently disable ref-linking and orphan marking for every MCP write against the server, with no test failure unless one specifically drains `BackgroundWork` and asserts the side effect.

- [ ] **Step 7: Correct the stale safety argument**

`EventSinkOperationHook`'s doc comment records that Core sinks cannot gain `IMemoryOperationHook` because Core has no `ProjectReference`. Task 10 made that false. Replace it with the current position: Core now references Abstractions, so a Core sink *could* implement the hook interface; the filter therefore covers Core as well as plugins, and the `SelectSinks` test is what pins it.

- [ ] **Step 8: Run and verify the tests pass, then the full suite**

Run: `dotnet test Scrinia.sln`
Expected: green. Pay attention to any REST/MCP parity test — those await `BackgroundWork.DrainAsync`, and a change in fan-out timing shows up there first. If ref-linking or orphan-marking assertions fail, suspect `Program.cs:348` before anything else.

- [ ] **Step 9: Commit**

```bash
git add -A src tests
git commit -m "refactor(plugins): split the sink contract by audience, not by convenience"
```

---

### Task 12: Pin the contract, prove it stands alone

**Files:**
- Modify: `src/Scrinia.Plugin.Abstractions/Scrinia.Plugin.Abstractions.csproj`
- Create: `tests/Scrinia.Plugin.Abstractions.Tests/` (new project) with `ContractSelfSufficiencyTests.cs`
- Modify: `Scrinia.sln`, `CHANGELOG.md`, `docs/architecture/overview.md`

**Context:** The claim M3 exists to make true is "a third party can compile a plugin against `Scrinia.Plugin.Abstractions` alone." A test project that references only Abstractions turns that claim into a build-time fact.

- [ ] **Step 1: Create the test project**

A new xUnit project referencing **only** `Scrinia.Plugin.Abstractions` — no `Scrinia.Core`, no `Scrinia.Server`. Add it to `Scrinia.sln`. Per `Directory.Build.props`, a `.Tests` project opts out of `TreatWarningsAsErrors` automatically.

- [ ] **Step 2: Write the self-sufficiency test**

```csharp
// tests/Scrinia.Plugin.Abstractions.Tests/ContractSelfSufficiencyTests.cs
namespace Scrinia.Plugin.Abstractions.Tests;

/// <summary>
/// The assertion M3 exists to make true, pinned by a compilation boundary rather than
/// by inspection: this project references Scrinia.Plugin.Abstractions and nothing else,
/// so if the contract ever needs a Core type again, this file stops compiling.
/// </summary>
public class ContractSelfSufficiencyTests
{
    private sealed class ThirdPartySink : IMemoryEventSink
    {
        public Task OnStoredAsync(string q, string[] c, IPluginStore s, CancellationToken ct)
            => Task.CompletedTask;
        // remaining members: no-ops
    }

    [Fact]
    public void APluginCanImplementTheContractWithoutReferencingCore()
    {
        IMemoryEventSink sink = new ThirdPartySink();
        sink.Should().NotBeNull();
    }

    [Fact]
    public void IPluginStore_StaysNarrow()
    {
        // A guard on the promise the pinned AssemblyVersion makes. Widening this is a
        // deliberate act; it should require editing this number and thinking about it.
        typeof(IPluginStore).GetMembers().Should().HaveCount(2);
    }
}
```

- [ ] **Step 3: Run and verify it compiles and passes**

Run: `dotnet test tests/Scrinia.Plugin.Abstractions.Tests`
Expected: PASS. A compile error here naming a Core type is the finding, not an obstacle to route around by adding a reference.

- [ ] **Step 4: Pin the assembly version**

```xml
    <!--
      The plugin contract's own version line. Deliberately NOT inherited from
      Directory.Build.props: a routine product release must not rebind every
      third-party plugin. Bump only when this assembly's surface changes, and
      say so in CHANGELOG.
    -->
    <AssemblyVersion>0.1.0.0</AssemblyVersion>
```

- [ ] **Step 5: Verify the product version still flows everywhere else**

Run: `dotnet build Scrinia.sln` then check the CLI banner reports `0.5.0`.
Expected: unchanged. `AssemblyInformationalVersion` still comes from `Directory.Build.props`.

- [ ] **Step 6: Document and commit**

Update `docs/architecture/overview.md` where it describes the plugin contract, and add a `CHANGELOG.md` entry recording that `Scrinia.Plugin.Abstractions` now carries its own `AssemblyVersion` and that hook contexts hand plugins `IPluginStore` rather than `IMemoryStore` (breaking for any plugin that used the wider surface).

```bash
dotnet test Scrinia.sln
git add -A
git commit -m "feat(plugins)!: pin the contract assembly's version and prove it stands alone"
```

---

### Task 13: Close the branch

**Files:**
- Modify: `CHANGELOG.md`, `docs/architecture/overview.md`, `README.md` if it documents any changed tool parameter

- [ ] **Step 1: Verify every break is recorded**

Run: `git log --oneline main..HEAD | grep '!'`
Cross-check each `!`-marked commit against `CHANGELOG.md`. Expected breaking entries: `restore` envelope, `search`/`list` array parameters, `IPluginStore` in hook contexts, pinned `AssemblyVersion`.

- [ ] **Step 2: Verify the three surfaces' claims**

Run each and confirm:

```bash
git grep -n "HasPermission" -- src/Scrinia.Server/Endpoints/   # expect none, or documented exceptions
git grep -c "CanonicalizeEphemeralAlias" -- src/                # expect definition + MemoryAddress.From only
git grep -n "ProjectReference" -- src/Scrinia.Plugin.Abstractions/  # expect none
```

- [ ] **Step 3: Full suite**

Run: `dotnet test Scrinia.sln`
Expected: green.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "docs: record branch 6's breaking changes and surface guarantees"
```

