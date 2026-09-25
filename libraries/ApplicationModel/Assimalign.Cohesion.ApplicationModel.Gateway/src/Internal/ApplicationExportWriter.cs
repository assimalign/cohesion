using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

/// <summary>
/// Writes application exports atomically so a concurrent reader never observes a partial
/// document.
/// </summary>
internal static class ApplicationExportWriter
{
    public static async Task WriteAsync(
        ApplicationExportDocument document,
        string? exportDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);

        string root = exportDirectory
            ?? Path.Combine(Environment.CurrentDirectory, ".cohesion");
        string applicationDirectory = Path.Combine(root, document.Application);
        Directory.CreateDirectory(applicationDirectory);

        string path = Path.Combine(applicationDirectory, "export.json");
        string temporaryPath = Path.Combine(
            applicationDirectory,
            $"export.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await document.SaveAsync(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public static Task DeleteAsync(
        ApplicationName application,
        string? exportDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string root = exportDirectory
            ?? Path.Combine(Environment.CurrentDirectory, ".cohesion");
        string path = Path.Combine(root, application.ToString(), "export.json");
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }
}
