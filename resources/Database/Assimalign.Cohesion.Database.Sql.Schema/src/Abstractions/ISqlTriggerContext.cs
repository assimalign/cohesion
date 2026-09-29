using System;

namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>Exposes operations available to a database trigger body.</summary>
public interface ISqlTriggerContext
{
    /// <summary>Records an audit event as part of the trigger transaction.</summary>
    /// <typeparam name="TValue">The audit payload type.</typeparam>
    /// <param name="eventName">The stable event name.</param>
    /// <param name="value">The event payload.</param>
    /// <exception cref="ArgumentException"><paramref name="eventName"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="eventName"/> is null.</exception>
    void Audit<TValue>(string eventName, TValue value);
}
