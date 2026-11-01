using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Hosting;

namespace Assimalign.Cohesion.Web.Hosting.ApplicationModel;

/// <summary>
/// Configures the Web resource that <c>builder.Services.AddResource(name, ...)</c> registers: the
/// platforms it runs on and the actions the application adds to the resource's own, through the
/// options every area's resource shares.
/// </summary>
/// <remarks>
/// <para>
/// A platform's hosting package opts the resource into its platform, such as
/// <c>resource =&gt; resource.UseInProcess()</c>; the platform owns how the instance reaches its
/// gateway, so nothing here configures a transport.
/// </para>
/// <para>
/// The resource's own actions come from the settings the application's features registered, such as
/// the <c>cors</c> action over the setting <c>builder.Services.AddCors(...)</c> registers. They and
/// their content types belong to the resource and are not configured here. The application adds its
/// own with <see cref="ApplicationInstanceOptions.AddAction(IApplicationInstanceAction, System.Text.Json.Serialization.JsonSerializerContext)"/>.
/// </para>
/// <para>
/// Control-plane content is JSON. Each action's input, output, and state types are written through
/// the source-generated context that declares them: the resource's own for its actions, and the one
/// an added action came with for that action.
/// </para>
/// </remarks>
public sealed class WebResourceOptions : ApplicationInstanceOptions
{
    internal WebResourceOptions(ResourceName name)
    {
        Name = name;
    }

    /// <summary>
    /// Gets the resource's name.
    /// </summary>
    public ResourceName Name { get; }
}
