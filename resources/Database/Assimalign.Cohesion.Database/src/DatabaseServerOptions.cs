using System;
using System.Globalization;
using System.Threading;
using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Database.Internal;
using Assimalign.Cohesion.Database.Security;

namespace Assimalign.Cohesion.Database;

/// <summary>Transport, authentication and session limits shared by the five model servers.</summary>
/// <remarks>Model leaves snapshot these settings at server creation. Listener ownership transfers
/// to the server, whose stop terminally disposes it. An explicit listener wins over the platform's
/// named endpoint (sql, documents, graph, kv or blob), used when managed and no listener is supplied.</remarks>
// Deviates from the repo interface-first rule per design decision: Database engines are concrete-first — abstract bases with protected cores and sealed model leaves (owner, 2026-10-04; database-area.md).
public abstract class DatabaseServerOptions
{
    /// <summary>Initializes the common server defaults.</summary>
    protected DatabaseServerOptions() { }

    /// <summary>Gets or sets the listener whose lifecycle transfers to the server.</summary>
    /// <remarks>When null and COHESION_GATEWAY is nonempty, server creation tries the model's named platform endpoint with plain TCP.
    /// With no valid endpoint, the existing missing-listener refusal applies. Resolution changes
    /// only the server's options snapshot, leaving the caller's options reusable.</remarks>
    public IDuplexConnectionListener? Listener { get; set; }

    /// <summary>Gets or sets the authenticator; null selects DatabaseAuthenticator.AllowAll.</summary>
    public DatabaseAuthenticator? Authenticator { get; set; }

    /// <summary>Gets or sets the maximum accepted concurrent sessions; defaults to 100.</summary>
    public int MaxSessions { get; set; } = 100;

    /// <summary>Gets or sets the unauthenticated connection timeout; defaults to ten seconds.</summary>
    public TimeSpan AuthenticationTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Gets or sets the idle timeout; defaults to thirty minutes. InfiniteTimeSpan disables eviction.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Gets or sets the shutdown and terminal-I/O drain budget; defaults to thirty seconds.
    /// Must be positive, finite, and at most 4,294,967,294 milliseconds.</summary>
    public TimeSpan ShutdownDrainTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Checks a model snapshot's common requirements, then resolves a missing listener from the platform.</summary>
    /// <param name="model">The model's diagnostic name. Reserved values SQL, Documents, Graph,
    /// KeyValuePair and Blob also select the sql, documents, graph, kv and blob platform assignments.</param>
    /// <param name="engineName">The engine the server fronts.</param>
    /// <exception cref="ArgumentException">Listener is missing or MaxSessions is not positive.</exception>
    /// <exception cref="InvalidOperationException">The assigned endpoint's bind host is unsupported.</exception>
    protected void ValidateCommon(string model, DatabaseName engineName)
    {
        if (MaxSessions <= 0)
        { throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.ServerSessionLimitInvalid, model, engineName), nameof(MaxSessions)); }
        Listener ??= DatabasePlatformEndpoint.CreateListener(model);
        if (Listener is null)
        { throw new ArgumentException(string.Format(CultureInfo.CurrentCulture, ErrorMessages.ServerListenerRequired, model, engineName), nameof(Listener)); }
    }

    /// <summary>Checks an existing model timer policy without changing its zero or infinite handling.</summary>
    /// <param name="value">The configured timeout.</param>
    /// <param name="option">The option name reported as ParamName.</param>
    /// <param name="model">The model's diagnostic name.</param>
    /// <param name="engineName">The engine the server fronts.</param>
    /// <param name="allowInfinite">Whether InfiniteTimeSpan is allowed.</param>
    /// <param name="allowZero">Whether zero is allowed.</param>
    /// <exception cref="ArgumentOutOfRangeException">The value violates the model's timer policy.</exception>
    protected static void ValidateTimeout(TimeSpan value, string option, string model, DatabaseName engineName,
        bool allowInfinite = false, bool allowZero = false)
    {
        if (allowInfinite && value == Timeout.InfiniteTimeSpan) { return; }
        if ((allowZero ? value >= TimeSpan.Zero : value > TimeSpan.Zero) && value.TotalMilliseconds <= uint.MaxValue - 1) { return; }
        string policy = allowInfinite
            ? allowZero ? ErrorMessages.ServerTimeoutNonnegativeOrInfinite : ErrorMessages.ServerTimeoutPositiveOrInfinite
            : ErrorMessages.ServerTimeoutPositiveFinite;
        throw new ArgumentOutOfRangeException(option, value,
            string.Format(CultureInfo.CurrentCulture, ErrorMessages.ServerTimeoutInvalid, model, engineName, option, policy));
    }
}
