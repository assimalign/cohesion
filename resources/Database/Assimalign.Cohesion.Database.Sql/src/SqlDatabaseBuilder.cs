using System;

using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// Declares one database of a SQL engine: its default collation, its typed schema and how the
/// engine's build provisions it. Created by
/// <see cref="SqlDatabaseEngineBuilder.AddDatabase(string, Action{SqlDatabaseBuilder}?)"/>.
/// </summary>
/// <remarks>
/// <para>
/// The database level of the three composition levels (B1 of the engine extensibility design,
/// owner decision 52 of 2026-10-09): it owns one database and never an engine setting. The engine
/// builder's <see cref="SqlDatabaseEngineBuilder.Build"/> compiles the declared schema before any
/// file is touched, then opens or creates the database and provisions it before it returns.
/// </para>
/// <para>
/// Every member refuses a change once the engine builder's one build has begun. Sealed, with an
/// internal constructor (<c>database-area.md</c>, rule 1).
/// </para>
/// </remarks>
public sealed class SqlDatabaseBuilder
{
    private readonly SqlDatabaseEngineBuilder _engine;
    private readonly DatabaseName _name;
    private Collation? _defaultCollation;
    private SqlProvisioningMode _provisioning = SqlProvisioningMode.Apply;
    private SqlSchema? _schema;

    /// <summary>Initializes the declaration of one database of an engine builder.</summary>
    /// <param name="engine">The engine builder the database belongs to.</param>
    /// <param name="name">The database name.</param>
    internal SqlDatabaseBuilder(SqlDatabaseEngineBuilder engine, DatabaseName name)
    {
        _engine = engine;
        _name = name;
    }

    /// <summary>Gets the database name, written once as the first argument of <c>AddDatabase</c>.</summary>
    public DatabaseName Name => _name;

    /// <summary>
    /// Gets or sets the collation the database's string columns inherit when they name none;
    /// <see cref="Collation.Binary"/> when unset.
    /// </summary>
    /// <remarks>
    /// A database is created with it, and an existing database keeps the collation it was created
    /// with: a key's collation id is persisted in it. The build refuses an existing database whose
    /// collation is not this one (<c>COHSQLP002</c>, owner decision 56 of 2026-10-09), an unset value
    /// meaning <see cref="Collation.Binary"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The engine builder's build has begun.</exception>
    public Collation? DefaultCollation
    {
        get => _defaultCollation;
        set
        {
            _engine.EnsureMutable();
            _defaultCollation = value;
        }
    }

    /// <summary>
    /// Gets or sets how the build provisions the database: <see cref="SqlProvisioningMode.Apply"/>,
    /// the default, or <see cref="SqlProvisioningMode.Verify"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The engine builder's build has begun.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a defined mode.</exception>
    public SqlProvisioningMode Provisioning
    {
        get => _provisioning;
        set
        {
            _engine.EnsureMutable();
            if (value is not (SqlProvisioningMode.Apply or SqlProvisioningMode.Verify))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The provisioning mode is not defined.");
            }

            _provisioning = value;
        }
    }

    /// <summary>
    /// Gets the declared schema, or null when the database is only ensured to exist.
    /// </summary>
    internal SqlSchema? DeclaredSchema => _schema;

    /// <summary>
    /// Declares the database's schema inline, named for the database. The <c>Sdk.Database</c>
    /// extractor reads this call to emit the database's schema artifact without running the
    /// program.
    /// </summary>
    /// <param name="declare">Declares the schema.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="declare"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// The engine builder's build has begun, or the database already declares a schema.
    /// </exception>
    public SqlDatabaseBuilder Schema(Action<SqlSchemaBuilder> declare)
    {
        _engine.EnsureMutable();
        ArgumentNullException.ThrowIfNull(declare);
        ThrowIfSchemaDeclared();
        _schema = SqlSchema.Create(_name, declare);
        return this;
    }

    /// <summary>
    /// Declares the database's schema from a reusable declaration, whose name must be the
    /// database's (ignoring case, as database names compare).
    /// </summary>
    /// <param name="schema">The schema declaration.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> is null.</exception>
    /// <exception cref="ArgumentException">The schema names another database.</exception>
    /// <exception cref="InvalidOperationException">
    /// The engine builder's build has begun, or the database already declares a schema.
    /// </exception>
    public SqlDatabaseBuilder Schema(SqlSchema schema)
    {
        _engine.EnsureMutable();
        ArgumentNullException.ThrowIfNull(schema);
        if (new DatabaseName(schema.Name) != _name)
        {
            throw new ArgumentException(
                $"Schema '{schema.Name}' cannot be the schema of database '{_name}': a schema names the database it declares.",
                nameof(schema));
        }

        ThrowIfSchemaDeclared();
        _schema = schema;
        return this;
    }

    private void ThrowIfSchemaDeclared()
    {
        if (_schema is not null)
        {
            throw new InvalidOperationException($"Database '{_name}' already declares its schema.");
        }
    }
}
