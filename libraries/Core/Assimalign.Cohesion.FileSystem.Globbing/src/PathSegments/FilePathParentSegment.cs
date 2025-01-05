using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.FileSystem.Globbing.PathSegments;

public class FilePathParentSegment : IFilePathSegment
{
    private const string LiteralParent = "..";

    public bool CanProduceStem { get { return false; } }

    public bool Match(string value)
    {
        return string.Equals(LiteralParent, value, StringComparison.Ordinal);
    }
}
