# Assimalign.Cohesion.FileSystem.InMemory

## Summary

Implements an in-memory IFileSystem backend and adds registration helpers for the shared file-system factory.

## Current Evaluation

- Status: Implemented
- Production source files: 13; key type candidates discovered: 8; test files discovered: 1.
- Project references: Assimalign.Cohesion.FileSystem
- Package references: None
- NotImplementedException markers: 0

## Primary Responsibilities

- InMemoryFileSystem is the public backend type and is configured through InMemoryFileSystemOptions.
- Internal node and stream types keep storage mechanics out of the public surface.
- Factory builder extensions make the backend easy to register alongside other file systems.

## Key Types

- InMemoryFileContent
- InMemoryFileStream
- InMemoryFileSystem
- InMemoryFileSystemDirectory
- InMemoryFileSystemDispatcher
- InMemoryFileSystemEventToken
- InMemoryFileSystemExtensions
- InMemoryFileSystemFile

## Source Layout

- src/Extensions
- src/Internal
