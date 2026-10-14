import struct

def inspect_file(path):
    with open(path, 'rb') as f:
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
    return data, flat

data_torso, flat_torso = inspect_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_Torso_V2_LOD2.grn')

print(f"Total flat chunks in Torso: {len(flat_torso)}")
chunk_types = {}
for cid, off in flat_torso:
    chunk_types[cid] = chunk_types.get(cid, 0) + 1

for cid, count in sorted(chunk_types.items()):
    print(f"  Chunk {hex(cid)}: count={count}")
