# IoTHub ApplicationModel

Compose a build-produced manifest with `builder.AddIoTHub(manifest, options)`. The typed descriptor supports dependency edges; the resource produces a platform-neutral Deployment plan. Generated resource code calls `IoTHubResourceControlPlane.Create()`. All command kinds remain deferred.

See [DESIGN.md](DESIGN.md) for defaults and package boundaries.
