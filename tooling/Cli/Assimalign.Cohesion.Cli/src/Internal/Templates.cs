using System;
using System.Linq;

namespace Assimalign.Cohesion.Cli;

internal static class Templates
{
    internal const string PackageId = "Assimalign.Cohesion.Templates";
    internal static readonly string[] Names =
    [
        "cohesion-app", "cohesion-landing-zone", "cohesion-gateway", "cohesion-composite",
        "cohesion-web", "cohesion-spa", "cohesion-database", "cohesion-secretstore",
        "cohesion-configurationstore", "cohesion-identityhub", "cohesion-rezolvr"
    ];
    private static readonly string[] deferred =
    [
        "apimanager", "emailhub", "eventhub", "iothub", "loadbalancer", "logspace",
        "mediahub", "messagehub", "natgateway", "notificationhub", "scheduler", "vpngateway"
    ];

    internal static void Validate(string name, Arguments arguments)
    {
        if (!Names.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            string area = name.StartsWith("cohesion-", StringComparison.OrdinalIgnoreCase) ? name[9..] : name;
            string detail = deferred.Contains(area, StringComparer.OrdinalIgnoreCase)
                ? "No template ships for this area yet (item 39 follow-up). " : "Unknown Cohesion template. ";
            throw new CliException(detail + "Available templates: " + string.Join(", ", Names) + ".");
        }
        if (arguments.Has("--topology"))
        {
            if (!name.Equals("cohesion-landing-zone", StringComparison.OrdinalIgnoreCase))
            {
                throw new CliException("--topology is valid only for cohesion-landing-zone.");
            }
            // Validate a copy so the original spelling and argument order remain intact.
            string? topology = new Arguments(arguments.Remaining).TakeValue("--topology");
            if (topology is not ("single" or "federated"))
            {
                throw new CliException("--topology expects single or federated.");
            }
        }
    }
}
