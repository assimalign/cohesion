# EventHub ApplicationModel

Compose a build-produced manifest with `builder.AddEventHub(manifest, options)`. The typed descriptor supports dependency edges; the resource produces a platform-neutral Deployment plan. Generated resource code calls `EventHubResourceControlPlane.Create()`. All command kinds remain deferred.

See [DESIGN.md](DESIGN.md) for defaults and package boundaries.
