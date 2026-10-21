namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>The meaning of the named value slots supplied by an expression's binder.</summary>
internal enum SqlBindingContext
{
    Table,
    Function,
    Domain,
}
