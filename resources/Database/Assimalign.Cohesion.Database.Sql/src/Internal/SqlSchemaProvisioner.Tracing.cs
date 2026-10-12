using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Schema;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlSchemaProvisioner
{
    // Apply/Verify own the admission gate throughout this trace. The deferred trace
    // belongs to the single claimed worker, after ownership was transferred.
    private SqlMigrationScriptStep? _executingStep;
    private SqlProvisioningTrace? _trace;
    private SqlProvisioningTrace? _deferredTrace;

    private void BeginTrace(SqlCompiledSchema schema, string mode)
    {
        _executingStep = null;
        if (SqlDatabaseEventSource.Log.IsEnabled())
        {
            _trace = new SqlProvisioningTrace(schema);
            _trace.Started = SqlDatabaseEventSource.Log.ProvisioningStart(_database, schema, mode);
        }
    }

    private void TraceStage(string kind, string code = "", int position = 0, int stepCount = -1)
    {
        _executingStep = null;
        if (_trace is { } trace)
        {
            trace.Kind = kind;
            trace.Code = code;
            trace.Position = position;
            if (stepCount >= 0)
            {
                trace.StepCount = stepCount;
            }
        }
    }

    private void TraceStep(SqlMigrationScript script, SqlMigrationScriptStep step)
    {
        _executingStep = step;
        if (_trace is { } trace)
        {
            trace.Kind = step.Operation.Kind.ToString();
            for (int index = 0; index < script.Steps.Count; index++)
            {
                if (ReferenceEquals(script.Steps[index], step))
                {
                    trace.Position = index + 1;
                    break;
                }
            }
        }
    }

    private void TraceOutcome(string outcome, int stepCount = -1)
    {
        if (_trace is { } trace)
        {
            trace.Outcome = outcome;
            if (stepCount >= 0)
            {
                trace.StepCount = stepCount;
            }
        }
    }

    private void TraceStepFailure(SqlCompiledSchema schema, SqlMigrationScript script, Exception failure)
    {
        bool late = _trace is null;
        EnsureFailureTrace(schema);
        if (late && _trace is not null)
        {
            var executingStep = _executingStep;
            TraceStage("Finalize", SqlProvisioningCodes.StepFailed, stepCount: script.Steps.Count);
            if (executingStep is { } step)
            {
                TraceStep(script, step);
            }
        }
        TraceFailure(failure);
    }

    private void EnsureFailureTrace(SqlCompiledSchema schema)
    {
        if (_trace is null && SqlDatabaseEventSource.Log.IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            _trace = new SqlProvisioningTrace(schema) { Kind = "Unknown" };
        }
    }

    private bool CaptureTraceFailure(SqlCompiledSchema schema, Exception failure)
    {
        // Do not write from a filter: the throwing frame has not unwound yet.
        if (failure is not OutOfMemoryException)
        {
            EnsureFailureTrace(schema);
            TraceFailure(failure);
        }
        return false;
    }

    private void TraceFailure(Exception failure)
    {
        if (_trace is { } trace)
        {
            trace.Outcome = failure is OperationCanceledException ? "Cancelled" : "Error";
            if (failure is OperationCanceledException)
            {
                trace.Failure = null;
            }
            else
            {
                trace.Failure ??= failure;
            }
        }
    }

    private void EndTrace()
    {
        _executingStep = null;
        if (_trace is { } trace)
        {
            _trace = null;
            SqlDatabaseEventSource.Log.ProvisioningFailed(_database, trace);
            SqlDatabaseEventSource.Log.ProvisioningStop(_database, trace);
        }
    }

    private void BeginDeferredTrace(SqlDeferredMigration migration, SqlCatalogMigrationStep step)
    {
        if (SqlDatabaseEventSource.Log.IsEnabled())
        {
            _deferredTrace = new SqlProvisioningTrace(migration.Schema)
            {
                Kind = step.Kind, Table = step.Table, ObjectName = step.ObjectName,
                Position = step.Position, StepCount = migration.Intent.StepCount, RowsProcessed = 0,
            };
            _deferredTrace.Started = SqlDatabaseEventSource.Log.DeferredStepBound(_database, _deferredTrace);
        }
    }

    private void TraceDeferredProgress(long? processed, long? total)
    {
        if (_deferredTrace is not { } trace)
        {
            return;
        }
        trace.RowsProcessed = processed ?? -1;
        trace.TotalRows = total ?? -1;
        if (SqlDatabaseEventSource.Log.IsEnabled(EventLevel.Verbose, EventKeywords.None) &&
            (trace.LastProgress == 0 || Stopwatch.GetElapsedTime(trace.LastProgress) >= TimeSpan.FromSeconds(1)))
        {
            trace.LastProgress = Stopwatch.GetTimestamp();
            SqlDatabaseEventSource.Log.DeferredStepProgress(_database, trace);
        }
    }

    private void EndDeferredTrace(string outcome)
    {
        if (_deferredTrace is { } trace)
        {
            _deferredTrace = null;
            trace.Outcome = outcome;
            SqlDatabaseEventSource.Log.DeferredStepCompleted(_database, trace);
        }
    }

    private void FailDeferredTrace(SqlDeferredMigration migration, int index, Exception failure)
    {
        if (!SqlDatabaseEventSource.Log.IsEnabled(EventLevel.Error, EventKeywords.None))
        {
            return;
        }
        // Marker failure follows completed steps and must not blame the last successful DDL.
        SqlDatabaseEventSource.Log.DeferredStepFailed(_database, _deferredTrace ?? new SqlProvisioningTrace(migration.Schema)
        {
            Kind = index < migration.Steps.Count ? migration.Intent.Steps[index].Kind : "Finalize",
            Table = index < migration.Steps.Count ? migration.Intent.Steps[index].Table : string.Empty,
            ObjectName = index < migration.Steps.Count ? migration.Intent.Steps[index].ObjectName : string.Empty,
            Position = index < migration.Steps.Count ? migration.Intent.Steps[index].Position : 0,
            StepCount = migration.Intent.StepCount,
            RowsProcessed = _database.Provisioning?.RowsProcessed ?? -1,
            TotalRows = _database.Provisioning?.TotalRows ?? -1,
        }, failure);
    }
}
