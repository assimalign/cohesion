# Assimalign.Cohesion.Configuration.CommandLine

## Summary

Supplies a command-line configuration source and provider that translate process arguments into configuration entries.

## Current Evaluation

- Status: Implemented
- Production source files: 3; key type candidates discovered: 3; test files discovered: 1.
- Project references: Assimalign.Cohesion.Configuration
- Package references: None
- NotImplementedException markers: 0

## Primary Responsibilities

- ConfigurationBuilder extension methods are the user-facing entry point.
- ConfigurationCommandLineSource captures raw arguments and optional switch mappings.
- ConfigurationCommandLineProvider turns normalized arguments into configuration keys and values.

## Key Types

- ConfigurationBuilderExtensions
- ConfigurationCommandLineProvider
- ConfigurationCommandLineSource

## Source Layout

- src/Extensions
