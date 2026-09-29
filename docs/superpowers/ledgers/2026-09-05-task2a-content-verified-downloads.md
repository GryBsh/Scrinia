# Task 2A Ledger — Content-verified model downloads with atomic promotion

| Decision | Evidence |
| --- | --- |
| Download integrity belongs in Core, not duplicated per manager | `VerifiedModelDownloader` (new, `Scrinia.Core.Embeddings`) is the single content-verified, atomically-promoted downloader used by both `Model2VecModelManager` and `VulkanModelManager`; both `IsModelAvailable` implementations delegate to `IsAssetAvailable`. |
| Public `EnsureModelAsync` signature retained in both managers | Both managers still declare `public Task EnsureModelAsync(string modelDir, ILogger logger, CancellationToken ct = default)`; a new `internal` overload with `ct` LAST (CA1068) carries the `Func<HttpClient>` + `IReadOnlyList<DownloadableAsset>` params. Solution builds clean; benchmark + setup callers compile unchanged. |
| Digest is SHA-256, hashed while streaming, validated before any move | `WriteAndHashAsync` streams via `CryptoStream(SHA256.Create())` to a unique per-download temp, disposes the `CryptoStream` (flush + finalize) before returning `sha.Hash!`; `DownloadAsync` compares the lowercase digest to `ExpectedSha256` and throws `ModelDownloadException` (after deleting the temp) on mismatch. `ModelDownloadIntegrityTests.DigestMismatch_IsRejectedBeforePromotion` asserts no final file exists. |
| Promotion is serialized and idempotent under a race | `lock (PromotionLock)` check-then-`File.Move`; a loser whose temp already validated discards its duplicate and returns when the destination exists (both pre-lock and post-lock `File.Exists` checks). `ConcurrentDownloads_UseUniqueTempsAndPublishOneValidAsset` proves distinct temps + single published asset + no leftover `.tmp`. |
| `.tmp`/partial files never count as available | `IsAssetAvailable` matches only a direct legacy file or a content-addressed `models/<digest>/<filename>`; `PreSeededPartialTemp_IsNotVisibleByIsModelAvailable` seeds a `.tmp` and asserts it is not visible. |
| Tests isolate `SCRINIA_HOME` and use fakes, never the network | `ModelDownloadIntegrityTests` collection sets/`unset` the `SCRINIA_HOME` env var per test with a fresh temp home; downloads go through `FakeHttpHandler`/`BlockingFakeHttpHandler` + `DelayingStream` (chunked reads, gate, `failAfter`, `ConcurrentCounter`). |
| Verification is deterministic and fast | `ModelDownloadIntegrityTests` 9/9 pass (2 s); full project 41/41; `dotnet build Scrinia.sln` 0 warnings / 0 errors. |
| BASE bounded to `ef3879e`, no frozen surface edited | Diff vs `ef3879e` touches only the five embeddings/manager files, two `csproj` `InternalsVisibleTo`, and the new test; `UserHome.cs` and all Phase 1–4 surfaces untouched. |

(End of file)
