using System;

namespace Assimalign.Cohesion.Resilience.Telemetry;

internal static class TelemetryUtil
{
    internal const string PollyDiagnosticSource = "Assimalign.Cohesion.Resilience";

    internal const string ExecutionAttempt = "ExecutionAttempt";

    internal const string PipelineExecuting = "PipelineExecuting";

    internal const string PipelineExecuted = "PipelineExecuted";

    public static void ReportExecutionAttempt<TResult>(
        ResilienceStrategyTelemetry telemetry,
        ResilienceContextO context,
        OutcomeO<TResult> outcome,
        int attempt,
        TimeSpan executionTime,
        bool handled)
    {
        ReportAttempt(
            telemetry,
            new(handled ? ResilienceEventSeverity.Warning : ResilienceEventSeverity.Information, ExecutionAttempt),
            context,
            outcome,
            new ExecutionAttemptArguments(attempt, executionTime, handled));
    }

    public static void ReportFinalExecutionAttempt<TResult>(
        ResilienceStrategyTelemetry telemetry,
        ResilienceContextO context,
        OutcomeO<TResult> outcome,
        int attempt,
        TimeSpan executionTime,
        bool handled)
    {
        ReportAttempt(
            telemetry,
            new(handled ? ResilienceEventSeverity.Error : ResilienceEventSeverity.Information, ExecutionAttempt),
            context,
            outcome,
            new ExecutionAttemptArguments(attempt, executionTime, handled));
    }

    private static void ReportAttempt<TResult>(
        ResilienceStrategyTelemetry telemetry,
        ResilienceEvent resilienceEvent,
        ResilienceContextO context,
        OutcomeO<TResult> outcome,
        ExecutionAttemptArguments args)
    {
        if (telemetry.Enabled)
        {
            telemetry.Report(resilienceEvent, context, outcome, args);
        }
    }
}
