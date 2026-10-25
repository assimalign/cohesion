namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>Controls whether a scalar function receives NULL arguments.</summary>
public enum KeyValueNullBehavior
{
    /// <summary>Return NULL without calling the body when any argument is NULL.</summary>
    ReturnsNullOnNullInput,

    /// <summary>Call the body with NULL arguments.</summary>
    CalledOnNullInput,
}
