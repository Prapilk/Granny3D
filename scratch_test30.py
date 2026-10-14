import struct

with open(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn', 'rb') as f:
    data = f.read()

# Chunk at 0x2028
off = 0x2028
cnt, tot_len = struct.unpack('<II', data[off:off+8])
raw = data[off+8:off+8+tot_len]

# Let's inspect string offsets
pos = 0
str_offsets = {}
s_idx = 0
while pos < len(raw):
    end = raw.find(b'\x00', pos)
    if end == -1: end = len(raw)
    s = raw[pos:end].decode('latin1')
    str_offsets[pos] = (s_idx, s)
    print(f"Offset {pos:3d} (0x{pos:03x}) | Idx {s_idx:2d}: '{s}'")
    s_idx += 1
    pos = end + 1
