# Assimalign.Cohesion.ApplicationModel

Core-only contracts for declaring a Cohesion application, producing a portable realization
model, importing resources owned by another application, and composing several application
models through one gateway. The package contains no hosting, transport, configuration, or
platform implementation and remains NativeAOT-safe.

The gateway SDK records its build-selected identity as
`[assembly: CohesionApplication("appa")]` for build and tooling inspection. This metadata is
descriptive only: generated `Gateway.CreateBuilder(args)` passes the same name directly to
`Application.CreateBuilder(ApplicationName, args)`, and runtime code does not reflect the attribute.

## Build and run one model

```csharp
IApplicationBuilder builder = Application
    .CreateBuilder(ApplicationName.Parse("appa"), args)
    .UseGateway(gateway);

var identityDeclaration = new ExternalResourceDeclaration(
    "identity-hub",
    ApplicationName.Parse("identity"),
    ["https"],
    optional: false,
    manifest: identityManifest);
IApplicationResourceDescriptor identity = builder.RemoteReference(
    identityDeclaration,
    remote => remote.File("imports/identity/export.json"));

IApplicationResourceDescriptor api = builder.AddResource(apiManifest);
api.DependsOn(identity);

await builder.Build().RunAsync();
```

`ExternalResourceDeclaration` is the immutable build-produced description of a boundary
crossing: owner application, consumed endpoint names, optionality, the embedded target manifest,
and (when available) its same-application manifest closure. It also carries canonical manifest
and closure hashes. `RemoteReference(...)` adds or rebinds that declaration as an ordinary graph
node and returns its `IApplicationResourceDescriptor`; a manifest-less overload accepts a name.
Because that overload intentionally has no build-time owner or hash, its selected file/gateway is
the authority and resolution validates the named resource against that export's embedded model.

The base package supplies code bindings for:

- `Gateway(Uri|string)` — resolve an application export through `IControlPlaneClient`.
- `Endpoint(name, Uri|string)` — use one or more static endpoints.
- `File(path)` — read an `ApplicationExportDocument` from disk.
- `Bind(IExternalResourceResolver)` — attach a platform or application-provided resolver.

Runtime binding precedence is deterministic: command-line `--external` wins over process
environment, process environment wins over the C# `RemoteReference` binding, and an external with
none of those bindings uses the unresolved resolver. Supported command-line forms are
`name=<endpoint>=<url>`, `name=<url>` when the declaration consumes at most one endpoint,
`name=file:<path>`, and `name=gateway:<url>`. Process environment uses
`Cohesion__External__<name>__File`, `...__Gateway`, or
`...__Endpoints__<endpoint>`; colon-separated aliases are accepted too.

`Gateway(...)` is only a typed binding in this package. A caller or gateway must supply an
`IControlPlaneClient`; the HTTP implementation and endpoint hosting are separate work.
Platform-specific bindings such as Kubernetes import are contributed outside this package.

## External lifecycle

`IExternalResourceResolver.ResolveAsync` returns an immutable `ExternalResourceResolution` with
observed endpoints and, when known, the provider's manifest hash and export schema version. The
gateway base owns the lifecycle policy:

- resolved with every referenced endpoint -> `Running` and the endpoints become observable;
- optional and unresolved -> `Skipped`;
- required and unresolved -> remains `Starting` until its readiness budget expires, then reports
  `Failed`, aborts startup, and blocks dependents;
- resolved without a referenced endpoint -> `Failed`, with expected/observed hashes and schema
  versions in the diagnostic;
- a changed manifest hash or reported export schema version with compatible referenced endpoints -> `Running` with a
  `ManifestDrift` warning, never a readiness failure.

Resolution is part of each reconcile pass. Stopping or deleting an external only detaches the
local observation; it does not mutate the application that owns the resource.

## Development realization

`--realize <external>` changes a declared external back into locally realized resources only when
the selected environment is `Development` and the selected gateway identity is `local`,
`inprocess`, or `docker`. The declaration must include its manifest. Realization walks the
reachable same-application manifests in the embedded closure; crossings from that closure into a
different application remain external. The realized resources retain their owning application
name for runtime naming while joining the current gateway's process set. Other environments and
gateway identities reject `--realize` during `Build()`.

## Portable documents

`ApplicationModelDocument` (`cohesion/model/v1`) serializes the immutable graph: application,
environment, gateway/owner intent, run mode, flags, dependencies, manifests, platform-neutral
plans, and each external's declaration, embedded closure, realization state, and portable
static/file/gateway binding. `--mode describe` writes this document without contacting the
selected gateway. An arbitrary resolver supplied with `Bind(...)` is intentionally serialized as
unbound; its platform integration must bind it again in the importing gateway. Static, file, and
gateway bindings round-trip without that loss.

`ApplicationExportDocument` (`schemaVersion: 1`) adds the application version, optional public
JWK, canonical manifest hashes, observed internal/public endpoint addresses, and the complete
portable model. `Create`, `Parse`, `Load`, `Save`, and `ToModel` validate the document and use the
package's source-generated JSON context. `export.json` is the conventional filename; this
contract library does not choose a storage location or serve it over HTTP.

## Compose an application set

```csharp
IApplicationSet set = Application.CreateSet(sharedGateway, args)
    .AddApplication(new ApplicationDeclaration(
        ApplicationName.Parse("identity"),
        ApplicationModelResolvers.ControlPlane(
            "Identity.Gateway.exe",
            "imports/identity/export.json")))
    .AddApplication(new ApplicationDeclaration(
        ApplicationName.Parse("appa"),
        ApplicationModelResolvers.File("imports/appa/export.json")));

await set.RunAsync();
```

An application set resolves every member at run start in declaration order, verifies that each
resolver returned the declared application, applies command-line/environment external overrides,
then sends the ordered models to one `IMultiModelApplicationGateway`. `ControlPlane(...)` invokes
the member executable with `--mode describe` in Development and reads its exported model in other
environments. `Executable`, `File`, and `Gateway` resolvers are also available directly.

For Development `--realize`, executable resolution first describes each member, then re-describes
only members that declare the requested external. File/control-plane imports accept the request
only when their exported model already records that external as realized; the set validates every
requested name once across all members.

The set supports `Run`, `Apply`, and `Teardown`. The shared gateway owns one lifecycle session and
must isolate state by `(application, resource)` so equal resource names/identifiers in different
models cannot collide. This package defines that composition seam; SDK-generated
`Applications.<Name>`, HTTP control-plane hosting, and Kubernetes export/import are not claimed by
this implementation.

See [docs/DESIGN.md](docs/DESIGN.md) for lifecycle rationale and package boundaries.
