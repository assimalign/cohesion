using System.IO;

using Assimalign.Cohesion.FileSystem;

namespace Assimalign.Cohesion.Web.StaticFiles.Internal;

/// <summary>
/// Mounts the static-files middleware over an application's web root, shared by
/// <c>UseStaticFiles</c> and <c>MapFallbackToFile</c>.
/// </summary>
internal static class WebRootStaticFiles
{
    /// <summary>
    /// Creates a read-only middleware over <see cref="IWebApplicationContext.WebRootPath"/>, or returns
    /// <see langword="null"/> when the application has no web root or the directory does not exist.
    /// Only the web root is ever mounted — never the content root or the working directory.
    /// </summary>
    /// <param name="context">The application context.</param>
    /// <param name="options">The validated options.</param>
    /// <param name="consumer">The composition verb requesting the mount.</param>
    /// <returns>The middleware, or <see langword="null"/>.</returns>
    public static StaticFilesMiddleware? TryCreate(IWebApplicationContext context, StaticFilesOptions options, string consumer)
    {
        // MapFallbackToFile defers its snapshot until pipeline build. A captured options instance may
        // have changed since registration; validate before opening the mount.
        FileName[] defaultDocuments = WebApplicationStaticFilesExtensions.Validate(options);

        return TryMount(context, new StaticFilesMiddleware(null, options, defaultDocuments), consumer);
    }

    /// <summary>
    /// Mounts <see cref="IWebApplicationContext.WebRootPath"/> read-only under a registration built
    /// without a file system, or returns <see langword="null"/> when the application has no web root or
    /// the directory does not exist. Only the web root is ever mounted — never the content root or the
    /// working directory.
    /// </summary>
    /// <param name="context">The application context.</param>
    /// <param name="registration">The validated snapshot to serve the web root with.</param>
    /// <param name="consumer">The composition verb requesting the mount.</param>
    /// <returns>The mounted middleware, or <see langword="null"/>.</returns>
    public static StaticFilesMiddleware? TryMount(IWebApplicationContext context, StaticFilesMiddleware registration, string consumer)
    {
        if (context.WebRootPath is not { IsEmpty: false } webRoot || !Directory.Exists(webRoot.ToString()))
        {
            StaticFilesEventSource.Log.WebRootUnavailable(context.WebRootPath, consumer);
            return null;
        }

        PhysicalFileSystem fileSystem = new(new PhysicalFileSystemOptions
        {
            Root = webRoot,
            IsReadOnly = true,
            Name = "StaticFiles",
        });

        StaticFilesMiddleware middleware = registration.WithFileSystem(fileSystem);
        StaticFilesEventSource.Log.WebRootMounted(webRoot, registration.RequestPath);
        return middleware;
    }
}
