using System;
using System.Text.Json;

namespace Assimalign.Cohesion.Hosting.Resources.Internal;

/// <summary>
/// Error descriptions a section reports in its configuration status. They are status text on the
/// resource's authenticated control plane, never exception messages, and never carry configuration values.
/// </summary>
internal static class ResourceConfigurationErrors
{
    internal const string NullValue = "invalid-value: the section value is null.";

    internal static string InvalidValue(JsonException exception) => "invalid-value: " + exception.Message;

    internal static string ApplyFailed(Exception exception) =>
        "apply-failed: " + (exception.GetType().FullName ?? exception.GetType().Name);
}
