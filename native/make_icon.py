"""Generate Kuroko.ico with no third-party deps (zlib + struct only).

Draws at 256x256 with 4x supersampling, box-downsamples to each icon size, and
writes a PNG-payload ICO (Vista+ reads PNG entries directly).

Design: dark rounded square, teal screen, scanlines, white play triangle - bold
shapes only, because anything finer turns to mush at 16px.
"""
import math, struct, zlib

SS = 4  # supersample factor


def draw(N):
    """Return an RGBA float buffer of size N*SS."""
    S = N * SS
    px = [[(0, 0, 0, 0)] * S for _ in range(S)]

    def rrect(x0, y0, x1, y1, r, col, buf):
        for y in range(int(y0), int(math.ceil(y1))):
            for x in range(int(x0), int(math.ceil(x1))):
                dx = max(x0 + r - x, 0, x - (x1 - r))
                dy = max(y0 + r - y, 0, y - (y1 - r))
                if dx * dx + dy * dy <= r * r:
                    buf[y][x] = col

    m = S * 0.045
    # body
    rrect(m, m, S - m, S - m, S * 0.20, (26, 30, 38, 255), px)
    # screen
    sx0, sy0, sx1, sy1 = S * 0.155, S * 0.235, S * 0.845, S * 0.700
    rrect(sx0, sy0, sx1, sy1, S * 0.045, (34, 197, 194, 255), px)

    # scanlines across the screen
    for y in range(int(sy0), int(sy1)):
        if ((y - int(sy0)) // (SS * 2)) % 2 == 0:
            for x in range(int(sx0), int(sx1)):
                r, g, b, a = px[y][x]
                if a:
                    px[y][x] = (int(r * 0.78), int(g * 0.78), int(b * 0.78), a)

    # play triangle
    cx, cy = S * 0.5, (sy0 + sy1) / 2
    h = (sy1 - sy0) * 0.42
    w = h * 0.92
    for y in range(int(cy - h), int(cy + h)):
        t = abs(y - cy) / h
        if t > 1:
            continue
        xe = cx - w * 0.45 + (1 - t) * w
        for x in range(int(cx - w * 0.45), int(xe)):
            if 0 <= x < S and 0 <= y < S:
                px[y][x] = (255, 255, 255, 255)

    # stand
    rrect(S * 0.40, sy1, S * 0.60, S * 0.775, S * 0.012, (70, 78, 92, 255), px)
    rrect(S * 0.28, S * 0.775, S * 0.72, S * 0.845, S * 0.030, (70, 78, 92, 255), px)
    return px, S


def downsample(px, S, N):
    out = bytearray()
    k = S // N
    for y in range(N):
        out.append(0)  # PNG filter byte
        for x in range(N):
            r = g = b = a = 0
            for j in range(k):
                for i in range(k):
                    pr, pg, pb, pa = px[y * k + j][x * k + i]
                    r += pr * pa; g += pg * pa; b += pb * pa; a += pa
            n = k * k
            if a:
                out += bytes((r // a, g // a, b // a, a // n))
            else:
                out += b'\0\0\0\0'
    return bytes(out)


def png(raw, N):
    def chunk(tag, data):
        c = tag + data
        return struct.pack('>I', len(data)) + c + struct.pack('>I', zlib.crc32(c) & 0xffffffff)
    ihdr = struct.pack('>IIBBBBB', N, N, 8, 6, 0, 0, 0)
    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', ihdr)
            + chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b''))


sizes = [256, 64, 48, 32, 16]
images = []
for n in sizes:
    buf, S = draw(n)
    images.append(png(downsample(buf, S, n), n))

out = struct.pack('<HHH', 0, 1, len(images))
offset = 6 + 16 * len(images)
for n, data in zip(sizes, images):
    out += struct.pack('<BBBBHHII', 0 if n >= 256 else n, 0 if n >= 256 else n,
                       0, 0, 1, 32, len(data), offset)
    offset += len(data)
for data in images:
    out += data

with open('Kuroko.ico', 'wb') as f:
    f.write(out)
print(f"wrote Kuroko.ico ({len(out)} bytes, sizes {sizes})")
