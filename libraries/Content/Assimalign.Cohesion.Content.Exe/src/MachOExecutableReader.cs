using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Exe.Internal;

namespace Assimalign.Cohesion.Content.Exe;

/// <summary>Inspects thin and universal Mach-O headers.</summary>
/// <remarks>Sources must be readable and seekable. Parsing starts at the current position and leaves the source open.</remarks>
[global::System.Diagnostics.DebuggerDisplay("MachOExecutableReader")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class MachOExecutableReader : IContentReader<IExecutableFile>
{
    private readonly ExecutableReaderOptions _options;

    /// <summary>Creates a reader with default metadata limits.</summary>
    public MachOExecutableReader() : this(new ExecutableReaderOptions()) { }

    private MachOExecutableReader(ExecutableReaderOptions options) => _options = options;

    /// <summary>Creates a reader with explicit metadata limits.</summary>
    /// <param name="options">Immutable reader limits.</param>
    /// <returns>The configured reader.</returns>
    /// <exception cref="ArgumentNullException">Options are null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A limit is not positive.</exception>
    public static MachOExecutableReader Create(ExecutableReaderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        return new MachOExecutableReader(options);
    }

    /// <summary>Gets the shared descriptor for Mach-O.</summary>
    public static ContentFormat Format { get; } = new()
    {
        Name = "Mach-O", Kind = ContentKind.Executable,
        MediaTypes = Array.AsReadOnly(new[] { "application/x-mach-binary" }),
        FileExtensions = Array.AsReadOnly(new[] { ".dylib" }),
        Specification = "https://developer.apple.com/library/archive/documentation/DeveloperTools/Conceptual/MachORuntime/Reference/reference.html"
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

    private sealed class DebugView
    {
        public DebugView(MachOExecutableReader value)
        {
        }
    }
}
