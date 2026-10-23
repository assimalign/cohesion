using System.Collections.Generic;

namespace Assimalign.Cohesion.Content.Pdf.Internal;

// Retain exact container revisions, not payload hashes or event subscriptions. References are leaves.
[global::System.Diagnostics.DebuggerDisplay("PdfObjectState")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
internal sealed class PdfObjectState
{
    private readonly List<(IPdfObject Value, long Revision)> _containers = new();
    private readonly long _position;
    internal PdfObjectState(IPdfObject value, int maxDepth, int maxObjects, long position)
    {
        _position = position;
        var visited = new HashSet<IPdfObject>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        Capture(value, 0, maxDepth, maxObjects, visited);
    }
    internal bool IsChanged
    {
        get
        {
            foreach (var (value, revision) in _containers)
            {
                if (value is PdfArray array && array.Revision != revision ||
                    value is PdfDictionary dictionary && dictionary.Revision != revision) { return true; }
            }
            return false;
        }
    }
    private void Capture(IPdfObject value, int depth, int maxDepth, int maxObjects, HashSet<IPdfObject> visited)
    {
        // The parser counts a stream's dictionary as its root, not as an extra nested node.
        if (value is PdfStream stream) { value = stream.Dictionary; }
        if (visited.Contains(value)) { return; }
        if (depth > maxDepth || visited.Count >= maxObjects)
        {
            throw new ContentFormatException(ErrorMessages.ReaderLimit, ContentFormatErrorCode.LimitExceeded, _position);
        }
        visited.Add(value);
        if (value is PdfArray array)
        {
            _containers.Add((array, array.Revision));
            foreach (var item in array) { Capture(item, depth + 1, maxDepth, maxObjects, visited); }
        }
        if (value is PdfDictionary dictionary)
        {
            _containers.Add((dictionary, dictionary.Revision));
            foreach (var pair in dictionary) { Capture(pair.Value, depth + 1, maxDepth, maxObjects, visited); }
        }
    }

    private sealed class DebugView
    {
        public DebugView(PdfObjectState value)
        {
        }
    }
}
