using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

// Statement-local edges are discovered before locking. Endpoint projections are requested
// only by row checks after preparation has validated the locked immutable definitions.
internal sealed record SqlBoundReference(SqlCatalogTable Child, SqlCatalogConstraint Constraint,
    SqlCatalogTable Parent, SqlBoundTableCache Definitions)
{
    private SqlBoundConstraintKey? _childKey;
    private SqlBoundConstraintKey? _parentKey;

    internal SqlBoundConstraintKey ChildKey => _childKey ??= Definitions.Get(Child).GetKey(Constraint.Columns);
    internal SqlBoundConstraintKey ParentKey => _parentKey ??= Definitions.Get(Parent).GetKey(Constraint.ReferencedColumns!);

    internal void Deconstruct(out SqlCatalogTable child, out SqlCatalogConstraint constraint)
    {
        child = Child;
        constraint = Constraint;
    }
}
