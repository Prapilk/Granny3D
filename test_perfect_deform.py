import struct, math
from test_clean_deform import mat_ident, quat_to_mat, trans_mat, mat_mul, mat_inv, transform_pos
from test_full_pipeline import parse_granny_file

mesh = parse_granny_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn')
anim = parse_granny_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn')

bones = mesh['bones']
num_bones = len(bones)
local_bind = [mat_mul(quat_to_mat(b['rot']), trans_mat(b['pos'])) for b in bones]
world_bind = [mat_ident() for _ in range(num_bones)]
inv_bind = [mat_ident() for _ in range(num_bones)]

for i in range(num_bones):
    p = bones[i]['parent']
    if p == i or p < 0 or p >= i:
        world_bind[i] = local_bind[i]
    else:
        world_bind[i] = mat_mul(local_bind[i], world_bind[p])
    inv_bind[i] = mat_inv(world_bind[i])

# Anim at t=0
local_anim = [mat_ident() for _ in range(num_bones)]
for i in range(num_bones):
    b = bones[i]
    if b['name'] in anim['tracks']:
        t = anim['tracks'][b['name']]
        local_anim[i] = mat_mul(quat_to_mat(t['r0']), trans_mat(t['p0']))
    else:
        local_anim[i] = local_bind[i]

world_anim = [mat_ident() for _ in range(num_bones)]
skin_mat = [mat_ident() for _ in range(num_bones)]

for i in range(num_bones):
    p = bones[i]['parent']
    if p == i or p < 0 or p >= i:
        world_anim[i] = local_anim[i]
    else:
        world_anim[i] = mat_mul(local_anim[i], world_anim[p])
    skin_mat[i] = mat_mul(inv_bind[i], world_anim[i])

# Map c0a (1-based object indices) to bone indices!
# Find b00 in mesh
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

b00 = [struct.unpack('<i', data[c[1]:c[1]+4])[0] for c in chunks if c[0] == 0xCA5E0B00]

# For each obj_idx_1 in c0a, find which bone has b00[bone_idx] == obj_idx_1!
c0a_to_bone = []
for obj_idx_1 in mesh['c0a']:
    if obj_idx_1 in b00:
        c0a_to_bone.append(b00.index(obj_idx_1))
    else:
        c0a_to_bone.append(0)

print(f"c0a to bone index mapping: {c0a_to_bone}")
for local_b, b_idx in enumerate(c0a_to_bone):
    print(f"  local {local_b} -> bone {b_idx} ('{bones[b_idx]['name']}')")

pts_off = mesh['pts_chunk'][1]
skin_off = mesh['skin_chunk'][1]
num_v, _, _ = struct.unpack('<iii', data[skin_off:skin_off+12])
p_ptr = skin_off + 12

print("\n--- Deforming vertices of H_Male_Lod2 with PROPER c0a->bone mapping ---")
total_diff = 0
max_diff = 0
for v in range(num_v):
    x, y, z = struct.unpack('<fff', data[pts_off + v*12 : pts_off + (v+1)*12])
    orig = (x, y, z)
    inf_count, = struct.unpack('<i', data[p_ptr:p_ptr+4])
    p_ptr += 4
    def_x, def_y, def_z = 0, 0, 0
    tw = 0
    influences_str = []
    for _ in range(inf_count):
        local_b, weight = struct.unpack('<if', data[p_ptr:p_ptr+8])
        p_ptr += 8
        master_b = c0a_to_bone[local_b] if local_b < len(c0a_to_bone) else 0
        tx, ty, tz = transform_pos(orig, skin_mat[master_b])
        def_x += tx * weight
        def_y += ty * weight
        def_z += tz * weight
        tw += weight
        if v < 10:
            influences_str.append(f"[{bones[master_b]['name']}: {weight:.2f}]")
    if tw > 0:
        def_x /= tw; def_y /= tw; def_z /= tw
    diff = math.sqrt((def_x - x)**2 + (def_y - y)**2 + (def_z - z)**2)
    total_diff += diff
    if diff > max_diff: max_diff = diff
    if v < 10:
        print(f"V{v:2d}: orig=({x:.3f}, {y:.3f}, {z:.3f}) -> def=({def_x:.3f}, {def_y:.3f}, {def_z:.3f}) diff={diff:.3f} | {' '.join(influences_str)}")

print(f"\nAverage deformation diff across all {num_v} vertices: {total_diff/num_v:.4f} meters")
print(f"Max deformation diff across all {num_v} vertices: {max_diff:.4f} meters")
