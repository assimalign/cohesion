using System;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Web.ApplicationModel.Tests;

public sealed class WebResourceGroupTests
{
    [Fact(DisplayName = "Cohesion Test [Web.ApplicationModel] - Resource groups: typed descriptors expose membership during authoring and after composition")]
    public void AddWeb_InsideResourceGroup_ShouldForwardAuthoringMembership()
    {
        // Arrange
        var gateway = new PlanningGateway();
        IApplicationBuilder builder = Application.CreateBuilder("appa", ["--environment", AppEnvironment.Keys.Local])
            .UseGateway(gateway);
        IWebResourceDescriptor? descriptor = null;

        // Act
        builder.AddResourceGroup("frontend", group =>
        {
            descriptor = group.AddWeb(WebManifestFactory.Create());
            descriptor.Plan.ShouldBeNull();
            descriptor.ResourceGroup.ShouldBe((ResourceGroupName)"frontend");
        });
        descriptor!.ResourceGroup.ShouldBe((ResourceGroupName)"frontend");
        IApplicationModel model = builder.Build().Model;

        // Assert
        gateway.ValidatedModel.ShouldBeSameAs(model);
        descriptor.ResourceGroup.ShouldBe((ResourceGroupName)"frontend");
        model.Descriptors.ShouldHaveSingleItem().ResourceGroup.ShouldBe((ResourceGroupName)"frontend");
        model.Plans.ShouldHaveSingleItem().ResourceGroup.ShouldBe((ResourceGroupName)"frontend");
        model.ResourceGroups.ShouldHaveSingleItem().Resources.ShouldHaveSingleItem().ShouldBe(descriptor.Resource.Name);
    }

    private sealed class PlanningGateway : IApplicationGateway
    {
        public ResourceName Name => "planning-test";
        public IApplicationModel? ValidatedModel { get; private set; }
        public void Validate(IApplicationModel model) => ValidatedModel = model;
        public Task StartAsync(IApplicationModel model, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReconcileAsync(IApplicationModel model, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UninstallAsync(IApplicationModel model, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
