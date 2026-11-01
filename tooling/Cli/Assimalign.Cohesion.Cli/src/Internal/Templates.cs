using System;
using System.Linq;

namespace Assimalign.Cohesion.Cli.Internal;

internal static class Templates
{
    internal const string PackageId = "Assimalign.Cohesion.Templates";
    internal static readonly string[] Names =
    [
        "cohesion-app", "cohesion-gateway",
        "cohesion-web", "cohesion-spa", "cohesion-database", "cohesion-secretstore",
        "cohesion-configurationstore", "cohesion-identityhub", "cohesion-rezolvr"
    ];
    private static readonly string[] _deferred =
    [
        "apimanager", "emailhub", "eventhub", "iothub", "loadbalancer", "logspace",
        "mediahub", "messagehub", "natgateway", "notificationhub", "scheduler", "vpngateway"
    ];

    internal static void Validate(string name, Arguments arguments)
    {
        if (!Names.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            string area = name.StartsWith("cohesion-", StringComparison.OrdinalIgnoreCase) ? name[9..] : name;
            string detail = _deferred.Contains(area, StringComparer.OrdinalIgnoreCase)
                ? "No template ships for this area yet (item 39 follow-up). " : "Unknown Cohesion template. ";
            throw new CliException(detail + "Available templates: " + string.Join(", ", Names) + ".");
        }
    }
}
