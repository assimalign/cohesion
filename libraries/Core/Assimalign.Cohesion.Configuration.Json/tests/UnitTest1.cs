namespace Assimalign.Cohesion.Configuration.Json.Tests;

public class UnitTest1
{
    [Fact]
    public void Test1()
    {
        var builder = default(IConfigurationBuilder);

        builder.AddProvider(async context =>
        {
            return default(IConfigurationProvider);
        });
    }
}
