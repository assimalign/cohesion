using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Database;

/// <summary>
/// Typed engine lookup on the database application context.
/// </summary>
/// <remarks>
/// The context lists its engines as the root base (<see cref="IDatabaseApplicationContext.Engines"/>),
/// because the hosting layer composes every model alike. A caller that knows an engine's model
/// reaches the typed engine here, in one call and without a cast of its own. The lookup is a static
/// extension member, not a member of the interface, because a generic virtual method is never
/// devirtualized under NativeAOT (rule 5 of <c>database-area.md</c>; concrete-types plan, row 4).
/// </remarks>
public static partial class DatabaseApplicationContextExtensions
{
    extension(IDatabaseApplicationContext context)
    {
        /// <summary>
        /// Retrieves a borrowed engine by its ordinal name, typed as the model's engine.
        /// </summary>
        /// <typeparam name="TEngine">The engine's type, for example <c>SqlDatabaseEngine</c>.</typeparam>
        /// <param name="name">The engine name.</param>
        /// <returns>The registered engine, without transferring ownership.</returns>
        /// <exception cref="ArgumentNullException">The context or <paramref name="name"/> is null.</exception>
        /// <exception cref="ArgumentException"><paramref name="name"/> is empty or white space.</exception>
        /// <exception cref="KeyNotFoundException">No engine has that name.</exception>
        /// <exception cref="InvalidOperationException">The engine of that name is not a <typeparamref name="TEngine"/>.</exception>
        public TEngine GetEngine<TEngine>(string name)
            where TEngine : DatabaseEngine
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            DatabaseEngine engine = context.GetEngine(name);
            return engine as TEngine ?? throw new InvalidOperationException(
                $"Database engine '{name}' is a {engine.GetType().Name}, not a {typeof(TEngine).Name}.");
        }
    }
}
