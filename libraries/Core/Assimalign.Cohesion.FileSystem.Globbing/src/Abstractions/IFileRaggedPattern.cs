using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.FileSystem.Globbing;

/// <summary>
/// This API supports infrastructure and is not intended to be used
/// directly from your code. This API may change or be removed in future releases.
/// </summary>
public interface IFileRaggedPattern : IFilePattern
{
    IList<IFilePathSegment> Segments { get; }

    IList<IFilePathSegment> StartsWith { get; }

    IList<IList<IFilePathSegment>> Contains { get; }

    IList<IFilePathSegment> EndsWith { get; }
}
