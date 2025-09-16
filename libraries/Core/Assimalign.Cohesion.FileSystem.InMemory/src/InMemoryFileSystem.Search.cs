using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.FileSystem;

public sealed partial class InMemoryFileSystem
{
    [Flags]
    private enum FindNodeFlags
    {
        CreatePathIfNotExist = 1 << 1,
        NodeCheck = 1 << 2,
        NodeShared = 1 << 3,
        NodeExclusive = 1 << 4,
        KeepParentNodeExclusive = 1 << 5,
        KeepParentNodeShared = 1 << 6,
    }
}
