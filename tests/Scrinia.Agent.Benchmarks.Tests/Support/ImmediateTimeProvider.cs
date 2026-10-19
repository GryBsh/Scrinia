namespace Scrinia.Agent.Benchmarks.Tests;

// Exercise Task.Delay's real timer path without waiting through simulated server downtime.
internal sealed class ImmediateTimeProvider : TimeProvider
{
    public List<TimeSpan> Delays { get; } = [];
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Delays.Add(dueTime);
        return System.CreateTimer(callback, state, TimeSpan.Zero, period);
    }
}
