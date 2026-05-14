# Assimalign.Cohesion.Dns.Client

Resolving DNS client for Cohesion. Provides recursive DNS resolution,
caching, and pluggable UDP / TCP / DoT / DoH / DoQ transports on top
of the `Assimalign.Cohesion.Dns` contracts.

Scaffolded at the opening of the L01.01.08 epic; implementation lands
in PRs 3 (transports + resolver). See
[`../Assimalign.Cohesion.Dns/docs/OVERVIEW.md`](../Assimalign.Cohesion.Dns/docs/OVERVIEW.md)
for the public contracts and
[`../Assimalign.Cohesion.Dns/docs/PROVENANCE.md`](../Assimalign.Cohesion.Dns/docs/PROVENANCE.md)
for the clean-room rules that apply to this area.
