# Secrets & Transport Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the three confirmed secrets & transport findings from the 2026-07-29 review — a leaked plugin API key on the child command line, a bearer key sent over cleartext HTTP, and silently-swallowed remote write failures.

**Architecture:** Three independent fixes. T2 makes the remote store's `WriteArtifactAsync` throw on non-success (matching `FileMemoryStore`). T1 adds a `RemoteTransportGuard` in Core that the `serve --remote` path calls before wiring the HTTP client, refusing a cleartext key to a non-loopback host unless opted in. S1 adds a shared `PluginConfigProtocol` in Core so the CLI host forwards secret config to the plugin child via the process environment instead of argv.

**Tech Stack:** C# / .NET 10, xUnit + FluentAssertions, ModelContextProtocol 1.0.0 (StdioClientTransport), `System.Net.Http`.

## Global Constraints

- No new NuGet dependencies. Build and test WITHOUT `-p:NuGetAudit=false` (Stream 3 cleared the audit; the flag is obsolete and masks future advisories).
- `TreatWarningsAsErrors=true` on non-test/bench src projects (root `Directory.Build.props`) — a warning fails the build. New code must be warning-clean.
- Do NOT stage, commit, revert, or edit these four working-tree files: `.gitignore`, `src/Scrinia.Mcp/plugins/opencode/package.json`, `src/Scrinia.Mcp/plugins/opencode/scrinia.ts`, `src/Scrinia.Mcp/prompts/guide.md`.
- `Scrinia.Merge` is untouched by this stream.
- Tests use xUnit (`[Fact]`) + FluentAssertions (`.Should()`). `Scrinia.csproj` already has `<InternalsVisibleTo Include="Scrinia.Tests" />`; `tests/Scrinia.Tests` references both `Scrinia` (assembly `scri`) and `Scrinia.Core`.
- The LSP may emit phantom CS diagnostics on edited/new files while `dotnet build` is clean — trust `dotnet build` (0 errors) and `git status`, not the diagnostic stream.

---

### Task 1: T2 — Remote writes throw on non-success

**Files:**
- Modify: `src/Scrinia/HttpMemoryStore.cs` (`WriteArtifactAsync`, currently ~L472-493)
- Test: `tests/Scrinia.Tests/HttpMemoryStoreWriteFailureTests.cs` (create)

**Interfaces:**
- Consumes: existing `HttpMemoryStore(HttpClient http, string store, TimeSpan? timeout, CircuitBreaker?, RetryOptions?)` ctor; `Nmp2ChunkedEncoder.Encode(string)` (produces a valid NMP/2 artifact string).
- Produces: `WriteArtifactAsync` now throws `System.Net.Http.HttpRequestException` (with `StatusCode` set) when the server returns a non-success status after retries; unchanged (completes) on success.

**Context:** `WriteArtifactAsync` posts through `RetryPolicy.ExecuteAsync`, then only updates the circuit breaker and returns `void` — a 4xx/5xx is swallowed and the caller reports a successful store. `FileMemoryStore.WriteArtifactAsync` throws on failure. `qualifiedName` is already a local in the method. `WriteArtifactAsync` first calls `Nmp2Strategy.Instance.Decode(artifactText)`, so tests must pass a real NMP/2 artifact. `BaseUrl` is relative (`/api/v1/stores/...`), so the test `HttpClient` needs a `BaseAddress`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Tests/HttpMemoryStoreWriteFailureTests.cs`:

```csharp
using System.Net;
using FluentAssertions;
using Scrinia;
using Scrinia.Core.Encoding;
using Scrinia.Core.Resilience;
using Xunit;

namespace Scrinia.Tests;

public class HttpMemoryStoreWriteFailureTests
{
    /// <summary>Test handler that returns a fixed status for every request.</summary>
    private sealed class FixedStatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private static HttpMemoryStore MakeStore(HttpStatusCode status, out FixedStatusHandler handler)
    {
        handler = new FixedStatusHandler(status);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000") };
        // MaxRetries: 0 keeps transient-status tests fast (no backoff sleeps).
        return new HttpMemoryStore(http, "default", retryOptions: new RetryOptions(MaxRetries: 0));
    }

    private static string Artifact() => Nmp2ChunkedEncoder.Encode("hello world");

