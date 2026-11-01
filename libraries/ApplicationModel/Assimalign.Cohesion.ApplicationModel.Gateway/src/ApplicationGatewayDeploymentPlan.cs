namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// What an infrastructure provider would do to deploy a resource, and the revision it would run.
/// </summary>
/// <param name="Change">What deploying the resource would change.</param>
/// <param name="Revision">
/// An opaque fingerprint of what the provider deploys for the resource beyond the model, such as its
/// image and the provider's own configuration, leaving out the values of the resource's settings and
/// its extensions; empty when the provider deploys nothing the model does not say.
/// </param>
public sealed record ApplicationGatewayDeploymentPlan(ApplicationGatewayDeploymentChange Change, string Revision);
