# Gateway HTTPS sample

A non-packable, real Web.Hosting executable used by the gateway certificate acceptance tests. The gateway supplies an ambient HTTPS endpoint, a single PEM Secret mount named `tls`, and its transport trust bundle. It preserves the existing Web.TestHost fixture and its required mount/reference inputs.
