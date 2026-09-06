# Licence policy

**StannumFab is MIT, and nothing goes in that puts that at risk.**

The rule, stated once so it does not have to be re-argued per pull request:

> A dependency is acceptable only if a downstream user can ship StannumFab under MIT, in a closed
> commercial product, without acquiring a licence, publishing source, or accepting terms beyond
> attribution.

## Permitted

MIT · BSD-2 · BSD-3 · Apache-2.0 · ISC · Zlib · Unlicense · CC0 · public domain

Apache-2.0 is permitted but not preferred: it is genuinely permissive, and it carries NOTICE and
patent-termination clauses that MIT does not, so its code cannot simply be relicensed. Prefer an MIT
or BSD alternative where one exists, and record the reason when one does not.

## Refused

GPL (any version) · LGPL · AGPL · MPL · SSPL · CDDL · EPL · CC-BY-SA · "source available" ·
anything with a commercial-use condition, a revenue threshold, or a field-of-use restriction.

LGPL deserves a specific note because it is the one people assume is fine: dynamic linking makes it
*workable*, not free of obligation, and it constrains static linking and AOT — which is most of this
project's deployment story. It is refused rather than managed.

## Dependencies today

**Zero.** `StannumFab.Core` and `StannumFab.Formats` use nothing outside the BCL. That is not an
aspiration that will quietly erode; it is the reason the asset layer compiles for WebAssembly, iOS
and Android with no per-platform matrix, and it should be defended.

## Named traps

**ImageSharp — refused.** The obvious choice for the image decoding this project needs, and it moved
to a split licence that is not free for all commercial use. Exactly what this policy exists to catch,
and worth naming because the pre-2.x packages on NuGet are Apache-2.0 and the change is easy to miss.

**MoltenVK — Apache-2.0, permitted but avoided.** Would enter only via a Vulkan-on-Apple backend.
Part of why the plan chose OpenGL.

**Assimp — BSD-3, permitted but not wanted.** It would replace the whole `Formats` project with a
native library, which is precisely the per-platform build matrix this design avoids. The licence is
fine; the shape is not.

## How images are handled instead

Textures arrive through a caller-supplied delegate rather than a bundled codec:

```csharp
var scene = GltfReader.ReadFile("model.glb", new GltfOptions
{
    DecodeImage = bytes =>
    {
        using var bitmap = SKBitmap.Decode(bytes);
        if (bitmap is null) return null;
        using var rgba = bitmap.Copy(SKColorType.Rgba8888);
        var pixels = new byte[rgba.Width * rgba.Height * 4];
        Marshal.Copy(rgba.GetPixels(), pixels, 0, pixels.Length);
        return new ImageData(pixels, rgba.Width, rgba.Height);
    },
});
```

That keeps the licence question with the caller, who already made it: a CupriFace app has SkiaSharp
(MIT), a desktop app may have something else, and a server-side loader may want no codec at all.
Skipping textures is fully supported — the geometry loads and `Scene.Report` says what is missing.

## Enforcement

CI fails the build on a package whose licence expression is not on the permitted list. A policy that
depends on remembering is not a policy.
