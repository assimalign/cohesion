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
