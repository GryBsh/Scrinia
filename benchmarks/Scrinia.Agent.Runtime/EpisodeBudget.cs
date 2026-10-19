namespace Scrinia.Agent.Benchmarks;

public sealed class EpisodeBudget(int maxRequests)
{
    private int _requests;
    public void BeforeModelRequest()
    {
        if (_requests >= maxRequests) throw new EpisodeLimitException();
        _requests++;
    }
}
public sealed class EpisodeLimitException : Exception
{
    public EpisodeLimitException() : base("Episode model request budget exhausted.") { }
}
