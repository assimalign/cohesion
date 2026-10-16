# Assimalign.Cohesion.Content.Media — Overview

The future home of the Content family's shared track, sample, and timeline model (foundation
plan G3, Stage 3). Today the package carries only `IMediaFile` and `MediaFileKind`.

It references `Content.Binary`; exact reads, slices, segments, FourCC, and fixed-point values
belong there. BMFF and future media profiles build above this layer. No shared track model,
codec processing, playback, or timeline manipulation is implemented yet.

See [DESIGN.md](DESIGN.md) for the package boundary.
