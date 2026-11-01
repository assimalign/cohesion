# Assimalign.Cohesion.Sdk.Web

Sdk.Web builds a Cohesion HTTP application with the base SDK's executable defaults and the App and
App.Web shared frameworks. It enables generated typed-handler interceptors and copies `wwwroot/**` into
build and publish output. An application makes itself a managed resource in `Program.cs` with
`builder.Services.AddResource(...)` from `Web.Hosting.ApplicationModel`, which App.Web carries.

Select `Assimalign.Cohesion.Sdk.Web` as the project SDK and pin both it and
`Assimalign.Cohesion.Sdk` to the same exact version in `global.json`, as described in the
[SDK consumption guide](../../../README.md#consumption). Configure the server and middleware through
`WebApplication.CreateBuilder(args)` in `Program.cs`.

NativeAOT Web applications default to `EventSourceSupport=true`, preserving delivery of the Web
server's operator diagnostics. Entry-point Web hosts forward their selected Web and HTTP transport
sources to configured logging sinks. This adds no sink, and an application's filter choices apply.

To explicitly remove EventSource delivery from a native publish, set the property in the project:

```xml
<PropertyGroup>
    <EventSourceSupport>false</EventSourceSupport>
</PropertyGroup>
```

The choice also disables an in-process EventListener and EventSource forwarding in that native
application. Server behavior and counter maintenance remain unchanged. See the
[design](DESIGN.md#nativeaot-event-delivery) for import precedence, measured native size costs, and the
independent SDK-evaluation and published-binary checks.
