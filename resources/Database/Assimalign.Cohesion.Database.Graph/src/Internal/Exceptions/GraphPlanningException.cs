using System;
using System.Globalization;
using System.Threading;
using Assimalign.Cohesion.Database.Graph.Language;

using Assimalign.Cohesion.Database.Language;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Internal;

/// <summary>A planner rejection with a stable diagnostic and the offending node's source span.</summary>
internal sealed class GraphPlanningException : DatabaseException
{
    private static readonly string _failed = ErrorMessages.FunctionCallbackFailure;
    internal static bool IsExternalFunctionFailure(Exception error, CancellationToken token) => error is not (DatabaseException or OutOfMemoryException or InsufficientExecutionStackException or TimeoutException or System.IO.IOException or Assimalign.Cohesion.Database.Storage.StorageException)
        && !(error is OperationCanceledException && token.IsCancellationRequested);
    internal static GraphPlanningException FunctionFailed(GraphFunctionName name, Exception error, Location? location = null)
        => new("CG05021", string.Format(CultureInfo.CurrentCulture, _failed, name), location, error);
    internal static GraphPlanningException FunctionContract(GraphFunctionName name, Location? location = null)
        => new("CG05021", string.Format(CultureInfo.CurrentCulture, _failed, name), location);

    internal static void ValidateFunctionValue(GraphFunction function, object? value, GraphFunctionType type)
    {
        try { GraphCompositeEncoding.Measure(value); }
        catch (DatabaseTypeException error) { throw FunctionFailed(function.Name, error); }
        if (!type.Accepts(value)) { throw FunctionContract(function.Name); }
    }

    internal GraphPlanningException(string code, string message, Location? location = null, Exception? innerException = null)
        : base(message, [new Diagnostic
        {
            Code = code,
            Message = message,
            Severity = DiagnosticSeverity.Error,
            Location = location is null ? null : DiagnosticLocation.Absolute,
            Start = location?.Start,
            End = location?.End,
            Line = location?.StartLine ?? 1,
        }], innerException)
    {
    }

    internal GraphPlanningException At(Location? location) => Diagnostics[0].Start is not null || location is null
        ? this : new(Diagnostics[0].Code!, Message, location, InnerException);
}
