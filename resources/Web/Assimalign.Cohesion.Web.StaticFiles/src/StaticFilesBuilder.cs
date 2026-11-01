using System;
using System.IO;

using Assimalign.Cohesion.FileSystem;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.StaticFiles.Internal;

namespace Assimalign.Cohesion.Web.StaticFiles;

/// <summary>
/// Configures the static-files middleware that <c>builder.Services.AddStaticFiles(files =&gt; ...)</c>
/// registers and <see cref="WebApplicationStaticFilesExtensions.UseStaticFiles(IWebApplicationPipelineBuilder)"/>
/// places.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Build"/> validates the options and snapshots them into the middleware, so an invalid option
/// fails where the application registers static files, never per request, and changing the options
/// afterwards has no effect.
/// </para>
/// <para>
/// The middleware serves the file system <see cref="UseFileSystem"/> names. Without one it serves the
/// application's web root (<see cref="IWebApplicationContext.WebRootPath"/>, <c>wwwroot</c> under the
/// content root by default), which <c>UseStaticFiles()</c> mounts read-only when the pipeline is built.
/// </para>
/// </remarks>
[global::System.Diagnostics.DebuggerDisplay("StaticFilesBuilder")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class StaticFilesBuilder
{
    private IFileSystem? _fileSystem;

    /// <summary>
    /// Initializes a builder that serves the application's web root with the default options: default
    /// documents (<c>index.html</c>/<c>index.htm</c>), conditional GET, single byte-range support,
    /// precompressed <c>.br</c>/<c>.gz</c> sibling negotiation, and unknown extensions blocked.
    /// </summary>
    public StaticFilesBuilder()
    {
    }

    /// <summary>
    /// Gets the options: the request-path prefix, the default documents, the <c>Cache-Control</c> value,
    /// the content-type overlays, and precompressed-sibling negotiation.
    /// </summary>
    public StaticFilesOptions Options { get; } = new();

    /// <summary>
    /// Serves <paramref name="fileSystem"/> instead of the application's web root.
    /// </summary>
    /// <remarks>
    /// Any implementation can be mounted (physical, in-memory, aggregate); the middleware can never
    /// address anything outside it. The application keeps ownership of the file system: the middleware
    /// never disposes it.
    /// </remarks>
    /// <param name="fileSystem">The file system to serve at <see cref="StaticFilesOptions.RequestPath"/>.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="fileSystem"/> is <see langword="null"/>.</exception>
    public StaticFilesBuilder UseFileSystem(IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        _fileSystem = fileSystem;
        return this;
    }

    /// <summary>
    /// Builds the static-files middleware from <see cref="Options"/>.
    /// </summary>
    /// <returns>
    /// The middleware, which serves <c>GET</c>/<c>HEAD</c> requests under the request-path prefix and
    /// passes every other request to the next middleware.
    /// </returns>
    /// <exception cref="ArgumentException">
    /// The request path does not begin with <c>/</c>, a default-document name is not a valid bare file
    /// name, the <c>Cache-Control</c> value does not parse per RFC 9111, or a content-type mapping or the
    /// fallback content type is empty or is not a valid HTTP field value.
    /// </exception>
    public IWebApplicationMiddleware Build() => BuildMiddleware();

    /// <summary>
    /// Validates and snapshots the options into the middleware. A middleware built without a file system
    /// is completed with the web root by <c>UseStaticFiles()</c>.
    /// </summary>
    internal StaticFilesMiddleware BuildMiddleware()
    {
        FileName[] defaultDocuments = WebApplicationStaticFilesExtensions.Validate(Options);

        return new StaticFilesMiddleware(_fileSystem, Options, defaultDocuments);
    }

    private sealed class DebugView
    {
        private readonly StaticFilesBuilder _target;

        public DebugView(StaticFilesBuilder value)
        {
            _target = value;
        }

        public StaticFilesOptions Options => _target.Options;
    }
}
