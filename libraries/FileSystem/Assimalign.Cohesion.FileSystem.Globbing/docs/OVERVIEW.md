# Assimalign.Cohesion.FileSystem.Globbing

## Summary

Provides include and exclude glob matching over the Cohesion file-system abstractions.

## Current Evaluation

- Status: Active
- Production source files: 9; key type candidates discovered: 8; test files discovered: 5.
- Project references: Assimalign.Cohesion.FileSystem
- Package references: None
- NotImplementedException markers: 0

## Primary Responsibilities

- GlobMatcherBuilder separates include and exclude pattern composition.
- IGlobMatcher operates over FileSystemPath, IFileSystemFile, and IFileSystemDirectory so it can be reused across backends.
- GlobMatchResults gives callers a focused result object instead of pushing match bookkeeping into each consumer.

## Key Types

- GlobContext
- GlobMatcher
- GlobMatcherBuilder
- GlobMatcherOptions
- GlobMatchResults
- IGlobContext
- IGlobMatcher
- IGlobMatcherBuilder

## Source Layout

- src/Abstractions
- src/Internal
- src/Properties
