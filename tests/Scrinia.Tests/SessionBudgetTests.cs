using System.Collections.Concurrent;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Mcp;

namespace Scrinia.Tests;

public sealed class SessionBudgetTests
{
    private sealed class BudgetScope : IDisposable
    {
        public BudgetScope() =>
            SessionBudget.OverrideStore(new ConcurrentDictionary<string, long>(StringComparer.OrdinalIgnoreCase));

        public void Dispose() => SessionBudget.OverrideStore(null);
    }

    [Fact]
    public void RecordAccess_SingleEntry_TracksChars()
    {
        using var scope = new BudgetScope();

        SessionBudget.RecordAccess("api:auth", 4000);

        SessionBudget.TotalCharsLoaded.Should().Be(4000);
        SessionBudget.EstimatedTokensLoaded.Should().Be(1000);
    }

    [Fact]
    public void RecordAccess_MultipleEntries_SumsCorrectly()
    {
        using var scope = new BudgetScope();

        SessionBudget.RecordAccess("api:auth", 4000);
        SessionBudget.RecordAccess("session-notes", 2000);

        SessionBudget.TotalCharsLoaded.Should().Be(6000);
        SessionBudget.EstimatedTokensLoaded.Should().Be(1500);
    }

    [Fact]
    public void RecordAccess_SameEntry_Accumulates()
    {
        using var scope = new BudgetScope();

        SessionBudget.RecordAccess("api:auth", 1000);
        SessionBudget.RecordAccess("api:auth", 3000);

        SessionBudget.TotalCharsLoaded.Should().Be(4000);
        var breakdown = SessionBudget.Breakdown;
        breakdown["api:auth"].Chars.Should().Be(4000);
        breakdown["api:auth"].EstTokens.Should().Be(1000);
    }

    [Fact]
    public void Breakdown_ReturnsPerMemoryStats()
    {
        using var scope = new BudgetScope();

        SessionBudget.RecordAccess("api:auth", 16400);
        SessionBudget.RecordAccess("session-notes", 3200);

        var breakdown = SessionBudget.Breakdown;

        breakdown.Should().HaveCount(2);
        breakdown["api:auth"].Chars.Should().Be(16400);
        breakdown["api:auth"].EstTokens.Should().Be(4100);
        breakdown["session-notes"].Chars.Should().Be(3200);
        breakdown["session-notes"].EstTokens.Should().Be(800);
    }

    [Fact]
    public void EmptyBudget_ZeroTotals()
    {
        using var scope = new BudgetScope();

        SessionBudget.TotalCharsLoaded.Should().Be(0);
        SessionBudget.EstimatedTokensLoaded.Should().Be(0);
        SessionBudget.Breakdown.Should().BeEmpty();
    }
}
