namespace Assimalign.Cohesion.Configuration.Tests;

public class ConfigurationPathTests
{
    [Fact]
    public void MixedFormatEqualityTest()
    {
        Key key1 = "/key1.key2\\key3[0]:key4";
        Key key2 = "/key1/key2:key3[0]\\key4";

        Assert.Equal(key1, key2);

        var entry = new ConfigurationEntry(key2)
        {
            Value = 23
        };
    }

  
}