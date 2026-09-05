namespace Scrinia.Core.Embeddings;

/// <summary>
/// Declarative identity for one downloadable model file: the <see cref="Identifier"/> that scopes it
/// under <c>SCRINIA_HOME/models</c>, the <see cref="Url"/> to fetch, the published <see cref="Filename"/>,
/// and the optional known-good <see cref="ExpectedSha256"/> used to verify content before promotion.
/// </summary>
/// <param name="Identifier">Logical model name scoping the published destination.</param>
/// <param name="Url">Source URL of the asset.</param>
/// <param name="Filename">Published file name once downloaded and verified.</param>
/// <param name="ExpectedSha256">Optional lowercase SHA-256 of the expected content; when set, the
/// downloaded bytes must match it or the download is rejected.</param>
public sealed record DownloadableAsset(
    string Identifier,
    string Url,
    string Filename,
    string? ExpectedSha256);
