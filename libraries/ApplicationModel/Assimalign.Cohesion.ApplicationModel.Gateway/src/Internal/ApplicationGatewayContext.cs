namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

// A gateway's context: its model, and the gateway itself as its instances' publisher.
internal sealed class ApplicationGatewayContext : IApplicationGatewayContext
{
    public ApplicationGatewayContext(IApplicationModel model, IApplicationInstancePublisher publisher)
    {
        Model = model;
        Publisher = publisher;
    }

    public IApplicationModel Model { get; }

    public IApplicationInstancePublisher Publisher { get; }
}
