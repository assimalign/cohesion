namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>Describes which instances of a resource run an action of its runtime surface.</summary>
public enum ResourceSurfaceActionTarget
{
    /// <summary>One instance runs the action (wire value <c>anyInstance</c>).</summary>
    AnyInstance,

    /// <summary>Every instance runs the action (wire value <c>everyInstance</c>).</summary>
    EveryInstance,
}
