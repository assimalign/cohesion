using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.ApiManager.Hosting.ApplicationModel;

/// <summary>
/// Configures the ApiManager resource that <c>builder.Services.AddResource(name, ...)</c> registers:
/// the platforms it runs on and the actions the application adds, through the options every area's
/// resource shares.
/// </summary>
/// <remarks>
/// <para>
/// A platform's hosting package opts the resource into its platform, such as
/// <c>resource =&gt; resource.UseInProcess()</c>; the platform owns how the instance reaches its
/// gateway, so nothing here configures a transport.
/// </para>
/// <para>
/// The resource exposes no actions of its own yet. The application adds its own with
/// <see cref="ApplicationInstanceOptions.AddAction(IApplicationInstanceAction, System.Text.Json.Serialization.JsonSerializerContext)"/>,
/// and each action's content is written as JSON through the source-generated context it came with.
/// </para>
/// </remarks>
public sealed class ApiManagerResourceOptions : ApplicationInstanceOptions
{
    internal ApiManagerResourceOptions(ResourceName name)
    {
        Name = name;
    }

    /// <summary>
    /// Gets the resource's name.
    /// </summary>
    public ResourceName Name { get; }
}
