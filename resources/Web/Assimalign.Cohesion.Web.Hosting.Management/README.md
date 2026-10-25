# Web hosting management

Opt-in runtime state adapters for CORS configuration and versioned TLS certificates. This package
is NuGet-only: it is not a member of App.Web or the private Web.Hosting closure shipped in other
resource frameworks. Add it explicitly when a Web resource advertises these controls.

Register `CorsResourceStateHandler.Create(cors)` or
`CertificateResourceStateHandler.Create(resolver, initialCertificate, initialReference)` as
`IResourceStateHandler` in the resource's services before Build. Handlers own their schemas and
publish their contracts and effective configured baselines at startup. No MSBuild state declarations
or generated state accessors are required. Use the same `CorsPolicyStore` in
the pipeline, and call the certificate adapter's `ConfigureTls` on the listener's existing TLS
options before creating the listener. Dispose owned adapters after listener shutdown.

See [design](docs/DESIGN.md) and [overview](docs/OVERVIEW.md). Tests run with
`dotnet test resources/Web/Assimalign.Cohesion.Web.Hosting.Management/tests/Assimalign.Cohesion.Web.Hosting.Management.Tests.csproj`.
