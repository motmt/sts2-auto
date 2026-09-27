"""分析 BMP 截图的画面特征：是否黑屏、主色调、亮度分布。"""
import struct, sys, os
from collections import Counter

path = sys.argv[1] if len(sys.argv) > 1 else r'D:\尖塔模组\game_shot.bmp'
data = open(path, 'rb').read()
print('file size', len(data))

# BMP header
sig = data[:2]
off = struct.unpack_from('<I', data, 10)[0]
hdr = struct.unpack_from('<IiiHH', data, 14)
hsize, w, h, planes, bpp = hdr
print('sig', sig, 'off', off, 'w', w, 'h', h, 'bpp', bpp)

if sig != b'BM':
    print('not BMP'); sys.exit(0)

w = abs(w); h = abs(h)
row = ((w * bpp // 8) + 3) // 4 * 4
px = data[off:]

# 采样统计
step = max(1, w // 200)
colors = Counter()
lum_sum = 0
n = 0
dark = 0
for y in range(0, h, 4):
    base = y * row
    for x in range(0, w, step * 4):
        p = base + x * (bpp // 8)
        if p + 2 >= len(px):
            continue
        b, g, r = px[p], px[p+1], px[p+2]
        lum = (r * 299 + g * 587 + b * 114) // 1000
        lum_sum += lum
        n += 1
        if lum < 25:
            dark += 1
        colors[(r // 32 * 32, g // 32 * 32, b // 32 * 32)] += 1

print('sampled', n)
print('avg luminance', round(lum_sum / max(1, n), 1))
print('very-dark ratio', round(dark / max(1, n), 3))
print('top colors (rgb -> count, pct):')
for c, cnt in colors.most_common(8):
    print('  ', c, cnt, round(cnt / max(1, n) * 100, 1), '%')
