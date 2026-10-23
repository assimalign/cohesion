using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Language;
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
internal sealed partial class SqlDeclaredDatabase
{
    private SqlSchemaMigrationResult? _result;

    private SqlDeclaredDatabase(DatabaseName name, Collation collation, SqlProvisioningMode mode, SqlCompiledSchema? schema, bool runMigrationsInline)
    {
        Name = name;
        Collation = collation;
        Mode = mode;
        Schema = schema;
        RunMigrationsInline = runMigrationsInline;
    }

    /// <summary>Gets the database name.</summary>
    internal DatabaseName Name { get; }

    /// <summary>Gets the declared default collation: <see cref="Collation.Binary"/> when the builder left it unset.</summary>
    internal Collation Collation { get; }

    /// <summary>Gets the provisioning mode.</summary>
    internal SqlProvisioningMode Mode { get; }

    /// <summary>Gets the compiled schema, or null when the database is only ensured to exist.</summary>
    internal SqlCompiledSchema? Schema { get; }

    internal bool RunMigrationsInline { get; }

    /// <summary>
    /// Gets the outcome of the schema's apply or verification once provisioning ran, or null before
    /// it did and for a database that declares no schema.
    /// </summary>
    internal SqlSchemaMigrationResult? Result => _result;

    /// <summary>
    /// Compiles one database declaration's schema and adds the domains it declares
    /// (<c>schema.Type&lt;T&gt;</c>) to the engine's types, before the build freezes them (phase 2):
    /// a schema that does not compile, declares a principal (owner decision 58 of 2026-10-09), or
    /// declares a domain whose name another type of the engine has with another base type is refused.
    /// Touches no file.
    /// </summary>
    /// <param name="engineName">The engine's name, for the message.</param>
    /// <param name="declaration">The database's declaration.</param>
    /// <param name="types">The engine's types, not yet frozen.</param>
    /// <returns>The compiled schema, or null when the database declares none.</returns>
    /// <exception cref="SqlSchemaMigrationException">The declaration is refused (<c>CS03101</c>).</exception>
    internal static SqlCompiledSchema? CompileSchema(string engineName, SqlDatabaseBuilder declaration, SqlTypeCollection types)
    {
        if (declaration.DeclaredSchema is not { } schema)
        {
            return null;
        }

        SqlCompiledSchema compiled;
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

        foreach (CompiledSchemaType declared in compiled.Types)
        {
            SqlType domain;
            try
            {
                domain = SqlSchemaProvisioner.CreateDomain(declared);
            }
            catch (ArgumentException invalid)
            {
                throw Refuse(engineName, declaration.Name, $"its schema declares type '{declared.Name}', which cannot be a domain: {invalid.Message}", invalid);
            }

            if (types.AddDeclared(domain) is { } conflict)
            {
                throw Refuse(engineName, declaration.Name, $"its schema conflicts with the engine's types: {conflict}.", inner: null);
            }
        }

        return compiled;
    }

