using System;
using System.Globalization;
using System.IO;
using System.Linq;

using Assimalign.Cohesion.FileSystem;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.StaticFiles.Internal;

namespace Assimalign.Cohesion.Web.StaticFiles;

/// <summary>
/// Pipeline-builder extension that places static-file serving in the Web application middleware
/// pipeline.
/// </summary>
/// <remarks>
/// Registration is dependency-free per the Web-area rules: <c>builder.Services.AddStaticFiles(files =&gt; ...)</c>
/// supplies the file system and options explicitly (<see cref="StaticFilesBuilder"/>) and snapshots them
/// into the middleware — no configuration binding, no request-time service location. Mount any
/// <see cref="IFileSystem"/> implementation (physical, in-memory, aggregate); the middleware can never
/// address anything outside that mount.
/// </remarks>
public static class WebApplicationStaticFilesExtensions
{
    extension(IWebApplicationPipelineBuilder builder)
    {
        /// <summary>
        /// Places the middleware that serves <c>GET</c>/<c>HEAD</c> requests from mounted file systems:
        /// every mount the application registered with <c>builder.Services.AddStaticFiles(files =&gt; ...)</c>,
        /// in registration order, or one with the default options over the application's web root when it
        /// registered none.
        /// </summary>
        /// <returns>The same pipeline builder for chaining.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="builder"/> is <see langword="null"/>.</exception>
        /// <remarks>
        /// <para>
        /// The file system and options are configured where the middleware is registered
        /// (<see cref="StaticFilesBuilder"/>); this verb only positions it. Each registration is a mount, so
        /// mounts add up rather than replace each other: with an asset folder under <c>/assets</c> and the web
        /// root registered, a request goes to the first mount that serves it, and one no mount serves passes
        /// to the next middleware.
        /// </para>
        /// <para>
        /// A registration that names no file system, and the default middleware, serve the application's
        /// web root (<see cref="IWebApplicationContext.WebRootPath"/>, <c>wwwroot</c> under the content root
        /// by default), which this verb mounts read-only when the pipeline is built. Only the web root is
        /// served — never the content root or the process working directory, which hold
        /// <c>appsettings*.json</c> and the application's binaries. When the application has no web root, or
        /// the directory does not exist when the pipeline is built, the verb places nothing and every request
        /// passes to the next middleware.
        /// </para>
        /// <para>
        /// Place it ahead of <c>UseRouting()</c>, so an existing asset is served before routing. Inside a
        /// <c>Map(path)</c> branch it serves below the branch's prefix.
        /// </para>
        /// </remarks>
        public IWebApplicationPipelineBuilder UseStaticFiles()
        {
            ArgumentNullException.ThrowIfNull(builder);

            return builder.Use(static (context, next) =>
            {
                StaticFilesMiddleware[] registrations = [.. context.Middleware.OfType<StaticFilesMiddleware>()];
                if (registrations.Length == 0)
                {
                    registrations = [new StaticFilesBuilder().BuildMiddleware()];
                }

                // Each registration is a mount. They serve in registration order, so the chain is composed
                // from the last mount back to the first; a mount with nothing to serve is left out.
                WebApplicationMiddleware pipeline = next;
                for (int index = registrations.Length - 1; index >= 0; index--)
                {
                    StaticFilesMiddleware registration = registrations[index];
                    StaticFilesMiddleware? middleware = registration.MountsWebRoot
                        ? WebRootStaticFiles.TryMount(context, registration, "UseStaticFiles")
                        : registration;

                    if (middleware is not null)
                    {
                        WebApplicationMiddleware following = pipeline;
                        pipeline = exchange => middleware.InvokeAsync(exchange, following);
                    }
                }

                return pipeline;
            });
        }
    }

    internal static FileName[] Validate(StaticFilesOptions options)
    {
        // HttpPath also admits the "*" asterisk form; a mount prefix must be an origin-form path.
        if (options.RequestPath.Value.Length == 0 || options.RequestPath.Value[0] != '/')
        {
            throw new ArgumentException(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.StaticFilesRequestPathInvalid, options.RequestPath.Value));
        }

        FileName[] defaultDocuments = new FileName[options.DefaultDocuments.Count];
        for (int index = 0; index < defaultDocuments.Length; index++)
        {
            string document = options.DefaultDocuments[index];
            if (string.IsNullOrWhiteSpace(document)
                || document.AsSpan().ContainsAny('/', '\\')
                || document.AsSpan().IndexOfAnyExcept('.') < 0)
            {
                throw new ArgumentException(
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.DefaultDocumentNameInvalid, document));
            }

            try
            {
                defaultDocuments[index] = new FileName(document);
            }
            catch (ArgumentException exception)
            {
                throw new ArgumentException(
                    string.Format(CultureInfo.CurrentCulture, ErrorMessages.DefaultDocumentNameInvalid, document),
                    nameof(options), exception);
            }
        }

        if (!string.IsNullOrEmpty(options.CacheControl)
            && !HttpCacheControl.TryParse(options.CacheControl, out _))
        {
            throw new ArgumentException(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.StaticFilesCacheControlInvalid, options.CacheControl));
        }

        foreach (string contentType in options.ContentTypeMappings.Values)
        {
            ValidateContentType(contentType);
        }

        ValidateContentType(options.FallbackContentType);
        return defaultDocuments;
    }

    private static void ValidateContentType(string contentType)
    {
        if (string.IsNullOrEmpty(contentType) || !HttpFieldValue.TryCreate(contentType, out _))
        {
            throw new ArgumentException(
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.ContentTypeFieldInvalid, contentType));
        }
    }
}
