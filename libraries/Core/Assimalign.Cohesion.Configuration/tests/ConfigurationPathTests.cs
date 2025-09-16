namespace Assimalign.Cohesion.Configuration.Tests;

public class ConfigurationPathTests
{
    [Fact]
    public void MixedFormatEqualityTest()
    {
        Path path1 = "/key1.key2\\key3[0]:key4";
        Path path2 = "/key1/key2:key3[0]\\key4";

        Assert.Equal(path1, path2);

        //var entry = new ConfigurationEntry(key2)
        //{
        //    Value = 23
        //};
    }

  
}