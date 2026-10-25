# Cohesion resource manifest

This package carries a generated Cohesion resource manifest for cross-repository gateway consumption.
It intentionally contains no runtime assemblies or package dependencies.

Reference it from a Cohesion project with `CohesionResourceReference`. The package's
`buildTransitive` props surface `cohesion/resource.json` as a `CohesionResourceManifest` item.

Source-built resources deliberately leave `artifact.image` empty. The gateway resolves
`ArtifactRef.Self` through the gathered `application.images.json` during artifact gathering;
the build never binds that index into generated `Manifests.<Name>` members. A populated
`artifact.image` is reserved for package manifests shipping a published image. The SDK pack path
currently ships the identity in `cohesion/image.json`, preserves the empty source field in the
portable manifest, and uses `CohesionImageRequired=true` to require a valid published image.
A gateway must accept the empty source field through validation before gather.

HTTPS endpoints with no Certificate metadata default to `Certificate="tls"`. The manifest task synthesizes the tls Secret mount at `/cohesion/mounts/tls` only when it is absent, before composite lifting; evaluated CohesionMount items remain unchanged. Explicit certificate names must identify Secret mounts (COHSDK010). Non-HTTPS certificate metadata is rejected, except reserved `public`, which creates no mount. Resource.g.cs registers endpoint-to-mount metadata for the ambient runtime. The certificate is one PEM file: leaf first, exactly one private-key block anywhere, optional chain including roots. Empty files mean absent material.

Composite lifting preserves the reserved `public` literal; named certificate mounts are prefixed together with their member endpoints.

Resources use `cohesion/resource/v2`, which contains resource facts only: no `commands` member and
no runtime member. A resource's runtime surface (its configuration sections, actions and tools) is a
separate `cohesion/runtime/v1` document the resource serves on its control plane and writes when its
host is built under `COHESION_DESCRIBE_PATH`; its desired configuration arrives as a deployment record
(`docs/RUNTIME_CONTRACT.md`, "Deployment record"). When the build that packed this package could
describe the resource, the package also carries that surface as `cohesion/resource.runtime.json`, so a
gateway can validate configuration for the resource without loading its code. The `buildTransitive`
props always give the `CohesionResourceManifest` item `RuntimeSurfacePath` metadata naming that file;
`Sdk.Gateway` attaches the surface to the generated `Manifests.<Name>` member when the file exists
and composes the resource without one when it does not, in which case the gateway's `Build()`
refuses deployment configuration for it.

The v1 manifest and its command metadata are no longer supported. All area SDKs emit v2 and use the
generic runtime control-plane factory, and a project that still declares `CohesionCommand` items
fails the build instead of having them ignored.
