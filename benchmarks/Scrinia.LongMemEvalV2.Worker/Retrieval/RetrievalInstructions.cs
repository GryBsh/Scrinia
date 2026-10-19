using Microsoft.Extensions.AI;
namespace Scrinia.LongMemEvalV2.Worker.Retrieval;
public static class RetrievalInstructions
{
    public static string Text {get;}=Read();
    public static IReadOnlySet<string> AllowedTools {get;}=new HashSet<string>(["search","list","recall"],StringComparer.Ordinal);
    private static string Read() {
        using var stream=typeof(RetrievalInstructions).Assembly.GetManifestResourceStream("Scrinia.LongMemEvalV2.Worker.Retrieval.retrieval-instructions.txt")!;
        using var reader=new StreamReader(stream);return reader.ReadToEnd();
    }
    public static void GuardResponse(ChatResponse response) {
        if(response.Messages.SelectMany(m=>m.Contents).OfType<FunctionCallContent>().Any(call=>!AllowedTools.Contains(call.Name)))
            throw new InvalidDataException("RETRIEVAL_MUTATION_FORBIDDEN");
    }
    public static void Guard(string name,AIFunctionArguments arguments) {
        if(!AllowedTools.Contains(name))throw new InvalidDataException("RETRIEVAL_MUTATION_FORBIDDEN");
        if(name=="recall") {
            string? path=arguments.TryGetValue("path",out var value)?value?.ToString():null;
            if(path is null || !(path.StartsWith("/source/",StringComparison.Ordinal)||path.StartsWith("/derived/",StringComparison.Ordinal))
                || path.Contains("..",StringComparison.Ordinal)||path.Contains('\\')||path.Contains(':'))
                throw new InvalidDataException("RETRIEVAL_PATH_FORBIDDEN");
        }
    }
}
