using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.FileSystem.Internal;

using Assimalign.Cohesion.Internal;

internal class FsNodeList : List<KeyValuePair<Path, FsNode>>, IDisposable
{
    private readonly InMemoryFileSystem fileSystem;

    public FsNodeList(InMemoryFileSystem fileSystem)
    {
        if (fileSystem is null)
        {
            ThrowHelper.ThrowArgumentNullException(nameof(fileSystem));
        }

        this.fileSystem = fileSystem;
    }
    public void Dispose()
    {
        for (var i = this.Count - 1; i >= 0; i--)
        {
            var entry = this[i];
            fileSystem.ExitExclusive(entry.Value);
        }
        Clear();
    }
}
