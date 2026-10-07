using Scrinia.Core.Bundles;
using Scrinia.Mcp;

namespace Scrinia.Commands;

public partial class ScriniaCommands
{
    private static int EmitBundleFailure(BundleImportException exception, bool json, string action) =>
        EmitMcpResult(ResponseBuilder.Error(exception,
            "Resolve the retained bundle recovery evidence before retrying."), json, action);
}
