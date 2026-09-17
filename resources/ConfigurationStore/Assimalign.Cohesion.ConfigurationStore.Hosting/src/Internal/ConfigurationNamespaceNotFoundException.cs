using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting;

internal sealed class ConfigurationNamespaceNotFoundException(string detail) : ResourceCommandRejectedException(detail);
