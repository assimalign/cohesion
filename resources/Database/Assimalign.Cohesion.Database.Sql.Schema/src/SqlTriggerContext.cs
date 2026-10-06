using System;

namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>
/// The operations a database trigger body may call: the first parameter of the expression a
/// trigger declares (<see cref="SqlSchemaBuilder.Trigger{TRow}"/>).
/// </summary>
/// <remarks>
/// <para>
/// A phantom: it appears only inside trigger expression trees, which the schema compiler and the
/// <c>Sdk.Database</c> canonicalizer read and never run. No instance exists, so its private
/// constructor is never called and its members never execute; the canonicalizers allow calls to
/// this type by its metadata name, so a rename changes the SDK's string in the same commit.
/// </para>
/// <para>
/// <b>Shape (concrete-types plan, phase 4, §6.7).</b> A public sealed class with a private
/// constructor; it replaced the <c>ISqlTriggerContext</c> interface. The type identity is part of
/// a trigger's canonical expression, so the rename changed the compiled hash of every schema that
/// declares a trigger (nothing has shipped; plan §1).
/// </para>
/// </remarks>
public sealed class SqlTriggerContext
{
    private SqlTriggerContext()
    {
    }

    /// <summary>Records an audit event as part of the trigger transaction.</summary>
    /// <typeparam name="TValue">The audit payload type.</typeparam>
    /// <param name="eventName">The stable event name.</param>
    /// <param name="value">The event payload.</param>
    /// <exception cref="NotSupportedException">Always: a trigger body is compiled to schema, never invoked in process.</exception>
    public void Audit<TValue>(string eventName, TValue value)
        => throw new NotSupportedException("A trigger body is compiled to schema; it is never invoked in process.");
}
