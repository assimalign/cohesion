using System;
using System.Linq.Expressions;

namespace Assimalign.Cohesion.Database.Sql;

internal sealed record SqlAggregateExpression(
    Type SourceType,
    LambdaExpression Selector,
    LambdaExpression Predicate) : ISqlAggregateExpression;
