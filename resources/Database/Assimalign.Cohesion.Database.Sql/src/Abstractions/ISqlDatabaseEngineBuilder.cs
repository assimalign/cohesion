using System;
using System.IO;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>Captures SQL engine options and deferred worker and server factories.</summary>
public interface ISqlDatabaseEngineBuilder : IDatabaseEngineBuilder
{
    /// <summary>Gets or sets the logical engine name.</summary>
    string? EngineName { get; set; }

    /// <summary>Gets or sets the optional directory for persistent storage.</summary>
    FileSystemPath? RootPath { get; set; }

    /// <summary>Gets or sets the commit durability policy.</summary>
    StorageCommitDurability? Durability { get; set; }

    /// <summary>Gets or sets the optional storage strategy.</summary>
    ISqlStorageStrategy? StorageStrategy { get; set; }

    /// <summary>Gets or sets the bounded grouped-commit flush window.</summary>
    TimeSpan GroupCommitWindow { get; set; }

    /// <summary>Gets or sets the checkpoint cadence.</summary>
    TimeSpan CheckpointInterval { get; set; }

    /// <summary>Gets or sets the page write-back cadence.</summary>
    TimeSpan PageWriteBackInterval { get; set; }

    /// <summary>Gets or sets the maximum pages written per pass.</summary>
    int PageWriteBackBatchSize { get; set; }

    /// <summary>Gets or sets the maintenance cadence.</summary>
    TimeSpan MaintenanceInterval { get; set; }
}
