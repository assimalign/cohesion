namespace Assimalign.Cohesion.Database.Graph;

/// <summary>The function's NULL input contract.</summary>
public enum GraphNullBehavior
{
    /// <summary>Returns NULL (or skips an aggregate row) before invoking the callback when any argument is NULL.</summary>
    ReturnsNullOnNullInput,
    /// <summary>The callback receives NULL inputs.</summary>
    CalledOnNullInput,
}
