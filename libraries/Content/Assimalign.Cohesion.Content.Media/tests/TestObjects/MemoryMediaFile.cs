using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Content.Media.Tests;

// An in-memory contract fixture built from the root Content implementations; no downloaded media.
internal sealed class MemoryMediaFile : IMediaFile, IComposableContent
{
    private readonly MemoryContent _content = MemoryContent.Create(new byte[] { 1, 2, 3 });
    private readonly CompositeContent _children;

    internal MemoryMediaFile(MediaTrackKind kind, bool isLive = false, MediaTimestamp? duration = null)
    {
        IsLive = isLive;
        Duration = duration;
        Tracks = new IMediaTrack[] { new TestMediaTrack(kind, new StreamingMediaSampleIndex()) };
        _children = CompositeContent.Create(new IContent[] { _content });
    }

    public IReadOnlyList<IMediaTrack> Tracks { get; }
    public MediaTimestamp? Duration { get; }
    public bool IsLive { get; }
    public string? Name => _content.Name;
    public ContentFormat Format => _content.Format;
    public string? MediaType => _content.MediaType;
    public long? Length => _content.Length;
    public bool IsReadOnly => _content.IsReadOnly;
    public bool CanReopen => _content.CanReopen;
    public Stream OpenRead() => _content.OpenRead();
    public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default) => _content.OpenReadAsync(cancellationToken);
    public IAsyncEnumerable<IContent> GetItemsAsync(CancellationToken cancellationToken = default) => _children.GetItemsAsync(cancellationToken);
    public void Dispose() => _children.Dispose();
    public ValueTask DisposeAsync() => _children.DisposeAsync();
}
