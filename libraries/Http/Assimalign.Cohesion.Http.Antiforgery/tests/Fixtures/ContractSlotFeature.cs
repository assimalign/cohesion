namespace Assimalign.Cohesion.Http.Antiforgery.Tests;

/// <summary>
/// An antiforgery feature installed by another party under the contract's slot name, as an
/// application-level registration does.
/// </summary>
internal sealed class ContractSlotFeature : IHttpAntiforgeryFeature
{
    public ContractSlotFeature(IHttpAntiforgery antiforgery)
    {
        Antiforgery = antiforgery;
    }

    public string Name => nameof(IHttpAntiforgeryFeature);

    public IHttpAntiforgery Antiforgery { get; }
}
