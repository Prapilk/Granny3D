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

for scid, off in flat:
    if (scid & 0xFFFFFF00) == 0xCA5E0500:
        print(f"Skeleton chunk {hex(scid)} at {hex(off)}:")
        # print some ints
        ints = struct.unpack('<8i', data[off:off+32])
        print(f"  {ints}")

print("\n0xCA5E0B01 chunk:")
for scid, off in flat:
    if scid == 0xCA5E0B01:
        ints = struct.unpack('<8i', data[off:off+32])
        print(f"  {ints}")
