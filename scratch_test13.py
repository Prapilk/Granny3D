import struct

with open(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn', 'rb') as f:
    data = f.read()

cid, _, data_off, _, _ = struct.unpack('<IIIII', data[0x74:0x74+20])
sub_count = struct.unpack('<I', data[data_off:data_off+4])[0]
list_pos = data_off + 16
chunks = []
for i in range(sub_count):
    lp = list_pos + i * 12
    scid, local_off, _ = struct.unpack('<III', data[lp:lp+12])
    chunks.append((scid, data_off + local_off))

bone_chunks = [c for c in chunks if c[0] == 0xCA5E0506]
for i, c in enumerate(bone_chunks):
    p, = struct.unpack('<i', data[c[1]:c[1]+4])
    pos = struct.unpack('<fff', data[c[1]+4:c[1]+16])
    print(f"Bone {i:2d}: parent={p:2d}, local_pos=({pos[0]:.3f}, {pos[1]:.3f}, {pos[2]:.3f})")
