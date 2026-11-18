using System;
using System.Globalization;

namespace Assimalign.Cohesion.Fabric.InProcess.Internal;

/// <summary>
/// Maps the in-process platform's reasons onto the fault catalog.
/// </summary>
/// <remarks>
/// An unknown reason is <see cref="FaultCode.PlatformError"/> with
/// <see cref="FaultDisposition.NeedsChange"/>: the in-process platform has no remote API whose
/// failures could clear by themselves, so retrying an unknown cause only loops.
/// </remarks>
internal sealed class InProcessFaultClassifier : IFabricFaultClassifier
{
    public FabricFault Classify(PlatformDetail detail, ResourceUid resource)
    {
        ArgumentNullException.ThrowIfNull(detail);

        return detail.Reason switch
        {
            InProcessFaultReasons.EntryPointThrew or InProcessFaultReasons.EntryPointExited => new FabricFault(
                FaultCode.WorkloadCrashing,
                FaultDisposition.NeedsChange,
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.WorkloadCrashed, resource),
                detail),
            InProcessFaultReasons.ProgramMissing => new FabricFault(
                FaultCode.ConfigInvalid,
                FaultDisposition.NeedsChange,
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.ProgramMissing, resource),
                detail),
            _ => new FabricFault(
                FaultCode.PlatformError,
                FaultDisposition.NeedsChange,
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.PlatformFailed, resource, detail.Reason),
                detail),
        };
    }
}
