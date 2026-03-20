# Assimalign.Cohesion.Configuration.Json

## Summary

Adds JSON parsing and registration helpers to the configuration stack.

## Current Evaluation

- Status: Partial
- Production source files: 8; key type candidates discovered: 8; test files discovered: 1.
- Project references: Assimalign.Cohesion.Configuration.FileSystem
- Package references: None
- NotImplementedException markers: 11

## Primary Responsibilities

- Builder extensions provide AddJsonFile and AddJsonStream so callers stay close to the core configuration experience.
- Stream-based parsing is the natural center of the implementation, regardless of the backing file source.
- The coexistence of older and newer provider code shows that the package is evolving and still being consolidated.

## Key Types

- ConfigurationBuilderExtensions
- ConfigurationJsonEntry
- ConfigurationJsonProviderOld
- ConfigurationJsonSection
- ConfigurationJsonStreamProvider
- ConfigurationJsonStreamSource
- JsonConfigurationProvider
- JsonConfigurationProviderOptions

## Source Layout

- src/Extensions
