using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database.Storage;

/// <summary>
/// A depth-first iterator that enumerates <see cref="IStorageUnit"/> records
/// across pages within a segment or storage file.
/// </summary>
public interface IStorageUnitIterator : IEnumerator<IStorageUnit>
{
    /// <summary>
    /// Attempts to advance to the next storage unit.
    /// </summary>
    /// <param name="unit">When this method returns <c>true</c>, contains the next unit; otherwise, <c>null</c>.</param>
    /// <returns><c>true</c> if the iterator advanced to a valid unit; otherwise, <c>false</c>.</returns>
    bool Next(out IStorageUnit? unit);
}
