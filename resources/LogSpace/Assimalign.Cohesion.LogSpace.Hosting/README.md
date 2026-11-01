# LogSpace Hosting

The LogSpace runtime composition entry point. Its OTLP/HTTP JSON ingest and query handlers stay internal and unregistered since the retired resource integration was removed on 2026-10-31; the resource integration, `Assimalign.Cohesion.LogSpace.Hosting.ApplicationModel`, registers none of them. See [Overview](docs/OVERVIEW.md) and [Design](docs/DESIGN.md).
