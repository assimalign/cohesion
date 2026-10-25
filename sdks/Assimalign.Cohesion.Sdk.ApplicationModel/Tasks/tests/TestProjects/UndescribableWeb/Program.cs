using System;

// Fails before it builds its host, the way a resource that needs a live dependency at startup does.
throw new InvalidOperationException("The undescribable fixture needs a live dependency before it builds its host.");
