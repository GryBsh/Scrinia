# Secrets & transport — design

**Date:** 2026-07-30
**Status:** Approved (design); implementation plan pending
**Stream:** 5 of 7 from the 2026-07-29 whole-codebase review remediation

## Context

Stream 5 addresses the three confirmed **secrets & transport** findings from the
2026-07-29 review. Each was re-verified against the current source and is live
exactly as described:

- **Secrets passed via plugin command-line args** — the embeddings plugin host
  passes the OpenAI API key to the child plugin process as a `--config` argv
  entry, where it is readable by any local user via the OS process table.
- **Remote CLI non-TLS bearer-key risk** — `scrinia serve --remote <url>` attaches
  an `Authorization: Bearer <key>` header regardless of URL scheme, so a plaintext
  `http://` target sends the key in cleartext over the network.
- **Remote writes silently ignoring non-success responses** — the remote store's
  `WriteArtifactAsync` inspects the HTTP status only to update its circuit breaker,
  then returns `void`; a 4xx/5xx server response looks like success to the caller.

No new dependencies are introduced; the solution-wide vulnerability audit stays
clean (Stream 3) — build and test WITHOUT `-p:NuGetAudit=false`. `Scrinia.Merge` is
untouched by this stream. The three tasks (S1, T1, T2) are independent of one
another and may be implemented in any order.

## S1 — Plugin secret passed on the child process command line

**Files:** `src/Scrinia/Services/McpPluginHost.cs` (`ConfigKeys`, `BuildArguments`,
`StartAsync`/`ConnectAsync`), `src/Scrinia.Plugin.Embeddings.Cli/Program.cs`
(arg parsing), plus a new shared helper in `src/Scrinia.Core/Embeddings/`.

**Now:** `McpPluginHost.BuildArguments` builds the child argument list by appending
`--config {key}={val}` for every key in `ConfigKeys`, which includes
`Scrinia:Embeddings:OpenAiApiKey`. The child is launched via `StdioClientTransport`
with those arguments. Process command lines are world-readable on the local host
(`ps -ef`, Windows Task Manager / WMI `CommandLine`, `/proc/<pid>/cmdline`), so a
configured OpenAI key is exposed to every local user for the lifetime of the
plugin process. `StdioClientTransportOptions` already exposes an
`EnvironmentVariables` dictionary (verified in the MCP SDK 1.0.0: the child
inherits the parent environment, then these entries augment/overwrite it), but it
is not used.

**Fix — pass secret config values through the environment, not argv:**

- Add a shared, dependency-free helper in `Scrinia.Core.Embeddings`
  (`PluginConfigProtocol`) that is the single source of truth for the host↔plugin
  config contract:
  - `IReadOnlyList<string> Keys` — the config keys the host forwards (relocated
    from `McpPluginHost.ConfigKeys`).
  - `bool IsSecret(string key)` — `true` when `key` ends with `ApiKey`
    (case-insensitive). This covers the current `OpenAiApiKey` and any future
    `*ApiKey` (Voyage, Azure, …) without re-touching this code.
  - `string EnvVarName(string key)` — deterministic, collision-safe mapping from a
    config key to an environment-variable name:
    `"SCRINIA_PLUGIN_CFG__"` + `key` uppercased with every character outside
    `[A-Z0-9]` replaced by `_`. Example:
    `Scrinia:Embeddings:OpenAiApiKey` → `SCRINIA_PLUGIN_CFG__SCRINIA_EMBEDDINGS_OPENAIAPIKEY`.
- `McpPluginHost`: when assembling the child launch, split the resolved config
  values by `IsSecret`:
  - **Non-secret** keys → `--config {key}={val}` argv entries, exactly as today.
  - **Secret** keys → entries in a `Dictionary<string,string?>` assigned to
    `StdioClientTransportOptions.EnvironmentVariables`, keyed by `EnvVarName(key)`.
    Secret keys never appear in argv.
  - This means `BuildArguments` returns non-secret args, and a parallel
    `BuildSecretEnvironment` returns the env dictionary; both are threaded through
    `StartAsync`/`ConnectAsync` so a reconnect uses the same values.
- Plugin CLI (`Program.cs`): after parsing `--config` argv into `configValues`, for
  each key in `PluginConfigProtocol.Keys` where `IsSecret(key)`, read
  `Environment.GetEnvironmentVariable(EnvVarName(key))` and, when present, add it to
  `configValues` (env value wins if somehow both are set — argv no longer carries
  secrets). The value lands in the same `configValues` dictionary it reached before,
  so downstream consumption is unchanged.

