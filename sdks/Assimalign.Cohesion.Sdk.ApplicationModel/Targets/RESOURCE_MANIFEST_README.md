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

The `commands` array lists the command kinds the resource's default control plane accepts, one
entry per distinct `CohesionCommand` kind. An entry takes one of two forms:

```json
"commands": [
  { "kind": "secretstore.add-secret", "requiresInputResolver": true },
  "secretstore.issue-certificate"
]
```

A kind is a string unless its item sets `RequiresInputResolver="true"`; only then is it written as
the object. `RequiresInputResolver` is the one metadata `CohesionCommand` accepts: `true` or
`false`, empty meaning `false`. Other metadata, or any other flag value, fails the build. Kinds are trimmed,
deduplicated ordinally, and sorted, and a kind declared more than once must carry the same
`RequiresInputResolver` value each time. Because only a set flag changes the written form, a
manifest that flags no command is the same string array it was before the object form existed.
Readers (`ResourceManifest` and the `Sdk.Gateway` manifest reader) accept both forms and reject an
object with any other property, a missing, empty, or non-string `kind`, or a non-boolean
`requiresInputResolver`.

The flag means the resource accepts the command only in resolved form: the delivering gateway must
rewrite the declared payload through an `IResourceCommandInputResolver` for that kind, for example
replacing a `parameter:<name>` or `<store>:<key>` source with the value it names. An application
that declares a flagged command therefore needs a resolver for the kind in its
`Providers.CommandInputs`, and `Build()` (or an application set starting that member) fails
without one. The error names the command, its key, and the target resource; for the application's
own resource it names the `Assimalign.Cohesion.<Kind>.ApplicationModel.Orchestration` package and
its `Use<Kind>(...)` verb, and for another application's resource it asks for a resolver in
`Providers.CommandInputs`. `Sdk.SecretStore` flags `secretstore.add-secret`, which
`UseSecretStore(...)` satisfies. A command whose kind is not flagged is still delivered as declared
when no resolver is registered. The SecretStore runtime still rejects an unresolved `add-secret`
at delivery, as defense in depth.
