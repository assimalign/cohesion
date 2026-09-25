using System;
using System.IO;

using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.KeyValuePair.Internal;

internal sealed class KeyValueDatabaseEngineBuilder : IKeyValueDatabaseEngineBuilder
{
    private readonly DatabaseEngineBuilderState _state = new();
    private readonly KeyValueDatabaseEngineOptions _options = new();

    public string? EngineName
    {
        get => _options.EngineName;
        set { _state.EnsureMutable(); _options.EngineName = value; }
    }

    public FileSystemPath? RootPath
    {
        get => _options.RootPath;
        set { _state.EnsureMutable(); _options.RootPath = value; }
    }

    public StorageCommitDurability? Durability
    {
        get => _options.Durability;
        set { _state.EnsureMutable(); _options.Durability = value; }
    }

    public IKeyValueStorageStrategy? StorageStrategy
    {
        get => _options.StorageStrategy;
        set { _state.EnsureMutable(); _options.StorageStrategy = value; }
    }

    public TimeSpan GroupCommitWindow
    {
        get => _options.GroupCommitWindow;
        set { _state.EnsureMutable(); _options.GroupCommitWindow = value; }
    }

    public TimeSpan CheckpointInterval
    {
        get => _options.CheckpointInterval;
        set { _state.EnsureMutable(); _options.CheckpointInterval = value; }
    }

    public TimeSpan PageWriteBackInterval
    {
        get => _options.PageWriteBackInterval;
        set { _state.EnsureMutable(); _options.PageWriteBackInterval = value; }
    }

    public int PageWriteBackBatchSize
    {
        get => _options.PageWriteBackBatchSize;
        set { _state.EnsureMutable(); _options.PageWriteBackBatchSize = value; }
    }

    public TimeSpan MaintenanceInterval
    {
        get => _options.MaintenanceInterval;
        set { _state.EnsureMutable(); _options.MaintenanceInterval = value; }
    }

    public IDatabaseEngineBuilder AddWorker(Func<IDatabaseEngine, IDatabaseEngineWorker> configure)
    {
        _state.AddWorker(configure);
        return this;
    }

    public IDatabaseEngineBuilder AddServer(Func<IDatabaseEngine, IDatabaseServer> configure)
    {
        _state.AddServer(configure);
        return this;
    }

    internal void Abort(Exception failure) => _state.Abort(failure);

    public IDatabaseEngine Build()
    {
        _state.BeginBuild();
        var engine = KeyValueDatabaseEngine.Create(_options);
        return _state.Complete(engine, engine.AttachWorker, engine.AttachServer);
    }
}
