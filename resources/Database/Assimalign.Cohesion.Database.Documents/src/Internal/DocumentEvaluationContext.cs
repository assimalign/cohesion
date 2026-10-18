using System;
using System.Text.Json;

namespace Assimalign.Cohesion.Database.Documents.Internal;

// Metadata travels with each row through grouping and projection; it is never a content path.
internal readonly record struct DocumentEvaluationContext(JsonElement Content, string? Id = null, ulong Version = 0,
    ReadOnlyMemory<byte> RawContent = default, DocumentVariableBinding? Bindings = null)
{
    internal DocumentEvaluationContext Bind(string name, object? value)
        => this with { Bindings = new DocumentVariableBinding(name, value, Bindings) };
}
