# Provenance

Wall detection code copied from **CinnabarSharp** (https://github.com/pgourlain/CinnabarSharp, same author), commit `8044f0554c9924a31b0935ff9c8a151205749c1f` ("Release 0.9.8").

| Here | Source | Changes |
|---|---|---|
| `LineExtraction.cs` | `CinnabarSharp.Core/Photo/LineExtraction.cs` | Input is a grayscale `byte[]` (no BGRA conversion, no `SelectionMask`); namespace `ImmoCad.Plan`. Algorithm untouched. |
| `MaskTracer.cs` | `CinnabarSharp.Vector/Geometry/MaskTracer.cs` | `VectorPath` replaced by `WallOutline` (outline + holes as `PointPx` lists); a small local `VPoint`. Algorithm untouched. |

Tests: `tests/ImmoCad.Plan.Tests/CinnabarPortedTests.cs` ports `WallExtractionTests` and `MaskTracerTests` (same synthetic plan, same pinned SHA-256 of the mask). The tests of CinnabarSharp types that were not copied (selection mask, vector rasterizer, effect preview) were replaced by equivalent checks.
