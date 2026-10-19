namespace Scrinia.LongMemEvalV2.Worker.Protocol;
public sealed record LearningConfiguration
{
    public required string Executable { get; init; }
    public required string[] PrefixArguments { get; init; }
    public required string ToolCatalogJson { get; init; }
    public required string CatalogHash { get; init; }
    public required string InstructionsHash { get; init; }
    public required string ProcessorRevision { get; init; }
    public required int InputTokenLimit { get; init; }
    public required int FragmentTokenLimit { get; init; }
    public required int ImageLimit { get; init; }
    public required int ServedContextTokens { get; init; }
}
public sealed record SourceFragment
{
    public required string SourceId { get; init; }
    public required int StateIndex { get; init; }
    public required int FragmentIndex { get; init; }
    public required int StartByte { get; init; }
    public required int EndByte { get; init; }
    public required string Text { get; init; }
    public required string TextHash { get; init; }
}
public sealed record PreparedBatch
{
    public required string[] SourceIds { get; init; }
    public required string MessageText { get; init; }
    public required ImageReference[] Images { get; init; }
    public required int InputTokens { get; init; }
}
