# Assimalign.Cohesion.ObjectMapping

## Summary

A small, profile-based object-to-object mapper. Mapping is configured explicitly
through profiles (no convention magic by default), built into an immutable mapper,
and replayed at run time. The scalar mapping path is reflection-free (compiled
getters and setters) for performance.

## Status

- Status: Active
- Project references: `Assimalign.Cohesion.Core`
- Package references: None
- Target framework: managed centrally (`build/Targets/Build.TargetFramework.props`)

## Primary Responsibilities

- `MapperBuilder` / `MapperFactoryBuilder` collect options and profiles before a
  mapper (or a named family of mappers) is created.
- `MapperProfile<TTarget, TSource>` and `MapperProfileDescriptor<TTarget, TSource>`
  are the configuration surface for each source/target pair.
- `Mapper` applies the recorded actions at run time and supports multi-source
  composition through multiple profiles targeting the same type.

## Key Types

- `IMapper` / `Mapper`
- `IMapperFactory`
- `IMapperFactoryBuilder` / `MapperFactoryBuilder`
- `IMapperBuilder` / `MapperBuilder`
- `IMapperProfile` / `MapperProfile<TTarget, TSource>`
- `MapperProfileDescriptor<TTarget, TSource>`
- `IMapperAction`
- `IMapperContext` / `MapperContext`
- `MapperOptions`
- `MapperException`
- `MapperIgnoreHandling`, `MapperCollectionHandling`

## Extension Surface

`MapperExtensions` adds the ergonomic mapping overloads on `IMapper`:

- `TTarget Map<TTarget, TSource>(TSource source)` — create a new target and map onto it.
- `TTarget Map<TTarget, TSource>(TTarget target, TSource source)` — map onto an existing target.
- `TTarget Map<TTarget>(params object[] sources)` — compose multiple sources into one target.
- `IEnumerable<TTarget> Map<TTarget, TSource>(IEnumerable<TSource> sources)` — project a sequence.

## Source Layout

- `src/Abstractions`
- `src/Exceptions`
- `src/Extensions`
- `src/Internal`
- `src/ValueTypes`

See `DESIGN.md` for the design rationale, error model, performance posture, and the
AOT constraint.
