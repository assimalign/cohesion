using Assimalign.Cohesion.Database.Graph.Language;

namespace Assimalign.Cohesion.Database.Graph.Internal;

internal readonly record struct GraphCompositeNode(GqlCompositeOperator? Operator, int Left, int Right, int Operand);
