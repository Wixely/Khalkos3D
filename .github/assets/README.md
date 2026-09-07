# The artwork

| | |
|---|---|
| `Khalkos3D.svg` | The logo as drawn. The source the others come from. |
| `Khalkos3D.png` | 1254×1254 RGBA, transparent. Used by the README. |
| `Khalkos3D.stl` | The K extruded, 2,880 triangles. **Compiled into the demo** and drawn as the centre of its scene — see `samples/Khalkos3D.Demo`. |
| `Khalkos3D.ico` | The K alone, for Windows. **The desktop demo's executable icon and its window icon**, both from this one file. |

These are linked into the sample projects rather than copied, so the model in the scene, the icon on
the taskbar and the image at the top of the README cannot drift apart.

## Regenerating the icon

The `.ico` is the K without the wordmark — which is illegible below about 48 pixels — squared with a
little margin, at 16, 24, 32, 48, 64, 128 and 256 pixels.

Every size below 256 is stored as a **DIB rather than a PNG**, and that is deliberate: the demo reads
this same file at startup to set its window icon, and it does that without an image codec, which this
project does not bundle. A DIB in an icon is a header and some bottom-up BGRA rows. See
`samples/Khalkos3D.DesktopDemo/WindowIcon.cs`.

```python
# Needs Pillow. Run from the repository root.
from PIL import Image
import struct, pathlib, io

src = Image.open(".github/assets/Khalkos3D.png").convert("RGBA")
k = src.crop((0, 0, src.width, 1018))          # above the wordmark
k = k.crop(k.split()[3].getbbox())
side = int(max(k.size) * 1.10)
tile = Image.new("RGBA", (side, side), (0, 0, 0, 0))
tile.paste(k, ((side - k.width) // 2, (side - k.height) // 2))

def dib(img):
    w, h = img.size
    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, w * h * 4, 0, 0, 0, 0)
    px, xor = img.load(), bytearray()
    for y in range(h - 1, -1, -1):             # bottom-up, as a DIB is
        for x in range(w):
            r, g, b, a = px[x, y]
            xor += bytes((b, g, r, a))
    stride = ((w + 31) // 32) * 4              # 1bpp AND mask, rows padded to 4 bytes
    return header + bytes(xor) + bytes(stride * h)

def png(img):
    buf = io.BytesIO(); img.save(buf, format="PNG"); return buf.getvalue()

entries = [(s, dib(tile.resize((s, s), Image.LANCZOS))) for s in (16, 24, 32, 48, 64, 128)]
entries.append((256, png(tile.resize((256, 256), Image.LANCZOS))))

directory, body, offset = bytearray(), bytearray(), 6 + 16 * len(entries)
for size, blob in entries:
    directory += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(blob), offset)
    body += blob
    offset += len(blob)

pathlib.Path(".github/assets/Khalkos3D.ico").write_bytes(
    struct.pack("<HHH", 0, 1, len(entries)) + bytes(directory) + bytes(body))
```