    [Fact]
    public async Task WriteArtifactAsync_NonTransientFailure_Throws()
    {
        using var store = MakeStore(HttpStatusCode.Forbidden, out _);

        var act = async () => await store.WriteArtifactAsync("note", "local", Artifact());

        (await act.Should().ThrowAsync<HttpRequestException>())
            .Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task WriteArtifactAsync_TransientFailure_ThrowsAfterRetries()
    {
        using var store = MakeStore(HttpStatusCode.ServiceUnavailable, out _);

        var act = async () => await store.WriteArtifactAsync("note", "local", Artifact());

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task WriteArtifactAsync_Success_DoesNotThrow()
    {
        using var store = MakeStore(HttpStatusCode.OK, out _);

        var act = async () => await store.WriteArtifactAsync("note", "local", Artifact());

        await act.Should().NotThrowAsync();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~HttpMemoryStoreWriteFailureTests"`
Expected: the two failure-case tests FAIL (no exception thrown — the write is silently swallowed); the success test PASSES.

- [ ] **Step 3: Make `WriteArtifactAsync` throw on non-success**

In `src/Scrinia/HttpMemoryStore.cs`, `WriteArtifactAsync`, replace the trailing status block:

```csharp
        if (resp.IsSuccessStatusCode)
            _circuitBreaker.RecordSuccess();
        else if (TransientDetector.IsTransient(resp))
            _circuitBreaker.RecordFailure();
```

with:

```csharp
        if (resp.IsSuccessStatusCode)
        {
            _circuitBreaker.RecordSuccess();
        }
        else
        {
            // A reachable server that answered with a failure status must not look like a
            // successful store to callers (matches FileMemoryStore's throw-on-failure contract).
            if (TransientDetector.IsTransient(resp))
                _circuitBreaker.RecordFailure();
            throw new HttpRequestException(
                $"Remote store write failed for '{qualifiedName}': {(int)resp.StatusCode} {resp.StatusCode}.",
                inner: null,
                statusCode: resp.StatusCode);
        }
```

(`HttpRequestException` is in `System.Net.Http`, already available; `resp.StatusCode` is `System.Net.HttpStatusCode`. No new usings needed.)

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~HttpMemoryStoreWriteFailureTests"`
Expected: all three PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia/HttpMemoryStore.cs tests/Scrinia.Tests/HttpMemoryStoreWriteFailureTests.cs
git commit -m "fix(remote): WriteArtifactAsync throws on non-success (T2)"
```

---

### Task 2: T1 — Refuse a bearer key over non-TLS transport

**Files:**
- Create: `src/Scrinia.Core/Resilience/RemoteTransportGuard.cs`
- Modify: `src/Scrinia/Commands/ScriniaCommands.Serve.cs` (`Serve` signature + remote block, currently ~L29-47)
- Test: `tests/Scrinia.Tests/RemoteTransportGuardTests.cs` (create)

**Interfaces:**
- Produces: `Scrinia.Core.Resilience.RemoteTransportGuard.Validate(Uri remote, bool hasApiKey, bool allowInsecure, Action<string> warn)` — returns void, throws `Scrinia.Core.Resilience.InsecureTransportException` on refusal. `InsecureTransportException : Exception`.
- Consumes (in Serve): the new `Validate` method; a new CLI param `bool allowInsecureTransport = false`.

**Policy (approved):** With an API key present, allow HTTPS and loopback silently; for non-loopback HTTP, throw unless `allowInsecure`, in which case warn and proceed. No key, or loopback, or HTTPS → silent OK.

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Tests/RemoteTransportGuardTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Core.Resilience;
using Xunit;

namespace Scrinia.Tests;

public class RemoteTransportGuardTests
{
    private static List<string> Run(string url, bool hasApiKey, bool allowInsecure)
    {
        var warnings = new List<string>();
        RemoteTransportGuard.Validate(new Uri(url), hasApiKey, allowInsecure, warnings.Add);
        return warnings;
    }

    [Theory]
    [InlineData("http://localhost:5000")]
    [InlineData("http://127.0.0.1:5000")]
    [InlineData("http://[::1]:5000")]
    public void Loopback_WithKey_IsAllowedSilently(string url)
    {
        var warnings = Run(url, hasApiKey: true, allowInsecure: false);
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void Https_WithKey_IsAllowedSilently()
    {
        var warnings = Run("https://example.com", hasApiKey: true, allowInsecure: false);
        warnings.Should().BeEmpty();
    }

    [Fact]
    public void NonLoopbackHttp_WithKey_NoOptIn_Throws()
    {
        var act = () => Run("http://10.0.0.5:5000", hasApiKey: true, allowInsecure: false);
        act.Should().Throw<InsecureTransportException>();
    }

    [Fact]
    public void NonLoopbackHttp_WithKey_OptIn_WarnsAndProceeds()
    {
        var warnings = Run("http://10.0.0.5:5000", hasApiKey: true, allowInsecure: true);
        warnings.Should().ContainSingle();
    }

    [Fact]
    public void NonLoopbackHttp_NoKey_IsAllowed()
    {
        var warnings = Run("http://10.0.0.5:5000", hasApiKey: false, allowInsecure: false);
        warnings.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~RemoteTransportGuardTests"`
Expected: FAIL to COMPILE — `RemoteTransportGuard` / `InsecureTransportException` do not exist yet.

- [ ] **Step 3: Create the guard**

Create `src/Scrinia.Core/Resilience/RemoteTransportGuard.cs`:

```csharp
namespace Scrinia.Core.Resilience;

/// <summary>
/// Thrown when an API key would be transmitted over a non-TLS transport to a
/// non-loopback host and the caller has not opted in to insecure transport.
/// </summary>
public sealed class InsecureTransportException : Exception
{
    public InsecureTransportException(string message) : base(message) { }
}

/// <summary>
/// Guards remote CLI connections against sending a bearer API key in cleartext.
/// A key may cross the wire only over HTTPS, or to a loopback host (where it never
/// leaves the machine), or when the caller explicitly opts in to insecure transport.
/// </summary>
public static class RemoteTransportGuard
{
    /// <param name="remote">The remote server URL.</param>
    /// <param name="hasApiKey">True when a non-empty API key would be sent.</param>
    /// <param name="allowInsecure">True to downgrade a refusal to a warning.</param>
    /// <param name="warn">Sink for the one-line warning emitted on opt-in.</param>
    /// <exception cref="InsecureTransportException">
    /// A key would be sent over non-HTTPS to a non-loopback host without opt-in.
    /// </exception>
    public static void Validate(Uri remote, bool hasApiKey, bool allowInsecure, Action<string> warn)
    {
        ArgumentNullException.ThrowIfNull(remote);
        ArgumentNullException.ThrowIfNull(warn);

        if (!hasApiKey) return;
        if (string.Equals(remote.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)) return;
        if (remote.IsLoopback) return;

        string target = remote.GetLeftPart(UriPartial.Authority);
        if (allowInsecure)
        {
            warn($"[scrinia:warn] Sending the API key over an unencrypted connection to {target}. " +
                 "Use an https:// URL to protect it.");
            return;
        }

        throw new InsecureTransportException(
            $"Refusing to send the API key over an unencrypted connection to {target}. " +
            "Use an https:// URL, connect to a loopback host, or pass --allow-insecure-transport to override.");
    }
}
```

- [ ] **Step 4: Run the guard tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~RemoteTransportGuardTests"`
Expected: all PASS.

- [ ] **Step 5: Wire the guard into `Serve`**

In `src/Scrinia/Commands/ScriniaCommands.Serve.cs`:

Add the using (with the other `Scrinia.Core.*` usings near the top):
```csharp
using Scrinia.Core.Resilience;
```

Add a doc line and the parameter to the `Serve` signature. After the existing
`/// <param name="store">...` line add:
```csharp
    /// <param name="allowInsecureTransport">Permit sending the API key over a non-HTTPS connection to a non-loopback host (insecure).</param>
```
and add the parameter (before `bool noAutoSetup = false`):
```csharp
        bool allowInsecureTransport = false,
```

Inside `if (!string.IsNullOrEmpty(remote))`, before `var httpClient = ...`, insert:
```csharp
            try
            {
                RemoteTransportGuard.Validate(
                    new Uri(remote),
                    hasApiKey: !string.IsNullOrWhiteSpace(apiKey),
                    allowInsecure: allowInsecureTransport,
                    warn: msg => Console.Error.WriteLine(msg));
            }
            catch (InsecureTransportException ex)
            {
                Console.Error.WriteLine($"[scrinia:error] {ex.Message}");
                return 1;
            }
```

- [ ] **Step 6: Build to verify the wiring compiles**

Run: `dotnet build src/Scrinia/Scrinia.csproj`
Expected: 0 errors. (ConsoleAppFramework maps `allowInsecureTransport` to `--allow-insecure-transport`.)

- [ ] **Step 7: Commit**

```bash
git add src/Scrinia.Core/Resilience/RemoteTransportGuard.cs src/Scrinia/Commands/ScriniaCommands.Serve.cs tests/Scrinia.Tests/RemoteTransportGuardTests.cs
git commit -m "feat(remote): guard bearer key against cleartext transport (T1)"
```

---

### Task 3: S1 — Plugin secrets via environment, not argv

**Files:**
- Create: `src/Scrinia.Core/Embeddings/PluginConfigProtocol.cs`
- Modify: `src/Scrinia/Services/McpPluginHost.cs` (`ConfigKeys`, `BuildArguments`, `StartAsync`, `ConnectAsync`)
- Modify: `src/Scrinia.Plugin.Embeddings.Cli/Program.cs` (after arg parsing, ~L29)
- Test: `tests/Scrinia.Tests/PluginSecretTransportTests.cs` (create)

**Interfaces:**
- Produces: `Scrinia.Core.Embeddings.PluginConfigProtocol` with:
  - `static IReadOnlyList<string> Keys`
  - `static bool IsSecret(string key)`
  - `static string EnvVarName(string key)`
  - `static void ApplySecretEnvironment(IDictionary<string,string> configValues, Func<string,string?> getEnv)`
  - `McpPluginHost.BuildArguments(string dataDir, string modelsDir, Func<string,string?> getConfig)` → `string[]` (now `internal static`, non-secret `--config` only)
  - `McpPluginHost.BuildSecretEnvironment(Func<string,string?> getConfig)` → `Dictionary<string,string?>` (new `internal static`)
- Consumes: `StdioClientTransportOptions.EnvironmentVariables` (nullable dictionary of `string`→`string?`; verified present in MCP SDK 1.0.0 — the child inherits the parent env, then these entries augment/overwrite it).

**Context:** `McpPluginHost.BuildArguments` currently appends `--config {key}={val}` for every key in the local `ConfigKeys` array, including `Scrinia:Embeddings:OpenAiApiKey` — a secret placed on the child's world-readable command line. The plugin CLI reads config only from `--config`. The `ConfigKeys` array is relocated to `PluginConfigProtocol.Keys` (single source of truth). `McpPluginHost` internals are visible to `Scrinia.Tests`.

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Tests/PluginSecretTransportTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Core.Embeddings;
using Scrinia.Services;
using Xunit;

namespace Scrinia.Tests;

public class PluginSecretTransportTests
{
    private const string SecretKey = "Scrinia:Embeddings:OpenAiApiKey";
    private const string SecretVal = "sk-super-secret-value";

    private static Func<string, string?> Config() => key => key switch
    {
        SecretKey => SecretVal,
        "Scrinia:Embeddings:Provider" => "openai",
        _ => null,
    };

    [Fact]
    public void IsSecret_TrueForApiKeys_FalseForOthers()
    {
        PluginConfigProtocol.IsSecret(SecretKey).Should().BeTrue();
        PluginConfigProtocol.IsSecret("Scrinia:Embeddings:VoyageAiApiKey").Should().BeTrue();
        PluginConfigProtocol.IsSecret("Scrinia:Embeddings:Provider").Should().BeFalse();
        PluginConfigProtocol.IsSecret("Scrinia:Embeddings:OllamaBaseUrl").Should().BeFalse();
    }

    [Fact]
    public void EnvVarName_IsDeterministicAndSanitized()
    {
        PluginConfigProtocol.EnvVarName(SecretKey)
            .Should().Be("SCRINIA_PLUGIN_CFG__SCRINIA_EMBEDDINGS_OPENAIAPIKEY");
    }

    [Fact]
    public void BuildArguments_OmitsSecretsButKeepsNonSecrets()
    {
        var args = McpPluginHost.BuildArguments("/data", "/models", Config());

        // Non-secret config is still present on the command line.
        args.Should().ContainInConsecutiveOrder("--config", "Scrinia:Embeddings:Provider=openai");
        // The secret value and its config pair never appear in argv.
        args.Should().NotContain(a => a.Contains(SecretVal));
        args.Should().NotContain(a => a.Contains(SecretKey));
    }

    [Fact]
    public void BuildSecretEnvironment_CarriesSecretUnderMappedName()
    {
        var env = McpPluginHost.BuildSecretEnvironment(Config());

        env.Should().ContainKey(PluginConfigProtocol.EnvVarName(SecretKey))
            .WhoseValue.Should().Be(SecretVal);
        // Non-secret keys are not placed in the environment.
        env.Should().NotContainKey(PluginConfigProtocol.EnvVarName("Scrinia:Embeddings:Provider"));
    }

    [Fact]
    public void ApplySecretEnvironment_RoundTripsFromBuiltEnvironment()
    {
        var env = McpPluginHost.BuildSecretEnvironment(Config());
        var configValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        PluginConfigProtocol.ApplySecretEnvironment(
            configValues, name => env.TryGetValue(name, out var v) ? v : null);

        configValues.Should().ContainKey(SecretKey).WhoseValue.Should().Be(SecretVal);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~PluginSecretTransportTests"`
Expected: FAIL to COMPILE — `PluginConfigProtocol` and `McpPluginHost.BuildSecretEnvironment` do not exist yet, and `BuildArguments` is not accessible (still `private`).

- [ ] **Step 3: Create `PluginConfigProtocol`**

Create `src/Scrinia.Core/Embeddings/PluginConfigProtocol.cs`:

```csharp
using System.Text;

namespace Scrinia.Core.Embeddings;

/// <summary>
/// The host↔plugin configuration contract. Single source of truth for which config
/// keys the CLI host forwards to the embeddings plugin child process, which of them
/// are secrets, and how a secret key maps to an environment-variable name.
///
/// Secrets travel to the child via its environment, never via argv — a process command
/// line is world-readable on the local host (ps / Task Manager / /proc/&lt;pid&gt;/cmdline).
/// </summary>
public static class PluginConfigProtocol
{
    /// <summary>Config keys the host forwards to the plugin child process.</summary>
    public static readonly IReadOnlyList<string> Keys =
    [
        "Scrinia:Embeddings:Provider",
        "Scrinia:Embeddings:Hardware",
        "Scrinia:Embeddings:SemanticWeight",
        "Scrinia:Embeddings:OllamaBaseUrl",
        "Scrinia:Embeddings:OllamaModel",
        "Scrinia:Embeddings:OpenAiApiKey",
        "Scrinia:Embeddings:OpenAiModel",
        "Scrinia:Embeddings:OpenAiBaseUrl",
    ];

    /// <summary>A key is a secret when it names an API key. Covers current and future *ApiKey keys.</summary>
    public static bool IsSecret(string key) =>
        key.EndsWith("ApiKey", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Deterministic, collision-safe environment-variable name for a config key:
    /// <c>SCRINIA_PLUGIN_CFG__</c> + the key uppercased with every character outside
    /// <c>[A-Z0-9]</c> replaced by <c>_</c>. Example:
    /// <c>Scrinia:Embeddings:OpenAiApiKey</c> → <c>SCRINIA_PLUGIN_CFG__SCRINIA_EMBEDDINGS_OPENAIAPIKEY</c>.
    /// </summary>
    public static string EnvVarName(string key)
    {
        var sb = new StringBuilder("SCRINIA_PLUGIN_CFG__");
        foreach (char c in key.ToUpperInvariant())
            sb.Append(c is (>= 'A' and <= 'Z') or (>= '0' and <= '9') ? c : '_');
        return sb.ToString();
    }

    /// <summary>
    /// Reads secret config values from the environment into <paramref name="configValues"/>.
    /// For each secret key, if <paramref name="getEnv"/> returns a non-null value for its
    /// mapped env var, that value is written to <paramref name="configValues"/> under the
    /// canonical key. Plugin-side counterpart to the host's secret-environment build.
    /// </summary>
    public static void ApplySecretEnvironment(
        IDictionary<string, string> configValues, Func<string, string?> getEnv)
    {
        foreach (var key in Keys)
        {
            if (!IsSecret(key)) continue;
            var val = getEnv(EnvVarName(key));
            if (val is not null)
                configValues[key] = val;
        }
    }
}
```

- [ ] **Step 4: Update `McpPluginHost` to split secrets into the environment**

In `src/Scrinia/Services/McpPluginHost.cs`:

Add the using (with the other `Scrinia.Core.*` usings):
```csharp
using Scrinia.Core.Embeddings;
```

Delete the local `ConfigKeys` array (the `private static readonly string[] ConfigKeys = [ ... ];` block) — `PluginConfigProtocol.Keys` replaces it.

Add a field next to `_arguments`:
```csharp
    private Dictionary<string, string?> _environment = new();
```

In `StartAsync`, after `_arguments = BuildArguments(dataDir, modelsDir, getConfig);` add:
```csharp
        _environment = BuildSecretEnvironment(getConfig);
```

Replace the `BuildArguments` method with these two `internal static` methods:
```csharp
    internal static string[] BuildArguments(string dataDir, string modelsDir, Func<string, string?> getConfig)
    {
        var args = new List<string> { "--data-dir", dataDir, "--models-dir", modelsDir };
        foreach (var key in PluginConfigProtocol.Keys)
        {
            if (PluginConfigProtocol.IsSecret(key)) continue; // secrets go via the environment
            var val = getConfig(key);
            if (val is not null)
            {
                args.Add("--config");
                args.Add($"{key}={val}");
            }
        }
        return args.ToArray();
    }

    internal static Dictionary<string, string?> BuildSecretEnvironment(Func<string, string?> getConfig)
    {
        var env = new Dictionary<string, string?>();
        foreach (var key in PluginConfigProtocol.Keys)
        {
            if (!PluginConfigProtocol.IsSecret(key)) continue;
            var val = getConfig(key);
            if (val is not null)
                env[PluginConfigProtocol.EnvVarName(key)] = val;
        }
        return env;
    }
```

In `ConnectAsync`, add the environment to the transport options. Change:
```csharp
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = _exePath,
            Arguments = _arguments,
            Name = "scrinia-plugin",
            StandardErrorLines = line => Console.Error.WriteLine(line),
            ShutdownTimeout = TimeSpan.FromSeconds(3),
        });
```
to:
```csharp
        var transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Command = _exePath,
            Arguments = _arguments,
            // Secret config (API keys) is handed to the child via its environment, never argv.
            EnvironmentVariables = _environment,
            Name = "scrinia-plugin",
            StandardErrorLines = line => Console.Error.WriteLine(line),
            ShutdownTimeout = TimeSpan.FromSeconds(3),
        });
```
(`EnvironmentVariables` is a nullable string→string? dictionary; assign `_environment` directly. An empty dictionary augments the inherited environment with nothing — the no-secrets case is a no-op. If the property's declared type is not exactly `Dictionary<string,string?>`, assign via the compatible interface it expects — the value is the same dictionary.)

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~PluginSecretTransportTests"`
Expected: all 5 PASS.

- [ ] **Step 6: Update the plugin CLI to read secrets from the environment**

In `src/Scrinia.Plugin.Embeddings.Cli/Program.cs`, after the arg-parse `for` loop (after the closing `}` at ~L29, before `dataDir ??= ...`), add:
```csharp
// Secret config (API keys) arrives via the environment, never argv (Stream 5 S1).
PluginConfigProtocol.ApplySecretEnvironment(configValues, Environment.GetEnvironmentVariable);
```
(`using Scrinia.Core.Embeddings;` is already present at the top of the file.)

- [ ] **Step 7: Build the plugin CLI to verify it compiles**

Run: `dotnet build src/Scrinia.Plugin.Embeddings.Cli/Scrinia.Plugin.Embeddings.Cli.csproj`
Expected: 0 errors.

- [ ] **Step 8: Commit**

```bash
git add src/Scrinia.Core/Embeddings/PluginConfigProtocol.cs src/Scrinia/Services/McpPluginHost.cs src/Scrinia.Plugin.Embeddings.Cli/Program.cs tests/Scrinia.Tests/PluginSecretTransportTests.cs
git commit -m "fix(plugin): pass secret config via environment, not argv (S1)"
```

---

### Task 4: Full-suite verification

**Files:** none (verification only).

- [ ] **Step 1: Build the whole solution**

Run: `dotnet build Scrinia.sln`
Expected: 0 errors (no `-p:NuGetAudit=false`). Warnings unchanged from baseline.

- [ ] **Step 2: Run the affected suites**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj`
Run: `dotnet test tests/Scrinia.Plugin.Embeddings.Tests/Scrinia.Plugin.Embeddings.Tests.csproj`
Expected: green (a known parallel-load flake, `UpdateCorrectnessBenchmarkTests.UpdatedFact_OldContentNotReturned`, passes in isolation if it trips).

- [ ] **Step 3: Confirm the four user files are untouched**

Run: `git status --short`
Expected: the only unstaged files are the four reserved user files; no accidental staging.

---

## Notes for the implementer

- Each task is independent; they may be implemented in any order. The order above (T2 → T1 → S1) is by increasing blast radius.
- `Nmp2ChunkedEncoder.Encode(string)` returns a valid NMP/2 artifact for the T2 test seed.
- For the T2 transient test, `new RetryOptions(MaxRetries: 0)` avoids real backoff sleeps.
- Do not add default parameter values that create overload ambiguity; the `Serve` param is a new trailing optional bool, which ConsoleAppFramework surfaces as `--allow-insecure-transport`.
