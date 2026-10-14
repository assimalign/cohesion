using System.Runtime.CompilerServices;

// Friend access for the Http.WebSockets test project only: the handshake, accept-key and
// permessage-deflate negotiation rules are tested directly.
[assembly: InternalsVisibleTo("Assimalign.Cohesion.Http.WebSockets.Tests")]
