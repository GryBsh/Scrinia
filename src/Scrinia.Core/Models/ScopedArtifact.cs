namespace Scrinia.Core.Models;

public sealed record ScopedArtifact(
    string Scope,
    ArtifactEntry Entry);
