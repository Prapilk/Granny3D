import struct, math

# Compare H_Male_Lod2 bones vs H_Male_Walk_01 anim bones
def get_bones(path):
    with open(path, 'rb') as f:
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
    bones = []
    for i, c in enumerate(bone_chunks):
        p, = struct.unpack('<i', data[c[1]:c[1]+4])
        pos = struct.unpack('<fff', data[c[1]+4:c[1]+16])
        rot = struct.unpack('<ffff', data[c[1]+16:c[1]+32])
        bones.append({'id': i, 'parent': p, 'pos': pos, 'rot': rot})
    return bones

mb = get_bones(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn')
ab = get_bones(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn')

print(f"Model bones: {len(mb)}, Anim bones: {len(ab)}")

# Find exact match for each Model Bone in Anim Bones by local position
for i, m in enumerate(mb):
    matches = []
    for j, a in enumerate(ab):
        dist = math.sqrt(sum((x-y)**2 for x,y in zip(m['pos'], a['pos'])))
        matches.append((dist, j))
    matches.sort(key=lambda x: x[0])
    best_dist, best_j = matches[0]
    second_dist, second_j = matches[1] if len(matches) > 1 else (999, -1)
    print(f"Model Bone {i:2d} (p={m['parent']:2d}, pos=({m['pos'][0]:.2f},{m['pos'][1]:.2f},{m['pos'][2]:.2f})) -> Anim Bone {best_j:2d} (dist={best_dist:.4f})")
