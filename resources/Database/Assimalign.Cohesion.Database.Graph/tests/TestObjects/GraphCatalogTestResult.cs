using System.Collections.Generic;

using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Tests;

internal sealed record GraphCatalogTestResult(
    IReadOnlyList<(string Name, DatabaseType Type)> Columns, IReadOnlyList<object?[]> Rows, long AffectedCount, IReadOnlyList<ProtocolDiagnostic> Diagnostics);
