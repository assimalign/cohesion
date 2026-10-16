using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Exe.Internal;

namespace Assimalign.Cohesion.Content.Exe;

/// <summary>Inspects ELF32 and ELF64 in either byte order.</summary>
/// <remarks>Sources must be readable and seekable. Parsing starts at the current position and leaves the source open.</remarks>
public sealed class ElfExecutableReader : IContentReader<IExecutableFile>
{
    private readonly ExecutableReaderOptions _options;

    /// <summary>Creates a reader with default metadata limits.</summary>
    public ElfExecutableReader() : this(new ExecutableReaderOptions()) { }

    private ElfExecutableReader(ExecutableReaderOptions options) => _options = options;

    /// <summary>Creates a reader with explicit metadata limits.</summary>
    /// <param name="options">Immutable reader limits.</param>
    /// <returns>The configured reader.</returns>
    /// <exception cref="ArgumentNullException">Options are null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A limit is not positive.</exception>
    public static ElfExecutableReader Create(ExecutableReaderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return new ElfExecutableReader(options);
    }

    /// <summary>Gets the shared descriptor for ELF.</summary>
    public static ContentFormat Format { get; } = new()
    {
        Name = "ELF", Kind = ContentKind.Executable,
        MediaTypes = Array.AsReadOnly(new[] { "application/x-elf" }),
        FileExtensions = Array.AsReadOnly(new[] { ".elf", ".so" }),
        Specification = "https://gabi.xinuos.com/elf/"
    };

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException">The stream is null.</exception>
    /// <exception cref="ContentFormatException">The source is non-seekable, malformed, unsupported or exceeds a limit.</exception>
    public IExecutableFile Read(Stream stream) =>
        ExecutableInspection.ReadAsync(stream, _options, Format, false).GetAwaiter().GetResult();

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException">The stream is null.</exception>
    /// <exception cref="ContentFormatException">The source is non-seekable, malformed, unsupported or exceeds a limit.</exception>
    /// <exception cref="OperationCanceledException">Inspection was canceled.</exception>
    public ValueTask<IExecutableFile> ReadAsync(Stream stream, CancellationToken cancellationToken = default) =>
        ExecutableInspection.ReadAsync(stream, _options, Format, true, cancellationToken);
}
