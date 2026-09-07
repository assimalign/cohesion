# Realization-plan golden fixtures

These files pin the `cohesion/plan/v1` output produced by `GenericPlanner` for the
workload and resource shapes whose platform-neutral semantics must not drift.
`KindMatrixTests` validates and serializes the typed contract through
`ResourcePlanJsonContext`, preserving the NativeAOT-safe serialization boundary,
then compares the complete JSON tree with the checked-in document.

This directory is the `KindMatrixTests` fixture set vendored by `cohesion-platforms`
items 33/34 so each platform compiler can be conformance-tested without resource-area
code present. Each `Sdk.<Area>` default manifest shape belongs in this set as that area
lands; the initial contract gate covers the representative shapes below.

| Fixture | Contract shape |
| --- | --- |
| `web.json` | Deployment with endpoint services, public exposure, and one-to-one probes |
| `database.json` | StatefulSet with a per-replica volume claim and governing headless service |
| `generic-volume.json` | Kind-neutral volume mapping with the same StatefulSet storage semantics |
| `daemon-set.json` | DaemonSet with the long-running readiness gate |
| `job.json` | Job with clean `Stopped` as its satisfying readiness state |

Treat a fixture change as a realization-plan contract change. Review the JSON diff
alongside the planner change rather than regenerating it implicitly during a test run.
