using System;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

public class ApplicationResourceDescriptorTests
{
    [Fact]
    public void DependsOn_LinksDependency()
    {
        IApplicationBuilder builder = Application.CreateBuilder().UseGateway(new FakeGateway());
        IApplicationResourceDescriptor a = builder.AddResource(new FakeResource("a"));
        IApplicationResourceDescriptor b = builder.AddResource(new FakeResource("b"));

        a.DependsOn(b);

        a.Dependencies.Count.ShouldBe(1);
        a.Dependencies.ShouldContain(b);
    }

    [Fact]
    public void DependsOn_Params_LinksAll()
    {
        IApplicationBuilder builder = Application.CreateBuilder().UseGateway(new FakeGateway());
        IApplicationResourceDescriptor a = builder.AddResource(new FakeResource("a"));
        IApplicationResourceDescriptor b = builder.AddResource(new FakeResource("b"));
        IApplicationResourceDescriptor c = builder.AddResource(new FakeResource("c"));

        a.DependsOn(b, c);

        a.Dependencies.Count.ShouldBe(2);
        a.Dependencies.ShouldContain(b);
        a.Dependencies.ShouldContain(c);
    }

    [Fact]
    public void DependsOn_SameDependencyTwice_IsDeduplicated()
    {
        IApplicationBuilder builder = Application.CreateBuilder().UseGateway(new FakeGateway());
        IApplicationResourceDescriptor a = builder.AddResource(new FakeResource("a"));
        IApplicationResourceDescriptor b = builder.AddResource(new FakeResource("b"));

        a.DependsOn(b).DependsOn(b);

        a.Dependencies.Count.ShouldBe(1);
    }

    [Fact]
    public void DependsOn_Self_Throws()
    {
        IApplicationBuilder builder = Application.CreateBuilder().UseGateway(new FakeGateway());
        IApplicationResourceDescriptor a = builder.AddResource(new FakeResource("a"));

        Should.Throw<InvalidOperationException>(() => a.DependsOn(a));
    }
}
