# StannumFab

A small, managed-first 3D engine for .NET. Load models, render them anywhere.

```csharp
var scene = ModelReader.ReadFile("bracket.stl");
Console.WriteLine($"{scene.TriangleCount:N0} triangles, {scene.Bounds}");
```

**`StannumFab.Core`** — `Mesh`, `Material`, `Scene`, `BoundingBox` and an angle-aware vertex welder.

**`StannumFab.Formats`** — STL (binary and ASCII), OBJ, 3MF and glTF 2.0 / GLB, all producing the
same `Scene`.

Both have **zero dependencies** and use nothing outside the BCL, so they run anywhere .NET does —
including WebAssembly, Android and iOS, with no native artefacts.

## What it does that most loaders do not

- **Welding that keeps corners sharp.** STL stores no shared vertices, so a box arrives as 36 of
  them. Merging them naively rounds off every corner; this merges only where the faces meet within an
  angle, so mechanical parts stay faceted and tessellated curves go smooth.
- **Units.** A 3MF says whether it means millimetres or inches. This converts to millimetres so
  measurements and build volumes are written once.
- **It reports what it could not do.** `Scene.Report` lists features that were in the file and are
  not in the result — animations, material extensions, external buffers. Where a feature changes how
  the *bytes* are encoded (Draco, meshopt), loading throws instead, because a half-decoded mesh is
  wrong rather than merely plain.

MIT licensed, and held to a [dependency policy](https://github.com/Wixely/StannumFab/blob/main/docs/LICENSING.md)
that keeps it that way.

**Status:** the asset layer is complete and tested; the renderer is next. See
[the plan](https://github.com/Wixely/StannumFab/blob/main/docs/PLAN.md).
