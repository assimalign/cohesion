using System;

using Assimalign.Cohesion.ApplicationModel.Internal;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Declares database mutations for the target's default control plane.</summary>
public static partial class DatabaseResourceCommandExtensions
{
    extension(IDatabaseResourceDescriptor descriptor)
    {
        /// <summary>Declares a database to create after the target is running.</summary>
        /// <param name="name">The database name without slash characters; an explicit engine prefixes its ownership conflict key.</param>
        /// <param name="engine">
        /// The engine's name as the target's <c>Program.cs</c> registers it (the model verb's first
        /// argument, <c>builder.AddSql("orders-sql", …)</c>), or null to select the target's sole engine.
        /// </param>
        /// <param name="optional">Whether rejection may allow dependents to start.</param>
        /// <returns>This descriptor for further declarations.</returns>
        /// <exception cref="ArgumentNullException">The descriptor or name is null.</exception>
        /// <exception cref="ArgumentException">The name or a supplied engine is blank, or the name contains a slash.</exception>
        /// <exception cref="InvalidOperationException">The descriptor is an immutable built snapshot.</exception>
        /// <remarks>
        /// The target creates an empty database on the running engine and owns it through this
        /// declaration: deleting the declaration drops it. A database the target's engine builder
        /// declares (<c>sql.AddDatabase("sales", …)</c>, with its schema) already exists when the
        /// engine is built and belongs to that declaration, so the target rejects a command that
        /// names it, as it rejects any database this declaration did not create.
        /// </remarks>
        public IDatabaseResourceDescriptor AddDatabase(string name, string? engine = null, bool optional = false)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (name.Contains('/'))
            {
                throw new ArgumentException("Database names must not contain '/' in declarative commands.", nameof(name));
            }
            if (engine is not null)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(engine);
            }
            string key = engine is null ? name : $"{engine}/{name}";
            descriptor.AddCommand("database.add-database", key, new AddDatabaseCommandPayload(name, engine),
                DatabaseCommandJsonContext.Default.AddDatabaseCommandPayload, optional);
            return descriptor;
        }

        /// <summary>Declares a principal; unsupported target mutation capabilities produce a named rejection.</summary>
        /// <param name="database">The database that owns the principal, without slash characters.</param>
        /// <param name="name">The principal name.</param>
        /// <param name="optional">Whether rejection may allow dependents to start.</param>
        /// <returns>This descriptor for further declarations.</returns>
        /// <exception cref="ArgumentNullException">The descriptor, database, or name is null.</exception>
        /// <exception cref="ArgumentException">The database or name is blank, or the database contains a slash.</exception>
        /// <exception cref="InvalidOperationException">The descriptor is an immutable built snapshot.</exception>
        public IDatabaseResourceDescriptor AddPrincipal(string database, string name, bool optional = false)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            ArgumentException.ThrowIfNullOrWhiteSpace(database);
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (database.Contains('/'))
            {
                throw new ArgumentException("Database names must not contain '/' in declarative commands.", nameof(database));
            }
            descriptor.AddCommand("database.add-principal", $"{database}/{name}", new AddPrincipalCommandPayload(database, name),
                DatabaseCommandJsonContext.Default.AddPrincipalCommandPayload, optional);
            return descriptor;
        }
    }
}
