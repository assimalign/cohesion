# CORS and certificate state across instances

An enabled Web resource references Web.Hosting.Management and registers its configured adapters:

```csharp
CorsPolicyStore cors = CorsPolicyStore.Create(configuredCors);
builder.Services.AddSingleton<IResourceStateHandler>(CorsResourceStateHandler.Create(cors));
// Configure the listener with the same certificate adapter registered in services.
CertificateResourceStateHandler certificate = CertificateResourceStateHandler.Create(
    resolver, startupCertificate, configuredCertificateReference);
builder.Services.AddSingleton<IResourceStateHandler>(certificate);
certificate.ConfigureTls(tlsOptions);
```

The SDK exports resource/v2 with only the logical runtime management binding. It exports no commands,
state catalog or initial configuration. Startup adapters own their schemas and capture effective
configuration after appsettings and environment overrides have been applied. The CORS adapter and
`UseDynamicCors` share one CorsPolicyStore. Certificates expose immutable provider references.

ApplicationModel snapshots the opaque fragment and composes resource dependencies and deployment
plans. It neither starts Web nor imports CORS/TLS handler types. Kubernetes compiles the generic
plan and adds a private projected state file, workload identity and live per-pod addresses. The
gateway discovers contracts and configured baselines through the authenticated runtime endpoint
before readiness. Group membership is composed by ApplicationModel and remains logical metadata.

An authorized operator creates logical CORS desired state through one gateway endpoint:

```http
PUT /cohesion/v2/resources/api/state/web.cors/configuration
Content-Type: application/json
If-Match: "1"
Idempotency-Key: 60d2abb4-26dd-4b97-a55d-8e10a77cbb18

{"exists":true,"value":{"defaultPolicy":{"origins":["https://portal.example"]}}}
```

Use the current quoted ETag in If-Match when a slot already has desired state. The caller needs
`api/state/web.cors/write`; a legacy command grant gives no management access. A 202 response is a
durable receipt, with ETag and resource-scoped operation Location.

The gateway discovers ten individual pod targets, verifies their resource/process bindings and
reconciles the same revision to them. Nine successes and one failure report degraded progress.
An HPA-created eleventh process reads the last accepted protected projection before traffic
admission; the gateway subsequently observes it and updates convergence. The projection excludes
private operation receipts. Existing dependency URLs continue to use Kubernetes Service DNS.

Certificate updates use a separate state slot and permission:

```json
{
  "exists": true,
  "value": {
    "certificateRef": {
      "provider": "secrets",
      "key": "api-tls-bundle",
      "version": "13"
    }
  }
}
```

PUT this body to `/cohesion/v2/resources/api/state/web.tls.certificate/https`. Each process resolves
that exact version through its configured provider, atomically installs the selected certificate
and revision, and uses it for new handshakes. Existing TLS sessions remain established. A failed
resolution leaves the previous certificate selected. Private keys are never sent to management
clients. Mutable aliases such as latest and `*` are refused.

Use the current baseline/desired revision in If-Match; If-None-Match applies only to an uninitialized
slot. Reapplying a deployment preserves accepted management overrides. DELETE on a state slot, with
If-Match and Idempotency-Key, clears an override and restores the current explicit baseline.
A missing baseline does not imply a destructive reset. Read the full wire/ownership contract in
[resource state management](../../libraries/ApplicationModel/RESOURCE_MANAGEMENT.md).
