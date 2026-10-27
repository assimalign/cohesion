using System;
using System.Collections;
using System.Collections.Generic;
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
    internal static bool IsExternalFunctionFailure(Exception error, CancellationToken token) => error is not (DatabaseException or OutOfMemoryException or InsufficientExecutionStackException)
        && !(error is OperationCanceledException && token.IsCancellationRequested);
    internal static GraphPlanningException FunctionFailed(GraphFunctionName name, Exception error, Location? location = null)
        => new("CG05021", string.Format(CultureInfo.CurrentCulture, _failed, name, error.Message), location, error);
    internal static GraphPlanningException FunctionContract(GraphFunctionName name, Location? location = null)
        => new("CG05021", string.Format(CultureInfo.CurrentCulture, _failed, name, ErrorMessages.FunctionContractInvalid), location);

    internal static void ValidateFunctionValue(GraphFunction function, object? value, GraphFunctionType type, bool argument = false)
    {
        try { GraphCompositeEncoding.Measure(value); }
        catch (DatabaseTypeException error) when (argument)
        { throw new GraphPlanningException("CG02013", error.Message, innerException: error); }
        catch (DatabaseTypeException error) { throw FunctionFailed(function.Name, error); }
        if (!type.Accepts(value) || !Finite(value))
        {
            if (argument) { throw new GraphPlanningException("CG02013", string.Format(CultureInfo.CurrentCulture, ErrorMessages.FunctionValue, function.Name)); }
            throw FunctionContract(function.Name);
        }
    }

    // Measure has already enforced depth/count bounds, including cyclic containers.
    private static bool Finite(object? value)
    {
        if (value is double number) { return double.IsFinite(number); }
        if (value is float single) { return float.IsFinite(single); }
        if (value is IReadOnlyDictionary<string, object?> map)
        { foreach (var child in map.Values) { if (!Finite(child)) { return false; } } }
        else if (value is IList list)
        { foreach (var child in list) { if (!Finite(child)) { return false; } } }
        return true;
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
