import struct

with open(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn', 'rb') as f:
    data = f.read()

num_roots = struct.unpack('<I', data[0x44:0x48])[0]
flat = []
pos = 0x60
for r in range(num_roots):
    cid, _, data_off, _, _ = struct.unpack('<IIIII', data[pos:pos+20])
    pos += 20
    sub_count = struct.unpack('<I', data[data_off:data_off+4])[0]
    list_pos = data_off + 16
    for i in range(sub_count):
        lp = list_pos + i * 12
        scid, local_off, _ = struct.unpack('<III', data[lp:lp+12])
        flat.append((scid, data_off + local_off))

def parse_strings(data, flat):
    chunks = [c for c in flat if c[0] == 0xCA5E0200 and c[1] > 0x1000]
    if not chunks:
        chunks = [c for c in flat if c[0] == 0xCA5E0200]
    off = chunks[-1][1]
    count, _ = struct.unpack('<ii', data[off:off+8])
    pos = off + 8
    strings = []
    for _ in range(count):
        end = data.find(b'\x00', pos)
        strings.append(data[pos:end].decode('latin1'))
        pos = end + 1
    return strings

strings = parse_strings(data, flat)

f00 = [c for c in flat if c[0] == 0xCA5E0F00]
f01 = [c for c in flat if c[0] == 0xCA5E0F01]
f02 = [c for c in flat if c[0] == 0xCA5E0F02]

print(f"Total f00 (objects): {len(f00)}, total f01: {len(f01)}")

# How are f01 chunks grouped into f00 objects?
# Let's inspect f00 chunks!
for i, c in enumerate(f00[:10]):
    ints = struct.unpack('<4i', data[c[1]:c[1]+16])
    print(f"  f00[{i}]: {ints}")
