using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration.Tests;

public class ConfigurationBindingAttributeTests
{
    [Fact(DisplayName = "Cohesion Test [Configuration] - BindingAttribute: Type property is set")]
    public void BindingAttribute_Type_ShouldBeSet()
    {
        var attribute = new ConfigurationBindingAttribute(typeof(string));

        Assert.Equal(typeof(string), attribute.Type);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - BindingAttribute: Generic version sets type")]
    public void BindingAttribute_Generic_ShouldSetType()
    {
        var attribute = new ConfigurationBindingAttribute<List<string>>();

        Assert.Equal(typeof(List<string>), attribute.Type);
    }

    [Fact(DisplayName = "Cohesion Test [Configuration] - BindingAttribute: Can be applied to property")]
    public void BindingAttribute_ShouldBeApplicableToProperty()
    {
        var attr = typeof(TestClass)
            .GetProperty(nameof(TestClass.Items))!
            .GetCustomAttributes(typeof(ConfigurationBindingAttribute), false);

        Assert.Single(attr);
        Assert.Equal(typeof(List<string>), ((ConfigurationBindingAttribute)attr[0]).Type);
    }

    private class TestClass
    {
        [ConfigurationBinding<List<string>>]
        public IEnumerable<string>? Items { get; set; }
    }
}
