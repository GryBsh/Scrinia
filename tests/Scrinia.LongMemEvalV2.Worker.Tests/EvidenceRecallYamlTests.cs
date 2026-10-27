using Scrinia.LongMemEvalV2.Worker.Evidence;

namespace Scrinia.LongMemEvalV2.Worker.Tests;

public sealed class EvidenceRecallYamlTests
{
    [Theory]
    [InlineData("status: success\ncontent: |2-\n  Chunk 2/3\n  \n    leading\n  last  \n", "  leading\nlast  ")]
    [InlineData("status: success\r\ncontent: |2+\r\n  Chunk 2/3\r\n  \r\n  last  \r\n  \r\n", "last  \n\n")]
    [InlineData("status: success\ncontent: |\n  Chunk 2/3\n  \n  last\n", "last\n")]
    [InlineData("status: success\ncontent: \"Chunk 2/3\\n\\nfirst\\r\\nlast  \\r\\n\\t\\u0001\"", "first\r\nlast  \r\n\t\u0001")]
    [InlineData("content: \"Chunk 2/3\\n\\n\"\nstatus: 'success'\n", "")]
    public void Valid_yaml_preserves_exact_recalled_content(string yaml, string expected)
    {
        EvidenceMaterializer.RequireRecallContent(yaml, 2, 3, expected);
    }

    [Theory]
    [InlineData("status: error\ncontent: \"Chunk 2/3\\n\\noriginal\"")]
    [InlineData("status: warning\ncontent: \"Chunk 2/3\\n\\noriginal\"")]
    [InlineData("status: success")]
    [InlineData("content: \"Chunk 2/3\\n\\noriginal\"")]
    [InlineData("status: success\ncontent: [original]")]
    [InlineData("status: [success]\ncontent: original")]
    [InlineData("status: success\nstatus: success\ncontent: \"Chunk 2/3\\n\\noriginal\"")]
    [InlineData("status: success\ncontent: \"Chunk 2/3\\n\\noriginal\"\ncontent: changed")]
    [InlineData("status: success\ncontent: \"Chunk 2/3\\n\\noriginal\"\n---\nstatus: success")]
    [InlineData("status: success\ncontent: \"unterminated")]
    [InlineData("[success, original]")]
    public void Invalid_or_ambiguous_envelopes_are_rejected(string yaml)
    {
        Assert.Throws<InvalidDataException>(() => EvidenceMaterializer.RequireRecallContent(yaml, 2, 3, "original"));
    }

    [Theory]
    [InlineData("Chunk 1/3\\n\\noriginal", "original")]
    [InlineData("Chunk 2/4\\n\\noriginal", "original")]
    [InlineData("Chunk 2/3\\n\\norig... (truncated)", "original")]
    [InlineData("Chunk 2/3\\n\\noriginal", "original  ")]
    [InlineData("Chunk 2/3\\n\\noriginal\\n", "original")]
    [InlineData("Chunk 2/3\\n\\nfirst\\nlast", "first\r\nlast")]
    [InlineData("Chunk 2/3\\n\\nfirst\\r\\nlast", "first\nlast")]
    public void Any_content_change_including_whitespace_is_rejected(string encoded, string expected)
    {
        string yaml = $"status: success\ncontent: \"{encoded}\"";
        var error = Assert.Throws<InvalidDataException>(() => EvidenceMaterializer.RequireRecallContent(yaml, 2, 3, expected));
        Assert.Equal("EVIDENCE_RECALL_CHANGED_OR_TRUNCATED", error.Message);
    }
}
