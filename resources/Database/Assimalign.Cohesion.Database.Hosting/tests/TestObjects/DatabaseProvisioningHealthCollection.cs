using Xunit;

namespace Assimalign.Cohesion.Database.Hosting.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DatabaseProvisioningHealthCollection
{
    public const string Name = "Database provisioning health";
}
