namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// One setting of a resource as manifest data: a configuration value its instances receive with
/// their deployment, either given in the model or filled in from another resource's output.
/// </summary>
/// <remarks>
/// Exactly one of <see cref="Value"/> and <see cref="Output"/> is set. The gateway that runs the
/// resource fills an output in once the resource it belongs to is deployed, or from its parent's
/// answer when the output is the parent's, and its infrastructure provider passes every setting to
/// the resource's instances, for example as environment variables.
/// </remarks>
/// <param name="Name">The setting's name, unique among the resource's settings.</param>
/// <param name="Value">The value, or <see langword="null"/> when <paramref name="Output"/> supplies it.</param>
/// <param name="Output">
/// The output that supplies the value, of a resource this one depends on or of one of the parent
/// gateway's resources, or <see langword="null"/> when <paramref name="Value"/> is given.
/// </param>
public sealed record ApplicationResourceSetting(
    SettingName Name,
    string? Value,
    ApplicationResourceOutput? Output);
