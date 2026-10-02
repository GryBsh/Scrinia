using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using FluentAssertions.Execution;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Encoding;
using Scrinia.Core.Formats;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

/// <summary>
/// The load-bearing pin for finding M4. Export serializes <see cref="ArtifactEntry"/> through a
/// source-generated context — every member, automatically — while import used to read members by
/// hand, so the two drifted and five members were silently dropped on every backup restore.
///
/// <para>The field list is NOT the defect; the shape is. So the pin below enumerates
/// <c>ArtifactEntry</c>'s public instance properties reflectively — every member with a public
/// GETTER, not just the ones the primary constructor happens to expose as parameters — so a
/// member added later, whether as a positional parameter or as a plain property (like
/// <see cref="ArtifactEntry.LegacyFields"/>), is covered without anyone remembering to add it
/// here. A get-only member is enumerated too: System.Text.Json SERIALIZES one and silently
/// ignores it on deserialize, which is this branch's own defect shape, so it is exactly the
/// member this pin must not filter away before it ever looks.</para>
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
        // ImportTopicEntries rebinds the logical address to the destination topic scope.
        nameof(ArtifactEntry.Address),
        // ImportTopicEntries regenerates the key for THIS machine's store root. The exporting
        // machine's key is not authoritative here, so preserving it would be the bug.
        nameof(ArtifactEntry.ArtifactKey),
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

    /// <summary>Members that CANNOT be assigned by a deserializer — a public getter with no
    /// setter or init accessor. System.Text.Json writes such a member on export and silently
    /// ignores it on import, so it is dropped on every restore by construction; a round trip
    /// cannot preserve it and the probe cannot set it. Excluded from both the guard and the
    /// comparison, for the same reason <see cref="NormalizedAwayOnLoad"/> is.
    ///
    /// <para><b>Deliberately EMPTY today, and that is the point.</b> <c>ArtifactEntry</c> has no
    /// such member right now. Adding a name here is a conscious, written-down edit — if you find
    /// yourself doing it because the pin went red, stop: the pin is telling you the member you
    /// just added is written into every <c>index.json</c> and read back by nothing. Give it an
    /// <c>init</c> accessor instead. Only put it here if being export-only is genuinely
    /// intended, and say why on the line above it.</para></summary>
    private static readonly HashSet<string> NotDeserializable = new(StringComparer.Ordinal)
    {
        // (empty — no ArtifactEntry member is get-only.)
    };

    /// <summary>
    /// Every public instance property on <see cref="ArtifactEntry"/> with a public GETTER — i.e.
    /// every member that export's source-generated serializer will WRITE. Deliberately not
    /// filtered down to the assignable ones: a get-only property serializes and then deserializes
    /// to nothing, which is this branch's exact defect shape, so filtering on the setter would
    /// remove that member from the guard AND the comparison before either could see it. Members
    /// that really cannot be assigned go through <see cref="NotDeserializable"/>, by name.
    ///
    /// <para>Also deliberately NOT constructor-parameter-based:
    /// <see cref="Type.GetConstructors()"/> only covers the primary constructor's positional
    /// members, so a member added later as a plain property — <see cref="ArtifactEntry.LegacyFields"/>
    /// already is one — would be invisible to a parameter-based enumeration and silently excluded
    /// from this pin.</para>
    /// </summary>
    private static PropertyInfo[] EntryMembers() =>
        typeof(ArtifactEntry)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetMethod is { IsPublic: true })
            .ToArray();

    private static object? Read(ArtifactEntry entry, string member) =>
        typeof(ArtifactEntry).GetProperty(member, BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(entry);

    /// <summary>
    /// Rewrites a <c>Dictionary&lt;string, JsonElement&gt;</c> (i.e. <see cref="ArtifactEntry.Unknown"/>)
    /// to its raw JSON text before comparison; every other member passes through untouched.
    ///
    /// <para><b>Why this is necessary, not cosmetic.</b> <see cref="JsonElement"/> exposes no
    /// public property besides <c>ValueKind</c> and implements no <c>IEquatable</c>, so
    /// FluentAssertions' structural walk falls back to <c>ValueType.Equals</c> — a comparison of
    /// the struct's own fields, which point at WHICH <see cref="JsonDocument"/> parsed it and
    /// WHERE in that document's buffer. Two JsonElements parsed from two separately-serialized
    /// copies of the identical JSON — exactly what export-then-import produces — are never the
    /// same instance underneath, so that fallback reports a mismatch regardless of content.
    /// Verified empirically: a standalone probe printed byte-identical <c>GetRawText()</c> for
    /// the probe and imported values, and the un-normalized pin still failed on Unknown.</para>
    ///
    /// <para><b>Why this does not weaken the pin.</b> It swaps a document-IDENTITY comparison for
    /// a content comparison, which is what "survives a bundle round trip" means for every other
    /// member already. A key that is dropped, renamed, or whose value's JSON actually changes
    /// still fails — the raw-text dictionaries differ in keys or values exactly as any other
    /// <c>Dictionary&lt;string, string&gt;</c> member's comparison would catch it.</para>
    /// </summary>
    private static object? ForComparison(object? value)
        => value is Dictionary<string, JsonElement> unknown
            ? unknown.ToDictionary(kv => kv.Key, kv => kv.Value.GetRawText())
            : value;

    /// <summary>
    /// The minimum <see cref="ArtifactEntry"/> the type permits: every member left at its own
    /// declared default. The guard compares the probe against THIS rather than against a
    /// per-type notion of "looks empty", because those two differ precisely where the pin is
    /// most at risk — a member whose declared default is itself populated-looking. Add
    /// <c>int SchemaVersion = 1</c> or <c>string Kind = "memory"</c> to ArtifactEntry and an
    /// "is it non-zero / non-empty?" test says yes while the probe never touched it; if import
    /// then drops the member, both sides sit at the same default and the comparison passes too —
    /// dropped on every restore, pin green throughout. "Differs from the default" states the
    /// thing the guard actually means, needs no per-type rules, and cannot be blind to a type it
    /// has never seen.
    /// </summary>
    private static ArtifactEntry BaselineEntry() => new("baseline", "artifacts/baseline.nmp2", 0, 0, default);

    /// <summary>True when the probe's value for a member is indistinguishable from the value that
    /// member has on a default-constructed entry — i.e. the probe did not set it.</summary>
    private static bool MatchesBaseline(object? probeValue, object? baselineValue)
        // Equals covers every value type, string, and "the probe set a reference where the
        // default is null". The empty-collection arm covers the one case Equals gets wrong: a
        // declared default that allocates a fresh empty collection per instance, where the probe's
        // untouched value and the baseline's are equal in every way that matters but are not the
        // same reference.
        => Equals(probeValue, baselineValue)
            || (probeValue is System.Collections.ICollection { Count: 0 }
                && baselineValue is System.Collections.ICollection { Count: 0 });

    // ── The pin ──────────────────────────────────────────────────────────────

    [Fact]
    public void EveryArtifactEntryMember_SurvivesABundleRoundTrip()
    {
        var probe = FullyPopulatedEntry();

        // THE GUARD THAT MAKES THIS TEST BITE. Without it, a member added to ArtifactEntry that
        // the probe never sets is never compared, and the test passes vacuously while the new
        // member is dropped on every restore — the exact failure this pin exists to prevent.
        // The comparison is against a DEFAULT-CONSTRUCTED entry, not against a per-type "looks
        // empty" rule; see BaselineEntry() for why that distinction is the whole guard.
        // NormalizedAwayOnLoad is excluded here too: the probe must NOT populate LegacyFields (no
        // store write path can produce that state), so demanding it differ would be wrong, not
        // merely unnecessary. NotDeserializable is excluded for the same kind of reason — nothing
        // can assign such a member, so the probe cannot set it.
        var baseline = BaselineEntry();
        string[] unset = EntryMembers()
            .Select(p => p.Name)
            .Where(name => !NormalizedAwayOnLoad.Contains(name))
            .Where(name => !NotDeserializable.Contains(name))
            .Where(name => MatchesBaseline(Read(probe, name), Read(baseline, name)))
            .ToArray();
        unset.Should().BeEmpty(
            "the probe must set EVERY ArtifactEntry member to a value that DIFFERS from its " +
            "default (other than ones excluded above for a stated reason), or this test cannot " +
            "detect a dropped one — a member left at its default round-trips from default to " +
            "default and compares equal even when import drops it. If you just added a member, " +
            "give it a distinctive value in FullyPopulatedEntry().");

        var imported = RoundTrip(probe).Entry;

        // The AssertionScope is load-bearing for diagnosis, not just tidiness. Without it
        // FluentAssertions throws on the FIRST failed member and the run names only that one.
        // You would see one name and have no idea several others were also dropped.
        using (new AssertionScope())
        {
            foreach (var member in EntryMembers())
            {
                string name = member.Name;
                if (RecomputedByImport.Contains(name)
                    || NormalizedAwayOnLoad.Contains(name)
                    || NotDeserializable.Contains(name))
                    continue;

                ForComparison(Read(imported, name)).Should().BeEquivalentTo(
                    ForComparison(Read(probe, name)),
                    $"ArtifactEntry.{name} must survive a bundle round trip");
            }
        }
    }

    [Fact]
    public void ArtifactKey_IsRegeneratedForTheImportingStore_NotCarriedFromTheBundle()
    {
        var (imported, targetRoot) = RoundTrip(FullyPopulatedEntry());

        imported.ArtifactKey.Should().Be("artifacts/totality-source/probe.nmp2");
        imported.ArtifactKey.Should().NotContain(targetRoot);
        imported.ArtifactKey.Should().NotContain("file://");
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

    [Fact]
    public void ANonObjectIndexRoot_ThrowsInvalidOperationException_NamingTheTopic_AndDoesNotHalfImport()
    {
        // Review round 1 on Task 5 (post-close): `JsonNode.Parse(indexJson) is not JsonObject`
        // is also true of syntactically VALID JSON that parses to something other than an object
        // — "[]", "42", "\"x\"", "null" all satisfy it without ever reaching the JsonException
        // catch below. The code used to `continue` in that case, which silently dropped the whole
        // topic while every OTHER topic in the bundle still imported: a silent partial restore,
        // and a regression from the pre-branch behavior (JsonElement.TryGetProperty threw on a
        // non-object root). This pins topic-a not being written when topic-b's index.json is "[]",
        // and the throw naming topic-b.
        var goodIndex = new JsonObject
        {
            ["entries"] = new JsonArray(new JsonObject
            {
                ["name"] = "good-entry",
                ["originalBytes"] = 11,
                ["chunkCount"] = 1,
            }),
        };

        string zipPath = Path.Combine(_tempDir, $"non-object-root-{Guid.NewGuid():N}.zip");
        using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteHandBuiltTopic(zip, "topic-a", goodIndex, "hello world");

            var badIndexEntry = zip.CreateEntry("topics/topic-b/index.json");
            using (var writer = new StreamWriter(badIndexEntry.Open()))
                writer.Write("[]");

            var manifest = zip.CreateEntry("manifest.json");
            using var writer2 = new StreamWriter(manifest.Open());
            writer2.Write(new JsonObject
            {
                ["version"] = 1,
                ["exported"] = DateTimeOffset.UtcNow.ToString("o"),
                ["topics"] = new JsonArray("topic-a", "topic-b"),
                ["totalEntries"] = 1,
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
            "a non-object index root for a LATER topic must not leave an earlier, valid topic " +
            "half-imported");
    }

    [Fact]
    public void AnEntryWithNoNameKey_ThrowsInvalidOperationException_NamingTheTopicAndIndex_AndDoesNotHalfImport()
    {
        // The same silent-drop defect as ANonObjectIndexRoot_... above, one nesting level down:
        // the pre-branch parse called entryEl.GetProperty("name"), which threw
        // KeyNotFoundException and aborted the import. Deserialization leaves Name null instead,
        // and the loop used to `continue` — so that one memory vanished from the restore while
        // every sibling entry imported, and since nothing compares the returned EntryCount
        // against the manifest's totalEntries, the user was told the restore succeeded.
        // Two topics, because the refusal has to happen in the BUILD pass: topic-a must not be
        // written even though it is perfectly valid and comes first.
        var goodIndex = new JsonObject
        {
            ["entries"] = new JsonArray(new JsonObject
            {
                ["name"] = "good-entry",
                ["originalBytes"] = 11,
                ["chunkCount"] = 1,
            }),
        };
        // The nameless entry sits at index 1, behind a valid sibling, so the reported index is
        // the entry's real position in the array and not a constant that happens to read as 0.
        var namelessIndex = new JsonObject
        {
            ["entries"] = new JsonArray(
                new JsonObject
                {
                    ["name"] = "named-sibling",
                    ["originalBytes"] = 11,
                    ["chunkCount"] = 1,
                },
                new JsonObject
                {
                    ["originalBytes"] = 11,
                    ["chunkCount"] = 1,
                }),
        };

        string zipPath = Path.Combine(_tempDir, $"nameless-entry-{Guid.NewGuid():N}.zip");
        using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            WriteHandBuiltTopic(zip, "topic-a", goodIndex, "hello world");
            WriteHandBuiltTopic(zip, "topic-b", namelessIndex, "hello world");

            var manifest = zip.CreateEntry("manifest.json");
            using var writer = new StreamWriter(manifest.Open());
            writer.Write(new JsonObject
            {
                ["version"] = 1,
                ["exported"] = DateTimeOffset.UtcNow.ToString("o"),
                ["topics"] = new JsonArray("topic-a", "topic-b"),
                ["totalEntries"] = 3,
            }.ToJsonString());
        }

        Action act = () =>
        {
            using var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            BundleFormatService.ImportTopicsFromZip(zip, _store, filterTopics: null, overwrite: true);
        };

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*topic-b*")
            .WithMessage("*index 1*",
                "the refusal has to say WHICH entry, or a hand-editable index.json with dozens " +
                "of entries gives the user nothing to go on");

        _store.LoadIndex(MemoryNaming.BuildScopedTopicScope("topic-a")).Should().BeEmpty(
            "a nameless entry in a LATER topic must not leave an earlier, valid topic " +
            "half-imported");
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
    public void AnUndecodableArtifact_IsRejectedBeforeMutation()
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

        Action import = () =>
        {
            using var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            BundleFormatService.ImportTopicsFromZip(zip, _store, filterTopics: null, overwrite: true);
        };
        import.Should().Throw<BundleValidationException>().WithMessage("*invalid artifact body*");
        _store.LoadIndex(MemoryNaming.BuildScopedTopicScope("undecodable-topic")).Should().BeEmpty();
    }

    [Fact]
    public void AnArtifactDeclaringMoreChunksThanTheBoundAllows_IsRejectedBeforeMutation()
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

        Action import = () =>
        {
            using var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            BundleFormatService.ImportTopicsFromZip(zip, _store, filterTopics: null, overwrite: true);
        };
        import.Should().Throw<BundleValidationException>().WithMessage("*invalid artifact body*");
        _store.LoadIndex(MemoryNaming.BuildScopedTopicScope("over-bound-topic")).Should().BeEmpty();
    }

    [Fact]
    public void AnArtifactWithANegativePadFooter_IsRejectedBeforeMutation()
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

        Action import = () =>
        {
            using var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            BundleFormatService.ImportTopicsFromZip(zip, _store, filterTopics: null, overwrite: true);
        };
        import.Should().Throw<BundleValidationException>().WithMessage("*invalid artifact body*");
        _store.LoadIndex(MemoryNaming.BuildScopedTopicScope("negative-pad-topic")).Should().BeEmpty();
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
        AgentKeywords: ["alpha"],
        // NOT an arbitrary distinctive value. The write gate refuses any entry declaring a
        // version above DurableFormats.ArtifactSidecar.Current, so a fixture using 7 (as this
        // did before the gate landed) makes the pin's own probe entry unwritable and fails five
        // tests in this class with ScopeReadOnlyException. Current is the HIGHEST version this
        // build can persist, and it is non-null, so it still satisfies the pin's "must differ
        // from the default" rule. Written as the constant rather than a literal 1 so the fixture
        // follows the contract automatically when the version is next bumped.
        SchemaVersion: DurableFormats.ArtifactSidecar.Current)
    {
        // Unknown is not a positional member (it cannot be — see ArtifactEntry.cs's remarks on
        // why [JsonExtensionData] forces `{ get; set; }`), so it is populated via initializer
        // rather than as a constructor argument like everything above it. See ForComparison()
        // above for why the pin's comparison rewrites this member to raw JSON text rather than
        // comparing JsonElement structs directly.
        //
        // NOTE ON WHAT THIS PIN DOES *NOT* COVER: this member's assertion survives deleting the
        // [JsonExtensionData] attribute entirely. Without it, Unknown is just an ordinary
        // dictionary property with a public setter — it serialises as a nested "unknown" object
        // and deserialises straight back, so the round trip still matches. The extension-data
        // SEMANTICS (unknown keys splicing in as flat top-level keys and surviving a read-write
        // cycle) are pinned by Scrinia.Tests.Formats.SidecarVersioningTests, not here.
        Unknown = new Dictionary<string, System.Text.Json.JsonElement>
        {
            ["futureField"] = System.Text.Json.JsonSerializer.SerializeToElement(
                "a value from a schema this build cannot understand yet"),
        },
    };

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

    // ── Layout identity ──────────────────────────────────────────────────────

    [Fact]
    public void ExportTopicsToZip_WritesTheExpectedArchiveLayout()
    {
        // Characterization: captures today's layout so the extraction in this task cannot change
        // it silently. It exercises ExportTopicsToZip only — see the sibling test below for the
        // one that actually drives the extracted writers.
        var seed = FullyPopulatedEntry();
        string scope = MemoryNaming.BuildScopedTopicScope("layout-topic");
        _store.Upsert(seed, scope);
        _store.WriteArtifactAsync(seed.Name, scope, Nmp2ChunkedEncoder.Encode("hello world"))
            .GetAwaiter().GetResult();

        string zipPath = Path.Combine(_tempDir, "layout.zip");
        using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            BundleFormatService.ExportTopicsToZip(zip, _store, ["layout-topic"]);

        using var read = new ZipArchive(new FileStream(zipPath, FileMode.Open, FileAccess.Read), ZipArchiveMode.Read);
        read.Entries.Select(e => e.FullName).Should().BeEquivalentTo(
        [
            "topics/layout-topic/index.json",
            $"topics/layout-topic/{_store.SanitizeName(seed.Name)}.nmp2",
            "manifest.json",
        ]);
    }

    [Fact]
    public void TheSharedWriters_DrivenWithTheCliShapedInputs_ProduceAnImportableBundle()
    {
        // THIS is the test that justifies the extraction. `scri bundle` builds entries from loose
        // files that were never memories — one topic, no file entities — and used to write the
        // archive with its own inline copy of the layout. Driving the extracted writers with that
        // exact input shape and then importing the result is what proves the CLI's bundles stay
        // readable. Asserting the two writers agree would be circular after the extraction: they
        // are the same code.
        var entry = new ArtifactEntry(
            Name: "loose-file",
            Uri: "",
            OriginalBytes: 11,
            ChunkCount: 1,
            CreatedAt: new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero),
            ContentPreview: "a preview");
        string artifact = Nmp2ChunkedEncoder.Encode("hello world");

        string zipPath = Path.Combine(_tempDir, "cli-shaped.zip");
        using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            BundleFormatService.WriteTopic(
                zip, _store, "cli-topic", [entry], [(entry.Name, artifact)]);
            BundleFormatService.WriteManifest(zip, ["cli-topic"], 1, fileEntities: null);
        }

        using (var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
            BundleFormatService.ImportTopicsFromZip(zip, _store, filterTopics: null, overwrite: true);

        var imported = _store.LoadIndex(MemoryNaming.BuildScopedTopicScope("cli-topic"))
            .Single(e => e.Name.Equals("loose-file", StringComparison.OrdinalIgnoreCase));
        imported.ContentPreview.Should().Be("a preview");
        imported.CreatedAt.Should().Be(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }
}
