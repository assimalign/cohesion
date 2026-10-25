using System;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Internal;

internal sealed record ResourceManagementRequestReceipt(string Digest, Guid OperationId);
