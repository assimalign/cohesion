using System.Collections.Generic;
using System.Text.Json;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// Declares one application resource in an application set, its logical form: a service, such as
/// <c>orders-api</c>, the resources it depends on, and how it is configured.
/// </summary>
/// <remarks>
/// The resource's <see cref="IApplicationResource.Id"/> is unique within <see cref="Set"/>. Across
/// gateways, a resource is identified by its set's <see cref="IApplicationSet.Id"/> and that id
/// together.
/// </remarks>
public interface IApplicationResourceDefinition : IApplicationResource
{
    /// <summary>
    /// Gets the set whose gateway runs the resource.
    /// </summary>
    IApplicationSet Set { get; }

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
    /// Gets the configuration of how particular platforms deploy the resource, in declaration order.
    /// </summary>
    IReadOnlyList<ApplicationResourceExtension> Extensions { get; }

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
    /// permissions to it. The other resource belongs to the same set: a gateway orders only its own
    /// resources, and a resource uses its parent gateway's resources through their outputs.
    /// </remarks>
    /// <param name="resource">The resource depended on.</param>
    /// <param name="permissions">The permissions this resource needs on it; none to depend without access.</param>
    /// <returns>This definition.</returns>
    /// <exception cref="System.ArgumentNullException">
    /// <paramref name="resource"/> or <paramref name="permissions"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="resource"/> is this resource, belongs to another set, or already depends
    /// on this resource, directly or through others; or a permission is <see langword="default"/>.
    /// </exception>
    IApplicationResourceDefinition DependsOn(IApplicationResourceDefinition resource, params Permission[] permissions);

    /// <summary>
    /// Gives the resource's instances a configuration value with their deployment.
    /// </summary>
    /// <remarks>
    /// The infrastructure provider passes the value to every instance, for example as an environment
    /// variable. Setting a name again replaces its value.
    /// </remarks>
    /// <param name="name">The configuration key, such as <c>Logging:LogLevel:Default</c>.</param>
    /// <param name="value">The value.</param>
    /// <returns>This definition.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.ArgumentException"><paramref name="name"/> is <see langword="default"/>.</exception>
    IApplicationResourceDefinition WithSetting(SettingName name, string value);

    /// <summary>
    /// Gives the resource's instances a configuration value filled in from another resource's
    /// output, such as the endpoint of a store it uses.
    /// </summary>
    /// <remarks>
    /// For an output of the set's own resources, the resource depends on the output's resource, with
    /// no permissions unless <see cref="DependsOn"/> grants them, so the gateway deploys it once that
    /// resource runs and its outputs are known, then fills the value in. For an output of the parent
    /// gateway's resources (<see cref="IApplicationParent.Output"/>), the parent hands the value over
    /// when the gateway joins it, and the model checks that the output's gateway is the parent. Setting
    /// a name again replaces its value.
    /// </remarks>
    /// <param name="name">The configuration key, such as <c>ConfigurationStore:Endpoint</c>.</param>
    /// <param name="output">The placeholder for the output, from the other resource's definition or the parent.</param>
    /// <returns>This definition.</returns>
    /// <exception cref="System.ArgumentNullException"><paramref name="output"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="name"/> or the output's name is <see langword="default"/>; the output is this
    /// resource's own, which exists only once it is deployed; the output's resource is in this gateway
    /// but not declared in its set; or that resource already depends on this one, directly or through
    /// others.
    /// </exception>
    IApplicationResourceDefinition WithSetting(SettingName name, ApplicationResourceOutput output);

    /// <summary>
    /// Configures how one platform deploys the resource, such as an autoscaler on Kubernetes.
    /// </summary>
    /// <remarks>
    /// Platform packages call this from their own verbs, such as <c>ConfigureHpa</c>, with their
    /// options serialized to JSON. The extension applies only where the resource's gateway deploys onto
    /// <paramref name="platform"/>: the gateway hands it to an infrastructure provider of that platform
    /// and to no other, so the same resource can carry extensions for every platform it may run on.
    /// The value is copied, so it does not depend on the document it came from. Declaring the same
    /// platform and name again replaces the value.
    /// </remarks>
    /// <param name="platform">The platform the extension applies on, such as <c>kubernetes</c>.</param>
    /// <param name="name">The extension's name, such as <c>autoscaler</c>.</param>
    /// <param name="value">The extension's options, as JSON that platform's provider reads.</param>
    /// <returns>This definition.</returns>
    /// <exception cref="System.ArgumentException">
    /// <paramref name="platform"/> or <paramref name="name"/> is <see langword="default"/>, or
    /// <paramref name="value"/> is the default <see cref="JsonElement"/>.
    /// </exception>
    IApplicationResourceDefinition WithExtension(PlatformName platform, ExtensionName name, JsonElement value);
}
