using System;
using System.Diagnostics;
using System.Diagnostics.Tracing;

using Assimalign.Cohesion.Database.Sql.Catalog;
using Assimalign.Cohesion.Database.Sql.Schema;

namespace Assimalign.Cohesion.Database.Sql.Internal;

internal sealed partial class SqlSchemaProvisioner
{
    // Apply/Verify own the admission gate throughout this trace. The deferred trace
    // belongs to the single claimed worker, after ownership was transferred. Both
    // owners retain the joint plan identity so a listener can attach after entry.
    private bool _jointTraceEntered;
    private SqlProvisioningTrace? _trace;
    private SqlProvisioningTrace? _deferredTrace;

    private void BeginTrace(SqlCompiledSchema schema, string mode)
    {
        _jointTraceEntered = false;
        _trace = null;
        if (SqlDatabaseEventSource.Log.IsEnabled())
        {
            _trace = new SqlProvisioningTrace(schema);
            _trace.Started = SqlDatabaseEventSource.Log.ProvisioningStart(_database, schema, mode);
        }
    }

    private void TraceStage(string kind, string code = "", int position = 0, int stepCount = -1)
    {
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
        _jointTraceEntered = false;
        if (_trace is { } trace)
        {
            _trace = null;
            SqlDatabaseEventSource.Log.ProvisioningFailed(_database, trace);
            SqlDatabaseEventSource.Log.ProvisioningStop(_database, trace);
        }
    }

    private void BeginDeferredTrace(SqlDeferredMigration migration, int index)
    {
        _jointTraceEntered = true;
        if (SqlDatabaseEventSource.Log.IsEnabled())
        {
            _deferredTrace = CreateDeferredTrace(migration, migration.Intent.Steps[index]);
            _deferredTrace.RowsProcessed = 0;
            _deferredTrace.TotalRows = -1;
            _deferredTrace.Started = SqlDatabaseEventSource.Log.DeferredStepBound(_database, _deferredTrace);
        }
    }

    private SqlProvisioningTrace CreateDeferredTrace(SqlDeferredMigration migration, SqlCatalogMigrationStep step)
    {
        var progress = _database.Provisioning;
        return new SqlProvisioningTrace(migration.Schema)
        {
            Kind = step.Kind, Table = step.Table, ObjectName = step.ObjectName,
            Position = step.Position, StepCount = migration.ProvisioningStepCount,
            RowsProcessed = progress?.RowsProcessed ?? -1,
            TotalRows = progress?.TotalRows ?? -1,
        };
    }

    private void TraceDeferredProgress(SqlDeferredMigration migration, SqlCatalogMigrationStep step, long? processed, long? total)
    {
        if (_deferredTrace is null && SqlDatabaseEventSource.Log.IsEnabled(EventLevel.Verbose, EventKeywords.None))
        {
            _deferredTrace = CreateDeferredTrace(migration, step);
        }
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

    private void EndDeferredTrace(SqlDeferredMigration migration, int index, string outcome)
    {
        // The existing step reference distinguishes cancellation before entry and
        // finalization after completion from an entered step whose Bound was missed.
        if (!_jointTraceEntered)
        {
            return;
        }
        if (_deferredTrace is null && SqlDatabaseEventSource.Log.IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            _deferredTrace = CreateDeferredTrace(migration, migration.Intent.Steps[index]);
        }
        _jointTraceEntered = false;
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
        // Attribute failure to the joint plan, including marker-only reconciliation.
        SqlDatabaseEventSource.Log.DeferredStepFailed(_database, _deferredTrace ?? new SqlProvisioningTrace(migration.Schema)
        {
            Kind = index < migration.Steps.Count ? migration.Intent.Steps[index].Kind : "Finalize",
            Table = index < migration.Steps.Count ? migration.Intent.Steps[index].Table : string.Empty,
            ObjectName = index < migration.Steps.Count ? migration.Intent.Steps[index].ObjectName : string.Empty,
            Position = index < migration.Steps.Count ? migration.Intent.Steps[index].Position : 0,
            StepCount = migration.ProvisioningStepCount,
            RowsProcessed = _database.Provisioning?.RowsProcessed ?? -1,
            TotalRows = _database.Provisioning?.TotalRows ?? -1,
        }, failure);
    }
}
