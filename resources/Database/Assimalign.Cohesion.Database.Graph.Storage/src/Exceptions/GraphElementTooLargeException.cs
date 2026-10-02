using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Storage.Units;

namespace Assimalign.Cohesion.Database.Graph.Storage;

/// <summary>
/// A graph element cannot be stored because its encoding exceeds a storage limit: a node's labels
/// and properties, a relationship's type and properties, or an index definition's names encode past
/// one slotted-page record (<see cref="SlottedPage.MaxRecordSize"/> bytes), or an indexed property
/// value encodes past the index key prefix (1,016 bytes). The store throws it before it writes
/// anything for the element.
/// </summary>
/// <remarks>
/// The failure belongs to the element, not to the store: no record or index entry is written for
/// the element, and the store stays usable. The Graph engine reports it as <c>COHDBG008</c>.
/// </remarks>
public sealed class GraphElementTooLargeException : StorageException
{
    /// <summary>Initializes a new instance of the <see cref="GraphElementTooLargeException"/> class.</summary>
    /// <param name="message">Which element and limit, and the encoded size.</param>
    public GraphElementTooLargeException(string message)
        : base(message)
    {
    }
}
