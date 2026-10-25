# Web hosting management overview

Web features own their configuration types and validation; Hosting.Resources owns the runtime surface
and the deployment record. This package joins the two for features whose configuration a gateway may
change while the resource runs. Today it has one section: `CorsPolicyStore.AsResourceConfiguration()`
publishes the live `Web:Cors` section, whose schema comes from `CorsJsonContext`.

The package is opt-in and NuGet-only. It adds no management dependency to Web.Hosting, Web.Cors or the
other resource frameworks, and it references neither Application Model nor a gateway. See
[design](DESIGN.md) for the dependency direction and the refusal model.
