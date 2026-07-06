using System.Collections.Generic;
using System.Threading;

namespace Assimalign.Cohesion.Content;

/// <summary>
/// Represents content composed of child content items, such as a container format's tracks, a
/// multipart body's parts, or a document's embedded resources.
/// </summary>
/// <remarks>
/// A composite owns its children by default: disposing the composite disposes every child. Composites
/// created over borrowed children leave them undisposed (see <c>ContentFactory.Compose</c>). A pure
/// composite — one assembled from items rather than parsed from a serialized source — has no byte
/// representation of its own, and its <see cref="IContent.OpenRead"/> throws
/// <see cref="System.NotSupportedException"/>.
/// </remarks>
public interface IComposableContent : IContent
{
    /// <summary>
    /// Enumerates the child content items in order.
    /// </summary>
    /// <param name="cancellationToken">A token used to cancel the enumeration.</param>
    /// <returns>The child items. Items remain owned by the composite; do not dispose them individually.</returns>
    IAsyncEnumerable<IContent> GetItemsAsync(CancellationToken cancellationToken = default);
}
