using System;

namespace Assimalign.Cohesion.Web.RequestTimeouts;

/// <summary>
/// Builder-time options for the request-timeout middleware
/// (<see cref="WebApplicationExtensions.UseRequestTimeouts(IWebApplicationPipelineBuilder, Action{RequestTimeoutOptions}?)"/>).
/// </summary>
public sealed class RequestTimeoutOptions
{
    private TimeProvider _timeProvider = TimeProvider.System;

    /// <summary>
    /// Gets or sets the global default timeout policy, applied to every request that reaches the
    /// middleware unless the matched endpoint carries its own <see cref="RequestTimeoutMetadata"/>.
    /// <see langword="null"/> (the default) applies no global timeout — only endpoints with
    /// metadata are governed.
    /// </summary>
    public RequestTimeoutPolicy? DefaultPolicy { get; set; }

    /// <summary>
    /// Gets or sets the time source the per-request timers measure against. Defaults to
    /// <see cref="TimeProvider.System"/>; inject a test provider to control expiry
    /// deterministically.
    /// </summary>
    /// <exception cref="ArgumentNullException">The value is set to <see langword="null"/>.</exception>
    public TimeProvider TimeProvider
    {
        get => _timeProvider;
        set => _timeProvider = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Gets or sets whether timeout enforcement is suspended while a debugger is attached
    /// (<see cref="System.Diagnostics.Debugger.IsAttached"/>), so a paused debug session does not
    /// cancel the request under inspection. Defaults to <see langword="true"/>, mirroring
    /// ASP.NET's request-timeouts middleware.
    /// </summary>
    public bool SuspendWhenDebuggerAttached { get; set; } = true;
}
