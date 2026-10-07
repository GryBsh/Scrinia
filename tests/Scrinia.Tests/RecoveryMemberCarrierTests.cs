using System.Security.Cryptography;
using Scrinia.Core;
using Scrinia.Core.Models;
using Xunit;

namespace Scrinia.Tests;

public class RecoveryMemberCarrierTests
{
    [Fact]
    public void RecoveryMember_IsDistinctFromRawMemberSnapshotAndRecoveryMemberSnapshot()
    {
        var address = MemoryAddress.Parse("/notes/candidate");
        RecoveryMember member = new(
            address, "notes", new byte[] { 1, 2, 3 }, "abc123",
            ".scrinia/memories/notes/candidate.nmp2.meta.json", "def456");

        Assert.Equal(address, member.Address);
        Assert.Equal("notes", member.Scope);
        Assert.Equal(new byte[] { 1, 2, 3 }, member.ArtifactBytes);
        Assert.Equal("abc123", member.ArtifactSha256);
        Assert.Equal(".scrinia/memories/notes/candidate.nmp2.meta.json", member.SidecarPath);
        Assert.Equal("def456", member.SidecarSha256);
        // Distinct type: a RecoveryMember is never a RawMemberSnapshot.
        Assert.False((object)member is RawMemberSnapshot);
        Assert.False((object)member is RecoveryMemberSnapshot);
    }

    [Fact]
    public void AppendCapture_StillUsesRawMemberSnapshot_NotRecoveryMember()
    {
        // Prove the justified change is boundary-only: capture keeps RawMemberSnapshot,
        // SHA-256 still enforced at capture.
        RawMemberSnapshot raw = RawMemberSnapshot.Capture("/x/nmp2", false, new byte[] { 9, 9 });
        Assert.IsType<RawMemberSnapshot>(raw);
        Assert.NotEqual(typeof(RecoveryMember), typeof(RawMemberSnapshot));
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(raw.Bytes)),
            raw.Sha256);
    }
}
