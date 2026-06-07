"""Create a simple 16x16 icon.ico file (24-bit BMP wrapped in ICO).

Run this script in the project root to generate `icon.ico`.
"""
from struct import pack
from pathlib import Path

W = 16
H = 16

# Create simple pattern: diagonal gradient
pixels = []  # rows bottom-up
for y in range(H-1, -1, -1):
    row = []
    for x in range(W):
        r = (x * 255) // (W-1)
        g = (y * 255) // (H-1)
        b = ((x+y) * 255) // (W+H-2)
        row.append((b, g, r))
    pixels.append(row)

# each row padded to 4-byte boundary
row_bytes = ((W * 3 + 3) // 4) * 4
bitmap_pixels = bytearray()
for row in pixels:
    row_data = bytearray()
    for (b, g, r) in row:
        row_data += bytes((b, g, r))
    # pad
    row_data += b"\x00" * (row_bytes - len(row_data))
    bitmap_pixels += row_data

# AND mask: 1 bit per pixel, rows padded to 32-bit boundary
mask_row_bytes = ((W + 31) // 32) * 4
mask = bytearray()
for y in range(H):
    # make mask fully opaque (0 bits) so no transparency
    mask += b"\x00" * mask_row_bytes

# BITMAPINFOHEADER
biSize = 40
biWidth = W
biHeight = H * 2  # XOR + AND
biPlanes = 1
biBitCount = 24
biCompression = 0
biSizeImage = len(bitmap_pixels) + len(mask)
biXPelsPerMeter = 0
biYPelsPerMeter = 0
biClrUsed = 0
biClrImportant = 0

bmp_header = pack('<IIIHHIIIIII', biSize, biWidth, biHeight, biPlanes, biBitCount,
                  biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter,
                  biClrUsed, biClrImportant)

image_data = bmp_header + bitmap_pixels + mask

# ICONDIR
reserved = 0
icon_type = 1  # 1 for icon
count = 1
icondir = pack('<HHH', reserved, icon_type, count)

# ICONDIRENTRY
width_byte = W if W < 256 else 0
height_byte = H if H < 256 else 0
color_count = 0
reserved_byte = 0
planes = 1
bitcount = 24
bytes_in_res = len(image_data)
image_offset = 6 + 16  # ICONDIR + ICONDIRENTRY
icondirentry = pack('<BBBBHHII', width_byte, height_byte, color_count, reserved_byte,
                     planes, bitcount, bytes_in_res, image_offset)

ico = icondir + icondirentry + image_data

out = Path(__file__).with_name('icon.ico')
with open(out, 'wb') as f:
    f.write(ico)
print('Wrote', out)
