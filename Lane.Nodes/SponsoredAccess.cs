using Lane.Core.Credits;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lane.Nodes;

public sealed class SponsoredAccess(ISponsorships sponsorships, NodesOptions options, ILogger<SponsoredAccess>? log = null)
    : ISponsoredAccess
{
    private readonly ILogger _log = log ?? NullLogger<SponsoredAccess>.Instance;

    public async ValueTask<bool> TryChargeAsync(SponsoredKind kind, string target, CancellationToken ct)
    {
        try
        {
            string? account = await sponsorships.ChargeAsync(kind, target, options.CreditsPerSponsoredRequest, ct).ConfigureAwait(false);

            if (account is null) _log.LogDebug("No sponsor can pay for {Kind} {Target}", kind, target);

            return account is not null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Could not charge the sponsors of {Kind} {Target}", kind, target);

            return false;
        }
    }

    public async ValueTask<SponsoredApiClient?> FindApiClientAsync(string key, CancellationToken ct)
    {
        try
        {
            return await sponsorships.FindApiClientByKeyAsync(key, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogError(ex, "Could not look up a sponsored API client");

            return null;
        }
    }
}
