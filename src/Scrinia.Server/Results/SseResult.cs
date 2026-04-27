namespace Scrinia.Server.Sse;

/// <summary>IResult that streams SSE events with proper headers.</summary>
internal sealed class SseResult(Func<StreamWriter, Task> writeAsync) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.ContentType = "text/event-stream";
        httpContext.Response.Headers.Append("Cache-Control", "no-cache");
        httpContext.Response.Headers.Append("X-Accel-Buffering", "no");

        await using var writer = new StreamWriter(httpContext.Response.Body, leaveOpen: true);
        await writeAsync(writer);
    }
}
