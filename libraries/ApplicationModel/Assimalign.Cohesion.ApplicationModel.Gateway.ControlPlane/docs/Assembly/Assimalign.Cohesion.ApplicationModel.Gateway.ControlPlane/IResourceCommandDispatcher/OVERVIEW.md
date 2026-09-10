# IResourceCommandDispatcher

The protocol-client boundary between a serving gateway and an area resource control plane.
Implementations identify one manifest resource kind and apply or delete the neutral
`ResourceCommand` envelope at the resource's observed default control-plane `System.Uri`. Each
dispatch receives the target resource's current bootstrap bearer credential from the serving
gateway.
