# Runtime state contracts

The approved architecture is implemented through three independent ownership boundaries. The
current wire and registration guide is [resource state management](../../libraries/ApplicationModel/RESOURCE_MANAGEMENT.md).

| Owner | Contract | Responsibility |
| --- | --- | --- |
| Hosting.Resources | ResourceRuntimeManifest, ResourceStateDefinition | Producer-owned catalog, schemas, delivery scope and permitted stages |
| Hosting.Resources | IResourceStateHandler, IResourceControlPlane | Validate and apply one process-bound revision against actual feature state |
| ApplicationModel | ResourceManifest.Runtime, ResourcePlan.Runtime | Preserve opaque JSON while composing deployment and dependency descriptors |
| Gateway | IResourceManagementGateway | Logical state operations, durable receipts and dated convergence |
| Gateway | IResourceManagementStore / IProjectedResourceManagementStore | Conditional durable intent and optional atomic workload projection |
| Gateway | ResourceInstanceInventory, ResourceManagementTarget | Trusted platform identity, generation, address and scale observation |
| Gateway | IResourceStateProtocolClient | Versioned wire transport or direct InProcess adaptation |

ApplicationModel does not reference Hosting.Resources or interpret state schemas. Gateway wire
records are owned by the Gateway assembly; state-definition/handler CLR types stay with Resources.
Their common stateless canonical codec is explicitly compiled through the StateProtocol shared
source module, rather than a new shared foundation dependency or cross-assembly internals access.

## Definition example

```json
{
  "name": "web.tls.certificate",
  "payloadSchemaId": "urn:cohesion:web:tls-certificate:v1",
  "contractDigest": "sha256:<computed by the SDK>",
  "keySchema": { "enum": ["https"] },
  "payloadSchema": {
    "type": "object",
    "required": ["certificateRef"],
    "properties": {
      "certificateRef": {
        "type": "object",
        "required": ["provider", "key", "version"],
        "properties": {
          "provider": { "type": "string", "minLength": 1 },
          "key": { "type": "string", "minLength": 1 },
          "version": { "type": "string", "minLength": 1 }
        },
        "additionalProperties": false
      }
    },
    "additionalProperties": false
  },
  "scope": "everyInstance",
  "uses": ["orchestration", "management"],
  "supportsAbsence": false
}
```

The digest placeholder above is illustrative; the runtime adapter computes the canonical digest.
Definitions advertise possible mutations, without live URLs, pod selectors, credentials, request
IDs or applied state. Providers resolve exact certificate versions; private keys never enter JSON.

## Runtime and coordination

The runtime dispatcher freezes the registered handlers' catalog and configured baselines before host attachment.
It validates the target process, key, payload and value digest, then invokes a feature adapter.
The actual feature atomically stores value plus revision/digest. Older revisions are refused;
equal revision/content is idempotent. No per-request runtime replay ledger is added.

The gateway owns conditional intent, subject-scoped retry identity, resource incarnation, leases,
backend fencing, topology snapshots and operation history. `everyInstance` retains desired state
for current and future processes. `sharedState` uses one capable executor; failover advances the
fence, and backend enforcement/idempotency is a required adapter contract. Actions such as issuing
a fresh certificate remain separate from selecting an immutable certificate version.

New resource/v2 build manifests contain only a management binding, without commands, states or
initial values. The gateway discovers the catalog and configured baselines at startup. Resource
groups belong to ApplicationModel's composition document, not individual producer documents.

Explicit programmatic commands remain independent of the producer manifest and gain no management
grants or instance fan-out. The old resource/v1 manifest is retired without a compatibility reader.
Database source remains frozen and may no longer build; its owner will migrate separately rather
than retaining a Health shim or manifest workaround. No live Kubernetes deployment is implied by
hermetic platform tests.
