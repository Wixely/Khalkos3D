using Silk.NET.Core;

namespace Khalkos3D.DesktopDemo;

/// <summary>
/// The application icon, read out of the .ico the executable already carries.
///
/// <para><b>Why parse an icon file rather than decode a PNG.</b> The window manager wants raw RGBA
/// and this project bundles no image codec — deliberately, see docs/LICENSING.md — so a PNG would
/// mean taking a decoder as a dependency to draw a 32-pixel picture. An .ico is not really a format:
/// a directory, then Windows DIBs. The entries below are BGRA bottom-up with a mask nobody needs when
/// there is an alpha channel, and reading one is the thirty lines here.
///
/// <para>The same file is the executable's icon, set by <c>ApplicationIcon</c> in the project. One
/// artwork, one file, two uses — a window whose icon disagrees with its executable's looks like two
/// applications.</para>
/// </summary>
internal static class WindowIcon
{
    private const string Resource = "Khalkos3D.DesktopDemo.Khalkos3D.ico";

    /// <summary>
    /// Every size the icon file offers as a DIB, largest last. The window manager picks whichever
    /// suits the title bar and the task switcher, which want different ones — handing it the set and
    /// letting it choose is why this returns more than one.
    /// </summary>
    internal static RawImage[] Sizes()
    {
        using var stream = typeof(WindowIcon).Assembly.GetManifestResourceStream(Resource);
        if (stream is null) return [];

        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var data = memory.ToArray();

        // ICONDIR: two reserved bytes, a type of 1, then how many pictures follow.
        if (data.Length < 6 || BitConverter.ToUInt16(data, 2) != 1) return [];
        var count = BitConverter.ToUInt16(data, 4);

        var images = new List<(int Size, RawImage Image)>();
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + i * 16;
            if (entry + 16 > data.Length) break;

            var offset = BitConverter.ToInt32(data, entry + 12);
            var length = BitConverter.ToInt32(data, entry + 8);
            if (offset < 0 || length < 40 || offset + length > data.Length) continue;

            // A PNG entry is legal in an .ico and is exactly what this cannot read. Skipping it is
            // why the file is built with DIBs for every size a window would want.
            if (data[offset] == 0x89 && data[offset + 1] == 'P') continue;

            var width = BitConverter.ToInt32(data, offset + 4);
            // A DIB in an icon declares TWICE its height, because the picture and the old 1-bit mask
            // are stored as one image. The mask is ignored here: these entries carry alpha.
            var height = BitConverter.ToInt32(data, offset + 8) / 2;
            var bits = BitConverter.ToUInt16(data, offset + 14);
            if (width <= 0 || height <= 0 || bits != 32) continue;

            var pixels = offset + 40;
            if (pixels + width * height * 4 > data.Length) continue;

            var rgba = new byte[width * height * 4];
            for (var y = 0; y < height; y++)
            {
                // Bottom-up, as every DIB is, and BGRA rather than RGBA.
                var source = pixels + (height - 1 - y) * width * 4;
                var destination = y * width * 4;
                for (var x = 0; x < width; x++)
                {
                    rgba[destination + x * 4 + 0] = data[source + x * 4 + 2];
                    rgba[destination + x * 4 + 1] = data[source + x * 4 + 1];
                    rgba[destination + x * 4 + 2] = data[source + x * 4 + 0];
                    rgba[destination + x * 4 + 3] = data[source + x * 4 + 3];
                }
            }

            images.Add((width, new RawImage(width, height, rgba)));
        }

        images.Sort((a, b) => a.Size.CompareTo(b.Size));
        return [.. images.Select(image => image.Image)];
    }
}
