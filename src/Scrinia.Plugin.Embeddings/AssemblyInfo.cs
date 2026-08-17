using System.Runtime.CompilerServices;

// Exposes internal members to the test project. EmbeddingsPlugin.ReindexStoreAsync is public now
// (it satisfies IEmbeddingsAdmin), but this stays in case other internals need test access.
[assembly: InternalsVisibleTo("Scrinia.Plugin.Embeddings.Tests")]
