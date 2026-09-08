# Assimalign.Cohesion.Scheduler Design

## Design intent

The area root owns the scheduler contracts that feature packages compose against. `ISchedulerApplicationBuilder` is the contract-only application seam, while `ISchedulerApplication` supplies the host lifecycle expected by an executable resource. Existing schedule, job, and provider abstractions remain separate from application construction.

## Hosting isolation

The root references only shared foundation libraries. The concrete application builder, host, context, and options remain internal to `Assimalign.Cohesion.Scheduler.Hosting`; feature libraries must not reference that runtime module.

## Composition lifecycle

The builder accepts existing `IHostService` instances and factories that receive the newly created area `IHostContext`. Each factory is invoked once per `Build()`, and the resulting services are retained in registration order so the shared host starts them in that order and stops them in reverse. The collection is empty when callers register nothing, and the host environment remains production.

No scheduled work or scheduler service is registered by default. The seam remains composition-only without claiming that scheduling behavior is ready.

The two incomplete legacy runtime sources remain preserved on disk but are excluded from the filler assembly because they still depend on the absent `TickerOptionsBuilder` implementation and obsolete thread-abort behavior. Checked-in generated value-type bodies are likewise preserved but excluded from compilation because current source generation supplies those bodies. The scheduler program must replace those legacy internals before runtime scheduling behavior is enabled.

## AOT posture

The application contracts require no reflection, dynamic code generation, runtime assembly scanning, or container-based activation and remain safe for trimming and NativeAOT.
