using System.Text.Json;
using FluentAssertions;
using Scrinia.Commands;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;

namespace Scrinia.Tests;

public sealed class CliWholeMemoryTransactionTests
{
    [Fact]
    public async Task ImportMalformedBodyReturnsInvalidBundleAndExitOneWithoutPublishingTopics()
    {
        using var scope = new TestHelpers.StoreScope();
        string bundle = Path.Combine(scope.WorkspaceDir, "scanner-malformed.scrinia-bundle");
        BundleRoundTripTotalityTests.WriteMalformedScannerBundle(bundle,
            Scrinia.Core.Encoding.Nmp2ChunkedEncoder.Encode("plain") + "\ntrailing");
        using var output = new StringWriter();
        TextWriter prior = Console.Out;
        try
        {
            Console.SetOut(output);
            int exit = await new ScriniaCommands().Import(bundle, workspaceRoot: scope.WorkspaceDir, overwrite: true, json: true);
            Assert.Equal(1, exit);
            CliImportOutput envelope = JsonSerializer.Deserialize(output.ToString(), CliJsonContext.Default.CliImportOutput)!;
            Assert.Equal(ErrorCodes.InvalidBundle, envelope.ErrorCode);
            Assert.Empty(TestHelpers.Store.LoadIndex("local:scanner-good"));
            Assert.Empty(TestHelpers.Store.LoadIndex("local:scanner-bad"));
        }
        finally { Console.SetOut(prior); }
    }