**Behavior preserved:** the secret value flows to the identical
`configValues[key]` slot in the plugin; only its transport (env vs argv) changes.
Non-secret config is byte-for-byte identical on the command line. First-run,
provider-unavailable, and reconnect paths are unaffected.

**Tests:**
- `McpPluginHost` (or the arg/env builders, exposed `internal` + `InternalsVisibleTo`):
  given a config resolver returning an `OpenAiApiKey`, the built argv contains **no**
  `--config` entry for the key and no occurrence of the secret value, and the built
  environment dictionary contains `EnvVarName("…OpenAiApiKey") → <secret>`.
- Non-secret keys (e.g. `Provider`, `OllamaBaseUrl`) still appear as `--config`
  argv entries and are **absent** from the secret env dictionary.
- `PluginConfigProtocol`: `IsSecret` is true for `…OpenAiApiKey` / `…VoyageAiApiKey`
  and false for `…Provider` / `…OllamaBaseUrl`; `EnvVarName` is deterministic and
  round-trips through the plugin CLI read path (integration-level: a value placed in
  the mapped env var is picked up into `configValues`).

## T1 — Bearer key transmitted over a non-TLS transport

**Files:** `src/Scrinia/Commands/ScriniaCommands.Serve.cs` (`Serve`), plus a new
`RemoteTransportGuard` in `src/Scrinia.Core/` (or `Scrinia.Core.Resilience`/a
transport-appropriate namespace).

**Now:** `Serve` constructs `new HttpClient { BaseAddress = new Uri(remote) }` and
unconditionally sets `Authorization: Bearer <apiKey>`. When `remote` is a plaintext
`http://` URL to a non-loopback host, the API key is transmitted in cleartext and is
interceptable on the network. There is no scheme check, warning, or opt-in.

**Fix — refuse cleartext key transmission, with a loopback allowance and an explicit
opt-in (per approved policy):**

- Add a CLI parameter to `Serve`: `bool allowInsecureTransport = false`
  (surface: `--allow-insecure-transport`). Document it as "permit sending the API
  key over a non-HTTPS connection to a non-loopback host (insecure)".
- Add `RemoteTransportGuard.Validate(Uri remote, bool hasApiKey, bool allowInsecure,
  Action<string> warn)` returning `void` (throws on refusal):
  - If `!hasApiKey` → return (nothing sensitive to transmit).
  - If `remote.Scheme` is `https` (case-insensitive) → return.
  - If the host is loopback — `Uri.IsLoopback`, which covers `localhost`,
    `127.0.0.0/8`, and `::1` → return (the key never leaves the machine).
  - Otherwise (a key would cross the network in cleartext):
    - If `allowInsecure` → call `warn(...)` with a clear one-line stderr warning and
      return.
    - Else → `throw` an exception whose message names the URL, states the key would
      be sent unencrypted, and tells the user to use `https://` or pass
      `--allow-insecure-transport` to override.
- `Serve` calls `RemoteTransportGuard.Validate(new Uri(remote), !string.IsNullOrEmpty(apiKey),
  allowInsecureTransport, msg => Console.Error.WriteLine(msg))` **before** building
  the `HttpClient` / setting the header. On refusal the exception propagates (or is
  caught to return a non-zero exit with the message) — no client is created and no
  request is ever sent.

**`hasApiKey` semantics:** treat null/empty/whitespace `apiKey` as "no key" — an
empty bearer carries nothing sensitive, so it never blocks. `--remote` without a key
is unaffected regardless of scheme.

**Tests (`RemoteTransportGuard`):**
- `http://localhost:5000` + key → OK (no throw, no warn).
- `http://127.0.0.1:5000` + key → OK; `http://[::1]:5000` + key → OK.
- `http://10.0.0.5:5000` + key, `allowInsecure=false` → throws.
- `http://10.0.0.5:5000` + key, `allowInsecure=true` → no throw; `warn` invoked once.
- `https://example.com` + key → OK (no throw, no warn).
- `http://10.0.0.5:5000` with empty/null key → OK (no throw): no secret to protect.

## T2 — Remote writes silently ignore non-success responses

**File:** `src/Scrinia/HttpMemoryStore.cs` (`WriteArtifactAsync`).

