import struct, math

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

def parse_strings(data, flat):
    chunks = [c for c in flat if c[0] == 0xCA5E0200 and c[1] > 0x1000]
    if not chunks:
        chunks = [c for c in flat if c[0] == 0xCA5E0200]
    if not chunks: return []
    off = chunks[-1][1]
    count, _ = struct.unpack('<ii', data[off:off+8])
    pos = off + 8
    strings = []
    for _ in range(count):
        end = data.find(b'\x00', pos)
        strings.append(data[pos:end].decode('latin1'))
        pos = end + 1
    return strings

def parse_bone_names(data, flat):
    strings = parse_strings(data, flat)
    f01 = [c for c in flat if c[0] == 0xCA5E0F01]
    obj_names = []
    for c in f01:
        v0, v1, v2, v3 = struct.unpack('<iiii', data[c[1]:c[1]+16])
        if v0 == 4:
            s = strings[v2] if v2 < len(strings) else f"str_{v2}"
            obj_names.append(s)
            
    b00 = [struct.unpack('<i', data[c[1]:c[1]+4])[0] for c in flat if c[0] == 0xCA5E0B00]
    bone_chunks = [c for c in flat if c[0] == 0xCA5E0506]
    bone_names = []
    for i in range(len(bone_chunks)):
        if i < len(b00):
            obj_idx = b00[i] - 1
            if 0 <= obj_idx < len(obj_names):
                bone_names.append(obj_names[obj_idx])
            else:
                bone_names.append(f"obj_{b00[i]}")
        else:
            bone_names.append(f"bone_{i}")
    return bone_names

def mat_ident():
    return [[1,0,0,0],[0,1,0,0],[0,0,1,0],[0,0,0,1]]

def quat_to_mat(q):
    x, y, z, w = q
    xx, yy, zz = x*x, y*y, z*z
    xy, xz, yz = x*y, x*z, y*z
    wx, wy, wz = w*x, w*y, w*z
    return [
        [1 - 2*(yy + zz), 2*(xy + wz), 2*(xz - wy), 0],
        [2*(xy - wz), 1 - 2*(xx + zz), 2*(yz + wx), 0],
        [2*(xz + wy), 2*(yz - wx), 1 - 2*(xx + yy), 0],
        [0, 0, 0, 1]
    ]

def trans_mat(pos):
    return [
        [1, 0, 0, 0],
        [0, 1, 0, 0],
        [0, 0, 1, 0],
        [pos[0], pos[1], pos[2], 1]
    ]

def mat_mul(a, b):
    c = [[0]*4 for _ in range(4)]
    for i in range(4):
        for j in range(4):
            c[i][j] = sum(a[i][k] * b[k][j] for k in range(4))
    return c

def mat_inv(m):
    r_inv = [[m[j][i] for j in range(3)] for i in range(3)]
    tx, ty, tz = m[3][0], m[3][1], m[3][2]
    itx = -(tx*r_inv[0][0] + ty*r_inv[1][0] + tz*r_inv[2][0])
    ity = -(tx*r_inv[0][1] + ty*r_inv[1][1] + tz*r_inv[2][1])
    itz = -(tx*r_inv[0][2] + ty*r_inv[1][2] + tz*r_inv[2][2])
    return [
        [r_inv[0][0], r_inv[0][1], r_inv[0][2], 0],
        [r_inv[1][0], r_inv[1][1], r_inv[1][2], 0],
        [r_inv[2][0], r_inv[2][1], r_inv[2][2], 0],
        [itx, ity, itz, 1]
    ]

def transform_pos(v, m):
    x = v[0]*m[0][0] + v[1]*m[1][0] + v[2]*m[2][0] + m[3][0]
    y = v[0]*m[0][1] + v[1]*m[1][1] + v[2]*m[2][1] + m[3][1]
    z = v[0]*m[0][2] + v[1]*m[1][2] + v[2]*m[2][2] + m[3][2]
    return (x, y, z)

# Test H_Male_Lod2
data_male, flat_male = inspect_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn')
male_bone_names = parse_bone_names(data_male, flat_male)

bone_chunks = [c for c in flat_male if c[0] == 0xCA5E0506]
bones = []
for i, c in enumerate(bone_chunks):
    p, = struct.unpack('<i', data_male[c[1]:c[1]+4])
    pos = struct.unpack('<fff', data_male[c[1]+4:c[1]+16])
    rot = struct.unpack('<ffff', data_male[c[1]+16:c[1]+32])
    name = male_bone_names[i]
    bones.append({'id': i, 'p': p, 'pos': pos, 'rot': rot, 'name': name})

data_walk, flat_walk = inspect_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn')
walk_strings = parse_strings(data_walk, flat_walk)
walk_tracks_by_name = {}
walk_tracks_by_index = {}

