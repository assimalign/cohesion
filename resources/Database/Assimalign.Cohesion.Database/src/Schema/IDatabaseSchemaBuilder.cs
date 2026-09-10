using System;
using System.Linq.Expressions;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// Builds a database schema from application-owned CLR types and expression trees.
/// </summary>
public interface IDatabaseSchemaBuilder
{
    /// <summary>Allows migrations that remove or rewrite existing data.</summary>
    void AllowDestructiveChanges();

    /// <summary>Declares a custom database type.</summary>
    /// <typeparam name="T">The CLR type represented by the declaration.</typeparam>
    /// <param name="configure">Configures the type.</param>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    void Type<T>(Action<IDatabaseTypeBuilder> configure);

    /// <summary>Declares a table whose row shape is <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The table row type.</typeparam>
    /// <param name="configure">Configures the table.</param>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    void Table<T>(Action<IDatabaseTableBuilder<T>> configure);

    /// <summary>Declares a named table whose row shape is <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The table row type.</typeparam>
    /// <param name="name">The stable database table name.</param>
    /// <param name="configure">Configures the table.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    void Table<T>(string name, Action<IDatabaseTableBuilder<T>> configure);

    /// <summary>Declares a named key-value collection.</summary>
    /// <typeparam name="T">The collection entry type.</typeparam>
    /// <param name="name">The stable collection name.</param>
    /// <param name="configure">Configures the collection fields and key.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is null.</exception>
    void Collection<T>(string name, Action<IDatabaseTableBuilder<T>> configure);

    /// <summary>Declares a model-specific schema extension.</summary>
    /// <param name="name">The extension name.</param>
    /// <param name="value">The canonical extension value.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    void Extension(string name, string value);

    /// <summary>Declares a parameterless database function.</summary>
    /// <typeparam name="TResult">The function result type.</typeparam>
    /// <param name="name">The function name.</param>
    /// <param name="body">The analyzable C# function body.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="body"/> is null.</exception>
    void Function<TResult>(string name, Expression<Func<TResult>> body);

    /// <summary>Declares a single-argument database function.</summary>
    /// <typeparam name="TArgument">The function argument type.</typeparam>
    /// <typeparam name="TResult">The function result type.</typeparam>
    /// <param name="name">The function name.</param>
    /// <param name="body">The analyzable C# function body.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="body"/> is null.</exception>
    void Function<TArgument, TResult>(string name, Expression<Func<TArgument, TResult>> body);

    /// <summary>Declares a trigger for a table row type.</summary>
    /// <typeparam name="TRow">The table row type.</typeparam>
    /// <param name="triggerEvent">The event that invokes the trigger.</param>
    /// <param name="body">The analyzable C# trigger body.</param>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> is null.</exception>
    void Trigger<TRow>(
        TriggerEvent triggerEvent,
        Expression<Action<IDatabaseTriggerContext, TRow>> body);

    /// <summary>Declares a database-scoped principal.</summary>
    /// <param name="name">The principal name.</param>
    /// <param name="configure">Configures the principal's grants.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="configure"/> is null.</exception>
    void Principal(string name, Action<IDatabasePrincipalBuilder> configure);
}
