# Assimalign.Cohesion.FileSystem.IsolatedStorage

## Summary

Introduces an isolated-storage-backed IFileSystem implementation for user-scoped persistence scenarios.

## Current Evaluation

- Status: Partial
- Production source files: 4; key type candidates discovered: 4; test files discovered: 1.
- Project references: Assimalign.Cohesion.FileSystem
- Package references: None
- NotImplementedException markers: 26

## Primary Responsibilities

- IsolatedStorageFileSystem is the single public backend type for the package.
- The design should mirror the same operations exposed by the core IFileSystem contract.
- Several members are unfinished, so the library currently describes a target direction more than a finished adapter.

## Key Types

- IsolatedStorageFileSystem
- IsolatedFilesystemDirectory
- IsolatedStorageFileSystemFile
- IsolatedStorageFileSystemInfo

## Source Layout

- src/Internal
