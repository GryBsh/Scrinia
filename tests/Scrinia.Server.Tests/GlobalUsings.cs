// Matches tests/Scrinia.Tests/GlobalUsings.cs. This project previously had no global usings, so
// all 30 test files carried `using Xunit;` individually — an inconsistency between the two test
// projects that kept getting re-reported as a defect. It was never a defect for this pair, and
// this project is now moot: no file here still carries a redundant `using Xunit;`. That is not
// true workspace-wide — Scrinia.Tests still has per-file `using Xunit;` beside this same global
// using, and Scrinia.Merge.Tests, Scrinia.Plugin.Embeddings.Tests and
// Scrinia.Retrieval.Benchmarks.Tests carry their own (not necessarily matching) conventions.
global using Xunit;
