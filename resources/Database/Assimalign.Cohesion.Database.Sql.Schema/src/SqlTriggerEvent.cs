namespace Assimalign.Cohesion.Database.Sql.Schema;

/// <summary>
/// Identifies the row event that invokes a database trigger.
/// </summary>
public enum SqlTriggerEvent : byte
{
    /// <summary>Invokes the trigger before a row is inserted.</summary>
    BeforeInsert = 0,

    /// <summary>Invokes the trigger after a row is inserted.</summary>
    AfterInsert,

    /// <summary>Invokes the trigger before a row is updated.</summary>
    BeforeUpdate,

    /// <summary>Invokes the trigger after a row is updated.</summary>
    AfterUpdate,

    /// <summary>Invokes the trigger before a row is deleted.</summary>
    BeforeDelete,

    /// <summary>Invokes the trigger after a row is deleted.</summary>
    AfterDelete,
}
