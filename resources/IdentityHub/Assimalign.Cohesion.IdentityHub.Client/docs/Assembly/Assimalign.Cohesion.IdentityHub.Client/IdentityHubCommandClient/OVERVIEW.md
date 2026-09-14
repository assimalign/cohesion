# IdentityHubCommandClient

Static factory `Create(Uri controlPlaneAddress, string credential)` returns an `IIdentityHubCommandClient`.
The URI must be absolute HTTP(S), include the control-plane prefix, and contain no user information,
query or fragment. The credential must be nonblank. Invalid arguments throw argument exceptions.
Dispose the returned client when finished; creation performs no network I/O.

`IdentityHubCommandClient.Create(Uri controlPlaneAddress, string bearerToken, HttpMessageInvoker transport)`
accepts a caller-owned transport, including its TLS trust policy. Disposing the returned client
does not dispose that transport; the caller retains it until requests finish and disposes it
afterward. The two-argument factory still creates a transport owned and disposed by the client.
Both overloads perform the same endpoint and credential validation; a null supplied transport
throws `ArgumentNullException`. The client does not discover application trust.
