using Lane.Core.Identity;
using Microsoft.Extensions.Configuration;

namespace Lane.Core.Surfaces;

/// <summary>
/// A transport. Turns platform events into <c>InboundEvent</c>s and attaches channels the
/// kernel can write back through.
///
/// A surface owns no model, no memory, no ear and no mouth — everything it used to own in
/// v2 is now a shared kernel part addressed by session. That is what allows several
/// surfaces, and several instances of one surface, to run at once.
/// </summary>
public interface ISurface : IAsyncDisposable
{
    SurfaceId Id { get; }

    Task StartAsync(CancellationToken ct);
    Task StopAsync(CancellationToken ct);
}

/// <summary>
/// Builds one configured surface instance. Registered keyed by <see cref="TypeName"/>, so
/// config can name the same type twice with different ids and options.
/// </summary>
public interface ISurfaceFactory
{
    /// <summary>The config discriminator: "discord", "terminal", "api".</summary>
    string TypeName { get; }

    ISurface Create(SurfaceId id, IConfiguration options, IServiceProvider services);
}
