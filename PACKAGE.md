# Khalkos3D

A small, managed-first 3D engine for .NET. Load models, render them anywhere.

```csharp
var scene = ModelReader.ReadFile("bracket.stl");
Console.WriteLine($"{scene.TriangleCount:N0} triangles, {scene.Bounds}");
```

**`Khalkos3D.Core`** — `Mesh`, `Material`, `Scene`, `BoundingBox`, `Camera` and an angle-aware
vertex welder.

**`Khalkos3D.Formats`** — STL (binary and ASCII), OBJ, 3MF and glTF 2.0 / GLB, all producing the
same `Scene`.

**`Khalkos3D.Gl`** — draws it, on desktop GL 3.3, OpenGL ES 3.0 and WebGL2 from one shader source.
It creates no window and no context, so it embeds wherever there is already one.

All three have **zero dependencies** and use nothing outside the BCL, so they run anywhere .NET does
— including WebAssembly, Android and iOS, with no native artefacts.

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

MIT licensed, and held to a [dependency policy](https://github.com/Wixely/Khalkos3D/blob/main/docs/LICENSING.md)
that keeps it that way.

**Status:** loaders, renderer and viewer controls all work and are tested against real GL drivers,
including on CupriFace's own teapot model. See
[the plan](https://github.com/Wixely/Khalkos3D/blob/main/docs/PLAN.md).
