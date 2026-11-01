# LogSpace Hosting

The LogSpace runtime composition entry point. Its OTLP/HTTP JSON ingest and query handlers are internal and unregistered; the resource integration, `Assimalign.Cohesion.LogSpace.Hosting.ApplicationModel`, registers none of them. See [Overview](docs/OVERVIEW.md) and [Design](docs/DESIGN.md).
