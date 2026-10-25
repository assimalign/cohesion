using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Hosting.Resources;

/// <summary>Requests process admission after all required desired state has been observed.</summary>
/// <param name="ProcessStartId">The verified process incarnation.</param>
/// <param name="States">The required slot revisions and digests.</param>
public sealed record ResourceStateAdmission(Guid ProcessStartId, IReadOnlyList<ResourceStateAdmissionSlot> States);
