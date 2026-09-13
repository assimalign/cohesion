# RezolvrCommandClient

Static factory `Create(Uri controlPlaneAddress, string credential)` returns an `IRezolvrCommandClient`.
The URI must be absolute HTTP(S), include the control-plane prefix, and contain no user information,
query or fragment. The credential must be nonblank. Invalid arguments throw argument exceptions.
Dispose the returned client when finished; creation performs no network I/O.
