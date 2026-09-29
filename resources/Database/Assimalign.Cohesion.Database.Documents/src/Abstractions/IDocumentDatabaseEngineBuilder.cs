using System;
using System.IO;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Documents;

/// <summary>Captures dependency-free options and deferred components for one document engine.</summary>
/// <remarks>Build is one-shot. Workers and servers are owned by the resulting engine.</remarks>
public interface IDocumentDatabaseEngineBuilder : IDatabaseEngineBuilder
{
    /// <inheritdoc cref="DocumentDatabaseEngineOptions.EngineName" />
    string? EngineName { get; set; }

    /// <inheritdoc cref="DocumentDatabaseEngineOptions.RootPath" />
    FileSystemPath? RootPath { get; set; }

    /// <inheritdoc cref="DocumentDatabaseEngineOptions.Durability" />
    StorageCommitDurability? Durability { get; set; }

    /// <inheritdoc cref="DocumentDatabaseEngineOptions.StorageStrategy" />
    IDocumentStorageStrategy? StorageStrategy { get; set; }

    /// <inheritdoc cref="DocumentDatabaseEngineOptions.GroupCommitWindow" />
    TimeSpan GroupCommitWindow { get; set; }

    /// <inheritdoc cref="DocumentDatabaseEngineOptions.CheckpointInterval" />
    TimeSpan CheckpointInterval { get; set; }

    /// <inheritdoc cref="DocumentDatabaseEngineOptions.PageWriteBackInterval" />
    TimeSpan PageWriteBackInterval { get; set; }

    /// <inheritdoc cref="DocumentDatabaseEngineOptions.PageWriteBackBatchSize" />
    int PageWriteBackBatchSize { get; set; }

    /// <inheritdoc cref="DocumentDatabaseEngineOptions.MaintenanceInterval" />
    TimeSpan MaintenanceInterval { get; set; }
}
