using System.Text;
using FluentAssertions;
using Scrinia.Core.Configuration;

namespace Scrinia.Tests.Shared;

public sealed class SharedConflictMarkerTests
{
    [Fact]
    public void Detects_only_ordered_line_start_marker_triplets_in_utf8_authoritative_files()
    {
        SharedConflictScanner.HasOrderedMarkerTriplet(Encoding.UTF8.GetBytes("<<<<<<< ours\nvalue\n=======\nvalue\n>>>>>>> theirs\n"))
            .Should().BeTrue();
        SharedConflictScanner.HasOrderedMarkerTriplet(Encoding.UTF8.GetBytes("é\r\n<<<<<<< ours\r\n=======\r\n>>>>>>> theirs\r\n"))
            .Should().BeTrue();
        SharedConflictScanner.HasOrderedMarkerTriplet(Encoding.UTF8.GetBytes("text <<<<<<< ours\n=======\n>>>>>>> theirs"))
            .Should().BeFalse();
        SharedConflictScanner.HasOrderedMarkerTriplet(Encoding.UTF8.GetBytes("<<<<<<< ours\n=======\ntext"))
            .Should().BeFalse();
        SharedConflictScanner.HasOrderedMarkerTriplet(Encoding.UTF8.GetBytes(">>>>>>> theirs\n=======\n<<<<<<< ours"))
            .Should().BeFalse();
        SharedConflictScanner.HasOrderedMarkerTriplet([.. Encoding.UTF8.GetBytes("<<<<<<< ours\n"), 0xC3, 0x28, .. Encoding.UTF8.GetBytes("\n=======\n>>>>>>> theirs")])
            .Should().BeFalse();
    }

    [Fact]
    public void Ordered_marker_in_authoritative_file_conflicts_the_whole_base_but_derived_markers_do_not()
    {
        using var fixture = ConflictFixture.Create();
        File.WriteAllText(Path.Combine(fixture.Root, ".scrinia", "note.md"), "<<<<<<< ours\n=======\n>>>>>>> theirs\n");
        using var runtime = new SharedBaseRuntimeFactory().Open(fixture.Entry);
        runtime.Snapshot.Availability.Should().Be(SharedBaseAvailability.Conflicted);
        runtime.Snapshot.Entries.Should().BeEmpty();
        runtime.Refresh().Snapshot.Availability.Should().Be(SharedBaseAvailability.Conflicted);

        File.Delete(Path.Combine(fixture.Root, ".scrinia", "note.md"));
        Directory.CreateDirectory(Path.Combine(fixture.Root, ".scrinia", "indexes"));
        File.WriteAllText(Path.Combine(fixture.Root, ".scrinia", "indexes", "cache.tmp"), "<<<<<<< ours\n=======\n>>>>>>> theirs\n");
        runtime.Refresh().Snapshot.Availability.Should().Be(SharedBaseAvailability.Available);
    }

    private sealed class ConflictFixture : IDisposable
    {
        public required string Root { get; init; }
        public required SharedBaseEntry Entry { get; init; }
        public static ConflictFixture Create()
        {
            string root = Path.Combine(Path.GetTempPath(), "scrinia-task6-conflict-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, ".scrinia"));
            return new() { Root = root, Entry = new() { Name = "team", Root = root } };
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
