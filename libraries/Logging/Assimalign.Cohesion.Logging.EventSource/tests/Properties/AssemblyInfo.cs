using Xunit;

// EventListeners and EventSources are process-wide: one test's listener sees every source any other test
// creates, and an event source's enabled level is the union of every listener's. Each test also uses its
// own uniquely named source, but running the suite serially keeps "this source is not enabled" assertions
// from observing a listener another test left attached for an instant.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
