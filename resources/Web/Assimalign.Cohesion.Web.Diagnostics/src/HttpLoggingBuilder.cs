using System;

using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Diagnostics.Internal;

namespace Assimalign.Cohesion.Web.Diagnostics;

/// <summary>
/// Configures the HTTP request/response logging middleware that
/// <c>builder.Services.AddHttpLogging(logging =&gt; ...)</c> registers and
/// <see cref="WebApplicationExtensions.UseHttpLogging(IWebApplicationPipelineBuilder)"/> places.
/// </summary>
/// <remarks>
/// <para>
/// The middleware writes its entries through a logger the application supplies, and there is no default:
/// <see cref="Build"/> refuses to run until <see cref="UseLogger"/> or <see cref="UseLoggerFactory"/>
/// supplies one. In a Web.Hosting application the logger factory is the application's own, which the
/// provider overload resolves:
/// <c>builder.Services.AddHttpLogging((services, logging) =&gt; logging.UseLoggerFactory(services.GetRequiredService&lt;ILoggerFactory&gt;()))</c>.
/// </para>
/// <para>
/// <see cref="Build"/> validates <see cref="Options"/> and freezes them into an immutable snapshot, so an
/// invalid option fails where the application registers HTTP logging, never per request, and changing the
/// options afterwards has no effect on the middleware already built.
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("HttpLoggingBuilder")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class HttpLoggingBuilder
{
    private ILogger? _logger;
    private ILoggerFactory? _loggerFactory;

    /// <summary>
    /// Initializes a builder with the default options and no logger, which <see cref="Build"/> refuses until
    /// <see cref="UseLogger"/> or <see cref="UseLoggerFactory"/> supplies one.
    /// </summary>
    public HttpLoggingBuilder()
    {
    }

    /// <summary>
    /// Gets the logging options: the fields captured, the category and level, the header allowlists and the
    /// redaction placeholder, the body-capture limits and content types, the client-address resolver, and the
    /// time source.
    /// </summary>
    public HttpLoggingOptions Options { get; } = new();

    /// <summary>
    /// Writes the entries through <paramref name="logger"/>.
    /// </summary>
    /// <param name="logger">
    /// The composed logger the entries are written to — typically <c>ILoggerFactory.Create(category)</c> for
    /// <see cref="HttpLoggingOptions.Category"/> on the application's logger factory, so the factory's filter
    /// rules for that category apply to it.
    /// </param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> is <see langword="null"/>.</exception>
    /// <remarks>A later call to this method or <see cref="UseLoggerFactory"/> replaces the logger an earlier call supplied.</remarks>
    public HttpLoggingBuilder UseLogger(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _loggerFactory = null;
        return this;
    }

    /// <summary>
    /// Writes the entries through a logger that <paramref name="loggerFactory"/> creates for
    /// <see cref="HttpLoggingOptions.Category"/> when <see cref="Build"/> runs.
    /// </summary>
    /// <param name="loggerFactory">The application's logger factory.</param>
    /// <returns>This builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="loggerFactory"/> is <see langword="null"/>.</exception>
    /// <remarks>A later call to this method or <see cref="UseLogger"/> replaces the logger an earlier call supplied.</remarks>
    public HttpLoggingBuilder UseLoggerFactory(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);

        _loggerFactory = loggerFactory;
        _logger = null;
        return this;
    }

    /// <summary>
    /// Builds the HTTP logging middleware from <see cref="Options"/> over the logger <see cref="UseLogger"/> or
    /// <see cref="UseLoggerFactory"/> supplied.
    /// </summary>
    /// <returns>
    /// The middleware, which emits one structured entry per exchange (fields per
    /// <see cref="HttpLoggingOptions.Fields"/>, header values redacted outside the configured allowlists) when
    /// the downstream pipeline completes or faults.
    /// </returns>
    /// <exception cref="InvalidOperationException">Neither <see cref="UseLogger"/> nor <see cref="UseLoggerFactory"/> was called.</exception>
    /// <exception cref="ArgumentException">
    /// The options are invalid: an empty category or redaction placeholder, a <see cref="LogLevel.None"/> level, or
    /// a <see langword="null"/> time provider (<see cref="ArgumentNullException"/>).
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">A body-capture limit is negative.</exception>
    public IWebApplicationMiddleware Build()
    {
        if (_logger is null && _loggerFactory is null)
        {
            throw new InvalidOperationException(ErrorMessages.HttpLoggingLoggerNotSet);
        }

        HttpLoggingSnapshot snapshot = HttpLoggingSnapshot.Create(Options);
        ILogger logger = _logger ?? _loggerFactory!.Create(snapshot.Category);

        return new HttpLoggingMiddleware(logger, snapshot);
    }

    private sealed class DebugView
    {
        private readonly HttpLoggingBuilder _target;

        public DebugView(HttpLoggingBuilder value)
        {
            _target = value;
        }

        public HttpLoggingOptions Options => _target.Options;
    }
}
