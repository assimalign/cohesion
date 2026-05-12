# Assimalign.Cohesion.ObjectMapping

## Summary

Implements a profile-based object mapping engine that applies mapping actions between source and target types.

## Current Evaluation

- Status: Active
- Production source files: 40; key type candidates discovered: 8; test files discovered: 18.
- Project references: Assimalign.Cohesion.Core
- Package references: None
- NotImplementedException markers: 3

## Primary Responsibilities

- MapperBuilder collects options and profiles before a mapper is created.
- MapperProfile and related descriptors are the configuration surface for each source and target pair.
- Mapper applies the recorded actions at runtime and supports multi-source composition through multiple profiles.

## Key Types

- IMapper
- IMapperAction
- IMapperActionDescriptor
- IMapperActionStack
- IMapperContext
- IMapperFactory
- IMapperProfile
- IMapperProfileBuilder

## Source Layout

- src/Abstractions
- src/Exceptions
- src/Extensions
- src/Internal
- src/Properties
