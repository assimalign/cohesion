using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Exe.Internal;

namespace Assimalign.Cohesion.Content.Exe;

/// <summary>Detects and inspects a retained executable format without launching it.</summary>
/// <remarks>Sources must be readable and seekable. Parsing starts at the current position and leaves the source open.</remarks>
public sealed class ExecutableReader : IContentReader<IExecutableFile>
{
    private readonly ExecutableReaderOptions _options;

    /// <summary>Creates a reader with default metadata limits.</summary>
    public ExecutableReader() : this(new ExecutableReaderOptions()) { }

    private ExecutableReader(ExecutableReaderOptions options) => _options = options;

    /// <summary>Creates a reader with explicit metadata limits.</summary>
    /// <param name="options">Immutable reader limits.</param>
    /// <returns>The configured reader.</returns>
    /// <exception cref="ArgumentNullException">Options are null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A limit is not positive.</exception>
    public static ExecutableReader Create(ExecutableReaderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return new ExecutableReader(options);
    }

    /// <summary>Probes a prefix without parsing payloads; a 12-byte prefix distinguishes Java from universal Mach-O.</summary>
    /// <param name="prefix">The first bytes at the executable's origin.</param>
    /// <returns>A retained descriptor, or ContentFormat.Unknown when unrecognized or insufficient.</returns>
    public static ContentFormat Detect(ReadOnlySpan<byte> prefix)
    {
        if (prefix.Length >= 2 && prefix[0] == 0x4D && prefix[1] == 0x5A)
        {
            return PeExecutableReader.Format;
        }
        if (prefix.Length < 4)
        {
            return ContentFormat.Unknown;
        }
        var reader = new BinaryContentReader(prefix);
        var magic = reader.ReadUInt32BigEndian();
        if (magic == 0x7F454C46)
        {
            return ElfExecutableReader.Format;
        }
        if (magic is 0xFEEDFACE or 0xCEFAEDFE or 0xFEEDFACF or 0xCFFAEDFE or 0xBEBAFECA or 0xCAFEBABF or 0xBFBAFECA)
        {
            return MachOExecutableReader.Format;
        }
        if (magic == 0xCAFEBABE && prefix.Length >= 12)
        {
            var count = reader.ReadUInt32BigEndian();
            var cpu = reader.ReadUInt32BigEndian();
            // Java's minor/major version occupies the fat count, followed by its constant pool.
            var javaVersion = (count >> 16 is 0 or 0xFFFF) && (count & 0xFFFF) >= 45;
            var plausibleCpu = (cpu & 0x00FFFFFF) is > 0 and < 256 && (cpu >> 24) <= 2;
            if (!javaVersion || plausibleCpu)
            {
                return MachOExecutableReader.Format;
            }
        }
        return ContentFormat.Unknown;
    }

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException">The stream is null.</exception>
    /// <exception cref="ContentFormatException">The source is non-seekable, malformed, unsupported or exceeds a limit.</exception>
    public IExecutableFile Read(Stream stream) =>
        ExecutableInspection.ReadAsync(stream, _options, ContentFormat.Unknown, false).GetAwaiter().GetResult();

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException">The stream is null.</exception>
    /// <exception cref="ContentFormatException">The source is non-seekable, malformed, unsupported or exceeds a limit.</exception>
    /// <exception cref="OperationCanceledException">Inspection was canceled.</exception>
    public ValueTask<IExecutableFile> ReadAsync(Stream stream, CancellationToken cancellationToken = default) =>
        ExecutableInspection.ReadAsync(stream, _options, ContentFormat.Unknown, true, cancellationToken);
}
