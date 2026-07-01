using System;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Tests;

public class ApplicationBuilderTests
{
    [Fact]
    public void AddResource_WithResource_ReturnsDescriptorWrappingResource()
    {
        IApplicationBuilder builder = Application.CreateBuilder().UseGateway(new FakeGateway());
        var resource = new FakeResource("dns");

        IApplicationResourceDescriptor descriptor = builder.AddResource(resource);

        descriptor.ShouldNotBeNull();
        descriptor.Resource.ShouldBeSameAs(resource);
    }

    [Fact]
    public void AddResource_DuplicateName_Throws()
    {
        IApplicationBuilder builder = Application.CreateBuilder().UseGateway(new FakeGateway());
        builder.AddResource(new FakeResource("dns"));

        Should.Throw<InvalidOperationException>(() => builder.AddResource(new FakeResource("dns")));
    }

    [Fact]
    public void Build_WithoutGateway_Throws()
    {
        IApplicationBuilder builder = Application.CreateBuilder();
        builder.AddResource(new FakeResource("dns"));

        Should.Throw<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void Build_WithGateway_Succeeds()
    {
        IApplicationBuilder builder = Application.CreateBuilder().UseGateway(new FakeGateway());
        builder.AddResource(new FakeResource("dns"));

        IApplication app = builder.Build();

        app.ShouldNotBeNull();
        app.Model.Resources.Count.ShouldBe(1);
    }

    [Fact]
    public void Build_WithCircularDependency_Throws()
    {
        IApplicationBuilder builder = Application.CreateBuilder().UseGateway(new FakeGateway());
        IApplicationResourceDescriptor a = builder.AddResource(new FakeResource("a"));
        IApplicationResourceDescriptor b = builder.AddResource(new FakeResource("b"));
        a.DependsOn(b);
        b.DependsOn(a);

        Should.Throw<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void Build_Model_ProjectsResourcesOneToOneWithDescriptors()
    {
        IApplicationBuilder builder = Application.CreateBuilder().UseGateway(new FakeGateway());
        builder.AddResource(new FakeResource("a"));
        builder.AddResource(new FakeResource("b"));

        IApplicationModel model = builder.Build().Model;

        model.Resources.Count.ShouldBe(model.Descriptors.Count);
        for (int i = 0; i < model.Descriptors.Count; i++)
        {
            model.Resources[i].ShouldBeSameAs(model.Descriptors[i].Resource);
        }
    }
}