track_chunks = [c for c in flat_walk if c[0] == 0xCA5E1204]
for idx, c in enumerate(track_chunks):
    off = c[1]
    str_idx, = struct.unpack('<i', data_walk[off:off+4])
    name = walk_strings[str_idx] if str_idx < len(walk_strings) else f"str_{str_idx}"
    cP, cR, cS = struct.unpack('<iii', data_walk[off+24:off+36])
    pos_off = off + 52 + (cP + cR + cS) * 4
    p0 = struct.unpack('<fff', data_walk[pos_off:pos_off+12]) if cP > 0 else (0,0,0)
    rot_off = pos_off + cP * 12
    r0 = struct.unpack('<ffff', data_walk[rot_off:rot_off+16]) if cR > 0 else (0,0,0,1)
    walk_tracks_by_name[name] = (p0, r0)
    walk_tracks_by_index[str_idx] = (p0, r0)

num_bones = len(bones)
local_bind = [mat_mul(quat_to_mat(b['rot']), trans_mat(b['pos'])) for b in bones]
world_bind = [mat_ident() for _ in range(num_bones)]
inv_bind = [mat_ident() for _ in range(num_bones)]

for i in range(num_bones):
    p = bones[i]['p']
    if p == i or p < 0 or p >= i:
        world_bind[i] = local_bind[i]
    else:
        world_bind[i] = mat_mul(local_bind[i], world_bind[p])
    inv_bind[i] = mat_inv(world_bind[i])

pts_chunk = [c for c in flat_male if c[0] == 0xCA5E0801][0]
skin_chunk = [c for c in flat_male if c[0] == 0xCA5E0702][0]
c0a = [struct.unpack('<i', data_male[c[1]:c[1]+4])[0] for c in flat_male if c[0] == 0xCA5E0C0A]

num_v, _, _ = struct.unpack('<iii', data_male[skin_chunk[1]:skin_chunk[1]+12])

def test_sim(use_name_matching):
    local_anim = [mat_ident() for _ in range(num_bones)]
    for i in range(num_bones):
        b = bones[i]
        if use_name_matching:
            if b['name'] in walk_tracks_by_name:
                p0, r0 = walk_tracks_by_name[b['name']]
                local_anim[i] = mat_mul(quat_to_mat(r0), trans_mat(p0))
            else:
                local_anim[i] = local_bind[i]
        else: # The bug: matching by b['id'] in tracks
            if b['id'] in walk_tracks_by_index:
                p0, r0 = walk_tracks_by_index[b['id']]
                local_anim[i] = mat_mul(quat_to_mat(r0), trans_mat(p0))
            else:
                local_anim[i] = local_bind[i]

    world_anim = [mat_ident() for _ in range(num_bones)]
    skin_mat = [mat_ident() for _ in range(num_bones)]
    for i in range(num_bones):
        p = bones[i]['p']
        if p == i or p < 0 or p >= i:
            world_anim[i] = local_anim[i]
        else:
            world_anim[i] = mat_mul(local_anim[i], world_anim[p])
        skin_mat[i] = mat_mul(inv_bind[i], world_anim[i])

    p_ptr = skin_chunk[1] + 12
    max_diff = 0
    total_diff = 0
    for v in range(num_v):
        x, y, z = struct.unpack('<fff', data_male[pts_chunk[1] + v*12 : pts_chunk[1] + (v+1)*12])
        orig = (x, y, z)
        inf_count, = struct.unpack('<i', data_male[p_ptr:p_ptr+4])
        p_ptr += 4
        def_x, def_y, def_z = 0, 0, 0
        tw = 0
        for _ in range(inf_count):
            local_b, weight = struct.unpack('<if', data_male[p_ptr:p_ptr+8])
            p_ptr += 8
            master_b = c0a[local_b] if local_b < len(c0a) else local_b
            tx, ty, tz = transform_pos(orig, skin_mat[master_b])
            def_x += tx * weight
            def_y += ty * weight
            def_z += tz * weight
            tw += weight
        if tw > 0:
            def_x /= tw; def_y /= tw; def_z /= tw
        diff = math.sqrt((def_x - x)**2 + (def_y - y)**2 + (def_z - z)**2)
        total_diff += diff
        if diff > max_diff:
            max_diff = diff
    avg_diff = total_diff / num_v
    return avg_diff, max_diff

avg_bug, max_bug = test_sim(False)
print(f"BUGGED (Index matching): Avg diff = {avg_bug:.3f}, Max diff = {max_bug:.3f}")

avg_fix, max_fix = test_sim(True)
print(f"FIXED (Name matching):   Avg diff = {avg_fix:.3f}, Max diff = {max_fix:.3f}")
