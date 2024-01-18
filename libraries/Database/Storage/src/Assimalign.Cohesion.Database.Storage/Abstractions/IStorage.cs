using System;
using System.IO;

namespace Assimalign.PanopticDb.Storage;

/// <summary>
/// 
/// </summary>
public interface IStorage : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// 
    /// </summary>
    StorageId Id { get; }
    /// <summary>
    /// 
    /// </summary>
    Name Name { get; }
    /// <summary>
    /// Represents the storage model implemented within the storage resource.
    /// </summary>
    StorageModel Model { get; }
    /// <summary>
    /// 
    /// </summary>
    StorageStream Stream { get; }
    /// <summary>
    /// Gets an iterator which 
    /// </summary>
    /// <remarks>
    /// Best for doing raw scans through-out the entire storage resource.
    /// </remarks>
    /// <returns></returns>
    IStorageUnitIterator GetUnitIterator();
    /// <summary>
    /// Gets the root segment iterator for the given storage.
    /// </summary>
    /// <returns></returns>
    IStorageSegmentIterator GetSegmentIterator();
}