from scratch_test14 import get_obj_names
from scratch_test import mat_mul, mat_quat, mat_trans, mat_inv_affine, mat_transform, bbox
import struct

# Parse Torso bones and skin
objs_t, data_t, chunks_t = get_obj_names(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_Torso_V2_LOD2.grn')
b00_t = [struct.unpack('<i', data_t[c[1]:c[1]+4])[0] for c in chunks_t if c[0] == 0xCA5E0B00]
bone_chunks_t = [c for c in chunks_t if c[0] == 0xCA5E0506]

torso_bones = []
for i, c in enumerate(bone_chunks_t):
    p, = struct.unpack('<i', data_t[c[1]:c[1]+4])
    pos = struct.unpack('<fff', data_t[c[1]+4:c[1]+16])
    rot = struct.unpack('<ffff', data_t[c[1]+16:c[1]+32])
    name = objs_t[b00_t[i] - 1]['name'] if i < len(b00_t) else f"Bone_{i}"
    torso_bones.append({'id': i, 'parent': p, 'pos': pos, 'rot': rot, 'name': name})

# Parse Walk anim
objs_a, data_a, chunks_a = get_obj_names(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn')
b00_a = [struct.unpack('<i', data_a[c[1]:c[1]+4])[0] for c in chunks_a if c[0] == 0xCA5E0B00]
track_chunks_a = [c for c in chunks_a if c[0] == 0xCA5E1204]

tracks_by_name = {}
for i, c in enumerate(track_chunks_a):
    off = c[1]
    name = objs_a[b00_a[i] - 1]['name'] if i < len(b00_a) else f"Track_{i}"
    cP, cR, cS = struct.unpack('<iii', data_a[off+24:off+36])
    pos_off = off + 52 + (cP + cR + cS) * 4
    p0 = struct.unpack('<fff', data_a[pos_off:pos_off+12]) if cP > 0 else (0,0,0)
    rot_off = pos_off + cP * 12
    r0 = struct.unpack('<ffff', data_a[rot_off:rot_off+16]) if cR > 0 else (0,0,0,1)
    tracks_by_name[name] = {'p0': p0, 'r0': r0}

# Parse Torso mesh and skin
c0a_t = [struct.unpack('<i', data_t[c[1]:c[1]+4])[0] for c in chunks_t if c[0] == 0xCA5E0C0A]
bone_map_t = [b00_t.index(x) if x in b00_t else 0 for x in c0a_t]

pts_chunk_t = [c for c in chunks_t if c[0] == 0xCA5E0801][0]
skin_chunk_t = [c for c in chunks_t if c[0] == 0xCA5E0702][0]

sz = chunks_t[chunks_t.index(pts_chunk_t)+1][1] - pts_chunk_t[1] if chunks_t.index(pts_chunk_t)+1 < len(chunks_t) else 0
# Torso verts
num_v, _, _ = struct.unpack('<iii', data_t[skin_chunk_t[1]:skin_chunk_t[1]+12])
verts_t = [struct.unpack('<fff', data_t[pts_chunk_t[1]+v*12:pts_chunk_t[1]+(v+1)*12]) for v in range(num_v)]

spos = skin_chunk_t[1] + 12
skin_t = []
for v in range(num_v):
    inf_count, = struct.unpack('<i', data_t[spos:spos+4])
    spos += 4
    infs = []
    for k in range(inf_count):
        local_b, w = struct.unpack('<if', data_t[spos:spos+8])
        spos += 8
        b_idx = bone_map_t[local_b] if local_b < len(bone_map_t) else local_b
        infs.append((b_idx, w))
    skin_t.append(infs)

# Build Bind & Anim Matrices for Torso
count = len(torso_bones)
local_bind = [mat_mul(mat_quat(b['rot']), mat_trans(b['pos'])) for b in torso_bones]
world_bind = [None] * count
inv_bind = [None] * count
for i in range(count):
    p = torso_bones[i]['parent']
    if p == i or p < 0 or p >= i:
        world_bind[i] = local_bind[i]
    else:
        world_bind[i] = mat_mul(local_bind[i], world_bind[p])
    inv_bind[i] = mat_inv_affine(world_bind[i])

# Anim matrices
local_anim = []
for i in range(count):
    b = torso_bones[i]
    pos = b['pos']
    rot = b['rot']
    if b['name'] in tracks_by_name:
        t = tracks_by_name[b['name']]
        pos = t['p0']
        rot = t['r0']
    local_anim.append(mat_mul(mat_quat(rot), mat_trans(pos)))

world_anim = [None] * count
skin_mat = [None] * count
for i in range(count):
    p = torso_bones[i]['parent']
    if p == i or p < 0 or p >= i:
        world_anim[i] = local_anim[i]
    else:
        world_anim[i] = mat_mul(local_anim[i], world_anim[p])
    skin_mat[i] = mat_mul(inv_bind[i], world_anim[i])

# Deform Torso verts
deformed = []
for v in range(num_v):
    orig = verts_t[v]
    infs = skin_t[v]
    tot_w = 0.0
    acc = [0.0, 0.0, 0.0]
    for b_id, w in infs:
        if 0 <= b_id < count:
            tp = mat_transform(orig, skin_mat[b_id])
            acc[0] += tp[0] * w
            acc[1] += tp[1] * w
            acc[2] += tp[2] * w
            tot_w += w
    if tot_w > 1e-4:
        deformed.append((acc[0]/tot_w, acc[1]/tot_w, acc[2]/tot_w))
    else:
        deformed.append(orig)

print("Torso Rest BBox:    ", bbox(verts_t))
print("Torso Deformed BBox:", bbox(deformed))
