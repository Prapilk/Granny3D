import struct, math
from test_clean_deform import mat_ident, quat_to_mat, trans_mat, mat_mul, mat_inv, transform_pos

# 1. Master Skeleton from H_Male_Lod2
with open(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn', 'rb') as f:
    data_male = f.read()

cid, _, data_off, _, _ = struct.unpack('<IIIII', data_male[0x74:0x74+20])
sub_count = struct.unpack('<I', data_male[data_off:data_off+4])[0]
list_pos = data_off + 16
chunks_male = []
for i in range(sub_count):
    lp = list_pos + i * 12
    scid, local_off, _ = struct.unpack('<III', data_male[lp:lp+12])
    chunks_male.append((scid, data_off + local_off))

# Strings
str_chunk = [c for c in chunks_male if c[0] == 0xCA5E0200][0]
count, _ = struct.unpack('<ii', data_male[str_chunk[1]:str_chunk[1]+8])
pos = str_chunk[1] + 8
strings_male = []
for _ in range(count):
    end = data_male.find(b'\x00', pos)
    strings_male.append(data_male[pos:end].decode('latin1'))
    pos = end + 1

# Objects
objects_male = []
current_obj = None
for scid, off in chunks_male:
    if scid == 0xCA5E0F00:
        current_obj = {'name': 'NO_NAME'}
        objects_male.append(current_obj)
    elif scid == 0xCA5E0F01 and current_obj is not None:
        v0, v1, v2, v3 = struct.unpack('<iiii', data_male[off:off+16])
        if v0 == 4 and v2 < len(strings_male):
            current_obj['name'] = strings_male[v2]

b00_male = [struct.unpack('<i', data_male[c[1]:c[1]+4])[0] for c in chunks_male if c[0] == 0xCA5E0B00]
bone_chunks_male = [c for c in chunks_male if c[0] == 0xCA5E0506]

master_bones = []
for i, c in enumerate(bone_chunks_male):
    p, = struct.unpack('<i', data_male[c[1]:c[1]+4])
    pos = struct.unpack('<fff', data_male[c[1]+4:c[1]+16])
    rot = struct.unpack('<ffff', data_male[c[1]+16:c[1]+32])
    name = f"Bone_{i}"
    if i < len(b00_male):
        obj_idx = b00_male[i] - 1
        if 0 <= obj_idx < len(objects_male):
            name = objects_male[obj_idx]['name']
    master_bones.append({'id': i, 'parent': p, 'pos': pos, 'rot': rot, 'name': name})

# 2. Walk Animation
with open(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn', 'rb') as f:
    data_walk = f.read()

cid, _, data_off, _, _ = struct.unpack('<IIIII', data_walk[0x74:0x74+20])
sub_count = struct.unpack('<I', data_walk[data_off:data_off+4])[0]
list_pos = data_off + 16
chunks_walk = []
for i in range(sub_count):
    lp = list_pos + i * 12
    scid, local_off, _ = struct.unpack('<III', data_walk[lp:lp+12])
    chunks_walk.append((scid, data_off + local_off))

str_chunk = [c for c in chunks_walk if c[0] == 0xCA5E0200][0]
count, _ = struct.unpack('<ii', data_walk[str_chunk[1]:str_chunk[1]+8])
pos = str_chunk[1] + 8
strings_walk = []
for _ in range(count):
    end = data_walk.find(b'\x00', pos)
    strings_walk.append(data_walk[pos:end].decode('latin1'))
    pos = end + 1

objects_walk = []
current_obj = None
for scid, off in chunks_walk:
    if scid == 0xCA5E0F00:
        current_obj = {'name': 'NO_NAME'}
        objects_walk.append(current_obj)
    elif scid == 0xCA5E0F01 and current_obj is not None:
        v0, v1, v2, v3 = struct.unpack('<iiii', data_walk[off:off+16])
        if v0 == 4 and v2 < len(strings_walk):
            current_obj['name'] = strings_walk[v2]

tracks_walk = {}
for c in [c for c in chunks_walk if c[0] == 0xCA5E1204]:
    off = c[1]
    obj_idx_1, = struct.unpack('<i', data_walk[off:off+4])
    name = f"Track_{obj_idx_1}"
    if 0 <= obj_idx_1 - 1 < len(objects_walk):
        name = objects_walk[obj_idx_1 - 1]['name']
    cP, cR, cS = struct.unpack('<iii', data_walk[off+24:off+36])
    pos_off = off + 52 + (cP + cR + cS) * 4
    p0 = struct.unpack('<fff', data_walk[pos_off:pos_off+12]) if cP > 0 else (0,0,0)
    rot_off = pos_off + cP * 12
    r0 = struct.unpack('<ffff', data_walk[rot_off:rot_off+16]) if cR > 0 else (0,0,0,1)
    tracks_walk[name] = {'p0': p0, 'r0': r0}

# 3. Compute Bind & Anim Matrices for Master Skeleton
num_master = len(master_bones)
local_bind = [mat_mul(quat_to_mat(b['rot']), trans_mat(b['pos'])) for b in master_bones]
world_bind = [mat_ident() for _ in range(num_master)]
inv_bind = [mat_ident() for _ in range(num_master)]

for i in range(num_master):
    p = master_bones[i]['parent']
    if p == i or p < 0 or p >= i:
        world_bind[i] = local_bind[i]
    else:
        world_bind[i] = mat_mul(local_bind[i], world_bind[p])
    inv_bind[i] = mat_inv(world_bind[i])

# Anim at t=0
local_anim = [mat_ident() for _ in range(num_master)]
for i in range(num_master):
    b = master_bones[i]
    if b['name'] in tracks_walk:
        t = tracks_walk[b['name']]
        local_anim[i] = mat_mul(quat_to_mat(t['r0']), trans_mat(t['p0']))
    else:
        local_anim[i] = local_bind[i]

world_anim = [mat_ident() for _ in range(num_master)]
skin_mat_master = [mat_ident() for _ in range(num_master)]

for i in range(num_master):
    p = master_bones[i]['parent']
    if p == i or p < 0 or p >= i:
        world_anim[i] = local_anim[i]
    else:
        world_anim[i] = mat_mul(local_anim[i], world_anim[p])
    skin_mat_master[i] = mat_mul(inv_bind[i], world_anim[i])

# 4. Deform Torso Mesh
with open(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_Torso_V2_LOD2.grn', 'rb') as f:
    data_torso = f.read()

cid, _, data_off, _, _ = struct.unpack('<IIIII', data_torso[0x74:0x74+20])
sub_count = struct.unpack('<I', data_torso[data_off:data_off+4])[0]
list_pos = data_off + 16
chunks_torso = []
for i in range(sub_count):
    lp = list_pos + i * 12
    scid, local_off, _ = struct.unpack('<III', data_torso[lp:lp+12])
    chunks_torso.append((scid, data_off + local_off))

pts_torso = [c for c in chunks_torso if c[0] == 0xCA5E0801][0][1]
skin_torso = [c for c in chunks_torso if c[0] == 0xCA5E0702][0][1]
c0a_torso = [struct.unpack('<i', data_torso[c[1]:c[1]+4])[0] for c in chunks_torso if c[0] == 0xCA5E0C0A]

num_v, _, _ = struct.unpack('<iii', data_torso[skin_torso:skin_torso+12])
p_ptr = skin_torso + 12

print(f"Torso c0a map: {c0a_torso}")
for local_b, master_b in enumerate(c0a_torso):
    print(f"  local {local_b} -> Master Bone {master_b} ('{master_bones[master_b]['name']}')")

print(f"\n--- Deforming {num_v} Torso vertices using Shared Master Skeleton ---")
total_diff = 0
max_diff = 0
for v in range(num_v):
    x, y, z = struct.unpack('<fff', data_torso[pts_torso + v*12 : pts_torso + (v+1)*12])
    orig = (x, y, z)
    inf_count, = struct.unpack('<i', data_torso[p_ptr:p_ptr+4])
    p_ptr += 4
    def_x, def_y, def_z = 0, 0, 0
    tw = 0
    influences_str = []
    for _ in range(inf_count):
        local_b, weight = struct.unpack('<if', data_torso[p_ptr:p_ptr+8])
        p_ptr += 8
        master_b = c0a_torso[local_b] if local_b < len(c0a_torso) else 0
        tx, ty, tz = transform_pos(orig, skin_mat_master[master_b])
        def_x += tx * weight
        def_y += ty * weight
        def_z += tz * weight
        tw += weight
        if v < 10:
            influences_str.append(f"[{master_bones[master_b]['name']}: {weight:.2f}]")
    if tw > 0:
        def_x /= tw; def_y /= tw; def_z /= tw
    diff = math.sqrt((def_x - x)**2 + (def_y - y)**2 + (def_z - z)**2)
    total_diff += diff
    if diff > max_diff: max_diff = diff
    if v < 10:
        print(f"V{v:2d}: orig=({x:.3f}, {y:.3f}, {z:.3f}) -> def=({def_x:.3f}, {def_y:.3f}, {def_z:.3f}) diff={diff:.3f} | {' '.join(influences_str)}")

print(f"\nAverage deformation diff across all {num_v} Torso vertices: {total_diff/num_v:.4f} meters")
print(f"Max deformation diff across all {num_v} Torso vertices: {max_diff:.4f} meters")
