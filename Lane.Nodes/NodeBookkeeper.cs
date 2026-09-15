using Lane.Core.Credits;
using Lane.Core.Models;
using Lane.Core.Nodes;
using Lane.Nodes.Protocol;
using Microsoft.Extensions.Logging;

namespace Lane.Nodes;

/// <summary>Records node identities in the directory and pays them for responses. Never throws; failures are logged.</summary>
public sealed class NodeBookkeeper(
    INodeDirectory           directory,
    ICreditLedger            ledger,
    NodesOptions             options,
    ILogger<NodeBookkeeper>? log = null)
{
    public async Task JoinedAsync(NodeHello hello)
    {
        try
        {
            await directory.SeenAsync(hello.Identity, hello.Name, hello.Delegation?.CredentialId, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Could not record node identity {KeyId}", hello.Identity.KeyId);
        }
    }

    public async Task AnsweredAsync(NodeIdentity identity)
    {
        try
        {
            await directory.RecordResponseAsync(identity, CancellationToken.None).ConfigureAwait(false);

            if (options.CreditsPerResponse > 0)
                await ledger.EarnAsync(identity.KeyId, options.CreditsPerResponse, "Answered a request", CancellationToken.None)
                    .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "Could not record a response by {KeyId}", identity.KeyId);
        }
    }
}
