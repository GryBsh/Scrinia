using System.Runtime.CompilerServices;

// Bench test project needs access to internal constructors on the LLM provider
// clients (used to inject stubbed HttpClient for unit tests).
[assembly: InternalsVisibleTo("Scrinia.Retrieval.Benchmarks.Tests")]
