# Wave 3 Report

## Commits
- 415e05b wave3: TextPreview helper, ILocalStoreLayout, truncation docs, merge-driver prose (N5,N7,N8,N10)
- fdd7e85 wave3: checks CI, compact mutation:none, link partial guard, facts docs, release scri-merge (N6,N17,N18,N19,N20,N1-N3)

BASE: 7e1b0ba

## Changes
- Added src/Scrinia.Core/TextPreview.cs (pure helper, N8 alias to ContentPreview)
- Added src/Scrinia.Core/ILocalStoreLayout.cs (narrowed layout interface N10)
- McpResponse.cs: added transport-boundary comment at MaxResponseChars (N5)
- MemoryTools.cs: added fence-aware ephemeral alias seam comment (N14)
- docs/multi-user-setup.md: added merge driver prose with correct *.meta.json merge=scrinia-meta + *.nmp2 merge=scrinia-nmp2 and generated .gitattributes note (N7)
- .github/workflows/checks.yml: new CI guards for skill/doc parity (7 skills), version single-source grep, xml-doc threshold (N1-N3)
- MemoryTools.Lifecycle.cs: compact single-chunk now returns Success with info mutation:none (N17)
- MemoryTools.Core.cs: link partial failure uses Error ?? Content guard and no YAML blob echo (N18)
- MemoryTools.Facts.cs: added truncation semantics DTO docs (N19)
- .github/workflows/release.yml: added Publish CLI and merge step (N20)
- tests/Scrinia.Tests/Wave3/Wave3HygieneTests.cs: 8 Wave3 hygiene tests (all green)

## Hygiene
- YamlDotNet audit: no reference in Scrinia.Mcp.csproj (hand-rolled serializer) - N4 vacuously compliant
- BackgroundWork uses Interlocked/Volatile for _inFlight; no _isRunning field - volatile hygiene vacuous
- Nmp2Strategy has no Regex (no hoist needed)
- TextAnalysis allocations acceptable for current hot path

## Verification
- dotnet build Scrinia.sln -c Debug: PASS (0 errors)
- dotnet test --filter Wave3: PASS 8/8
- dotnet test Scrinia.Tests full: 2735 passed, 19 failed (ModelRelocationTests UnauthorizedAccessException - pre-existing env, not wave3), 19 skipped
- CI local checks: skill count 7 == 7, version grep clean

## Notes
- ILocalStoreLayout 32-site retype deferred (interface exists, FileMemoryStore already has members); narrow retype is pure rename with no behavior change, compiler-checked when adopted.
- SaveIndex already removed from IMemoryStore (N9 done), kept on FileMemoryStore.
- Release archive now includes scri + scri-merge via publish step.
