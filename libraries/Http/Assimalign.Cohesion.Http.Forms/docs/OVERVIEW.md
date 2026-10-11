# Assimalign.Cohesion.Http.Forms

Streaming parsers and collections for `application/x-www-form-urlencoded`
and `multipart/form-data`, layered on the core Http contracts. Form feature
state lives on `IHttpContext.Features`. Read it with
`await context.ReadFormAsync(cancellationToken)`; a parsed or pre-attached
form is available through `request.Form`. Web.Forms provides the pipeline
middleware that parses eagerly and maps parsing failures to HTTP responses.

## Repeated fields

For `tag=first&tag=&tag=last`, or three multipart scalar parts named `tag`,
`form["tag"]` contains `"first"`, `""`, and `"last"` in that order.
Enumerate it or use its integer indexer and `Count` for every occurrence.
`form["tag"].Value` reads `"first"`. A comma in one value is not a separator.
Collection `Count` counts distinct scalar keys, and `Files` is separate.
`HttpFormCollection.Add` appends all supplied values to the named field.

Both encodings now follow the same first-scalar and multi-value rules
(decision 31). This changes preview.1's multipart last-value and urlencoded
comma-joined scalar behavior. See [DESIGN](DESIGN.md) for compatibility,
consumer effects, parsing limits, uploaded file ownership, and AOT posture.
