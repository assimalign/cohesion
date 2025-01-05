using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.FileSystem.Globbing.PathSegments;

public class RecursiveWildcardSegment : IFilePathSegment
{
    public bool CanProduceStem { get { return true; } }

    public bool Match(string value)
    {
        return false;
    }
}
