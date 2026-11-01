# Web area overview

Web.Hosting is the Web area's runtime module: a plain host that composes the application's
services, servers, and pipeline. It carries no resource-management or health integration; Web
applications serve health through Web.Health. The 17 other area frameworks carry Web.Hosting
privately.

A Web application becomes a managed resource instance through Web.Hosting.ApplicationModel, the
area's one application-model integration and a member of App.Web:
`builder.Services.AddResource("orders-api", resource => resource.UseInProcess())`. Without a
gateway platform in its environment the application runs unmanaged. The area design records the
references both packages take.

- [Area project map](../../../resources/Web/README.md)
- [Area design](DESIGN.md)
- [Web.Hosting design](../../../resources/Web/Assimalign.Cohesion.Web.Hosting/docs/DESIGN.md)
- [Web.Hosting.ApplicationModel design](../../../resources/Web/Assimalign.Cohesion.Web.Hosting.ApplicationModel/docs/DESIGN.md)
