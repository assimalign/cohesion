using System.Collections.Generic;

using Assimalign.Cohesion.Database.Sql.Language;

namespace Assimalign.Cohesion.Database.Sql.Internal;

// A declaration records the resolver's choices in their actual input/group/ordering
// scopes. Persisted bodies use an ordinary read-only pin map instead.
internal sealed class SqlViewCallCapture : Dictionary<SqlFunctionCallExpression, string>
{
    internal SqlViewCallCapture() : base(ReferenceEqualityComparer.Instance) { }
}
