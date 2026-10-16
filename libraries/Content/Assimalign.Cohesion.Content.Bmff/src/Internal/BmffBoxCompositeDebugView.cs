using System.Collections.Generic;

namespace Assimalign.Cohesion.Content.Bmff.Internal;

// Expose the existing list without copying or enumerating it during debugger evaluation.
internal sealed class BmffBoxCompositeDebugView
{
    private readonly BmffBoxComposite _box;
    public BmffBoxCompositeDebugView(BmffBoxComposite box) => _box = box;
    public IList<BmffBox> Children => _box.Children;
}
