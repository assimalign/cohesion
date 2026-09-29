using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content.Yaml.Internal;

/// <summary>
/// The content-family reader seam over <see cref="YamlText.Parse(Stream)"/>.
/// </summary>
internal sealed class YamlStreamReader : IContentReader<YamlStream>
{
    public YamlStream Read(Stream stream) => YamlText.Parse(stream);

    public ValueTask<YamlStream> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<YamlStream>(Read(stream));
    }
}

/// <summary>
/// The content-family writer seam over <see cref="YamlText.Write(Stream, YamlStream, YamlWriterOptions?)"/>.
/// </summary>
internal sealed class YamlStreamWriter : IContentWriter<YamlStream>
{
    private readonly YamlWriterOptions _options;

    /// <summary>Initializes a new instance of the <see cref="YamlStreamWriter"/> class.</summary>
    /// <param name="options">The writer options applied to every document written.</param>
    public YamlStreamWriter(YamlWriterOptions options)
    {
        _options = options;
    }

    public void Write(Stream stream, YamlStream document) => YamlText.Write(stream, document, _options);

    public ValueTask WriteAsync(Stream stream, YamlStream document, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(stream, document);
        return ValueTask.CompletedTask;
    }
}
