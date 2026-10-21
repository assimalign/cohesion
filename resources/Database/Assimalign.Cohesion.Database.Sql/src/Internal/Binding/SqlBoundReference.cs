using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed record SqlBoundReference(SqlCatalogTable Child, SqlCatalogConstraint Constraint,
    SqlBoundConstraintKey ChildKey, SqlBoundConstraintKey ParentKey)
{
    internal void Deconstruct(out SqlCatalogTable child, out SqlCatalogConstraint constraint)
    {
        child = Child;
        constraint = Constraint;
    }
}
