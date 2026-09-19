namespace Assimalign.Cohesion.Database.Mapping;

/// <summary>Identifies the persistence operation for a tracked entity.</summary>
public enum EntityChangeKind
{
    /// <summary>Inserts an entity with an application-assigned key.</summary>
    Added,
    /// <summary>Updates the mapped values of an existing entity.</summary>
    Modified,
    /// <summary>Deletes an existing entity.</summary>
    Deleted,
}
