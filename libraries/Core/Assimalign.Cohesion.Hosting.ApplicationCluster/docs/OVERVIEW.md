# Assimalign.Cohesion.Hosting.ApplicationCluster

## Summary

Defines the early abstraction set for coordinating multiple application resources as a cluster.

## Current Evaluation

- Status: Implemented
- Production source files: 9; key type candidates discovered: 7; test files discovered: 1.
- Project references: Assimalign.Cohesion.Hosting
- Package references: None
- NotImplementedException markers: 0

## Primary Responsibilities

- IApplicationCluster and IApplicationClusterBuilder describe the outer orchestration surface.
- ResourceId and ResourceName give cluster members stable identities and names.
- The lack of implementation types suggests this package is a contract layer for future cluster runtimes.

## Key Types

- ApplicationEvent
- IApplicationCluster
- IApplicationClusterBuilder
- IApplicationClusterHostResource
- IApplicationClusterResource
- ResourceId
- ResourceName

## Source Layout

- src/Abstractions
- src/ValueObjects
