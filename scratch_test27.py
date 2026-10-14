import math, struct
from scratch_test26 import tb, ab, torso_to_anim, data_t, chunks_t, data_a, chunks_a
from scratch_test import mat_mul, mat_quat, mat_trans, mat_inv_affine, mat_transform, bbox

# Parse Torso mesh and skin
c0a_t = [struct.unpack('<i', data_t[c[1]:c[1]+4])[0] for c in chunks_t if c[0] == 0xCA5E0C0A]
b00_t = [struct.unpack('<i', data_t[c[1]:c[1]+4])[0] for c in chunks_t if c[0] == 0xCA5E0B00]
bone_map_t = [b00_t.index(x) if x in b00_t else 0 for x in c0a_t]

pts_chunk_t = [c for c in chunks_t if c[0] == 0xCA5E0801][0]
skin_chunk_t = [c for c in chunks_t if c[0] == 0xCA5E0702][0]

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

# Extract animation track data for each Anim Bone
track_chunks_a = [c for c in chunks_a if c[0] == 0xCA5E1204]
tracks_by_anim_bone = {}
for i, tc in enumerate(track_chunks_a):
    off = tc[1]
    cP, cR, cS = struct.unpack('<iii', data_a[off+24:off+36])
    tpos = off + 52 + (cP + cR + cS) * 4
    p0 = struct.unpack('<fff', data_a[tpos:tpos+12]) if cP > 0 else (0,0,0)
    rot_off = tpos + cP * 12
    r0 = struct.unpack('<ffff', data_a[rot_off:rot_off+16]) if cR > 0 else (0,0,0,1)
    tracks_by_anim_bone[i] = {'p0': p0, 'r0': r0}

# Build Bind & Anim Matrices for Torso
count = len(tb)
local_bind = [mat_mul(mat_quat(b['rot']), mat_trans(b['pos'])) for b in tb]
world_bind = [None] * count
inv_bind = [None] * count
for i in range(count):
    p = tb[i]['parent']
    if p == i or p < 0 or p >= i:
        world_bind[i] = local_bind[i]
    else:
        world_bind[i] = mat_mul(local_bind[i], world_bind[p])
    inv_bind[i] = mat_inv_affine(world_bind[i])

# Anim matrices
local_anim = []
for i in range(count):
    b = tb[i]
    pos = b['pos']
    rot = b['rot']
    target_anim_bone = torso_to_anim[i]
    if target_anim_bone in tracks_by_anim_bone:
        t = tracks_by_anim_bone[target_anim_bone]
        pos = t['p0']
        rot = t['r0']
    local_anim.append(mat_mul(mat_quat(rot), mat_trans(pos)))

world_anim = [None] * count
skin_mat = [None] * count
for i in range(count):
    p = tb[i]['parent']
    if p == i or p < 0 or p >= i:
        world_anim[i] = local_anim[i]
    else:
        world_anim[i] = mat_mul(local_anim[i], world_anim[p])
    skin_mat[i] = mat_mul(inv_bind[i], world_anim[i])

# Deform Torso verts
deformed = []
diffs = []
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
        p_def = (acc[0]/tot_w, acc[1]/tot_w, acc[2]/tot_w)
    else:
        p_def = orig
    deformed.append(p_def)
    d = math.sqrt(sum((x-y)**2 for x,y in zip(orig, p_def)))
    diffs.append(d)

print("Torso Rest BBox:    ", bbox(verts_t))
print("Torso Deformed BBox:", bbox(deformed))
print(f"Avg diff: {sum(diffs)/len(diffs):.4f} m, Max diff: {max(diffs):.4f} m")
