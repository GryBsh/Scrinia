# Build the archived worker and Scrinia payloads

Run these commands from a clean extraction of source.zip. Install the .NET SDK pinned by global.json (10.0.203). The archive contains the worker and Scrinia CLI project graphs, their transitive project references, shared build inputs and embedded resources.

Restore both entry projects before using --no-restore. For offline builds, provide an existing NuGet global package cache containing the recorded dependencies and an empty local package source directory. Set NUGET_PACKAGES to the existing cache if it is outside the user's default cache. The commands below disable NuGet auditing and use only the specified local source; a missing cached dependency fails restore.

Create C:/absolute/owned-build/empty-feed as an empty directory in your owned build location, then run:

```powershell
rtk proxy dotnet restore benchmarks/Scrinia.LongMemEvalV2.Worker/Scrinia.LongMemEvalV2.Worker.csproj --locked-mode --source C:/absolute/owned-build/empty-feed -p:NuGetAudit=false
rtk proxy dotnet restore src/Scrinia/Scrinia.csproj --locked-mode --source C:/absolute/owned-build/empty-feed -p:NuGetAudit=false
rtk proxy dotnet build benchmarks/Scrinia.LongMemEvalV2.Worker/Scrinia.LongMemEvalV2.Worker.csproj -c Release --no-restore --nologo
rtk proxy dotnet build src/Scrinia/Scrinia.csproj -c Release --no-restore --nologo
rtk proxy dotnet publish benchmarks/Scrinia.LongMemEvalV2.Worker/Scrinia.LongMemEvalV2.Worker.csproj -c Release --no-restore --no-self-contained -o C:/absolute/owned-build/published-worker
rtk proxy dotnet publish src/Scrinia/Scrinia.csproj -c Release --no-restore --no-self-contained -o C:/absolute/owned-build/published-scri
```

Keep every file in each published directory together. Configure worker_command with published-worker/scrinia-lme-v2-worker.exe and mcp_executable with published-scri/scri.exe on Windows; other platforms use the corresponding apphost names. Both payloads require the matching installed .NET runtime.

Run the Python adapter from this same extracted checkout and set source_root to its canonical extraction root. Copy the experiment preset, resolve its paths and prerequisites, capture the catalog from the published Scrinia executable, and create a new freeze. Existing freezes remain bound to their original executing source checkout and payload bytes. Never carry prior grades into a rebuilt experiment.

The archive's project/resource closure test checks determinable ProjectReference, Import, EmbeddedResource, Content, None and Compile inputs. Isolated restore, build and publish validation checks the supported worker and CLI graphs, including shared SDK imports. The full repository solution is outside this archive build contract.
