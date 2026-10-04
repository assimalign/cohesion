using System;
using System.IO;

using Assimalign.Cohesion.Database.Sql.Language;
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

    /// <summary>Gets or sets the checkpoint time backstop (<see cref="SqlDatabaseEngineOptions.CheckpointInterval"/>).</summary>
    TimeSpan CheckpointInterval { get; set; }

    /// <summary>
    /// Gets or sets the journal size, in bytes, that triggers a checkpoint
    /// (<see cref="SqlDatabaseEngineOptions.CheckpointJournalSize"/>; 256 MiB by default, zero for time only).
    /// </summary>
    long CheckpointJournalSize { get; set; }

    /// <summary>
    /// Gets or sets each database's buffer pool capacity, in bytes
    /// (<see cref="SqlDatabaseEngineOptions.BufferPoolCapacity"/>; 32 MiB by default). Build validates it.
    /// </summary>
    long BufferPoolCapacity { get; set; }

    /// <summary>Gets or sets the page write-back cadence.</summary>
    TimeSpan PageWriteBackInterval { get; set; }

    /// <summary>Gets or sets the maximum pages written per pass.</summary>
    int PageWriteBackBatchSize { get; set; }

    /// <summary>Gets or sets the maintenance cadence.</summary>
    TimeSpan MaintenanceInterval { get; set; }

    /// <summary>
    /// Gets or sets how many levels a SQL expression may nest in a statement the built engine
    /// executes (#1151): the deepest expression tree, in which a chain of <c>AND</c> (or
    /// <c>OR</c>) terms is one level however many terms it has, and the deepest grouping
    /// parentheses. Until it is set, it reports the engine's default,
    /// <see cref="SqlQueryParserOptions.DefaultExpressionNestingLimit"/> (256). The value must
    /// lie within <see cref="SqlQueryParserOptions.MinimumExpressionNestingLimit"/> (32) and
    /// <see cref="SqlQueryParserOptions.MaximumExpressionNestingLimit"/> (4096). Setting the
    /// property does not check it; <see cref="IDatabaseEngineBuilder.Build"/> throws
    /// <see cref="ArgumentOutOfRangeException"/> for a value outside that range, before it
    /// creates the engine.
    /// </summary>
    /// <remarks>
    /// It is the builder's form of <see cref="SqlDatabaseEngineOptions.ExpressionNestingLimit"/>,
    /// which describes how the engine applies the limit. Every implementation carries the value
    /// it is given to the engine it builds; one that builds through
    /// <see cref="SqlDatabaseEngine.Create"/> passes it as
    /// <see cref="SqlDatabaseEngineOptions.ExpressionNestingLimit"/>, and that method's range
    /// check is the one <see cref="IDatabaseEngineBuilder.Build"/> reports.
    /// </remarks>
    int ExpressionNestingLimit { get; set; }
}
