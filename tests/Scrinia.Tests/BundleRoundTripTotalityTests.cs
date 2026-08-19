using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using FluentAssertions.Execution;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

/// <summary>
/// The load-bearing pin for finding M4. Export serializes <see cref="ArtifactEntry"/> through a
/// source-generated context — every member, automatically — while import used to read members by
/// hand, so the two drifted and five members were silently dropped on every backup restore.
///
/// <para>The field list is NOT the defect; the shape is. So the pin below enumerates
/// <c>ArtifactEntry</c>'s public instance properties reflectively — every member with a setter or
/// init accessor, not just the ones the primary constructor happens to expose as parameters — so a
/// member added later, whether as a positional parameter or as a plain property (like
/// <see cref="ArtifactEntry.LegacyFields"/>), is covered without anyone remembering to add it
/// here.</para>
/// </summary>
public sealed class BundleRoundTripTotalityTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _workspaceRoot;
    private readonly FileMemoryStore _store;

    public BundleRoundTripTotalityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "scrinia-bundletotality-" + Guid.NewGuid().ToString("N")[..8]);
        _workspaceRoot = Path.Combine(_tempDir, "workspace");
        Directory.CreateDirectory(_workspaceRoot);
        _store = new FileMemoryStore(_workspaceRoot);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── Reflection helpers ───────────────────────────────────────────────────

    /// <summary>Members the store deliberately RECOMPUTES on import. Excluded from the pin's
    /// comparison only — the probe still has to set these, since the guard still requires them
    /// populated; a round trip that failed to overwrite one would be a different bug.</summary>
    private static readonly HashSet<string> RecomputedByImport = new(StringComparer.Ordinal)
    {
        // ImportTopicEntries rewrites Uri for THIS machine's store root. The exporting
        // machine's URI is wrong here, so preserving it would be the bug.
        nameof(ArtifactEntry.Uri),
    };

    /// <summary>Members a load seam deliberately CLEARS before an entry escapes. Excluded from
    /// both the guard (the probe must NOT populate these — that would assert an impossible state)
    /// and the comparison (a round trip that preserved one would be the bug).</summary>
    private static readonly HashSet<string> NormalizedAwayOnLoad = new(StringComparer.Ordinal)
    {
        // NormalizeAttributesKey() folds LegacyFields into Attributes and nulls it, on every load
        // seam, so re-serialization never emits a "fields" key from an already-normalized entry.
        // A probe that populated LegacyFields directly would not represent anything the store's
        // own write path can produce.
        nameof(ArtifactEntry.LegacyFields),
    };

    /// <summary>
    /// Every public instance property on <see cref="ArtifactEntry"/> that has a setter or init
    /// accessor — i.e. every member something (a caller, or a deserializer) can actually assign.
    /// Deliberately NOT constructor-parameter-based: <see cref="Type.GetConstructors()"/> only
    /// covers the primary constructor's positional members, so a member added later as a plain
    /// property — <see cref="ArtifactEntry.LegacyFields"/> already is one — would be invisible to
    /// a parameter-based enumeration and silently excluded from this pin.
    /// </summary>
    private static PropertyInfo[] EntryMembers() =>
        typeof(ArtifactEntry)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is not null)
            .ToArray();

    private static object? Read(ArtifactEntry entry, string member) =>
        typeof(ArtifactEntry).GetProperty(member, BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(entry);

    /// <summary>True when a value is distinguishable from its type's default/empty form.</summary>
    private static bool IsPopulated(object? value) => value switch
    {
        null => false,
        string s => s.Length > 0,
        DateTimeOffset d => d != default,
        long l => l != 0,
        int i => i != 0,
        System.Collections.ICollection c => c.Count > 0,
        // NOT `_ => true`. A silent catch-all here is finding M4 reproduced inside the artifact
        // built to prevent it: add `bool Pinned = false` to ArtifactEntry tomorrow, and a `true`
        // fallthrough would report it "populated" while it sits at its own default, let the guard
        // pass, and then let a same-default-to-same-default comparison pass too — dropped on every
        // restore, pin green throughout. Any type without a rule here must fail LOUDLY so the rule
        // gets added, not quietly rubber-stamp whatever value shows up.
        _ => throw new InvalidOperationException(
            $"IsPopulated has no rule for {value.GetType()}. Add one — a silent `true` here " +
            $"would let a defaulted member pass the guard and defeat the pin."),
    };

    // ── The pin ──────────────────────────────────────────────────────────────

    [Fact]
    public void EveryArtifactEntryMember_SurvivesABundleRoundTrip()
    {
        var probe = FullyPopulatedEntry();

        // THE GUARD THAT MAKES THIS TEST BITE. Without it, a member added to ArtifactEntry that
        // the probe never sets is never compared, and the test passes vacuously while the new
        // member is dropped on every restore — the exact failure this pin exists to prevent.
        // NormalizedAwayOnLoad is excluded here too: the probe must NOT populate LegacyFields (no
        // store write path can produce that state), so demanding it be populated would be wrong,
        // not merely unnecessary.
        string[] unpopulated = EntryMembers()
            .Select(p => p.Name)
            .Where(name => !NormalizedAwayOnLoad.Contains(name))
            .Where(name => !IsPopulated(Read(probe, name)))
            .ToArray();
        unpopulated.Should().BeEmpty(
            "the probe must set EVERY ArtifactEntry member (other than ones excluded above for a " +
            "stated reason), or this test cannot detect a dropped one. If you just added a " +
            "member, populate it in FullyPopulatedEntry().");

        var imported = RoundTrip(probe).Entry;

        // The AssertionScope is load-bearing for diagnosis, not just tidiness. Without it
        // FluentAssertions throws on the FIRST failed member and the run names only that one.
        // You would see one name and have no idea several others were also dropped.
        using (new AssertionScope())
        {
            foreach (var member in EntryMembers())
            {
                string name = member.Name;
                if (RecomputedByImport.Contains(name) || NormalizedAwayOnLoad.Contains(name))
                    continue;

                Read(imported, name).Should().BeEquivalentTo(
                    Read(probe, name),
                    $"ArtifactEntry.{name} must survive a bundle round trip");
            }
        }
    }

    [Fact]
    public void Uri_IsRecomputedForTheImportingStore_NotCarriedFromTheBundle()
    {
        var (imported, targetRoot) = RoundTrip(FullyPopulatedEntry());

        imported.Uri.Should().NotBe("file:///somewhere/else/probe.nmp2");
        // "not the bundle's value" and "not empty" would both still pass if Uri were recomputed
        // against the SOURCE store's root instead of the importing one — which is the half this
        // test's name actually claims. Assert the URI names the IMPORTING store, specifically.
        imported.Uri.Should().StartWith($"file://{targetRoot}",
            "the store rewrites Uri to point at the IMPORTING store's own root, not the source's");
    }

    // ── Legacy bundles ───────────────────────────────────────────────────────

    // The two legacy attribute carriers are NEVER merged, and these are two tests rather than one
    // because of it. `ArtifactEntry.NormalizeAttributesKey` is a fallback, not a merge —
    // `Attributes ?? _legacyFields` (ArtifactEntry.cs:85-88) — so once the "tags" migration has
    // created an Attributes object, a "fields" payload on the same entry is discarded whole. That
    // matches today's behaviour (the hand-parse never read "fields" at all), so it is not a
    // regression, but a single fixture carrying both keys would assert something that is false.

    [Fact]
    public void ALegacyBundle_ImportsItsDescriptionAndTagsKeys()
    {
        var index = new JsonObject
        {
            ["entries"] = new JsonArray(new JsonObject
            {
                ["name"] = "legacy-probe",
                ["originalBytes"] = 11,
                ["chunkCount"] = 1,
                ["createdAt"] = "2024-03-04T05:06:07+00:00",
                ["description"] = "legacy display text",
                ["tags"] = new JsonArray("alpha"),
            }),
        };

        var imported = ImportHandBuiltBundle("legacy-topic", index, "hello world");

        imported.ContentPreview.Should().Be("legacy display text");
        imported.Attributes.Should().ContainKey(KnownAttributes.TagKey)
            .WhoseValue.Should().Equal("alpha");
    }

    [Fact]
    public void ALegacyBundle_WithNoTags_PicksUpThePreRenameFieldsKey()
    {
        // "fields" is folded ONLY when nothing else produced an Attributes object. Deserializing
        // through the context is what picks it up at all — the hand-parse this replaces read
        // "attributes" only, so such a bundle lost its attributes entirely.
        var index = new JsonObject
        {
            ["entries"] = new JsonArray(new JsonObject
            {
                ["name"] = "fields-probe",
                ["originalBytes"] = 11,
                ["chunkCount"] = 1,
                ["fields"] = new JsonObject { ["topic"] = new JsonArray("retrieval") },
            }),
        };

        var imported = ImportHandBuiltBundle("fields-topic", index, "hello world");

        imported.Attributes.Should().ContainKey("topic")
            .WhoseValue.Should().Equal("retrieval");
    }

    // ── Timestamps ───────────────────────────────────────────────────────────

    [Fact]
    public void CreatedAtAndUpdatedAt_ArePreservedFromTheBundle()
    {
        var created = new DateTimeOffset(2023, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var updated = new DateTimeOffset(2024, 6, 7, 8, 9, 10, TimeSpan.Zero);

        var imported = RoundTrip(FullyPopulatedEntry() with { CreatedAt = created, UpdatedAt = updated }).Entry;

        imported.CreatedAt.Should().Be(created, "a restore reinstates the memory as it was");
        imported.UpdatedAt.Should().Be(updated);
    }

    [Fact]
    public void AnEntryWithNoCreatedAt_FallsBackToNow()
    {
        var index = new JsonObject
        {
            ["entries"] = new JsonArray(new JsonObject
            {
                ["name"] = "no-timestamp",
                ["originalBytes"] = 11,
                ["chunkCount"] = 1,
            }),
        };

        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var imported = ImportHandBuiltBundle("stamp-topic", index, "hello world");

        imported.CreatedAt.Should().BeOnOrAfter(before,
            "a bundle carrying no timestamp gets one now rather than the year 1");
    }

    [Fact]
    public void AnEntryWithNoChunkCount_DefaultsToOne_NotZero()
    {
        // The hand-parse this replaces defaulted a missing "chunkCount" to 1; source-generated
        // deserialization leaves it at 0. Only pre-Merge-3 and hand-crafted bundles omit the key,
        // but a ChunkCount of 0 is not a value any reader is prepared for.
        var index = new JsonObject
        {
            ["entries"] = new JsonArray(new JsonObject
            {
                ["name"] = "no-chunk-count",
                ["originalBytes"] = 11,
            }),
        };

        var imported = ImportHandBuiltBundle("chunk-topic", index, "hello world");

        imported.ChunkCount.Should().Be(1);
    }

    [Fact]
    public void AMalformedCreatedAt_ThrowsInvalidOperationException_NamingTheTopic()
    {
        // The hand-parse this replaces silently rewrote an unparseable createdAt to "now"
        // (TryParse ? parsed : UtcNow). Source-generated DateTimeOffset deserialization throws
        // JsonException on anything but ISO 8601-1 extended instead, and that is now surfaced as
        // the InvalidOperationException ImportTopicsFromZip already documents — not swallowed into
        // a guessed timestamp, which would be the CreatedAt reset in miniature.
        var index = new JsonObject
        {
            ["entries"] = new JsonArray(new JsonObject
            {
                ["name"] = "bad-timestamp",
                ["originalBytes"] = 11,
                ["chunkCount"] = 1,
                ["createdAt"] = "not-a-date",
            }),
        };

        Action act = () => ImportHandBuiltBundle("malformed-topic", index, "hello world");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*malformed-topic*")
            .Which.Should().NotBeOfType<JsonException>();
    }

    [Fact]
    public void AStructurallyMalformedIndex_ThrowsInvalidOperationException_NamingTheTopic()
    {
        // AMalformedCreatedAt_ThrowsInvalidOperationException_NamingTheTopic above pins the
        // OTHER half of the JsonException conversion at :240 — an unparseable VALUE inside
        // otherwise-valid JSON, which Deserialize rejects. This pins the half that test does
        // not: index.json text that is not JSON at all, so JsonNode.Parse itself throws before
        // Deserialize ever runs.
        Action act = () => ImportHandBuiltBundleWithRawIndex("malformed-index-topic", "{ not json");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*malformed-index-topic*")
            .Which.Should().NotBeOfType<JsonException>();
    }

    [Fact]
    public void AMalformedLaterTopic_DoesNotLeaveAnEarlierValidTopicHalfImported()
    {
        // THE FINDING (fix round 2): converting the malformed-index JsonException into
        // InvalidOperationException from INSIDE the per-topic write loop meant
        // store.ImportTopicEntries had already run for every topic before the bad one by the time
        // the throw fired. A bundle with a good topic A followed by a malformed topic B wrote A
        // to the store and only THEN told the caller the whole bundle was invalid — exactly the
        // half-import this method's own up-front topic-name validation loop exists to rule out
        // (":156" — "validate EVERY manifest topic before writing anything ... before any side
        // effect"). ImportTopicsFromZip now parses and validates every topic (the build pass)
        // before writing any of them (the write pass), so this asserts BOTH halves: the throw
        // still fires, AND — the half the earlier round's fix did not cover — topic A was never
        // written at all.
        var goodIndex = new JsonObject
        {
            ["entries"] = new JsonArray(new JsonObject
            {
                ["name"] = "good-entry",
                ["originalBytes"] = 11,
                ["chunkCount"] = 1,
            }),
        };
        var badIndex = new JsonObject
        {
            ["entries"] = new JsonArray(new JsonObject
            {
                ["name"] = "bad-entry",
                ["originalBytes"] = 11,
                ["chunkCount"] = 1,
                ["createdAt"] = "not-a-date",
            }),
        };

        string zipPath = Path.Combine(_tempDir, $"two-topic-{Guid.NewGuid():N}.zip");
        using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteHandBuiltTopic(zip, "topic-a", goodIndex, "hello world");
            WriteHandBuiltTopic(zip, "topic-b", badIndex, "hello world");

            var manifest = zip.CreateEntry("manifest.json");
            using var writer = new StreamWriter(manifest.Open());
            writer.Write(new JsonObject
            {
                ["version"] = 1,
                ["exported"] = DateTimeOffset.UtcNow.ToString("o"),
                ["topics"] = new JsonArray("topic-a", "topic-b"),
                ["totalEntries"] = 2,
            }.ToJsonString());
        }

        Action act = () =>
        {
            using var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            BundleFormatService.ImportTopicsFromZip(zip, _store, filterTopics: null, overwrite: true);
        };

        act.Should().Throw<InvalidOperationException>().WithMessage("*topic-b*");

        _store.LoadIndex(MemoryNaming.BuildScopedTopicScope("topic-a")).Should().BeEmpty(
            "a malformed LATER topic must not leave an earlier, valid topic half-imported");
    }

    // ── Derivation ───────────────────────────────────────────────────────────

    [Fact]
    public void AnEntryWithNoKeywords_HasThemDerivedFromItsContent()
    {
        var index = new JsonObject
        {
            ["entries"] = new JsonArray(new JsonObject
            {
                ["name"] = "underived",
                ["originalBytes"] = 40,
                ["chunkCount"] = 1,
                ["contentPreview"] = "a preview",
            }),
        };

        var imported = ImportHandBuiltBundle(
            "derive-topic", index, "kubernetes scheduling latency investigation");

        imported.Keywords.Should().NotBeNullOrEmpty(
            "a bundle of loose files carries no keywords, so import must derive them or the " +
            "memory is unsearchable");
        imported.Keywords.Should().Contain(k => k.Contains("kubernetes", StringComparison.OrdinalIgnoreCase));
        imported.TermFrequencies.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void AnEntryThatAlreadyCarriesKeywords_KeepsExactlyThose()
    {
        var imported = RoundTrip(FullyPopulatedEntry()).Entry;

        imported.Keywords.Should().Equal(["alpha", "beta"],
            "derivation must not run over an entry the bundle already derived");
    }

    [Fact]
    public void AnEmptyKeywordsArray_IsARealAnswer_AndIsNotOverwritten()
    {
        // Null means "never derived"; empty means "derived, found nothing". Gating on
        // `is not { Length: > 0 }` instead of `is null` would silently conflate the two.
        var index = new JsonObject
        {
            ["entries"] = new JsonArray(new JsonObject
            {
                ["name"] = "empty-keywords",
                ["originalBytes"] = 40,
                ["chunkCount"] = 1,
                ["keywords"] = new JsonArray(),
            }),
        };

        var imported = ImportHandBuiltBundle(
            "empty-topic", index, "kubernetes scheduling latency investigation");

        imported.Keywords.Should().BeEmpty(
            "an empty keywords array is a derived answer, not a missing one");
    }

    [Fact]
    public void AnUndecodableArtifact_IsToleratedNotRejected_AndLeavesKeywordsNull()
    {
        // A bundle is untrusted external input. Before this feature existed, import never decoded
        // artifact bytes at all, so a bundle carrying non-NMP/2 artifact content (hand-crafted,
        // foreign, or corrupted) still imported successfully. Making decode failure fatal here
        // would make THIS branch newly reject bundles that previously imported fine — an acceptance
        // regression caused by an enrichment feature that is supposed to be purely additive.
        var index = new JsonObject
        {
            ["entries"] = new JsonArray(new JsonObject
            {
                ["name"] = "not-nmp2",
                ["originalBytes"] = 40,
                ["chunkCount"] = 1,
            }),
        };

        // ImportHandBuiltBundle always encodes its content via Nmp2ChunkedEncoder, so it cannot
        // produce non-NMP2 artifact bytes; write the zip by hand instead.
        string zipPath = Path.Combine(_tempDir, $"undecodable-{Guid.NewGuid():N}.zip");
        using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var indexEntry = zip.CreateEntry("topics/undecodable-topic/index.json");
            using (var writer = new StreamWriter(indexEntry.Open()))
                writer.Write(index.ToJsonString());

            var artifactEntry = zip.CreateEntry("topics/undecodable-topic/not-nmp2.nmp2");
            using (var writer = new StreamWriter(artifactEntry.Open()))
                writer.Write("this is not NMP/2 content, just plain text");

            var manifest = zip.CreateEntry("manifest.json");
            using (var writer = new StreamWriter(manifest.Open()))
                writer.Write(new JsonObject
                {
                    ["version"] = 1,
                    ["exported"] = DateTimeOffset.UtcNow.ToString("o"),
                    ["topics"] = new JsonArray("undecodable-topic"),
                    ["totalEntries"] = 1,
                }.ToJsonString());
        }

        (int TopicCount, int EntryCount, List<string> Names, List<string> SkippedCategories) result;
        using (var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            result = BundleFormatService.ImportTopicsFromZip(zip, _store, filterTopics: null, overwrite: true);

        result.EntryCount.Should().Be(1, "the import must succeed rather than throw over one entry's undecodable artifact");

        var imported = _store.LoadIndex(MemoryNaming.BuildScopedTopicScope("undecodable-topic"))
            .Single(e => e.Name == "not-nmp2");

        imported.Keywords.Should().BeNull(
            "decline to enrich, not fabricate — a decode failure must leave Keywords in its " +
            "exact prior state (never derived), not silently become an empty derived answer");
    }

    [Fact]
    public void AnArtifactDeclaringMoreChunksThanTheBoundAllows_RoutesThroughTheSameTolerance()
    {
        // The bound itself (DecodeAllChunksVerified rejecting a declared chunk count above
        // Nmp2Strategy.MaxChunkCount) is already pinned by
        // Nmp2ChunkedEncoderTests.DecodeAllChunksVerified_MultiChunk_DeclaredCountExceedsMaxChunkCount_Throws
        // — not duplicated here. What THIS test pins is the ROUTING: hostile bundle input that
        // trips that bound must degrade gracefully through import's tolerance (Keywords stays
        // null, the import still succeeds) rather than let the InvalidDataException escape and
        // abort the whole import, or hang. Forged-header shape mirrors that existing test exactly.
        string forged = $"NMP/2 1B CRC32:00000000 BR+B64 C:{Nmp2Strategy.MaxChunkCount + 1}\nNMP/END";

        var index = new JsonObject
        {
            ["entries"] = new JsonArray(new JsonObject
            {
                ["name"] = "over-bound",
                ["originalBytes"] = 40,
                ["chunkCount"] = 1,
            }),
        };

        // ImportHandBuiltBundle always encodes its content via Nmp2ChunkedEncoder, so it cannot
        // produce a forged header; write the zip by hand instead, same shape as the undecodable
        // test above.
        string zipPath = Path.Combine(_tempDir, $"over-bound-{Guid.NewGuid():N}.zip");
        using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var indexEntry = zip.CreateEntry("topics/over-bound-topic/index.json");
            using (var writer = new StreamWriter(indexEntry.Open()))
                writer.Write(index.ToJsonString());

            var artifactEntry = zip.CreateEntry("topics/over-bound-topic/over-bound.nmp2");
            using (var writer = new StreamWriter(artifactEntry.Open()))
                writer.Write(forged);

            var manifest = zip.CreateEntry("manifest.json");
            using (var writer = new StreamWriter(manifest.Open()))
                writer.Write(new JsonObject
                {
                    ["version"] = 1,
                    ["exported"] = DateTimeOffset.UtcNow.ToString("o"),
                    ["topics"] = new JsonArray("over-bound-topic"),
                    ["totalEntries"] = 1,
                }.ToJsonString());
        }

        (int TopicCount, int EntryCount, List<string> Names, List<string> SkippedCategories) result;
        using (var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            result = BundleFormatService.ImportTopicsFromZip(zip, _store, filterTopics: null, overwrite: true);

        result.EntryCount.Should().Be(1,
            "hostile input tripping the chunk-count bound must not abort the whole import");

        var imported = _store.LoadIndex(MemoryNaming.BuildScopedTopicScope("over-bound-topic"))
            .Single(e => e.Name == "over-bound");

        imported.Keywords.Should().BeNull(
            "a forged over-limit chunk count is just another undecodable artifact from this " +
            "method's point of view — same tolerance, same outcome");
    }

    [Fact]
    public void AnArtifactWithANegativePadFooter_RoutesThroughTheSameTolerance()
    {
        // Nmp2Strategy parses the ##PAD:{n} footer with a bare int.TryParse, which accepts a
        // negative sign (Nmp2Strategy.cs:246-247, :306-308). A negative pad makes
        // `compressedLen = padded.Length - pad` EXCEED padded.Length; the `compressedLen <= 0`
        // guard only catches the low side, so slicing `padded[..compressedLen]` throws
        // ArgumentOutOfRangeException (Nmp2Strategy.cs:177-181). This pins the ROUTING for that
        // failure mode specifically — a REAL NMP/2 artifact, tampered only in its footer, so this
        // fails for the reason under test and not for some unrelated malformed-header reason a
        // hand-forged artifact could accidentally trigger instead.
        string realArtifact = Nmp2ChunkedEncoder.Encode("some real content for the negative pad test");
        realArtifact.Should().Contain("##PAD:0",
            "a freshly encoded artifact always emits pad 0 — if this ever changes, the " +
            "string-replace below silently stops tampering anything");
        string tampered = realArtifact.Replace("##PAD:0", "##PAD:-999999");

        var index = new JsonObject
        {
            ["entries"] = new JsonArray(new JsonObject
            {
                ["name"] = "negative-pad",
                ["originalBytes"] = 40,
                ["chunkCount"] = 1,
            }),
        };

        // ImportHandBuiltBundle always encodes its own content, so it cannot inject a tampered
        // footer; write the zip by hand instead, same shape as the other tolerance tests above.
        string zipPath = Path.Combine(_tempDir, $"negative-pad-{Guid.NewGuid():N}.zip");
        using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var indexEntry = zip.CreateEntry("topics/negative-pad-topic/index.json");
            using (var writer = new StreamWriter(indexEntry.Open()))
                writer.Write(index.ToJsonString());

            var artifactEntry = zip.CreateEntry("topics/negative-pad-topic/negative-pad.nmp2");
            using (var writer = new StreamWriter(artifactEntry.Open()))
                writer.Write(tampered);

            var manifest = zip.CreateEntry("manifest.json");
            using (var writer = new StreamWriter(manifest.Open()))
                writer.Write(new JsonObject
                {
                    ["version"] = 1,
                    ["exported"] = DateTimeOffset.UtcNow.ToString("o"),
                    ["topics"] = new JsonArray("negative-pad-topic"),
                    ["totalEntries"] = 1,
                }.ToJsonString());
        }

        (int TopicCount, int EntryCount, List<string> Names, List<string> SkippedCategories) result;
        using (var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            result = BundleFormatService.ImportTopicsFromZip(zip, _store, filterTopics: null, overwrite: true);

        result.EntryCount.Should().Be(1,
            "a negative-pad footer tripping ArgumentOutOfRangeException must not abort the whole import");

        var imported = _store.LoadIndex(MemoryNaming.BuildScopedTopicScope("negative-pad-topic"))
            .Single(e => e.Name == "negative-pad");

        imported.Keywords.Should().BeNull(
            "a tampered footer that throws ArgumentOutOfRangeException is just another " +
            "undecodable artifact from this method's point of view — same tolerance, same outcome");
    }

    [Fact]
    public void DerivationDoesNotFabricateADriftBaselineFromThisMachinesFiles()
    {
        var index = new JsonObject
        {
            ["entries"] = new JsonArray(new JsonObject
            {
                ["name"] = "no-hashes",
                ["originalBytes"] = 40,
                ["chunkCount"] = 1,
            }),
        };

        var imported = ImportHandBuiltBundle(
            "hash-topic", index, "see src/Scrinia.Core/FileMemoryStore.cs for details");

        imported.FileHashes.Should().BeNullOrEmpty(
            "hashing THIS machine's files against paths captured on the exporting machine would " +
            "fabricate a drift baseline that never existed");
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    /// <summary>
    /// An entry with EVERY member set to a distinctive non-default value. When you add a member to
    /// <see cref="ArtifactEntry"/>, add it here — the guard in the pin will tell you if you forget.
    ///
    /// <para>Collection values are kept in ASCII-ascending order because
    /// <c>FileMemoryStore.WriteSidecarCore</c> genuinely does sort Keywords, TermFrequencies,
    /// FileHashes and each ChunkEntry's keywords on the way to disk — but <c>BeEquivalentTo</c>
    /// (used throughout the pin below) ignores collection order by default, so this fixture's own
    /// order is NOT load-bearing for this test. It is kept sorted for readability, not because an
    /// assertion here depends on it.</para>
    ///
    /// <para><b>The probe is compared against its own as-constructed form</b> (see
    /// <see cref="RoundTrip"/>), so every value set here must pre-satisfy whatever the SOURCE
    /// store's own cold <c>LoadIndex</c> would otherwise derive or normalize on its way to disk —
    /// the <c>Attributes["file"]</c> / <c>FileHashes</c> pairing below is one instance of that
    /// rule, not the whole rule. A future store-side normalization of some other member will red
    /// this pin for a reason that has nothing to do with bundles at all; if that happens, make the
    /// fixture match the invariant rather than treating the pin as wrong.</para>
    /// </summary>
    private static ArtifactEntry FullyPopulatedEntry() => new(
        Name: "probe",
        Uri: "file:///somewhere/else/probe.nmp2",
        OriginalBytes: 4321,
        ChunkCount: 2,
        CreatedAt: new DateTimeOffset(2021, 2, 3, 4, 5, 6, TimeSpan.Zero),
        ContentPreview: "a distinctive preview",
        Keywords: ["alpha", "beta"],
        TermFrequencies: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["alpha"] = 3 },
        UpdatedAt: new DateTimeOffset(2022, 3, 4, 5, 6, 7, TimeSpan.Zero),
        ChunkEntries:
        [
            new ChunkEntry(0, "chunk zero preview", ["alpha"],
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["alpha"] = 1 }),
        ],
        FileHashes: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["src/x.cs"] = "deadbeef" },
        // Attributes["file"] MUST mirror FileHashes.Keys. That is a store invariant, not a
        // stylistic choice: MemoryDerivation.ForStore derives FileHashes from the `file`
        // attribute, and AttributeMigration.Forward re-derives the attribute from the hash keys
        // on every cold LoadIndex (AttributeMigration.cs:71, called from FileMemoryStore.cs:757).
        // A probe carrying FileHashes without the matching attribute is a state the store's write
        // path cannot produce, and export's own cold load would add the key — making the round
        // trip look lossy when it is the FIXTURE that was impossible.
        Attributes: new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["topic"] = ["retrieval"],
            ["file"] = ["src/x.cs"],
        },
        AgentKeywords: ["alpha"]);

    /// <summary>Seeds one entry, exports it to a zip, imports it into a fresh scope, reads it back.
    /// Returns the importing store's workspace root alongside the entry, so a caller can assert
    /// the recomputed Uri actually names THAT root rather than merely "some non-bundle value".</summary>
    private (ArtifactEntry Entry, string TargetRoot) RoundTrip(ArtifactEntry seed)
    {
        string sourceScope = MemoryNaming.BuildScopedTopicScope("totality-source");
        _store.Upsert(seed, sourceScope);
        _store.WriteArtifactAsync(seed.Name, sourceScope, Nmp2ChunkedEncoder.Encode("hello world"))
            .GetAwaiter().GetResult();

        string zipPath = Path.Combine(_tempDir, $"totality-{Guid.NewGuid():N}.zip");
        using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            BundleFormatService.ExportTopicsToZip(zip, _store, ["totality-source"]);

        // Import into a SECOND workspace, so nothing survives by having never left.
        string otherRoot = Path.Combine(_tempDir, "target-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(otherRoot);
        using var target = new FileMemoryStore(otherRoot);

        using (var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            BundleFormatService.ImportTopicsFromZip(zip, target, filterTopics: null, overwrite: true);

        var entry = target.LoadIndex(MemoryNaming.BuildScopedTopicScope("totality-source"))
            .Single(e => e.Name.Equals(seed.Name, StringComparison.OrdinalIgnoreCase));
        return (entry, target.WorkspaceRoot);
    }

    /// <summary>Builds a bundle zip from a hand-written index — the only way to test shapes this
    /// codebase no longer exports — imports it, and returns the single entry.</summary>
    private ArtifactEntry ImportHandBuiltBundle(string topic, JsonObject index, string content)
    {
        string entryName = index["entries"]![0]!["name"]!.GetValue<string>();
        string zipPath = Path.Combine(_tempDir, $"handbuilt-{Guid.NewGuid():N}.zip");

        using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var indexEntry = zip.CreateEntry($"topics/{topic}/index.json");
            using (var writer = new StreamWriter(indexEntry.Open()))
                writer.Write(index.ToJsonString());

            var artifactEntry = zip.CreateEntry($"topics/{topic}/{_store.SanitizeName(entryName)}.nmp2");
            using (var writer = new StreamWriter(artifactEntry.Open()))
                writer.Write(Nmp2ChunkedEncoder.Encode(content));

            var manifest = zip.CreateEntry("manifest.json");
            using (var writer = new StreamWriter(manifest.Open()))
                writer.Write(new JsonObject
                {
                    ["version"] = 1,
                    ["exported"] = DateTimeOffset.UtcNow.ToString("o"),
                    ["topics"] = new JsonArray(topic),
                    ["totalEntries"] = 1,
                }.ToJsonString());
        }

        using (var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            BundleFormatService.ImportTopicsFromZip(zip, _store, filterTopics: null, overwrite: true);

        return _store.LoadIndex(MemoryNaming.BuildScopedTopicScope(topic))
            .Single(e => e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Builds a bundle zip whose topic index.json is written from raw text rather than a
    /// <see cref="JsonObject"/> — the only way to test an index that is not valid JSON at all,
    /// which a <see cref="JsonObject"/>-typed parameter could never represent. Imports it and lets
    /// any exception propagate to the caller; there is no well-formed entry to read back.</summary>
    private void ImportHandBuiltBundleWithRawIndex(string topic, string rawIndexJson)
    {
        string zipPath = Path.Combine(_tempDir, $"handbuilt-raw-{Guid.NewGuid():N}.zip");

        using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            var indexEntry = zip.CreateEntry($"topics/{topic}/index.json");
            using (var writer = new StreamWriter(indexEntry.Open()))
                writer.Write(rawIndexJson);

            var manifest = zip.CreateEntry("manifest.json");
            using (var writer = new StreamWriter(manifest.Open()))
                writer.Write(new JsonObject
                {
                    ["version"] = 1,
                    ["exported"] = DateTimeOffset.UtcNow.ToString("o"),
                    ["topics"] = new JsonArray(topic),
                    ["totalEntries"] = 1,
                }.ToJsonString());
        }

        using (var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            BundleFormatService.ImportTopicsFromZip(zip, _store, filterTopics: null, overwrite: true);
    }

    /// <summary>Writes one topic's index.json and its single artifact into an ALREADY-OPEN zip
    /// archive, without touching the manifest. <see cref="ImportHandBuiltBundle"/> only supports a
    /// single topic per bundle; this is the multi-topic building block for tests that need more
    /// than one topic in the same bundle.</summary>
    private void WriteHandBuiltTopic(ZipArchive zip, string topic, JsonObject index, string content)
    {
        string entryName = index["entries"]![0]!["name"]!.GetValue<string>();

        var indexEntry = zip.CreateEntry($"topics/{topic}/index.json");
        using (var writer = new StreamWriter(indexEntry.Open()))
            writer.Write(index.ToJsonString());

        var artifactEntry = zip.CreateEntry($"topics/{topic}/{_store.SanitizeName(entryName)}.nmp2");
        using (var writer = new StreamWriter(artifactEntry.Open()))
            writer.Write(Nmp2ChunkedEncoder.Encode(content));
    }
}
