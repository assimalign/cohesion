namespace Assimalign.Cohesion.Database.Documents.Internal;

// Persistent frames share the owned FROM value and preceding bindings across expanded rows.
internal sealed record DocumentVariableBinding(string Name, object? Value, DocumentVariableBinding? Parent);
