# Assimalign.Cohesion.Configuration.Json

## Summary

Adds JSON parsing and registration helpers to the configuration stack.

## Current Evaluation

- Status: Implemented
- Production source files: 5; key type candidates discovered: 5; test files discovered: 1.
- Project references: Assimalign.Cohesion.Configuration.FileSystem
- Package references: None
- NotImplementedException markers: 0

## Primary Responsibilities

- Builder extensions provide AddJsonFile and AddJsonStream so callers stay close to the core configuration experience.
- ConfigurationJsonProvider builds on the shared file-backed provider layer from Configuration.FileSystem.
- JsonConfigurationParser flattens JSON objects and arrays into composite configuration paths.

## Key Types

- ConfigurationBuilderExtensions
- ConfigurationJsonOptions
- ConfigurationJsonProvider
- ConfigurationJsonStreamProvider
- JsonConfigurationParser

## Source Layout

- src/Extensions
- src/Internal
