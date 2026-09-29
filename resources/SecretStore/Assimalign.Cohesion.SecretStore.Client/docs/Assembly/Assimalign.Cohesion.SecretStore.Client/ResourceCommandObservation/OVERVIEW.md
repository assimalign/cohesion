# ResourceCommandObservation

Immutable value with Status and nullable Detail. ObserveCommandAsync returns Applied for a
successful POST; DeleteCommandAsync returns Deleted for a successful DELETE. Rejected carries
the endpoint's JSON refusal detail, or a named HTTP detail for a legacy empty response.
The type introduces no dependency on Hosting.Resources or another area client.
