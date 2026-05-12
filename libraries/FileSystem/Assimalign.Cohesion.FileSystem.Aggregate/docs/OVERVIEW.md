# Assimalign.Cohesion.FileSystem.Aggregate

## Summary

Defines the planned aggregate file-system implementation that should compose multiple underlying IFileSystem backends behind one contract.

## Current Evaluation

- Status: Partial
- Production source files: 1; key type candidates discovered: 1; test files discovered: 1.
- Project references: Assimalign.Cohesion.FileSystem
- Package references: None
- NotImplementedException markers: 21

## Primary Responsibilities

- AggregateFileSystem is intended to remain an IFileSystem so callers do not need a second API surface.
- A completed implementation would need routing rules for lookups, enumeration, and change notifications.
- Today the class mostly marks the intended seam for future composition work.

## Key Types

- AggregateFileSystem

## Source Layout

- No source subfolders were discovered.