**Now:** `WriteArtifactAsync` POSTs the store request through the retry policy, then:
`if (resp.IsSuccessStatusCode) _circuitBreaker.RecordSuccess(); else if
(TransientDetector.IsTransient(resp)) _circuitBreaker.RecordFailure();` — and
returns. The method is `Task` (no result), so a non-transient failure (e.g. 403
unauthorized, 409 conflict, 400 validation, 500) is swallowed: the caller
(`MemoryOrchestrator`, the MCP write tools) observes a completed task and reports a
successful store while nothing was persisted. `FileMemoryStore.WriteArtifactAsync`
throws on write failure, so the remote store diverges from the shared
`IMemoryStore` contract. The sibling remote mutations (`Remove`, `DeleteArtifact`,
`CopyMemory`) already surface status via their return values; only the write path
swallows.

**Fix — throw on non-success, matching the local store:**

- After the retry loop, keep the circuit-breaker bookkeeping, then:
  `if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"Remote store
  write failed for '{qualifiedName}': {(int)resp.StatusCode} {resp.StatusCode}.",
  inner: null, statusCode: resp.StatusCode);`
  (using the `HttpRequestException` overload that carries `HttpStatusCode`).
- The existing `_circuitBreaker.EnsureClosed()` open-circuit throw
  (`CircuitBreakerOpenException`) and the retry policy's exhaustion behavior are
  unchanged. Transient failures still record a breaker failure before throwing;
  non-transient failures throw without recording (the server was reachable and
  answered — not a circuit-tripping condition).

**Contract alignment:** callers already treat a returning `WriteArtifactAsync` as a
durable write (that is exactly why the silent-swallow is a bug). Making the remote
store throw on failure brings it in line with `FileMemoryStore`; no caller changes
are required — an exception surfaces to the MCP layer as a tool error instead of a
false success.

**Tests (`HttpMemoryStore`, `HttpMessageHandler` stub):**
- Server returns 200 → `WriteArtifactAsync` completes; no throw; breaker records
  success.
- Server returns 403 (non-transient) → `WriteArtifactAsync` throws
  `HttpRequestException` with `StatusCode == Forbidden`; nothing is reported as
  stored.
- Server returns 503 (transient) → after retries are exhausted, throws; breaker
  records failure.

## Ordering

S1, T1, and T2 are mutually independent. Suggested order by blast radius:
T2 (single-method, single-file) → T1 (new guard + one call site + one CLI param) →
S1 (shared helper spanning host + plugin CLI). Each task ends green with its own
regression test.

## Testing / validation

Every fix ships regression tests (per-task notes above). Relevant suites, all green
solution-wide WITHOUT `-p:NuGetAudit=false`:

- CLI / host: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj`
- Plugin: `dotnet test tests/Scrinia.Plugin.Embeddings.Tests/Scrinia.Plugin.Embeddings.Tests.csproj`

Two load-bearing invariants beyond per-fix tests:

- **S1:** the secret value never appears in the child process argv — only in the
  environment dictionary.
- **T1:** a bearer key is never sent over a non-loopback, non-HTTPS transport unless
  `--allow-insecure-transport` is explicitly passed.

## Non-goals (other streams)

Streams 6 (web frontend) and 7 (docs / minor) remain their own specs. Streams 1
(containment & authz), 2 (data integrity), 3 (supply chain / CI-CD), and 4
(retrieval correctness) are merged. This stream does not touch the server-side auth
surface (Stream 1), does not add TLS termination or certificate handling to the
CLI, and does not change how secrets are *stored* at rest (only how they are handed
to the child plugin process and whether the key is transmitted over the wire).

## Risks

- **S1** changes the host↔plugin launch contract. The shared `PluginConfigProtocol`
  keeps both sides in agreement; the round-trip test (value in mapped env var →
  `configValues`) is the safety net. Risk that a secret is *dropped* rather than
  merely relocated is bounded by asserting the env dictionary carries it.
- **T1** must not block legitimate flows: no key, loopback, and HTTPS all pass
  untouched; only the genuine cleartext-key-to-remote-host case is refused, and the
  opt-in flag preserves the escape hatch. The loopback check relies on
  `Uri.IsLoopback` — the tests pin `localhost`, `127.0.0.1`, and `::1`.
- **T2** turns a previously silent path into a throwing one. This is the intended
  correction, but it changes observable behavior for callers that relied on the
  swallow (none should — they already assume success). The MCP layer surfaces the
  exception as a tool error, which is strictly more correct than a false success.
