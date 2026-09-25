using System;
using System.IO;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Blob;

/// <summary>Captures dependency-free options and deferred components for one blob engine.</summary>
/// <remarks>Build is one-shot. Workers and servers are owned by the resulting engine.</remarks>
public interface IBlobDatabaseEngineBuilder : IDatabaseEngineBuilder
{
    /// <inheritdoc cref="BlobDatabaseEngineOptions.EngineName" />
    string? EngineName { get; set; }

    /// <inheritdoc cref="BlobDatabaseEngineOptions.RootPath" />
    FileSystemPath? RootPath { get; set; }

    /// <inheritdoc cref="BlobDatabaseEngineOptions.Durability" />
    StorageCommitDurability? Durability { get; set; }

    /// <inheritdoc cref="BlobDatabaseEngineOptions.StorageStrategy" />
    IBlobStorageStrategy? StorageStrategy { get; set; }

    /// <inheritdoc cref="BlobDatabaseEngineOptions.GroupCommitWindow" />
    TimeSpan GroupCommitWindow { get; set; }

    /// <inheritdoc cref="BlobDatabaseEngineOptions.CheckpointInterval" />
    TimeSpan CheckpointInterval { get; set; }

    /// <inheritdoc cref="BlobDatabaseEngineOptions.PageWriteBackInterval" />
    TimeSpan PageWriteBackInterval { get; set; }

    /// <inheritdoc cref="BlobDatabaseEngineOptions.PageWriteBackBatchSize" />
    int PageWriteBackBatchSize { get; set; }

    /// <inheritdoc cref="BlobDatabaseEngineOptions.MaintenanceInterval" />
    TimeSpan MaintenanceInterval { get; set; }
}
