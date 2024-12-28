# Mono-repo/Fixed Versioning

The type of versioning utilized for releases with the cohesion SDK is **mono-repo versioning**, specifically a **"fixed versioning"** or **"synchronized versioning"** strategy. This is commonly used in mono repositories, where all the packages in the repository share the same version number, even if changes occur only in a subset of the packages or just one package.

#### Pros:

- Simplifies version management across multiple packages.
- Reduces the risk of version conflicts and dependencies between packages.

#### Cons:

- Can lead to version number inflation for packages that haven’t changed.
- May cause confusion if consumers expect version increments to reflect actual changes in each package.