    /// <summary>
    /// Binds one database declaration's compiled schema to the engine's frozen catalogs and refuses
    /// what the engine cannot provision, before any file is touched (phase 3 of the build): a column
    /// whose declared type the type catalog does not resolve or does not store as its member's type,
    /// or a CHECK that does not bind to the function catalog: one that is not exactly one predicate,
    /// names an unknown column or function, calls a function with arguments no overload accepts, or
    /// calls a function that is not immutable (owner decision 64).
    /// </summary>
    /// <param name="engineName">The engine's name, for the message.</param>
    /// <param name="declaration">The database's declaration.</param>
    /// <param name="compiled">The schema phase 2 compiled, or null when the database declares none.</param>
    /// <param name="functions">The engine's frozen function catalog (phase 2).</param>
    /// <param name="types">The engine's frozen type catalog (phase 2).</param>
    /// <param name="parserOptions">The engine's parser options, which its DDL will parse the CHECK with.</param>
    /// <returns>The compiled declaration.</returns>
    /// <exception cref="SqlSchemaMigrationException">The declaration is refused (<c>CS03101</c>).</exception>
    internal static SqlDeclaredDatabase Compile(string engineName, SqlDatabaseBuilder declaration, SqlCompiledSchema? compiled,
        SqlFunctionCatalog functions, SqlTypeCatalog types, SqlQueryParserOptions parserOptions)
    {
        Collation collation = declaration.DefaultCollation ?? Collation.Binary;
        if (compiled is not null)
        {
            try
            {
                var environment = new SqlFunctionEnvironment(functions, declaration.Name, types);
                SqlSchemaProvisioner.BindDeclaredTypes(compiled, types);
                // Qualifier rules depend on whether this hash was already applied. The
                // provisioner checks them after opening the catalog, before applying any DDL.
                SqlSchemaProvisioner.BindDeclaredChecks(compiled, collation, environment, parserOptions, validateQualifiers: false);
                // Identity belongs to compilation, before any I/O. The provisioning
                // phase reads the cached hash and never serializes or hashes a declaration.
                _ = compiled.Hash;
            }
            catch (DatabaseException invalid)
            {
                throw Refuse(engineName, declaration.Name, invalid.Message, invalid);
            }
        }

        var scripts = declaration.Scripts.ConvertAll(script => new SqlDeclaredScript(script.Name, script.Sql, parserOptions));
        ValidateScriptOwnership(compiled, scripts);
        return new SqlDeclaredDatabase(
            declaration.Name,
            collation,
            declaration.Provisioning,
            compiled,
            declaration.RunMigrationsInline || scripts.Count > 0)
        {
            _scripts = scripts.AsReadOnly(),
            _scriptHash = SqlDeclaredScript.Hash(scripts),
        };
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
    /// The collation differs (<c>CS03102</c>), the database drifted from a Verify declaration or
    /// does not exist (<c>CS03103</c>), or the schema's apply failed (<c>CS03104</c> for a
    /// failed step).
    /// </exception>
    internal async ValueTask ProvisionAsync(SqlDatabaseEngine engine, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SqlDatabase database = await OpenOrCreateAsync(engine, cancellationToken).ConfigureAwait(false);

        database.Definitions.EnsureDefinitions(cancellationToken);
        Collation existing = database.Catalog.DefaultCollation;
        if (existing.Id != Collation.Id)
        {
            throw new SqlSchemaMigrationException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.DeclaredDatabaseProvisionAsyncFailure, SqlProvisioningCodes.CollationMismatch, engine.Name, Name, existing.Name, Collation.Name));
        }

        await ValidateScriptsAsync(database, cancellationToken).ConfigureAwait(false);
        if (Schema is not null)
        {
            _result = Mode == SqlProvisioningMode.Verify
                ? await database.VerifySchemaAsync(Schema, cancellationToken).ConfigureAwait(false)
                : await database.SchemaProvisioner.ApplyAsync(Schema, cancellationToken, !RunMigrationsInline).ConfigureAwait(false);
        }

        await ApplyScriptsAsync(database, cancellationToken).ConfigureAwait(false);

        // Step 6 (owner decision 65): every persisted definition binds to the engine's catalog. A
        // CHECK that calls a function this engine does not register opens, and fails each write;
        // for a database the engine declares, the application that declares it removed the
        // function, so its build fails before it accepts work.
        if (database.Definitions.FindUnresolved(cancellationToken) is { } unresolved)
        {
            throw new SqlSchemaMigrationException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.DeclaredDatabaseProvisionAsyncFailure2, engine.Name, Name, unresolved.Describe()),
                SqlEvaluationException.UnregisteredFunction(unresolved));
        }

        // The same rule for a column of a domain (phase E3): the database opens and writes to the
        // column fail, but the application that declares the database removed the domain.
        if (database.Definitions.FindUnresolvedDomain(cancellationToken) is { } domain)
        {
            throw new SqlSchemaMigrationException(
                string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.DeclaredDatabaseProvisionAsyncFailure3, engine.Name, Name, domain.Describe()),
                SqlEvaluationException.UnregisteredDomain(domain));
        }
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
                    string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.DatabaseOpenOrCreateAsyncFailure, SqlProvisioningCodes.Drift, engine.Name, Name),
                    missing);
            }
        }

        // The first launch: the database is born on the declared collation.
        return await engine.CreateDatabaseAsync(Name, Collation, cancellationToken).ConfigureAwait(false);
    }

    private static SqlSchemaMigrationException Refuse(string engineName, DatabaseName databaseName, string reason, Exception? inner)
    {
        string message = string.Format(System.Globalization.CultureInfo.CurrentCulture, ErrorMessages.SqlDeclaredDatabaseRefuseFailure, SqlProvisioningCodes.DeclarationRefused, engineName, databaseName, reason);
        return inner is null ? new SqlSchemaMigrationException(message) : new SqlSchemaMigrationException(message, inner);
    }
}
