using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Storage;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Internal;

// The storage layer can carry format diagnostics without depending on the engine root.
internal sealed class GraphRecordFormatException : StorageException
{
    internal GraphRecordFormatException(string message) : base(message)
        => Diagnostics = Array.AsReadOnly<Diagnostic>([new Diagnostic
        { Code = "COHDBG017", Message = message, Severity = DiagnosticSeverity.Error }]);

    internal IReadOnlyList<Diagnostic> Diagnostics { get; }
}
