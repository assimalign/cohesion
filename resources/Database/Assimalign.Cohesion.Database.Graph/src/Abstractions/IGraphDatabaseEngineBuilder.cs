using System;
using System.IO;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Graph;

/// <summary>Captures dependency-free options and deferred components for one graph engine.</summary>
/// <remarks>Build is one-shot. Workers and servers are owned by the resulting engine.</remarks>
public interface IGraphDatabaseEngineBuilder : IDatabaseEngineBuilder
{
    /// <inheritdoc cref="GraphDatabaseEngineOptions.EngineName" />
    string? EngineName { get; set; }

    /// <inheritdoc cref="GraphDatabaseEngineOptions.RootPath" />
    FileSystemPath? RootPath { get; set; }

    /// <inheritdoc cref="GraphDatabaseEngineOptions.Durability" />
    StorageCommitDurability? Durability { get; set; }

    /// <inheritdoc cref="GraphDatabaseEngineOptions.StorageStrategy" />
    IGraphStorageStrategy? StorageStrategy { get; set; }

    /// <inheritdoc cref="GraphDatabaseEngineOptions.GroupCommitWindow" />
    TimeSpan GroupCommitWindow { get; set; }

    /// <inheritdoc cref="GraphDatabaseEngineOptions.CheckpointInterval" />
    TimeSpan CheckpointInterval { get; set; }

    /// <inheritdoc cref="GraphDatabaseEngineOptions.PageWriteBackInterval" />
    TimeSpan PageWriteBackInterval { get; set; }

    /// <inheritdoc cref="GraphDatabaseEngineOptions.PageWriteBackBatchSize" />
    int PageWriteBackBatchSize { get; set; }

    /// <inheritdoc cref="GraphDatabaseEngineOptions.MaintenanceInterval" />
    TimeSpan MaintenanceInterval { get; set; }
}
