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

# Print all chunk types and counts
from collections import Counter
counts = Counter([c[0] for c in flat])
print("Chunk types in H_Male_Lod2.grn:")
for cid, cnt in counts.most_common():
    print(f"  {hex(cid)}: {cnt}")
