# SchedulerApplication

Namespace: Assimalign.Cohesion.Scheduler.Hosting

Assembly: Assimalign.Cohesion.Scheduler.Hosting

SchedulerApplication.CreateBuilder(args) creates the internal runtime builder and discovers an enabled resource control-plane registration from the entry assembly. A null argument array throws ArgumentNullException.

Build materializes caller services, validates schedule bindings, installs the http control-plane service when an ambient endpoint exists, and adds the provider execution service. RunAsync owns the full shared-host lifecycle.
