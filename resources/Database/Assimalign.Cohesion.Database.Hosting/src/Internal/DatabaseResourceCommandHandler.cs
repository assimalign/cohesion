using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.Database.Hosting;

internal sealed class DatabaseResourceCommandHandler : IResourceCommandHandler
{
    private readonly IDatabaseApplicationContext _context;
    private readonly Dictionary<string, IDatabaseEngine> _createdDatabases = new(StringComparer.Ordinal);

    internal DatabaseResourceCommandHandler(string kind, IDatabaseApplicationContext context)
    {
        Kind = kind;
        _context = context;
    }

    public string Kind { get; }

    public async ValueTask<ReadOnlyMemory<byte>> ExecuteAsync(ResourceCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using JsonDocument document = JsonDocument.Parse(command.Payload);
        JsonElement payload = document.RootElement;
        string database = Required(payload, "database");
        if (Kind == "database.add-principal")
        {
            string principal = Required(payload, "name");
            throw new ResourceCommandRejectedException(
                $"database.add-principal cannot create principal '{principal}' in database '{database}': the database runtime has no principal mutation seam for this command.");
        }
        if (database.Contains('/'))
        {
            throw new ResourceCommandRejectedException($"database.add-database database name '{database}' must not contain '/'; the final slash separates the engine from the database.");
        }
        string expectedKey = payload.TryGetProperty("engine", out JsonElement engineName) && engineName.ValueKind is not JsonValueKind.Null
            ? Required(payload, "engine") + "/" + database : database;
        if (command.Key != expectedKey)
        {
            throw new ResourceCommandRejectedException($"database.add-database key '{command.Key}' must match database key '{expectedKey}'.");
        }
        IDatabaseEngine engine = ResolveEngine(payload);
        if (_createdDatabases.TryGetValue(command.Key, out IDatabaseEngine? originalEngine))
        {
            if (!ReferenceEquals(engine, originalEngine))
            {
                throw new ResourceCommandRejectedException($"database.add-database cannot move owned database '{database}' from engine '{originalEngine.Name}' to '{engine.Name}'; delete its declaration before changing engines.");
            }
            if (engine.TryGetDatabase(database, out _))
            {
                return ReadOnlyMemory<byte>.Empty;
            }
        }
        if (engine.TryGetDatabase(database, out _))
        {
            throw new ResourceCommandRejectedException($"database.add-database cannot claim existing database '{database}' on engine '{engine.Name}'; it was not created by this declaration.");
        }
        try
        {
            await engine.CreateDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
            _createdDatabases[command.Key] = engine;
        }
        catch (DatabaseException exception)
        {
            throw new ResourceCommandRejectedException($"database.add-database '{database}' on engine '{engine.Name}' was refused: {exception.Message}");
        }
        return ReadOnlyMemory<byte>.Empty;
    }

    public async ValueTask<ReadOnlyMemory<byte>> DeleteAsync(ResourceCommand command, CancellationToken cancellationToken = default)
    {
        using JsonDocument document = JsonDocument.Parse(command.Payload);
        string database = Required(document.RootElement, "database");
        if (Kind != "database.add-database")
        {
            throw new ResourceCommandRejectedException($"{Kind} cannot be deleted: the database runtime has no principal mutation seam.");
        }
        IDatabaseEngine engine = ResolveEngine(document.RootElement);
        try
        {
            if (engine.TryGetDatabase(database, out _))
            {
                await engine.DropDatabaseAsync(database, cancellationToken).ConfigureAwait(false);
            }
            _createdDatabases.Remove(command.Key);
        }
        catch (DatabaseException exception)
        {
            throw new ResourceCommandRejectedException($"database.add-database deletion of '{database}' on engine '{engine.Name}' was refused: {exception.Message}");
        }
        return ReadOnlyMemory<byte>.Empty;
    }

    private IDatabaseEngine ResolveEngine(JsonElement payload)
    {
        var engines = new HashSet<IDatabaseEngine>(ReferenceEqualityComparer.Instance);
        foreach (IDatabaseEngine engine in _context.Engines)
        {
            engines.Add(engine);
        }
        foreach (IDatabaseServer server in _context.Servers)
        {
            engines.Add(server.Context.Engine);
        }
        string? name = payload.TryGetProperty("engine", out JsonElement configured) && configured.ValueKind is not JsonValueKind.Null
            ? Required(payload, "engine") : null;
        IDatabaseEngine[] candidates = engines.Where(engine => name is null || engine.Name == name).ToArray();
        return candidates.Length == 1 ? candidates[0] : throw new ResourceCommandRejectedException(
            $"database.add-database requires exactly one matching engine; engine '{name ?? "(unspecified)"}' matched {candidates.Length}. Specify a registered engine name.");
    }

    private static string Required(JsonElement payload, string property)
    {
        if (payload.ValueKind is not JsonValueKind.Object || !payload.TryGetProperty(property, out JsonElement value) ||
            value.ValueKind is not JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new JsonException($"The database command payload requires a nonblank '{property}'.");
        }
        return value.GetString()!;
    }
}
