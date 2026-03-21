# Assimalign.Cohesion.Configuration.EnvironmentVariables

## Summary

Supplies an environment-variable configuration source and provider for process and machine settings.

## Current Evaluation

- Status: Implemented
- Production source files: 3; key type candidates discovered: 3; test files discovered: 1.
- Project references: Assimalign.Cohesion.Configuration
- Package references: None
- NotImplementedException markers: 0

## Primary Responsibilities

- The options object only captures the filtering values needed for provider creation.
- The provider is responsible for enumerating environment variables and mapping them into configuration keys.
- The builder extension keeps the caller API consistent with the rest of the configuration provider packages.

## Key Types

- ConfigurationBuilderExtensions
- ConfigurationEnvironmentVariablesProvider
- ConfigurationEnvironmentVariablesOptions

## Source Layout

- src/Extensions
