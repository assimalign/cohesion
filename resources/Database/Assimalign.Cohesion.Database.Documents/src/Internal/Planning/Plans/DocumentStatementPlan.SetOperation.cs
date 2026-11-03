using System.Collections.Generic;

using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Execution;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed record DocumentSetOperationPlan(OqlSetOperationExpression Query, DocumentStatementPlan Left,
    DocumentStatementPlan Right, DocumentSetResultShape Shape, IReadOnlyList<DatabaseColumn> Columns,
    IReadOnlyList<bool> HeterogeneousColumns, IReadOnlySet<string> MemberNames, bool HasOpenMembers, long? Limit, long Offset) : DocumentStatementPlan;

internal enum DocumentSetResultShape { Value, Columns, Document }
