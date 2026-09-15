namespace Lane.Nodes;

public sealed class NodesOptions
{
    public bool Enabled { get; set; }

    public string Urls { get; set; } = "http://0.0.0.0:5070";

    /// <summary>How long a request waits for a node in its pool to have a free slot.</summary>
    public TimeSpan AcquireTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a new connection has to send its hello.</summary>
    public TimeSpan HelloTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Credits paid to an identity for each accepted response.</summary>
    public long CreditsPerResponse { get; set; } = 1;

    /// <summary>Credits a sponsored Discord message or API request costs its sponsors.</summary>
    public long CreditsPerSponsoredRequest { get; set; } = 1;

    /// <summary>How long a security key sign-in to the portal lasts.</summary>
    public TimeSpan PortalSignInLifetime { get; set; } = TimeSpan.FromHours(1);
}
