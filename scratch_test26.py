import struct, math
from scratch_test import mat_mul, mat_quat, mat_trans, mat_inv_affine, mat_transform, bbox

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
    return bones, data, chunks

tb, data_t, chunks_t = get_bones(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_Torso_V2_LOD2.grn')
ab, data_a, chunks_a = get_bones(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn')

print(f"Torso bones: {len(tb)}, Anim bones: {len(ab)}")

# Match each Torso bone to Anim bone
torso_to_anim = []
for i, t in enumerate(tb):
    matches = []
    for j, a in enumerate(ab):
        p_dist = math.sqrt(sum((x-y)**2 for x,y in zip(t['pos'], a['pos'])))
        r_dist = math.sqrt(sum((x-y)**2 for x,y in zip(t['rot'], a['rot'])))
        # score
        score = p_dist * 2.0 + r_dist
        matches.append((score, p_dist, j))
    matches.sort(key=lambda x: x[0])
    best = matches[0]
    torso_to_anim.append(best[2] if best[1] < 0.1 else -1)
    print(f"Torso Bone {i:2d} (p={t['parent']:2d}, pos=({t['pos'][0]:.2f},{t['pos'][1]:.2f},{t['pos'][2]:.2f})) -> Anim Bone {best[2]:2d} (dist={best[1]:.4f})")
