using System.Text.Json;
using Scrinia.Commands;
using Scrinia.Core;
using Scrinia.Core.Configuration;
using Scrinia.TestDoubles;
using Spectre.Console;
using CoreWorkspaceConfig = Scrinia.Core.Configuration.WorkspaceConfig;

namespace Scrinia.Tests;

// B5 Task 8 — shared-result maintenance command (TDD).
// Covers the CLI/Core continuation ownership boundary (local framing/ASCII/base64/size
// rejection with zero Core calls versus Core semantic pass-through), the legacy framing
// boundary, human/structured rendering, binding/expiry/cancellation/contention behavior,
// and the absence of a second CLI deletion path.
[Collection(GlobalWorkspaceConfigCollection.Name)]
public sealed class CliMaintenanceTests
{
    private static string NewWorkspace()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "scrinia-maint-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        return workspace;
    }

    private static void DeleteWorkspace(string workspace)
    {
        try { Directory.Delete(workspace, recursive: true); } catch { }
    }

    private static string WellFormedToken()
    {
        byte[] payload = System.Security.Cryptography.RandomNumberGenerator.GetBytes(160);
        string encoded = Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return "b5c2." + encoded;
    }

    private static MaintenanceResult CompletedResult(string? continuationToken = null) =>
        new(3, 1, 2, true, continuationToken is not null, false, false, false, false, false,
            continuationToken,
            new[]
            {
                new MaintenanceScopeResult("local", 3, 1, 2, 4, false, continuationToken is not null,
                    continuationToken, Array.Empty<MaintenanceCandidateResult>(), ["Scope 'local' retained 4 entries."])
            },
            new[] { "Scope 'local' retained 4 entries." }, null);

    private static MaintenanceResult ContendedResult() =>
        new(0, 0, 0, false, false, false, true, false, false, false, null,
            new[]
            {
                new MaintenanceScopeResult("local", 0, 0, 0, 0, true, false, null,
                    Array.Empty<MaintenanceCandidateResult>(), ["Scope 'local' lock contended."])
            },
            new[] { "Scope 'local' lock contended." }, null);

    // ── CLI-owned syntax gate: framing ──────────────────────────────────────

    public static TheoryData<string> InvalidFramingTokens()
    {
        string body = WellFormedToken()["b5c2.".Length..];
        return new TheoryData<string>
        {
            "",
            "   ",
            "legacy-token",
            "v1.token",
            "b5c2",
            "x5c2." + body,
            "b5c1." + body,
            "B5C2." + body,
        };
    }

    [Fact]
    public void SyntaxGate_EmptyBodyReportsInvalidSize()
    {
        Assert.False(MaintenanceContinuationSyntax.TryValidate("b5c2.", out MaintenanceContinuationSyntaxError error));
        Assert.Equal(MaintenanceContinuationSyntaxCode.InvalidSize, error.Code);
    }

    [Theory]
    [MemberData(nameof(InvalidFramingTokens))]
    public void SyntaxGate_RejectsInvalidFraming(string token)
    {
        bool valid = MaintenanceContinuationSyntax.TryValidate(token, out MaintenanceContinuationSyntaxError error);
        Assert.False(valid);
        Assert.Equal(MaintenanceContinuationSyntaxCode.InvalidFraming, error.Code);
        Assert.Equal("Invalid continuation.", error.Message);
    }

    [Theory]
    [MemberData(nameof(InvalidFramingTokens))]
    public async Task LegacyAndMalformedFraming_AreRejectedLocallyWithZeroCoreCalls(string token)
    {
        string workspace = NewWorkspace();
        int calls = 0;
        try
        {
            using (ScriniaCommands.OverrideMaintenanceInvokerForTest((_, _, _, _) =>
            {
                calls++;
                return Task.FromResult(CompletedResult());
            }))
            {
                var (exit, output) = await CaptureJsonAsync(() =>
                    new ScriniaCommands().Maintenance(workspaceRoot: workspace, continuationToken: token, json: true));
                Assert.Equal(1, exit);
                Assert.Contains("Invalid continuation", output, StringComparison.Ordinal);
            }
            Assert.Equal(0, calls);
            Assert.False(Directory.Exists(Path.Combine(workspace, ".scrinia")));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    // ── CLI-owned syntax gate: ASCII / encoding / size ──────────────────────

    [Fact]
    public void SyntaxGate_RejectsNonAscii()
    {
        string token = "b5c2." + new string('A', 213) + "é";
        Assert.False(MaintenanceContinuationSyntax.TryValidate(token, out MaintenanceContinuationSyntaxError error));
        Assert.Equal(MaintenanceContinuationSyntaxCode.NonAscii, error.Code);
    }

    [Theory]
    [InlineData('+')]
    [InlineData('/')]
    [InlineData('=')]
    [InlineData(' ')]
    public void SyntaxGate_RejectsNonCanonicalEncoding(char bad)
    {
        string token = "b5c2." + new string('A', 213) + bad;
        Assert.False(MaintenanceContinuationSyntax.TryValidate(token, out MaintenanceContinuationSyntaxError error));
        Assert.Equal(MaintenanceContinuationSyntaxCode.NonCanonicalEncoding, error.Code);
    }

    [Theory]
    [InlineData(212)]
    [InlineData(213)]
    [InlineData(215)]
    [InlineData(216)]
    public void SyntaxGate_RejectsWrongSize(int bodyLength)
    {
        string token = "b5c2." + new string('A', bodyLength);
        Assert.False(MaintenanceContinuationSyntax.TryValidate(token, out MaintenanceContinuationSyntaxError error));
        Assert.Equal(MaintenanceContinuationSyntaxCode.InvalidSize, error.Code);
    }

    [Fact]
    public void SyntaxGate_AcceptsWellFormedToken()
    {
        Assert.True(MaintenanceContinuationSyntax.TryValidate(WellFormedToken(), out MaintenanceContinuationSyntaxError error));
        Assert.Equal(MaintenanceContinuationSyntaxCode.None, error.Code);
    }

    [Theory]
    [InlineData('+')]
    [InlineData('=')]
    [InlineData('é')]
    public async Task SyntaxGateFailures_MakeZeroCoreCalls(char bad)
    {
        string workspace = NewWorkspace();
        string token = "b5c2." + new string('A', 213) + bad;
        int calls = 0;
        try
        {
            using (ScriniaCommands.OverrideMaintenanceInvokerForTest((_, _, _, _) =>
            {
                calls++;
                return Task.FromResult(CompletedResult());
            }))
            {
                int exit = await new ScriniaCommands().Maintenance(
                    workspaceRoot: workspace, continuationToken: token, json: true);
                Assert.Equal(1, exit);
            }
            Assert.Equal(0, calls);
            Assert.False(Directory.Exists(Path.Combine(workspace, ".scrinia")));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    // ── Ownership: local shape matches Core shape; valid syntax reaches Core ──

    [Fact]
    public async Task LocalInvalidContinuation_MatchesCoreInvalidContinuationShape()
    {
        MaintenanceResult local = ScriniaCommands.BuildLocalInvalidContinuation();
        Assert.True(ScriniaCommands.IsInvalidContinuation(local));

        MaintenanceResult core;
        using (var scope = new TestHelpers.StoreScope())
        {
            var store = (FileMemoryStore)TestHelpers.Store;
            var service = new FileMemoryMaintenanceService(store, new CoreWorkspaceConfig());
            core = await service.ExecuteAsync(
                new MaintenanceRequest(null, null, null, WellFormedToken()), CancellationToken.None);
        }

        Assert.True(ScriniaCommands.IsInvalidContinuation(core));
        Assert.Equal(core.Inspected, local.Inspected);
        Assert.Equal(core.DeletedArchives, local.DeletedArchives);
        Assert.Equal(core.DeletedRecoveryEvidence, local.DeletedRecoveryEvidence);
        Assert.Equal(core.Completed, local.Completed);
        Assert.Equal(core.MoreWork, local.MoreWork);
        Assert.Equal(core.RecoveryRetained, local.RecoveryRetained);
        Assert.Equal(core.LockContended, local.LockContended);
        Assert.Equal(core.PolicyInvalid, local.PolicyInvalid);
        Assert.Equal(core.PartialFailure, local.PartialFailure);
        Assert.Equal(core.ContinuationToken, local.ContinuationToken);
        Assert.Equal(core.Scopes.Count, local.Scopes.Count);
        Assert.Equal(core.Warnings, local.Warnings);
        Assert.Equal(core.PolicyError, local.PolicyError);
        Assert.Equal(ScriniaCommands.ExitCodeFor(core), ScriniaCommands.ExitCodeFor(local));
    }

    [Fact]
    public async Task SemanticallyInvalidToken_PassesThroughToCoreAndFailsClosed()
    {
        string workspace = NewWorkspace();
        int calls = 0;
        try
        {
            using (ScriniaCommands.OverrideMaintenanceInvokerForTest((store, config, request, ct) =>
            {
                calls++;
                return ScriniaCommands.InvokeMaintenanceCoreAsync(store, config, request, ct);
            }))
            {
                var (exit, output) = await CaptureJsonAsync(() =>
                    new ScriniaCommands().Maintenance(
                        workspaceRoot: workspace, continuationToken: WellFormedToken(), json: true));
                Assert.Equal(1, calls);
                Assert.Equal(1, exit);
                using JsonDocument document = JsonDocument.Parse(output);
                Assert.False(document.RootElement.GetProperty("completed").GetBoolean());
                Assert.Equal(0, document.RootElement.GetProperty("inspected").GetInt32());
                string warnings = document.RootElement.GetProperty("warnings").GetRawText();
                Assert.Contains("Invalid continuation", warnings, StringComparison.Ordinal);
            }
            // Core performed zero enumeration or deletion for the unknown binding.
            Assert.False(Directory.Exists(Path.Combine(workspace, ".scrinia")));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    // ── Binding/expiry retention: unknown token keeps memories ───────────────

    [Fact]
    public async Task UnknownBinding_RetainsMemoriesWithZeroDeletions()
    {
        string workspace = NewWorkspace();
        string contentFile = Path.Combine(workspace, "content.md");
        await File.WriteAllTextAsync(contentFile, "retain me");
        try
        {
            var (storeExit, _) = await CaptureJsonAsync(() =>
                new ScriniaCommands().Store("notes", contentFile, workspace, json: true));
            Assert.Equal(0, storeExit);

            int calls = 0;
            using (ScriniaCommands.OverrideMaintenanceInvokerForTest((store, config, request, ct) =>
            {
                calls++;
                return ScriniaCommands.InvokeMaintenanceCoreAsync(store, config, request, ct);
            }))
            {
                var (exit, output) = await CaptureJsonAsync(() =>
                    new ScriniaCommands().Maintenance(
                        workspaceRoot: workspace, continuationToken: WellFormedToken(), json: true));
                Assert.Equal(1, calls);
                Assert.Equal(1, exit);
                using JsonDocument document = JsonDocument.Parse(output);
                Assert.Equal(0, document.RootElement.GetProperty("deletedArchives").GetInt32());
                Assert.Equal(0, document.RootElement.GetProperty("deletedRecoveryEvidence").GetInt32());
            }

            var (showExit, _) = await CaptureJsonAsync(() =>
                new ScriniaCommands().Show("notes", workspaceRoot: workspace, json: true));
            Assert.Equal(0, showExit);
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    // ── Human and structured rendering ───────────────────────────────────────

    [Fact]
    public async Task HumanOutput_RendersSharedResult()
    {
        string workspace = NewWorkspace();
        string token = WellFormedToken();
        try
        {
            using (ScriniaCommands.OverrideMaintenanceInvokerForTest((_, _, _, _) =>
                Task.FromResult(CompletedResult(token))))
            {
                var (exit, output) = await CaptureTextAsync(() =>
                    new ScriniaCommands().Maintenance(workspaceRoot: workspace, json: false));
                Assert.Equal(0, exit);
                Assert.Contains("inspected 3", output, StringComparison.Ordinal);
                Assert.Contains("local", output, StringComparison.Ordinal);
                Assert.Contains("retained 4 entries", output, StringComparison.Ordinal);
                Assert.Contains(token, output, StringComparison.Ordinal);
            }
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [Fact]
    public async Task JsonOutput_RendersSharedResult()
    {
        string workspace = NewWorkspace();
        string token = WellFormedToken();
        try
        {
            using (ScriniaCommands.OverrideMaintenanceInvokerForTest((_, _, _, _) =>
                Task.FromResult(CompletedResult(token))))
            {
                var (exit, output) = await CaptureJsonAsync(() =>
                    new ScriniaCommands().Maintenance(workspaceRoot: workspace, json: true));
                Assert.Equal(0, exit);
                using JsonDocument document = JsonDocument.Parse(output);
                Assert.Equal(3, document.RootElement.GetProperty("inspected").GetInt32());
                Assert.Equal(1, document.RootElement.GetProperty("deletedArchives").GetInt32());
                Assert.Equal(2, document.RootElement.GetProperty("deletedRecoveryEvidence").GetInt32());
                Assert.True(document.RootElement.GetProperty("completed").GetBoolean());
                Assert.Equal(token, document.RootElement.GetProperty("continuationToken").GetString());
                Assert.Equal("local", document.RootElement.GetProperty("scopes")[0].GetProperty("scope").GetString());
                Assert.False(document.RootElement.TryGetProperty("policyError", out _));
            }
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    // ── Policy, cancellation, contention ─────────────────────────────────────

    [Fact]
    public async Task InvalidPolicy_IsOwnedByCoreAndFailsClosed()
    {
        string workspace = NewWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace, ".scrinia"));
        await File.WriteAllTextAsync(
            Path.Combine(workspace, ".scrinia", "config.json"),
            """{"Scrinia:Maintenance:JournalCleanupBatchSize": "0"}""");
        int calls = 0;
        try
        {
            using (ScriniaCommands.OverrideMaintenanceInvokerForTest((store, config, request, ct) =>
            {
                calls++;
                return ScriniaCommands.InvokeMaintenanceCoreAsync(store, config, request, ct);
            }))
            {
                var (exit, output) = await CaptureJsonAsync(() =>
                    new ScriniaCommands().Maintenance(workspaceRoot: workspace, json: true));
                Assert.Equal(1, calls);
                Assert.Equal(1, exit);
                using JsonDocument document = JsonDocument.Parse(output);
                Assert.True(document.RootElement.GetProperty("policyInvalid").GetBoolean());
                Assert.Equal(0, document.RootElement.GetProperty("inspected").GetInt32());
                Assert.Contains("INVALID_POLICY",
                    document.RootElement.GetProperty("policyError").GetString(), StringComparison.Ordinal);
            }
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelledToken_ReturnsOneWithoutWork(bool json)
    {
        string workspace = NewWorkspace();
        try
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            int exit;
            string output;
            if (json)
                (exit, output) = await CaptureJsonAsync(() =>
                    new ScriniaCommands().Maintenance(
                        workspaceRoot: workspace, json: true, cancellationToken: cancelled.Token));
            else
                (exit, output) = await CaptureTextAsync(() =>
                    new ScriniaCommands().Maintenance(
                        workspaceRoot: workspace, json: false, cancellationToken: cancelled.Token));
            Assert.Equal(1, exit);
            Assert.Contains("cancelled", output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LockContention_ReturnsOneWithVisibleWarning(bool json)
    {
        string workspace = NewWorkspace();
        try
        {
            using (ScriniaCommands.OverrideMaintenanceInvokerForTest((_, _, _, _) =>
                Task.FromResult(ContendedResult())))
            {
                int exit;
                string output;
                if (json)
                    (exit, output) = await CaptureJsonAsync(() =>
                        new ScriniaCommands().Maintenance(workspaceRoot: workspace, json: true));
                else
                    (exit, output) = await CaptureTextAsync(() =>
                        new ScriniaCommands().Maintenance(workspaceRoot: workspace, json: false));
                Assert.Equal(1, exit);
                Assert.Contains("lock contended", output, StringComparison.OrdinalIgnoreCase);
                if (json)
                {
                    using JsonDocument document = JsonDocument.Parse(output);
                    Assert.True(document.RootElement.GetProperty("lockContended").GetBoolean());
                }
            }
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(51)]
    [InlineData(-1)]
    public async Task InvalidBatchSize_IsRejectedBeforeCore(int batchSize)
    {
        string workspace = NewWorkspace();
        int calls = 0;
        try
        {
            using (ScriniaCommands.OverrideMaintenanceInvokerForTest((_, _, _, _) =>
            {
                calls++;
                return Task.FromResult(CompletedResult());
            }))
            {
                var (exit, output) = await CaptureJsonAsync(() =>
                    new ScriniaCommands().Maintenance(
                        workspaceRoot: workspace, batchSize: batchSize, json: true));
                Assert.Equal(1, exit);
                Assert.Contains("Batch size", output, StringComparison.Ordinal);
            }
            Assert.Equal(0, calls);
            Assert.False(Directory.Exists(Path.Combine(workspace, ".scrinia")));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    // ── Exit mapping ─────────────────────────────────────────────────────────

    [Fact]
    public void ExitCode_MapsResultClassification()
    {
        Assert.Equal(0, ScriniaCommands.ExitCodeFor(CompletedResult()));
        Assert.Equal(0, ScriniaCommands.ExitCodeFor(CompletedResult(WellFormedToken())));
        Assert.Equal(1, ScriniaCommands.ExitCodeFor(ScriniaCommands.BuildLocalInvalidContinuation()));
        Assert.Equal(1, ScriniaCommands.ExitCodeFor(ContendedResult()));
        var policyInvalid = new MaintenanceResult(0, 0, 0, false, false, false, false, true,
            false, false, null, Array.Empty<MaintenanceScopeResult>(), Array.Empty<string>(),
            "INVALID_POLICY:Scrinia:Maintenance:JournalCleanupBatchSize:OUT_OF_RANGE");
        Assert.Equal(1, ScriniaCommands.ExitCodeFor(policyInvalid));
        var partial = new MaintenanceResult(2, 1, 0, true, false, false, false, false, true,
            false, null, Array.Empty<MaintenanceScopeResult>(), ["partial failure"], null);
        Assert.Equal(1, ScriniaCommands.ExitCodeFor(partial));
    }

    // ── No second path: source scans ─────────────────────────────────────────

    [Fact]
    public void MaintenanceCommand_HasNoSecondDeletionOrCodecPath()
    {
        string path = RepoPaths.Combine("src", "Scrinia", "Commands", "ScriniaCommands.Maintenance.cs");
        string source = File.ReadAllText(path);

        Assert.Contains("WorkspaceSetup.Configure", source, StringComparison.Ordinal);
        Assert.Contains("MaintenanceRequest", source, StringComparison.Ordinal);
        Assert.Contains("MaintenanceContinuationSyntax", source, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(source, ".ExecuteAsync("));

        foreach (string forbidden in new[]
        {
            "MaintenanceCursorCodec",
            "TryDecode",
            "ComputeCandidateKey",
            "ComputeWorkspaceKey",
            "HMACSHA256",
            "Directory.Enumerate",
            "File.Delete",
            "RetryPolicy",
            "Classify",
            "SHA256",
        })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    [Fact]
    public void MaintenanceJsonRoots_CoverResultGraphOnly()
    {
        string path = RepoPaths.Combine("src", "Scrinia", "Commands", "CliJsonContext.cs");
        string source = File.ReadAllText(path);

        Assert.Contains("CliMaintenanceOutput", source, StringComparison.Ordinal);

        foreach (string forbidden in new[]
        {
            "MaintenanceCursor",
            "MaintenanceCandidateKey",
            "MaintenanceWorkspaceKey",
            "MaintenanceCandidateProjection",
            "MaintenanceCandidateIdentity",
            "MaintenanceRunId",
            "MaintenanceHash256",
        })
            Assert.DoesNotContain(forbidden, source, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static async Task<(int Exit, string Output)> CaptureJsonAsync(Func<Task<int>> command)
    {
        using var output = new StringWriter();
        TextWriter previous = Console.Out;
        Console.SetOut(output);
        try
        {
            return (await command(), output.ToString());
        }
        finally
        {
            Console.SetOut(previous);
        }
    }

    private static async Task<(int Exit, string Output)> CaptureTextAsync(Func<Task<int>> command)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        TextWriter previousOut = Console.Out;
        TextWriter previousError = Console.Error;
        IAnsiConsole previousAnsiConsole = AnsiConsole.Console;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        AnsiConsole.Console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(stdout)
        });
        try
        {
            int exit = await command();
            return (exit, stdout.ToString() + stderr.ToString());
        }
        finally
        {
            AnsiConsole.Console = previousAnsiConsole;
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }
    }
}
