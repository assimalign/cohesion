using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content.Yaml.Internal;

/// <summary>
/// The content-family reader seam over <see cref="YamlText.Parse(Stream)"/>.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("YamlStreamReader")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class YamlStreamReader : IContentReader<YamlStream>
{
    private readonly YamlParserOptions _options;

    internal YamlStreamReader(YamlParserOptions options)
    {
        options.Validate();
        _options = options;
    }

    public YamlStream Read(Stream stream) => YamlText.Parse(stream, _options);

    public ValueTask<YamlStream> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<YamlStream>(Read(stream));
    }

    private sealed class DebugView
    {
        public DebugView(YamlStreamReader value)
        {
        }
    }
}

/// <summary>
/// The content-family writer seam over <see cref="YamlText.Write(Stream, YamlStream, YamlWriterOptions?)"/>.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("YamlStreamWriter")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class YamlStreamWriter : IContentWriter<YamlStream>
{
    private readonly YamlWriterOptions _options;

    /// <summary>Initializes a new instance of the <see cref="YamlStreamWriter"/> class.</summary>
    /// <param name="options">The writer options applied to every document written.</param>
    public YamlStreamWriter(YamlWriterOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.IndentSize, nameof(options.IndentSize));
        _options = options;
    }

    public void Write(Stream stream, YamlStream document) => YamlText.Write(stream, document, _options);

    public ValueTask WriteAsync(Stream stream, YamlStream document, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(stream, document);
        return ValueTask.CompletedTask;
    }

    private sealed class DebugView
    {
        public DebugView(YamlStreamWriter value)
        {
        }
    }
}
