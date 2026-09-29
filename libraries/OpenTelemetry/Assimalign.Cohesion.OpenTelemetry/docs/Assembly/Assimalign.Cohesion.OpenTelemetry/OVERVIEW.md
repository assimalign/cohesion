# OpenTelemetry overview

`OtlpExporter.CreateLogExporter(OtlpExporterOptions)` returns `IOtlpLogExporter`. Set Endpoint and optional Headers, ResourceAttributes and HandlerFactory, enqueue `OtlpLogRecord` values, then flush/dispose asynchronously. Defaults: queue 2048, batch 512, interval 5 s, timeout 10 s. Caller owns the exporter. `ExportAsync` returns acceptance rather than propagating transport failures. Configuration errors throw at construction.

Public types: OtlpProtocol, OtlpSignal, OtlpExporterOptions, OtlpLogRecord, IOtlpLogExporter, OtlpExporter. Logs alone are implemented.
