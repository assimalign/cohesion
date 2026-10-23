using Assimalign.Cohesion.Database.Sql.Catalog;

namespace Assimalign.Cohesion.Database.Sql.Internal;

// Statement-local edges are discovered before locking. Endpoint projections are requested
// only by row checks after preparation has validated the locked immutable definitions.
[global::System.Diagnostics.DebuggerDisplay("SqlBoundReference")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundReference.DebugView))]
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

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundReference _target;

        public DebugView(global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundReference value)
        {
            _target = value;
        }

        public global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogTable Child => _target.Child;
        public global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogConstraint Constraint => _target.Constraint;
        public global::Assimalign.Cohesion.Database.Sql.Catalog.SqlCatalogTable Parent => _target.Parent;
        public global::Assimalign.Cohesion.Database.Sql.Internal.SqlBoundTableCache Definitions => _target.Definitions;
    }
}
