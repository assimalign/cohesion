# Assimalign.Cohesion.Rezolvr.Client

The `RezolvrCommandClient.Create(controlPlaneAddress, credential)` factory returns an
`IRezolvrCommandClient` for authenticated resource command delivery. The address includes the
control-plane prefix, normally `/cohesion/v1`; the client appends `/commands`.

This standalone NuGet package references only `Assimalign.Cohesion.Core`. It is not included in
the `App.Rezolvr` framework. ApplicationModel declares commands, Hosting executes them, and the
gateway depends on this client to cross the process boundary.

See [Design](docs/DESIGN.md), [Overview](docs/OVERVIEW.md), and
[API reference](docs/Assembly/Assimalign.Cohesion.Rezolvr.Client/OVERVIEW.md).
