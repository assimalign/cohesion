using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content.Markdown;

/// <summary>
/// The content-family reader seam over <see cref="MarkdownText.Parse(Stream)"/>.
/// </summary>
internal sealed class MarkdownDocumentReader : IContentReader<MarkdownDocument>
{
    public MarkdownDocument Read(Stream stream) => MarkdownText.Parse(stream);

    public ValueTask<MarkdownDocument> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<MarkdownDocument>(Read(stream));
    }
}

/// <summary>
/// The content-family writer seam over <see cref="MarkdownText.Write(Stream, MarkdownDocument)"/>.
/// </summary>
internal sealed class MarkdownDocumentWriter : IContentWriter<MarkdownDocument>
{
    public void Write(Stream stream, MarkdownDocument document) => MarkdownText.Write(stream, document);

    public ValueTask WriteAsync(Stream stream, MarkdownDocument document, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(stream, document);
        return ValueTask.CompletedTask;
    }
}
