using Scrinia.LongMemEvalV2.Worker.Evidence;
using Scrinia.LongMemEvalV2.Worker.Protocol;
namespace Scrinia.LongMemEvalV2.Worker.Tests;
public sealed class SnapshotValidationTests
{
    [Fact]
    public void Complete_catalog_requires_original_order_contiguous_utf8_offsets_and_source_hashes()
    {
        var text = "2026-03-04 caf\u00e9 \U0001f600";
        var fragments = new[] { new SourceFragment { SourceId = "synthetic", StateIndex = 0,
            FragmentIndex = 0, StartByte = 0, EndByte = System.Text.Encoding.UTF8.GetByteCount(text), Text = text, TextHash=SourceCatalog.Hash(System.Text.Encoding.UTF8.GetBytes(text)) } };
        var state = new TrajectoryState { StateIndex = 0, Step = 7, Url = "synthetic", Action = "click",
            Thoughts = "source thoughts", Text = text,
            Screenshot = new ImageReference { Path = "sources/s.png", Sha256 = new string('b',64), MediaType = "image/png", ByteLength = 1 } };
        var trajectory = new TrajectoryManifest { Id = "synthetic", Goal = "goal", Outcome = null, StartUrl = "synthetic",
            Actions = ["click"], SourceHash = new string('a',64), States = [state], OriginalJson = "{\"date\":\"2026-03-04\"}" };
        // Opaque IDs are deterministic; a forged ID must fail even when the raw text matches.
        Assert.Throws<InvalidDataException>(() => SourceCatalog.ValidateFragments(trajectory, fragments));
        fragments[0] = fragments[0] with { SourceId = SourceCatalog.SourceId(trajectory.SourceHash, 0, 0) };
        SourceCatalog.ValidateFragments(trajectory, fragments);
        Assert.NotEqual(SourceCatalog.LogicalHash(trajectory),SourceCatalog.LogicalHash(
            trajectory with {States=[state with {Text=text+"changed"}]}));
        Assert.Throws<InvalidDataException>(() => SourceCatalog.ValidateFragments(trajectory, [fragments[0] with { StartByte = 1 }]));
        Assert.Throws<InvalidDataException>(() => SourceCatalog.ValidateFragments(trajectory, []));
        Assert.Throws<InvalidDataException>(()=>SourceCatalog.ValidateFragments(trajectory,
            [fragments[0] with {TextHash=new string('f',64)}]));
    }
    [Fact]
    public void Fingerprint_detects_any_file_change_but_excludes_only_generation_receipt()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try {
            File.WriteAllText(Path.Combine(root,"source.txt"),"synthetic");
            var first = SnapshotValidator.Fingerprint(root);
            File.WriteAllText(Path.Combine(root,".generation.json"),"receipt");
            Assert.Equal(first,SnapshotValidator.Fingerprint(root));
            File.WriteAllText(Path.Combine(root,"source.txt"),"changed");
            Assert.NotEqual(first,SnapshotValidator.Fingerprint(root));
        } finally { Directory.Delete(root,true); }
    }
}
