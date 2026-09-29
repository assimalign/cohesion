# Hosting telemetry overview

ResourceTelemetry.IsEnabled reads the resource's invocation environment. Configure(context, logging, out IHostService? lifetime) composes an existing builder (Web). Configure(context, out IHostService? lifetime) creates and returns an owned ILoggerFactory only when enabled (seventeen other hosts). Register lifetime before producers so reverse-order StopAsync drains them first. The overloads without lifetime remain available for callers that explicitly own factory disposal. Existing builders are never built by Configure.

Dependencies: Hosting, Hosting.Resources, Logging, Logging.Console and OpenTelemetry. ResourceContext.TryGetEnvironmentValue reads the invocation snapshot in both local and in-process topologies; process environment is never read directly. A gateway name and nonblank absolute HTTP(S) telemetry endpoint enable export. Unset protocol defaults to otlp-http. Invalid configuration throws, unreachable collectors do not.
