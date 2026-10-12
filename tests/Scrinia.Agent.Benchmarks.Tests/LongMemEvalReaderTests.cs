using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Scrinia.Agent.Benchmarks.Tests;

public sealed class LongMemEvalReaderTests
{
    internal static string Fixture => Path.Combine(AppContext.BaseDirectory, "Fixtures", "minimal-longmemeval.json");

    [Theory]
    [InlineData("question_date", null)]
    [InlineData("question_date", "not a date")]
    [InlineData("question", " ")]
    [InlineData("question_type", "unknown")]
    [InlineData("answer", null)]
    public async Task Invalid_selected_record_reports_case_and_field(string field, string? value)
    {
        using var scratch = new ScratchDirectory();
        var data = JsonNode.Parse(File.ReadAllText(Fixture))!.AsArray();
        data[0]![field] = value;
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new LongMemEvalReader().LoadAsync(
            scratch.Write("data.json", data.ToJsonString()), [], CancellationToken.None));
        Assert.Contains("sample_abs", error.Message);
        Assert.Contains(field, error.Message);
    }

    [Theory]
    [InlineData("dates")]
    [InlineData("sessions")]
    [InlineData("role")]
    [InlineData("content")]
    [InlineData("session-id")]
    [InlineData("answer-shape")]
    public async Task Malformed_history_is_rejected_instead_of_skipped(string mutation)
    {
        using var scratch = new ScratchDirectory();
        var data = JsonNode.Parse(File.ReadAllText(Fixture))!.AsArray();
        var item = data[0]!;
        switch (mutation)
        {
            case "dates": item["haystack_dates"]!.AsArray().RemoveAt(0); break;
            case "sessions": item["haystack_sessions"]!.AsArray().Clear(); break;
            case "role": item["haystack_sessions"]![0]![0]!["role"] = "system"; break;
            case "content": item["haystack_sessions"]![0]![0]!["content"] = ""; break;
            case "session-id": item["haystack_session_ids"]![1] = ""; break;
            case "answer-shape": item["answer"] = new JsonObject(); break;
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => new LongMemEvalReader().LoadAsync(
            scratch.Write("data.json", data.ToJsonString()), [], CancellationToken.None));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate-selection")]
    [InlineData("duplicate-source")]
    public async Task Ambiguous_or_missing_selection_fails(string mutation)
    {
        using var scratch = new ScratchDirectory();
        var data = JsonNode.Parse(File.ReadAllText(Fixture))!.AsArray();
        if (mutation == "duplicate-source") data.Add(data[0]!.DeepClone());
        string[] selection = mutation == "unknown" ? ["missing"] : ["sample_abs", "sample_abs"];
        if (mutation == "duplicate-source") selection = [];
        await Assert.ThrowsAsync<InvalidDataException>(() => new LongMemEvalReader().LoadAsync(
            scratch.Write("data.json", data.ToJsonString()), selection, CancellationToken.None));
    }

    [Fact]
    public async Task Empty_selection_includes_all_and_explicit_selection_keeps_requested_order()
    {
        using var scratch = new ScratchDirectory();
        var data = JsonNode.Parse(File.ReadAllText(Fixture))!.AsArray();
        var second = data[0]!.DeepClone();
        second["question_id"] = "second";
        second["answer"] = 42;
        data.Add(second);
        string path = scratch.Write("data.json", data.ToJsonString());
        var all = await new LongMemEvalReader().LoadAsync(path, [], CancellationToken.None);
        Assert.Equal(["sample_abs", "second"], all.Cases.Select(c => c.Evaluation.QuestionId));
        var selected = await new LongMemEvalReader().LoadAsync(path, ["second", "sample_abs"], CancellationToken.None);
        Assert.Equal(["second", "sample_abs"], selected.Cases.Select(c => c.Evaluation.QuestionId));
        Assert.Equal("42", selected.Cases[0].Evaluation.ReferenceAnswer);
        Assert.False(selected.Cases[0].Evaluation.Abstention);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))), all.Sha256);
    }

    [Theory]
    [InlineData("2024-06-14")]
    [InlineData("2024/06/14 08:00")]
    [InlineData("2024/06/14 (Fri) 08:00")]
    [InlineData("2024-06-14T08:00:00-04:00")]
    [InlineData("2024-06-14T08:00:00Z")]
    public async Task Dates_are_invariant_and_equal_timestamps_preserve_source_order(string date)
    {
        using var scratch = new ScratchDirectory();
        var data = JsonNode.Parse(File.ReadAllText(Fixture))!.AsArray();
        data[0]!["haystack_dates"] = new JsonArray(date, date);
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var result = await new LongMemEvalReader().LoadAsync(scratch.Write("data.json", data.ToJsonString()), [], CancellationToken.None);
            Assert.Equal("Please book the train.", result.Cases[0].Sessions[0].Turns[0].Content);
            Assert.Equal(date, result.Cases[0].Sessions[0].Date);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public async Task Repeated_source_session_ids_preserve_every_dated_occurrence()
    {
        using var scratch = new ScratchDirectory();
        var data = JsonNode.Parse(File.ReadAllText(Fixture))!.AsArray();
        var item = data[0]!;
        item["haystack_session_ids"]![1] = item["haystack_session_ids"]![0]!.DeepClone();
        item["haystack_sessions"]![1] = item["haystack_sessions"]![0]!.DeepClone();
        item["haystack_dates"] = new JsonArray("2023/05/30 (Tue) 04:52", "2023/05/25 (Thu) 12:40");
        string path = scratch.Write("data.json", data.ToJsonString());
        var result = await new LongMemEvalReader().LoadAsync(path, [], CancellationToken.None);
        var sessions = Assert.Single(result.Cases).Sessions;
        Assert.Equal(2, sessions.Count);
        Assert.Equal([1, 2], sessions.Select(s => s.Ordinal));
        Assert.Equal(["2023/05/25 (Thu) 12:40", "2023/05/30 (Tue) 04:52"], sessions.Select(s => s.Date));
        Assert.Equal(sessions[0].Turns, sessions[1].Turns);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path))), result.Sha256);
    }
}
