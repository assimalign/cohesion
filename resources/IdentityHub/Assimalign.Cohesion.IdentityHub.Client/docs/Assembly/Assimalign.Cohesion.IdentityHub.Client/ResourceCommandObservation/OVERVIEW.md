# ResourceCommandObservation

Immutable result carrying Status and nullable Detail. Applied confirms a POST; Deleted confirms
a DELETE. Rejected carries the serving endpoint's actionable refusal. A success response need
not contain a JSON observation; the client derives the successful status from the HTTP method.
