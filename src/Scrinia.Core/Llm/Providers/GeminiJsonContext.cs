using System.Text.Json.Serialization;

namespace Scrinia.Core.Llm.Providers;

/// <summary>
/// Source-generated JSON contracts for Google Gemini's <c>generateContent</c> API.
/// Split into two contexts (request + response) to sidestep a JsonSourceGenerator
/// hintName collision that fires when a single context contains multiple
/// <c>[JsonSerializable]</c> attributes whose graphs both reach the same shared
/// nested types — even when the types themselves are correctly de-duplicated, the
/// generator emits duplicate hint slots for primitive types like <c>String</c>.
/// Two contexts means each only walks one graph; no collision.
///
/// <para>Reference: https://ai.google.dev/api/generate-content</para>
/// </summary>
internal sealed class GeminiGenerateContentRequest
{
    [JsonPropertyName("system_instruction")]
    public GeminiSystemInstruction? SystemInstruction { get; set; }

    [JsonPropertyName("contents")]
    public GeminiTurn[] Contents { get; set; } = [];

    [JsonPropertyName("generationConfig")]
    public GeminiGenerationConfig GenerationConfig { get; set; } = new();
}

internal sealed class GeminiSystemInstruction
{
    [JsonPropertyName("parts")]
    public GeminiRequestPart[] Parts { get; set; } = [];
}

internal sealed class GeminiTurn
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";

    [JsonPropertyName("parts")]
    public GeminiRequestPart[] Parts { get; set; } = [];
}

internal sealed class GeminiRequestPart
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}

internal sealed class GeminiGenerationConfig
{
    [JsonPropertyName("temperature")]
    public double Temperature { get; set; }

    [JsonPropertyName("maxOutputTokens")]
    public int MaxOutputTokens { get; set; }
}

internal sealed class GeminiGenerateContentResponse
{
    [JsonPropertyName("candidates")]
    public GeminiCandidate[]? Candidates { get; set; }
}

internal sealed class GeminiCandidate
{
    [JsonPropertyName("content")]
    public GeminiResponseContent? Content { get; set; }

    [JsonPropertyName("finishReason")]
    public string? FinishReason { get; set; }
}

internal sealed class GeminiResponseContent
{
    [JsonPropertyName("parts")]
    public GeminiResponsePart[] Parts { get; set; } = [];
}

internal sealed class GeminiResponsePart
{
    [JsonPropertyName("text")]
    public string Text { get; set; } = "";
}

[JsonSerializable(typeof(GeminiGenerateContentRequest))]
internal partial class GeminiRequestJsonContext : JsonSerializerContext;

[JsonSerializable(typeof(GeminiGenerateContentResponse))]
internal partial class GeminiResponseJsonContext : JsonSerializerContext;
