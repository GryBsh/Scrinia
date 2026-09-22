using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;

namespace Scrinia.Mcp;

public sealed partial class ScriniaMcpTools
{
    /// <summary>
    /// Auto-promotes a legacy <c>local:agent</c> NMP/2 entry to a first-class
    /// <c>.scrinia/agent/{subject}.md</c> file entity on first touch, then removes the legacy
    /// artifact. Mirrors the <c>/skill/</c> disk-first resolution so <c>/agent/</c> and
    /// <c>/skill/</c> answer the same way once the legacy data is gone.
    ///
    /// <para>Promotion is TWO ORDERED STEPS, matching the <c>remember</c> file-entity publish:
    /// <enumerate>
    /// <item>publish the <c>.md</c> body plus its <c>.meta.json</c> sidecar through the shared
    /// <see cref="FileEntityTransactionCoordinator"/>;</item>
    /// <item>on <c>Created</c>/<c>Replaced</c> only, delete the legacy artifact so it can never be
    /// served again.</item>
    /// </enumerate>
    /// The second read of a promoted agent finds the published file on disk and returns it
    /// directly, so promotion is idempotent and never re-deletes. Returns <c>true</c> only when the
    /// publish succeeded and the legacy entry was removed; <c>false</c> when there is no legacy
    /// entry (or the publish fails), leaving the fall-through NMP/2 resolution untouched.
    ///
    /// <para>Shared with the bulk sweep in the follow-up task — the single promotion primitive for
    /// every legacy <c>local:agent</c> entry.</para>
    /// </summary>
    internal static async Task<bool> PromoteLocalAgentAsync(
        IMemoryStore store, MemoryAddress address, string agentSubject, CancellationToken cancellationToken)
    {
        string legacyArtifact;
        try
        {
            legacyArtifact = await store.ReadArtifactAsync(address, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            return false;
        }

        byte[] body = Nmp2Strategy.Instance.Decode(legacyArtifact);
        string dir = PathRouter.ToFileEntityDir("agent", store.WorkspaceRoot);
        string bodyPath = Path.Combine(dir, agentSubject + ".md");
        string metaPath = Path.ChangeExtension(bodyPath, ".meta.json");
        string versionsDir = Path.Combine(dir, "versions");

        FileEntityMutationResult publish;
        try
        {
            publish = await new FileEntityTransactionCoordinator(store.WorkspaceRoot)
                .ExecuteAsync(
                    new FileEntityMutation("agent", agentSubject, bodyPath, metaPath, versionsDir, body, null, false, false),
                    cancellationToken);
        }
        catch
        {
            return false;
        }

        if (publish.Outcome is not (WholeMemoryOutcome.Created or WholeMemoryOutcome.Replaced))
            return false;

        (string scope, string subject) = store.ResolveAddress(address);
        store.Remove(address);
        store.DeleteArtifact(subject, scope);
        return true;
    }
}
