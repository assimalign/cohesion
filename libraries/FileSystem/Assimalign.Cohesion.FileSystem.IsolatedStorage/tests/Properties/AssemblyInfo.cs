using Xunit;

// IsolatedStorageFile.GetUserStoreForAssembly() returns the same physical store for every test
// in this assembly. Running the suite in parallel would let tests stomp on each other's data, so
// we serialize them. Test methods still get a fresh logical state because each test clears the
// store before exercising the file system.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
