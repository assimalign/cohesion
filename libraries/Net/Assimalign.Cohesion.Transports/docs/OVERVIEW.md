# Assimalign.Cohesion.Transports

## Summary

Implements the shared transport model for client and server networking, including middleware pipelines and concrete TCP, UDP, and QUIC transports.

## Current Evaluation

- Status: Active
- Production source files: 63; key type candidates discovered: 8; test files discovered: 8.
- Project references: Assimalign.Cohesion.Core
- Package references: None
- NotImplementedException markers: 0

## Primary Responsibilities

- ClientTransport and ServerTransport define the shared connection lifecycle for protocols.
- TransportPipelineBuilder inserts middleware around ITransportConnection activity.
- Concrete TCP, UDP, and QUIC types reuse the shared contracts instead of each protocol inventing its own runtime shape.

## Key Types

- ConnectionState
- FlushResultExtensions
- ITransport
- ITransportConnection
- ITransportConnectionContext
- ITransportConnectionPipe
- ITransportPipeline
- ITransportPipelineBuilder

## Source Layout

- src/Abstractions
- src/Delegates
- src/Exceptions
- src/Extensions
- src/Internal
- src/Properties
- src/Transports
