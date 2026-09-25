using System;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

public class CohesionApplicationAttributeTests
{
    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Cohesion application metadata preserves its name")]
    public void Constructor_Name_PreservesName()
    {
        var attribute = new CohesionApplicationAttribute("appa");

        attribute.Name.ShouldBe("appa");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Cohesion application metadata rejects a null name")]
    public void Constructor_NullName_Throws()
    {
        ArgumentNullException error = Should.Throw<ArgumentNullException>(
            () => new CohesionApplicationAttribute(null!));

        error.ParamName.ShouldBe("name");
    }

    [Theory(DisplayName = "Cohesion Test [ApplicationModel] - Cohesion application metadata rejects a blank name")]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    public void Constructor_BlankName_Throws(string name)
    {
        ArgumentException error = Should.Throw<ArgumentException>(
            () => new CohesionApplicationAttribute(name));

        error.ParamName.ShouldBe("name");
    }

    [Fact(DisplayName = "Cohesion Test [ApplicationModel] - Cohesion application metadata is assembly-only and singular")]
    public void AttributeUsage_IsAssemblyOnlyAndSingular()
    {
        AttributeUsageAttribute? usage = Attribute.GetCustomAttribute(
            typeof(CohesionApplicationAttribute),
            typeof(AttributeUsageAttribute)) as AttributeUsageAttribute;

        usage.ShouldNotBeNull();
        usage.ValidOn.ShouldBe(AttributeTargets.Assembly);
        usage.AllowMultiple.ShouldBeFalse();
        usage.Inherited.ShouldBeFalse();
    }
}
