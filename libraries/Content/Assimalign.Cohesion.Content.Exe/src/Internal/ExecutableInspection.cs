using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content.Exe.Internal;

internal static class ExecutableInspection
{
    internal static async ValueTask<IExecutableFile> ReadAsync(
        Stream stream, ExecutableReaderOptions options, ContentFormat format, bool asynchronous,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var context = new InspectionContext(stream, options, asynchronous);
        if (format == ContentFormat.Unknown)
        {
            var prefix = await context.ReadAtAsync(0, (int)Math.Min(12UL, context.Length), cancellationToken).ConfigureAwait(false);
            format = ExecutableReader.Detect(prefix.Span);
        }
        if (format == PeExecutableReader.Format)
        {
            return await PeInspection.ReadAsync(context, cancellationToken).ConfigureAwait(false);
        }
        if (format == ElfExecutableReader.Format)
        {
            return await ElfInspection.ReadAsync(context, cancellationToken).ConfigureAwait(false);
        }
        if (format == MachOExecutableReader.Format)
        {
            return await MachOInspection.ReadAsync(context, cancellationToken).ConfigureAwait(false);
        }
        throw new ContentFormatException(ErrorMessages.UnknownFormat, ContentFormatErrorCode.Unsupported, context.Origin);
    }
}
