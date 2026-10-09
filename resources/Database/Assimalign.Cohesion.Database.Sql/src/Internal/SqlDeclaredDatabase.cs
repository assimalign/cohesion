using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// One database a SQL engine's builder declared, compiled before any file is touched (phase 3 of
/// the build), and the flow that provisions it once the engine exists (phase 6; the design's
/// §5.3): open or create, the collation check, then the schema applied or verified.
/// </summary>
/// <remarks>
/// The engine keeps its declared databases (<see cref="SqlDatabaseEngine.DeclaredDatabases"/>), so
/// it refuses to drop one (owner decision 56 of 2026-10-09), and each records the outcome of its
/// provisioning for this assembly's tests.
/// </remarks>
internal sealed class SqlDeclaredDatabase
{
    private SqlSchemaMigrationResult? _result;

    private SqlDeclaredDatabase(DatabaseName name, Collation collation, SqlProvisioningMode mode, SqlCompiledSchema? schema)
    {
        Name = name;
        Collation = collation;
        Mode = mode;
        Schema = schema;
    }

    /// <summary>Gets the database name.</summary>
    internal DatabaseName Name { get; }

    /// <summary>Gets the declared default collation: <see cref="Collation.Binary"/> when the builder left it unset.</summary>
    internal Collation Collation { get; }

    /// <summary>Gets the provisioning mode.</summary>
    internal SqlProvisioningMode Mode { get; }

    /// <summary>Gets the compiled schema, or null when the database is only ensured to exist.</summary>
    internal SqlCompiledSchema? Schema { get; }

    /// <summary>
    /// Gets the outcome of the schema's apply or verification once provisioning ran, or null before
    /// it did and for a database that declares no schema.
    /// </summary>
    internal SqlSchemaMigrationResult? Result => _result;

    /// <summary>
    /// Compiles one database declaration and refuses what the engine cannot provision, before any
    /// file is touched (phase 3 of the build): a schema that does not compile, or declares a
    /// principal (owner decision 58 of 2026-10-09) or a custom type. Binding a declared CHECK or
    /// DEFAULT to the engine's function and type catalog joins this phase when the typed schema
    /// can declare one (E2).
    /// </summary>
    /// <param name="engineName">The engine's name, for the message.</param>
    /// <param name="declaration">The database's declaration.</param>
    /// <returns>The compiled declaration.</returns>
    /// <exception cref="SqlSchemaMigrationException">The declaration is refused (<c>COHSQLP001</c>).</exception>
    internal static SqlDeclaredDatabase Compile(string engineName, SqlDatabaseBuilder declaration)
    {
        SqlCompiledSchema? compiled = null;
        if (declaration.DeclaredSchema is { } schema)
        {
            try
            {
                compiled = schema.Compile();
            }
            catch (SqlSchemaValidationException invalid)
            {
                throw Refuse(engineName, declaration.Name, invalid.Message, invalid);
            }

            if (SqlSchemaProvisioner.DescribeUnsupported(compiled) is { } unsupported)
            {
                throw Refuse(engineName, declaration.Name, $"its schema declares {unsupported}.", inner: null);
            }
        }

        return new SqlDeclaredDatabase(
            declaration.Name,
            declaration.DefaultCollation ?? Collation.Binary,
            declaration.Provisioning,
            compiled);
    }

    /// <summary>
    /// Provisions the database on the engine its builder just created (phase 6 of the build): opens
    /// it, or creates it when it does not exist (Apply mode only), refuses it when its collation is
    /// not the declared one, then applies or verifies the declared schema.
    /// </summary>
    /// <param name="engine">The engine.</param>
    /// <param name="cancellationToken">Observed by each step.</param>
    /// <returns>A task that completes once the database is provisioned.</returns>
    /// <exception cref="SqlSchemaMigrationException">
    /// The collation differs (<c>COHSQLP002</c>), the database drifted from a Verify declaration or
    /// does not exist (<c>COHSQLP003</c>), or the schema's apply failed (<c>COHSQLP004</c> for a
    /// failed step).
    /// </exception>
    internal async ValueTask ProvisionAsync(SqlDatabaseEngine engine, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SqlDatabase database = await OpenOrCreateAsync(engine, cancellationToken).ConfigureAwait(false);

        Collation existing = database.Catalog.DefaultCollation;
        if (existing.Id != Collation.Id)
        {
            throw new SqlSchemaMigrationException(
                $"{SqlProvisioningCodes.CollationMismatch}: SQL engine '{engine.Name}', database '{Name}': the database was created " +
                $"with default collation '{existing.Name}', but it is declared with '{Collation.Name}'. A key's collation is " +
                "persisted in it and cannot change: declare the collation the database has, or move its data to a new database.");
        }

        if (Schema is null)
        {
            return;
        }

        _result = Mode == SqlProvisioningMode.Verify
            ? await database.VerifySchemaAsync(Schema, cancellationToken).ConfigureAwait(false)
            : await database.ApplySchemaAsync(Schema, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<SqlDatabase> OpenOrCreateAsync(SqlDatabaseEngine engine, CancellationToken cancellationToken)
    {
        if (engine.TryGetDatabase(Name, out SqlDatabase? open))
        {
            return open;
        }

        try
        {
            return await engine.OpenDatabaseAsync(Name, cancellationToken).ConfigureAwait(false);
        }
        catch (DatabaseNotFoundException missing)
        {
            if (Mode == SqlProvisioningMode.Verify)
            {
                throw new SqlSchemaMigrationException(
                    $"{SqlProvisioningCodes.Drift}: SQL engine '{engine.Name}', database '{Name}': the database does not exist. It is " +
                    "declared in Verify mode, so it is not created; create and migrate it out of band, or declare it in Apply mode.",
                    missing);
            }
        }

        // The first launch: the database is born on the declared collation.
        return await engine.CreateDatabaseAsync(Name, Collation, cancellationToken).ConfigureAwait(false);
    }

    private static SqlSchemaMigrationException Refuse(string engineName, DatabaseName databaseName, string reason, Exception? inner)
    {
        string message = $"{SqlProvisioningCodes.DeclarationRefused}: SQL engine '{engineName}' cannot provision database " +
            $"'{databaseName}': {reason} Nothing was opened or written.";
        return inner is null ? new SqlSchemaMigrationException(message) : new SqlSchemaMigrationException(message, inner);
    }
}
