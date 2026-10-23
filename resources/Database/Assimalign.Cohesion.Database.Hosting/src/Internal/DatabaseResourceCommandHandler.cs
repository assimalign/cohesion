using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.Database.Hosting.Internal;

[global::System.Diagnostics.DebuggerDisplay("DatabaseResourceCommandHandler {Kind,nq}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Hosting.Internal.DatabaseResourceCommandHandler.DebugView))]
internal sealed class DatabaseResourceCommandHandler : IResourceCommandHandler
{
    private readonly IDatabaseApplicationContext _context;
    private readonly Dictionary<string, DatabaseEngine> _createdDatabases = new(StringComparer.OrdinalIgnoreCase);

    internal DatabaseResourceCommandHandler(string kind, IDatabaseApplicationContext context)
    {
        Kind = kind;
        _context = context;
    }

    public string Kind { get; }

    public ValueTask<ReadOnlyMemory<byte>> ExecuteAsync(ResourceCommand command, CancellationToken cancellationToken = default)
        => DatabaseHostingEventSource.Log.IsEnabled(EventLevel.Warning, EventKeywords.None)
            ? ExecuteTracedAsync(command, cancellationToken)
            : ExecuteCoreAsync(command, cancellationToken);

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<ReadOnlyMemory<byte>> ExecuteTracedAsync(ResourceCommand command, CancellationToken cancellationToken)
    {
        try
        {
            var result = await ExecuteCoreAsync(command, cancellationToken).ConfigureAwait(false);
            DatabaseHostingEventSource.Log.ResourceCommandHandled(command, "Execute");
            return result;
        }
        catch (Exception exception) when (exception is ResourceCommandRejectedException or JsonException)
        {
            DatabaseHostingEventSource.Log.ResourceCommandRejected(command, "Execute", exception);
            throw;
        }
    }

    // Internal so the owning tests can measure the untraced dispatch baseline without copying it.
    internal async ValueTask<ReadOnlyMemory<byte>> ExecuteCoreAsync(ResourceCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using JsonDocument document = JsonDocument.Parse(command.Payload);
        JsonElement payload = document.RootElement;
        string database = Required(payload, "database");
        if (Kind == "database.add-principal")
        {
            string principal = Required(payload, "name");
            throw new ResourceCommandRejectedException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.CommandHandlerExecuteCoreAsyncFailure, principal, database));
        }
        if (database.Contains('/'))
        {
            throw new ResourceCommandRejectedException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.CommandHandlerExecuteCoreAsyncFailure2, database));
        }
        string expectedKey = payload.TryGetProperty("engine", out JsonElement engineName) && engineName.ValueKind is not JsonValueKind.Null
            ? Required(payload, "engine") + "/" + database : database;
        if (!string.Equals(command.Key, expectedKey, StringComparison.OrdinalIgnoreCase))
        {
            throw new ResourceCommandRejectedException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.CommandHandlerExecuteCoreAsyncFailure3, command.Key, expectedKey));
        }
        DatabaseEngine engine = ResolveEngine(payload);
        if (_createdDatabases.TryGetValue(command.Key, out DatabaseEngine? originalEngine))
        {
            if (!ReferenceEquals(engine, originalEngine))
            {
                throw new ResourceCommandRejectedException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.CommandHandlerExecuteCoreAsyncFailure4, database, originalEngine.Name, engine.Name));
            }
            if (engine.TryGetDatabase(database, out _))
            {
                return ReadOnlyMemory<byte>.Empty;
            }
        }
        if (engine.TryGetDatabase(database, out _))
        {
            throw new ResourceCommandRejectedException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.CommandHandlerExecuteCoreAsyncFailure5, database, engine.Name));
        }
        try
        {
            await engine.CreateDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
            _createdDatabases[command.Key] = engine;
        }
        catch (Exception exception) when (exception is DatabaseException or ArgumentException)
        {
            // An ArgumentException is the engine's refusal of a name that breaks the area's name
            // rule (#1392); its message states the rule, so it reaches the gateway as a rejection
            // with that detail, not as a bare BadRequest.
            throw new ResourceCommandRejectedException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.CommandHandlerExecuteCoreAsyncFailure6, database, engine.Name, exception.Message));
        }
        return ReadOnlyMemory<byte>.Empty;
    }

    public ValueTask<ReadOnlyMemory<byte>> DeleteAsync(ResourceCommand command, CancellationToken cancellationToken = default)
        => DatabaseHostingEventSource.Log.IsEnabled(EventLevel.Warning, EventKeywords.None)
            ? DeleteTracedAsync(command, cancellationToken)
            : DeleteCoreAsync(command, cancellationToken);

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<ReadOnlyMemory<byte>> DeleteTracedAsync(ResourceCommand command, CancellationToken cancellationToken)
    {
        try
        {
            var result = await DeleteCoreAsync(command, cancellationToken).ConfigureAwait(false);
            DatabaseHostingEventSource.Log.ResourceCommandHandled(command, "Delete");
            return result;
        }
        catch (Exception exception) when (exception is ResourceCommandRejectedException or JsonException)
        {
            DatabaseHostingEventSource.Log.ResourceCommandRejected(command, "Delete", exception);
            throw;
        }
    }

    // Internal for the same allocation baseline as ExecuteCoreAsync.
    internal async ValueTask<ReadOnlyMemory<byte>> DeleteCoreAsync(ResourceCommand command, CancellationToken cancellationToken)
    {
        using JsonDocument document = JsonDocument.Parse(command.Payload);
        string database = Required(document.RootElement, "database");
        if (Kind != "database.add-database")
        {
            throw new ResourceCommandRejectedException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.CommandHandlerDeleteCoreAsyncFailure, Kind));
        }
        DatabaseEngine engine = ResolveEngine(document.RootElement);
        try
        {
            if (engine.TryGetDatabase(database, out _))
            {
                await engine.DropDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
            }
            _createdDatabases.Remove(command.Key);
        }
        catch (Exception exception) when (exception is DatabaseException or ArgumentException)
        {
            throw new ResourceCommandRejectedException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.CommandHandlerDeleteCoreAsyncFailure2, database, engine.Name, exception.Message));
        }
        return ReadOnlyMemory<byte>.Empty;
    }

    private DatabaseEngine ResolveEngine(JsonElement payload)
    {
        var engines = new HashSet<DatabaseEngine>(ReferenceEqualityComparer.Instance);
        foreach (DatabaseEngine engine in _context.Engines)
        {
            engines.Add(engine);
        }
        foreach (DatabaseServer server in _context.Servers)
        {
            engines.Add(server.Engine);
        }
        string? name = payload.TryGetProperty("engine", out JsonElement configured) && configured.ValueKind is not JsonValueKind.Null
            ? Required(payload, "engine") : null;
        DatabaseEngine[] candidates = engines.Where(engine => name is null || string.Equals(engine.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        return candidates.Length == 1 ? candidates[0] : throw new ResourceCommandRejectedException(
            string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.CommandHandlerResolveEngineFailure, name ?? "(unspecified)", candidates.Length));
    }

    private static string Required(JsonElement payload, string property)
    {
        if (payload.ValueKind is not JsonValueKind.Object || !payload.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind is not JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new JsonException(string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.ResourceCommandHandlerRequiredJson, property));
        }
        return value.GetString()!;
    }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Hosting.Internal.DatabaseResourceCommandHandler _target;

        public DebugView(global::Assimalign.Cohesion.Database.Hosting.Internal.DatabaseResourceCommandHandler value)
        {
            _target = value;
        }

        public string Kind => _target.Kind;
    }
}
