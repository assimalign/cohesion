namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>Defines which instances of a resource run an action.</summary>
public enum ResourceActionTarget
{
    /// <summary>Any one instance, as the platform's own load balancing chooses.</summary>
    AnyInstance,

    /// <summary>Every current instance, such as for flushing a local cache.</summary>
    EveryInstance,
}
