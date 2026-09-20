using Scrinia.Core;
using Scrinia.Plugin.Abstractions;
using static Scrinia.Core.TransactionalRetryDefaults;

namespace Scrinia.Tests;

public class TransactionalRetryDefaultTests
{
    [Fact]
    public void Core_Policy_Defaults_To_MaxAttempts_3()
    {
        var policy = new Scrinia.Core.TransactionalRetryPolicy();
        Assert.Equal(3, policy.MaxAttempts);
        Assert.Equal(DefaultMaxAttempts, policy.MaxAttempts);
    }

    [Fact]
    public void Abstractions_Policy_Defaults_To_Same_MaxAttempts()
    {
        var policy = new Scrinia.Plugin.Abstractions.TransactionalRetryPolicy();
        Assert.Equal(3, policy.MaxAttempts);
    }
}
