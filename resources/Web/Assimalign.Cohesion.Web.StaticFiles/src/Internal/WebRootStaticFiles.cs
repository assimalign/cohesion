using System.IO;

using Assimalign.Cohesion.FileSystem;

namespace Assimalign.Cohesion.Web.StaticFiles.Internal;

/// <summary>
/// Builds the static-files middleware over an application's web root, shared by
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
    /// <returns>The middleware, or <see langword="null"/>.</returns>
    public static StaticFilesMiddleware? TryCreate(IWebApplicationContext context, StaticFilesOptions options)
    {
        if (context.WebRootPath is not { IsEmpty: false } webRoot || !Directory.Exists(webRoot.ToString()))
        {
            return null;
        }

        PhysicalFileSystem fileSystem = new(new PhysicalFileSystemOptions
        {
            Root = webRoot,
            IsReadOnly = true,
            Name = "StaticFiles",
        });

        return new StaticFilesMiddleware(fileSystem, options);
    }
}
