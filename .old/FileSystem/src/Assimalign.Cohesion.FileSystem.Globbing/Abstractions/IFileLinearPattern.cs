using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.FileSystem.Globbing;

/// <summary>
/// This API supports infrastructure and is not intended to be used
/// directly from your code. This API may change or be removed in future releases.
/// </summary>
public interface IFileLinearPattern : IFilePattern
{
    IList<IFilePathSegment> Segments { get; }
}
