using System.Runtime.CompilerServices;

// Exposes internal members (e.g. EmbeddingsPlugin.ReindexStoreAsync) to the test project so the
// namespaced-scope reindex regression test can call it directly instead of going through the
// full ASP.NET /embeddings/reindex HTTP pipeline.
[assembly: InternalsVisibleTo("Scrinia.Plugin.Embeddings.Tests")]
