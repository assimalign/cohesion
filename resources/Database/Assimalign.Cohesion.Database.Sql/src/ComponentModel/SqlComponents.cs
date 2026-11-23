using System;
using System.ComponentModel;

namespace Assimalign.Cohesion.Database.Sql;

/// <summary>
/// The factory behind <c>builder.Services.AddSql(name, configure)</c>. Applications call that verb, or
/// the root builder's <c>builder.AddSql(name, configure)</c>, not this type.
/// </summary>
/// <remarks>
/// <para>
/// The verb is a component integration (<c>Properties/ComponentIntegrations.cs</c>): every compilation
/// that references both this package and <c>Assimalign.Cohesion.DependencyInjection</c>, as every
/// database application does through <c>Database.Hosting</c>, receives <c>AddSql(name, configure)</c> on
/// <c>IServiceProviderBuilder</c>. The verb registers the <see cref="DatabaseEngineRegistration"/> this
/// factory returns as a singleton instance; <c>Database.Hosting</c> refuses a duplicate engine name
/// before any engine factory runs, then builds the engine, once, during application Build.
/// </para>
/// <para>
/// The type is public only because the generated verb, compiled into the application, calls it. It is
/// the static-factory shape because the engine name is a required argument (owner decision 39, #1395:
/// a required value keeps the static factory, so a missing name is a compile-time error).
/// </para>
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class SqlComponents
{
    /// <summary>
    /// Creates the registration of a SQL engine of that name, built with its declared databases
    /// during application Build.
    /// </summary>
    /// <param name="name">The engine name, written once (owner decision 52 of 2026-10-09).</param>
    /// <param name="configure">
    /// Configures the engine builder; invoked once, during application Build, so a closure over the
    /// application's configuration reads its final values.
    /// </param>
    /// <returns>The registration, which the verb registers as a singleton instance.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="configure"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or white space.</exception>
    /// <remarks>
    /// Creating the registration does no I/O. The engine's factory creates
    /// <see cref="SqlDatabaseEngine.CreateBuilder(string)"/>, runs <paramref name="configure"/> and builds; when
    /// either throws, it aborts the builder and the failure propagates out of application Build after
    /// the engines built before it are disposed.
    /// </remarks>
    public static DatabaseEngineRegistration CreateEngineRegistration(string name, Action<SqlDatabaseEngineBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(configure);
        return DatabaseEngineRegistration.Create(name, context =>
        {
            var engineBuilder = SqlDatabaseEngine.CreateBuilder(name);
            try
            {
                configure(engineBuilder);
                return engineBuilder.Build(context.BuildCancellationToken);
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                engineBuilder.Abort(failure);
                throw;
            }
        });
    }
}
