# Hosting

`Assimalign.Cohesion.Hosting` provides Cohesion's dependency-light host lifecycle contracts and
implementations. It also defines the transport-neutral `IHealthContributor` seam used to collect
named health snapshots from in-process components. The package depends only on Core and does not
own dependency injection, configuration, logging, aggregation, or HTTP delivery.