    [Fact]
    public async Task AppendMalformedStoredBodyUsesInternalEnvelopeAndExitOneWithoutMutation()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await new ScriniaMcpTools().MemoryRemember("/cli/scanner", ["first", "second"], description: "first");
        string path = store.ArtifactPath("scanner", "local:cli");
        string malformed = (await File.ReadAllTextAsync(path)).Replace("##PAD:0", "##PAD:0\n##PAD:0", StringComparison.Ordinal);
        await File.WriteAllTextAsync(path, malformed);
        byte[] body = File.ReadAllBytes(path);
        byte[] sidecar = File.ReadAllBytes(store.MetaPath("scanner", "local:cli"));
        string input = Path.Combine(scope.WorkspaceDir, "new.md");
        await File.WriteAllTextAsync(input, "new");
        using var output = new StringWriter();
        TextWriter prior = Console.Out;
        try
        {
            Console.SetOut(output);
            int exit = await new ScriniaCommands().Append("/cli/scanner", input, workspaceRoot: scope.WorkspaceDir, json: true);
            Assert.Equal(1, exit);
            CliErrorOutput envelope = JsonSerializer.Deserialize(output.ToString(), CliJsonContext.Default.CliErrorOutput)!;
            Assert.Equal(ErrorCodes.Internal, envelope.Code);
            Assert.Equal(body, File.ReadAllBytes(path));
            Assert.Equal(sidecar, File.ReadAllBytes(store.MetaPath("scanner", "local:cli")));
        }
        finally { Console.SetOut(prior); }
    }

    [Fact]
    public async Task CompactRecoveryJsonUsesTheSharedHandlerEnvelopeAndWarningExitPolicy()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await new ScriniaMcpTools().MemoryRemember("/cli/transaction", ["old one", "old two"], description: "old one");
        store.FailOnceAt(WholeMemoryFaultPoint.LiveSidecarWrite);

        using var output = new StringWriter();
        TextWriter prior = Console.Out;
        try
        {
            Console.SetOut(output);
            int exit = await new ScriniaCommands().Compact(
                "/cli/transaction", workspaceRoot: scope.WorkspaceDir, json: true);

            exit.Should().Be(1);
            CliMcpOutput envelope = JsonSerializer.Deserialize(
                output.ToString(), CliJsonContext.Default.CliMcpOutput)!;
            McpResponseAssert mapped = ResponseParser.Parse(envelope.Yaml);
            mapped.ErrorCode.Should().Be(ErrorCodes.DurableWriteFailed);
            mapped.OperationId.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            Console.SetOut(prior);
        }
    }

    [Fact]
    public void TransactionErrorJsonRetainsOperationAndRecoveryEnvelope()
    {
        McpResponse response = ResponseBuilder.Error(
                "write requires recovery", ErrorCodes.RecoveryRequired, "resolve evidence")
            .WithOperationId("op-7")
            .WithRecovery("local:transaction", "versions/.whole-memory.journal");

        using var output = new StringWriter();
        TextWriter prior = Console.Out;
        try
        {
            Console.SetOut(output);
            int exit = ScriniaCommands.EmitMcpResult(response, json: true, action: "store");

            exit.Should().Be(1);
            CliMcpOutput envelope = JsonSerializer.Deserialize(
                output.ToString(), CliJsonContext.Default.CliMcpOutput)!;
            envelope.Status.Should().Be("error");
            McpResponseAssert mapped = ResponseParser.Parse(envelope.Yaml);
            mapped.ErrorCode.Should().Be(ErrorCodes.RecoveryRequired);
            mapped.OperationId.Should().Be("op-7");
            mapped.Scope.Should().Be("local:transaction");
            mapped.JournalPath.Should().Be("versions/.whole-memory.journal");
        }
        finally
        {
            Console.SetOut(prior);
        }
    }

    [Fact]
    public async Task CompactLockTimeoutUsesDurableErrorEnvelopeAndExitOne()
    {
        using var scope = new TestHelpers.StoreScope();
        using var provider = EmbeddingsHost.OverrideEmbeddingProviderForTest(null);
        var store = (FileMemoryStore)TestHelpers.Store;
        await new ScriniaMcpTools().MemoryRemember("/cli/locked", ["one", "two"], description: "one");
        using FileLock held = FileLock.AcquireExclusive(
            Path.Combine(store.GetStoreDirForScope("local:cli"), ".lock"));

        using var output = new StringWriter();
        TextWriter prior = Console.Out;
        try
        {
            Console.SetOut(output);
            int exit = await new ScriniaCommands().Compact(
                "/cli/locked", workspaceRoot: scope.WorkspaceDir, json: true);

            exit.Should().Be(1);
            CliMcpOutput envelope = JsonSerializer.Deserialize(
                output.ToString(), CliJsonContext.Default.CliMcpOutput)!;
            ResponseParser.Parse(envelope.Yaml).ErrorCode.Should().Be(ErrorCodes.DurableWriteFailed);
        }
        finally
        {
            Console.SetOut(prior);
        }
    }

    [Fact]
    public async Task StoreJsonTransactionErrorUsesCliMcpOutputEnvelope()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        string input = Path.Combine(scope.WorkspaceDir, "input.md");
        await File.WriteAllTextAsync(input, "new content");
        await new ScriniaMcpTools().MemoryRemember("/cli/store", ["old content"], description: "old content");
        store.FailOnceAt(WholeMemoryFaultPoint.LiveSidecarWrite);

        using var output = new StringWriter();
        TextWriter prior = Console.Out;
        try
        {
            Console.SetOut(output);
            int exit = await new ScriniaCommands().Store(
                "/cli/store", input, workspaceRoot: scope.WorkspaceDir, json: true);

            exit.Should().Be(1);
            CliMcpOutput envelope = JsonSerializer.Deserialize(
                output.ToString(), CliJsonContext.Default.CliMcpOutput)!;
            envelope.Status.Should().Be("error");
            McpResponseAssert mapped = ResponseParser.Parse(envelope.Yaml);
            mapped.ErrorCode.Should().Be(ErrorCodes.DurableWriteFailed);
            mapped.OperationId.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            Console.SetOut(prior);
        }
    }

    [Fact]
    public async Task CompactJsonArchiveCleanupWarningUsesCliMcpOutputEnvelope()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await new ScriniaMcpTools().MemoryRemember("/cli/warning", ["old one", "old two"], description: "old one");
        store.FailOnceAt(WholeMemoryFaultPoint.ArchiveCleanup);

        using var output = new StringWriter();
        TextWriter prior = Console.Out;
        try
        {
            Console.SetOut(output);
            int exit = await new ScriniaCommands().Compact(
                "/cli/warning", workspaceRoot: scope.WorkspaceDir, json: true);

            exit.Should().Be(1);
            CliMcpOutput envelope = JsonSerializer.Deserialize(
                output.ToString(), CliJsonContext.Default.CliMcpOutput)!;
            envelope.Status.Should().Be("success_with_warning");
            McpResponseAssert mapped = ResponseParser.Parse(envelope.Yaml);
            mapped.Status.Should().Be("success_with_warning");
            mapped.OperationId.Should().NotBeNullOrWhiteSpace();
            mapped.JournalPath.Should().NotBeNullOrWhiteSpace();
            mapped.JournalPath.Should().NotContain(scope.WorkspaceDir);
            mapped.JournalPath.Should().Be(Path.GetFileName(mapped.JournalPath));
        }
        finally
        {
            Console.SetOut(prior);
        }
    }
}
