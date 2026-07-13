using System;
using System.Net;

using Assimalign.Cohesion.Connections.Tcp;
using Assimalign.Cohesion.Database.Hosting;
using Assimalign.Cohesion.Database.Sql;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Application.Internal;

/// <summary>
/// Builds the standalone database host from the bound environment configuration:
/// a file-backed SQL engine at the data path, a TCP listener on the configured
/// port, the wire-protocol server, and the <see cref="DatabaseApplication"/>
/// composing them. <c>Program</c> is a thin shim over this type so tests can
/// drive the whole composition without spawning a process.
/// </summary>
internal static class DatabaseApplicationBootstrap
{
    /// <summary>
    /// The name of the database the host provisions (create-if-missing, open on
    /// restart) before the endpoint starts. The wire protocol has no CREATE DATABASE
    /// verb, so the server process brings its default database with it; clients bind
    /// it by this name.
    /// </summary>
    internal const string DefaultDatabaseName = "app";

    /// <summary>
    /// Composes a runnable database application from the configuration, through the
    /// area's builder pattern: <see cref="DatabaseApplication.CreateBuilder()"/> is
    /// the entry point, the SQL model registers its own engine via its
    /// <c>AddSqlDatabase</c> verb (shipped by <c>Database.Sql</c> against the root's
    /// <see cref="IDatabaseApplicationBuilder"/> seam), and the endpoint is a
    /// deferred server factory that receives the registered engines at build time.
    /// </summary>
    /// <param name="configuration">The bound host configuration.</param>
    /// <returns>The composed parts; the caller owns their disposal.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is null.</exception>
    /// <exception cref="FormatException">The durability value is not a recognized mode.</exception>
    internal static DatabaseApplicationComposition Compose(DatabaseHostConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        StorageCommitDurability durability = MapDurability(configuration.Durability);

        DatabaseApplicationBuilder builder = DatabaseApplication.CreateBuilder();

        // File-backed at the mounted data path; the in-memory strategy when no path
        // is configured (ephemeral dev runs — the orchestration manifest always
        // injects the data path of its volume mount).
        SqlDatabaseEngine engine = builder.AddSqlDatabase(options =>
        {
            options.EngineName = "sql";
            options.RootPath = configuration.DataPath;
            options.Durability = durability;
        });

        // Bind all interfaces (container-style: the gateway injects the port and
        // owns the network boundary); an unset port lets the OS assign one.
        var listener = new TcpConnectionListener(new TcpConnectionListenerOptions
        {
            EndPoint = new IPEndPoint(IPAddress.Any, configuration.Port ?? 0),
        });

        // Deferred: the factory runs at Build with the final registered engine
        // list, so the endpoint always serves exactly what the builder composed.
        builder.AddServer(engines =>
        {
            var serverOptions = new DatabaseServerOptions { Listener = listener };

            foreach (IDatabaseEngine registered in engines)
            {
                serverOptions.Engines.Add(registered);
            }

            return DatabaseServer.Create(serverOptions);
        });

        // Provision the default database after the engine starts and before the
        // endpoint accepts (additional services sit between the worker slots and the
        // endpoint in the start order), so a client can always bind it.
        builder.Options.Services.Add(new DefaultDatabaseProvisioner(engine, DefaultDatabaseName));

        DatabaseApplication application = builder.Build();
        IDatabaseServer server = builder.Options.Server!;

        return new DatabaseApplicationComposition(engine, listener, server, application);
    }

    /// <summary>
    /// Maps the <c>COHESION_DATABASE_DURABILITY</c> convention onto the engine's
    /// commit-durability modes: unset, <c>full</c>, or <c>synchronous</c> select the
    /// per-commit durable flush (the default); <c>grouped</c> or <c>relaxed</c>
    /// select the group-commit window (batched fsync — commits are still never
    /// acknowledged before they are durable).
    /// </summary>
    /// <param name="durability">The raw configuration value, or null when unset.</param>
    /// <returns>The mapped commit-durability mode.</returns>
    /// <exception cref="FormatException">The value is not a recognized mode.</exception>
    internal static StorageCommitDurability MapDurability(string? durability)
    {
        if (string.IsNullOrWhiteSpace(durability))
        {
            return StorageCommitDurability.Synchronous;
        }

        return durability.Trim().ToLowerInvariant() switch
        {
            "full" or "synchronous" => StorageCommitDurability.Synchronous,
            "grouped" or "relaxed" => StorageCommitDurability.Grouped,
            _ => throw new FormatException(
                $"Unrecognized durability mode '{durability}'. Accepted values: full, synchronous, grouped, relaxed."),
        };
    }
}
