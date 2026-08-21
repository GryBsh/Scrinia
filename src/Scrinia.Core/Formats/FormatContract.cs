namespace Scrinia.Core.Formats;

/// <summary>
/// One durable format's version contract. Every format that persists bytes this project reads
/// back must have one, registered in <see cref="DurableFormats"/>.
///
/// <para><b>Why <see cref="OnOlder"/> and <see cref="OnNewer"/> are separate knobs.</b> The
/// obvious design is one policy for "version mismatch". It cannot express the one format in
/// this repository whose gate already works: <c>facts.json</c> rebuilds on an OLDER version
/// too, because its version tracks the DERIVATION RULE rather than the file's shape, so a
/// cache written by a previous build is a path around the new rule. Collapsing the two knobs
/// would force that format out of the registry.</para>
/// </summary>
/// <param name="Name">Stable identifier, used in diagnostics and in the registry tests.</param>
/// <param name="Current">The version this build writes.</param>
/// <param name="AbsentMeans">The version implied by a missing version field, or null when a
/// missing field makes the file invalid (a format whose version is a required sentinel).</param>
public sealed record FormatContract(
    string Name,
    int Current,
    int? AbsentMeans,
    FormatDisposition OnOlder,
    FormatDisposition OnNewer);
