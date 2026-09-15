using FluentAssertions;
using Scrinia.Core.Configuration;

namespace Scrinia.Tests;

public sealed class MaintenancePolicyTests
{
    private const string MaxVersions = "Scrinia:Retention:MaxArchivedVersions";
    private const string MinimumAge = "Scrinia:Retention:MinimumAgeDays";
    private const string JournalBatch = "Scrinia:Maintenance:JournalCleanupBatchSize";
    private const string TimeBudget = "Scrinia:Maintenance:TimeBudgetSeconds";
    private const string Background = "Scrinia:Maintenance:BackgroundEnabled";

    [Fact]
    public void Load_AbsentKeys_UsesExactDefaults()
    {
        MaintenancePolicyLoadResult result = MaintenancePolicyLoader.Load(new WorkspaceConfig());

        result.IsValid.Should().BeTrue();
        result.Policy.Should().Be(new MaintenancePolicy(10, 30, 50, 5, true));
        result.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Load_ValidSnapshot_ParsesAllFieldsAndIgnoresUnknownKeys()
    {
        var config = new WorkspaceConfig(new Dictionary<string, string>
        {
            [MaxVersions] = "42", [MinimumAge] = "0", [JournalBatch] = "10000",
            [TimeBudget] = "3600", [Background] = "false", ["unknown"] = "ignored",
        });

        MaintenancePolicyLoadResult result = MaintenancePolicy.Load(config);

        result.Policy.Should().Be(new MaintenancePolicy(42, 0, 10000, 3600, false));
        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(MaxVersions, "1")]
    [InlineData(MaxVersions, "100000")]
    [InlineData(MinimumAge, "0")]
    [InlineData(MinimumAge, "36500")]
    [InlineData(JournalBatch, "1")]
    [InlineData(JournalBatch, "10000")]
    [InlineData(TimeBudget, "1")]
    [InlineData(TimeBudget, "3600")]
    public void Load_AcceptsInclusiveNumericBounds(string key, string value)
    {
        MaintenancePolicyLoadResult result = MaintenancePolicyLoader.Load(new WorkspaceConfig(new() { [key] = value }));

        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(MaxVersions, "0")]
    [InlineData(MaxVersions, "100001")]
    [InlineData(MinimumAge, "-1")]
    [InlineData(MinimumAge, "36501")]
    [InlineData(JournalBatch, "0")]
    [InlineData(JournalBatch, "10001")]
    [InlineData(TimeBudget, "0")]
    [InlineData(TimeBudget, "3601")]
    [InlineData(MaxVersions, "")]
    [InlineData(MaxVersions, "+1")]
    [InlineData(MaxVersions, " 1")]
    [InlineData(MaxVersions, "1 ")]
    [InlineData(MaxVersions, "1.0")]
    [InlineData(MaxVersions, "999999999999999999999999")]
    public void Load_RejectsInvalidNumericValues(string key, string value)
    {
        MaintenancePolicyLoadResult result = MaintenancePolicyLoader.Load(new WorkspaceConfig(new() { [key] = value }));

        result.IsValid.Should().BeFalse();
        result.Policy.Should().BeNull();
        result.Errors.Should().ContainSingle(error => error.Key == key);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void Load_AcceptsOnlyLowercaseBoolean(string value, bool expected)
    {
        MaintenancePolicyLoadResult result = MaintenancePolicyLoader.Load(new WorkspaceConfig(new() { [Background] = value }));

        result.Policy!.BackgroundEnabled.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("True")]
    [InlineData("FALSE")]
    [InlineData("1")]
    [InlineData(" true")]
    public void Load_RejectsNonCanonicalBoolean(string value)
    {
        MaintenancePolicyLoadResult result = MaintenancePolicyLoader.Load(new WorkspaceConfig(new() { [Background] = value }));

        result.IsValid.Should().BeFalse();
        result.Errors.Should().ContainSingle(error => error.Key == Background && error.Code == "INVALID_BOOLEAN");
    }

    [Fact]
    public void Load_AggregatesInvalidFieldsWithStableKeysAndCodes()
    {
        var config = new WorkspaceConfig(new() { [MaxVersions] = "0", [MinimumAge] = "bad", [Background] = "True" });

        MaintenancePolicyLoadResult result = MaintenancePolicyLoader.Load(config);

        result.Errors.Select(error => (error.Key, error.Code)).Should().Equal(
            (MaxVersions, "OUT_OF_RANGE"), (MinimumAge, "INVALID_INTEGER"), (Background, "INVALID_BOOLEAN"));
    }

    [Fact]
    public void Load_ReadsOneConfigurationSnapshotPerInvocation()
    {
        var config = new MutatingConfig(new() { [MaxVersions] = "42" });

        MaintenancePolicyLoadResult result = MaintenancePolicyLoader.Load(config);

        result.Policy!.MaxArchivedVersions.Should().Be(42);
        config.KeysRead.Should().Equal(MaxVersions, MinimumAge, JournalBatch, TimeBudget, Background);
    }

    private sealed class MutatingConfig(Dictionary<string, string> values) : IWorkspaceConfig
    {
        public List<string> KeysRead { get; } = [];
        public string? GetValue(string key)
        {
            KeysRead.Add(key);
            string? value = values.GetValueOrDefault(key);
            values[key] = key == MaxVersions ? "999" : value ?? "bad";
            return value;
        }
        public void SetValue(string key, string value) => throw new NotSupportedException();
        public void UnsetValue(string key) => throw new NotSupportedException();
        public IReadOnlyList<string> GetEnabledBases() => throw new NotSupportedException();
        public IReadOnlyList<string> UpdateEnabledBases(Func<IReadOnlyList<string>, IReadOnlyList<string>> mutate) => throw new NotSupportedException();
        public void Save() => throw new NotSupportedException();
        public TrackingPolicy GetTrackingPolicy() => throw new NotSupportedException();
    }
}
