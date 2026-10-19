using System.Text.Json;
using Microsoft.Extensions.AI;
using Scrinia.LongMemEvalV2.Worker.Learning;
using Scrinia.LongMemEvalV2.Worker.Protocol;
namespace Scrinia.LongMemEvalV2.Worker.Tests;
public sealed class LearningTests
{
    [Fact]
    public void Learning_guard_refuses_sources_replacement_unknown_refs_and_mutations()
    {
        var hash = new string('a', 64); var id = new string('b', 64);
        var guard = LearningInstructions.Guard(hash, [id]);
        AIFunctionArguments Args(string path, string source, bool replace = false) => new()
        {
            ["path"] = path, ["description"] = "Synthetic source-backed note",
            ["content"] = JsonDocument.Parse("[\"Synthetic date 2026-03-04.\"]").RootElement,
            ["attributes"] = JsonDocument.Parse("{\"source\":[\"" + source + "\"]}").RootElement,
            ["replace"] = replace
        };
        guard("remember", Args("/derived/" + hash + "/note", id));
        Assert.ThrowsAny<Exception>(() => guard("remember", Args("/source/" + hash + "/0", id)));
        Assert.ThrowsAny<Exception>(() => guard("remember", Args("/derived/" + hash + "/note", id, true)));
        Assert.ThrowsAny<Exception>(() => guard("remember", Args("/derived/" + hash + "/note", "forged")));
        foreach (var name in new[] { "compact", "forget", "link", "shared_remember", "append" })
            Assert.ThrowsAny<Exception>(() => guard(name, Args("/derived/" + hash + "/note", id)));
    }

    [Fact]
    public void Accounting_mismatch_is_rejected_before_host_write()
    {
        Assert.Throws<InvalidDataException>(() => LearningInstructions.VerifyContext(
            new LearningConfiguration { Executable = "unused", PrefixArguments = [], ToolCatalogJson = "[]",
                CatalogHash = new string('a',64), InstructionsHash = new string('b',64),
                ProcessorRevision = "c202236235762e1c871ad0ccb60c8ee5ba337b9a",
                InputTokenLimit = 24576, FragmentTokenLimit = 4096, ImageLimit = 4, ServedContextTokens = 65536 },
            new string('c',64)));
    }
}
