using Assimalign.Cohesion.Database.Documents.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed record DocumentProjection(string Name, OqlExpression Expression, DatabaseType Type);
