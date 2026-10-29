using System.Collections.Generic;

using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Declares one application resource in an application set: a logical service, such as
/// <c>orders-api</c>, and the resources it depends on.
/// </summary>
public interface IApplicationResourceDescriptor
{
    /// <summary>
    /// Gets the id the resource's instances share.
    /// </summary>
    /// <remarks>
    /// The id is unique within <see cref="Set"/>. Across the hierarchy, a resource is identified by
    /// its set's <see cref="IApplicationSet.Id"/> and this id together.
    /// </remarks>
    ResourceId Id => ResourceId.FromName(Provider, Name);

    /// <summary>
    /// Gets the set whose gateway runs the resource.
    /// </summary>
    IApplicationSet Set { get; }

    /// <summary>
    /// Gets the resource's provider.
    /// </summary>
    ProviderName Provider { get; }

    /// <summary>
    /// Gets the resource's name.
    /// </summary>
    ResourceName Name { get; }

    /// <summary>
    /// Gets the resources this one depends on, in declaration order, each with the permissions it
    /// needs there.
    /// </summary>
    IReadOnlyList<ApplicationResourceDependency> Dependencies { get; }

    /// <summary>
    /// Gets the configuration values the resource's instances receive with their deployment, in
    /// declaration order.
    /// </summary>
    IReadOnlyList<ApplicationResourceSetting> Settings { get; }

    /// <summary>
    /// Gets the placeholder for the endpoint the resource's platform gives it, through which its
    /// dependents reach every instance, such as a load balancer's address.
    /// </summary>
    ApplicationResourceOutput Endpoint => Output(OutputName.Endpoint);

    /// <summary>
    /// Gets the placeholder for one of the resource's outputs: a value it has only once it is
    /// deployed.
    /// </summary>
    /// <remarks>
    /// The gateway that runs the resource records the value its infrastructure provider reports, and
    /// fills it in where another resource refers to it.
    /// </remarks>
    /// <param name="name">The output's name.</param>
    /// <returns>The placeholder.</returns>
    /// <exception cref="System.ArgumentException"><paramref name="name"/> is <see langword="default"/>.</exception>
    ApplicationResourceOutput Output(OutputName name);

    /// <summary>
    /// Declares that this resource depends on another and needs the specified permissions on it.
    /// </summary>
    /// <remarks>
    /// A dependency orders deployment: the gateway deploys this resource once the other one runs and
    /// its outputs are known, so dependencies may not form a cycle. A resource that must change one it
    /// depends on does so at run time, through an action that resource exposes. The gateway also
    /// registers the permissions with the tenant's identity provider, which issues this resource
    /// credentials for them when it runs. Declaring the same dependency again adds the new
    /// permissions to it. The other resource may belong to any set of the same hierarchy.
    /// </remarks>
    /// <param name="resource">The resource depended on.</param>
    /// <param name="permissions">The permissions this resource needs on it; none to depend without access.</param>
    /// <returns>This descriptor.</returns>
    /// <exception cref="System.ArgumentNullException">
    /// <paramref name="resource"/> or <paramref name="permissions"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="resource"/> is this resource, belongs to another hierarchy, or already depends
    /// on this resource, directly or through others; or a permission is <see langword="default"/>.
    /// </exception>
    IApplicationResourceDescriptor DependsOn(IApplicationResourceDescriptor resource, params ResourcePermission[] permissions);

    /// <summary>
    /// Gives the resource's instances a configuration value with their deployment.
    /// </summary>
    /// <remarks>
    /// The infrastructure provider passes the value to every instance, for example as an environment
    /// variable. Setting a name again replaces its value.
    /// </remarks>
    /// <param name="name">The configuration key, such as <c>Logging:LogLevel:Default</c>.</param>
    /// <param name="value">The value.</param>
    /// <returns>This descriptor.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.ArgumentException"><paramref name="name"/> is <see langword="default"/>.</exception>
    IApplicationResourceDescriptor WithSetting(SettingName name, string value);

    /// <summary>
    /// Gives the resource's instances a configuration value filled in from another resource's
    /// output, such as the endpoint of a store it uses.
    /// </summary>
    /// <remarks>
    /// The resource depends on the output's resource, with no permissions unless
    /// <see cref="DependsOn"/> grants them, so the gateway deploys it once that resource runs and
    /// its outputs are known, then fills the value in. Setting a name again replaces its value.
    /// </remarks>
    /// <param name="name">The configuration key, such as <c>ConfigurationStore:Endpoint</c>.</param>
    /// <param name="output">The placeholder for the output, from the other resource's descriptor.</param>
    /// <returns>This descriptor.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="output"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="name"/> or the output's name is <see langword="default"/>; the output is this
    /// resource's own, which exists only once it is deployed; the output's resource is not declared
    /// in this hierarchy; or that resource already depends on this one, directly or through others.
    /// </exception>
    IApplicationResourceDescriptor WithSetting(SettingName name, ApplicationResourceOutput output);
}
