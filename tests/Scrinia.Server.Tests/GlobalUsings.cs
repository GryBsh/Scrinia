// Matches tests/Scrinia.Tests/GlobalUsings.cs. This project previously had no global usings, so
// all 30 test files carried `using Xunit;` individually — an inconsistency between the two test
// projects that kept getting re-reported as a defect. It was never a defect; it is now moot.
global using Xunit;
