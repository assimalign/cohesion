# OpenTelemetry design

## Intent and boundaries

This implements design item 31b ruling R-1: OTLP/HTTP JSON export of logs, using BCL HttpClient and System.Text.Json. Core is the only direct project dependency. Hosting.Telemetry owns the Logging adapter; this package is transport-only, with internal implementation types. There is no receiver or instrumentation SDK here, no gRPC and no protobuf binary encoder. Protobuf is deferred. Traces and metrics are reserved in OtlpSignal but deferred: libraries/Logging has no span or instrument primitive; ILoggerEntry (Abstractions/ILoggerEntry.cs) is its only structured diagnostics record.

## JSON mapping and AOT

OtlpJsonContext is source-generated. LowerCamelCase fields follow the OTLP protobuf-JSON mapping. timeUnixNano and observedTimeUnixNano are decimal string-encoded unsigned nanoseconds. traceId/spanId are lowercase hex (32/16 characters), deliberately the OTLP exception to protobuf bytes/base64. Absent identifiers are omitted. Other future bytes fields would use base64.

AnyValue has exactly one selected member: stringValue, boolValue, intValue (decimal string), doubleValue, arrayValue or kvlistValue. CLR integers, booleans, finite floating point values, arrays and string-keyed dictionaries map accordingly; unknown values use ToString, and null becomes the string "null". Nested values are bounded to depth 16 and arrays to 2048 elements. One resourceLogs entry contains process resource attributes; scopeLogs groups by Category with scope.name set to it. Requests POST application/json to the configured base plus /v1/logs.

The adapter maps Logging levels as follows (None is suppressed):

| Level | Number | Text |
|---|---:|---|
| Trace | 1 | TRACE |
| Debug | 5 | DEBUG |
| Information | 9 | INFO |
| Warning | 13 | WARN |
| Error | 17 | ERROR |
| Critical | 21 | FATAL |
| Event | 10 | EVENT |
| None | omitted | omitted |

Exceptions map to exception.type, exception.message and exception.stacktrace. Logging has no scope stack: BeginScope emits a seed entry and correlates Id/ParentId. The adapter exports those as cohesion.log.id/cohesion.log.parent_id attributes, without inventing spans or a scope API.

## Lifecycle and failure model

An example request (timestamps are strings and identifiers are hex):

```json
{"resourceLogs":[{"resource":{"attributes":[{"key":"service.name","value":{"stringValue":"web"}}]},"scopeLogs":[{"scope":{"name":"orders","version":"1"},"logRecords":[{"timeUnixNano":"1700000000000000000","observedTimeUnixNano":"1700000000000000100","severityNumber":9,"severityText":"INFO","body":{"stringValue":"created"},"attributes":[{"key":"text","value":{"stringValue":"hello"}},{"key":"flag","value":{"boolValue":true}},{"key":"count","value":{"intValue":"42"}},{"key":"ratio","value":{"doubleValue":1.25}},{"key":"items","value":{"arrayValue":{"values":[{"stringValue":"a"}]}}},{"key":"labels","value":{"kvlistValue":{"values":[{"key":"region","value":{"stringValue":"east"}}]}}},{"key":"nil","value":{"stringValue":"null"}}],"traceId":"abcdef0123456789abcdef0123456789","spanId":"abcdef0123456789"}]}]}]}
```

The mapping follows the [OTLP JSON specification](https://opentelemetry.io/docs/specs/otlp/#json-protobuf-encoding). This delivery uses the brief's required full-acceptance response `{"partialSuccess":{}}`; the current upstream full-success rule instead omits that field.

TryEnqueue never blocks or throws; a bounded channel drops oldest and counts drops. A periodic worker drains batches. Exports use at most three attempts within Timeout; permanent 4xx responses drop, while 429, 5xx and transport failures use bounded backoff and respect Retry-After. Partial-success rejections are counted and never retried. FailedExportCount counts failed attempts; DroppedCount counts queue overflow and abandoned shutdown records. Disposal drains within Timeout. No unbounded retry or collector network activity is required for host startup.

Hosting.Telemetry registers an IHostService to flush within the lesser of five seconds and host cancellation; factory disposal alone is insufficient. HandlerFactory supplies caller-owned trust policy to the exporter-owned HttpClient; redirects and cookies are disabled by default.

## gRPC deferral

The Cohesion HTTP server has no application/grpc framing or service dispatch. IHttpResponse.Trailers defaults to HttpTrailerCollection.Unsupported (libraries/Http/Assimalign.Cohesion.Http/src/Abstractions/IHttpResponse.cs); HTTP/1 and HTTP/2 read trailers but do not emit response trailers/grpc-status. LocalPlanController.CanRealize already refuses gRPC probes. Implementing gRPC requires that separate server capability, not just changing a content type.
