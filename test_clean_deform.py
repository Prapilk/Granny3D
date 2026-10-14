import struct, math
from test_full_pipeline import parse_granny_file

mesh = parse_granny_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn')
anim = parse_granny_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn')

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

data = mesh['data']
pts_off = mesh['pts_chunk'][1]
skin_off = mesh['skin_chunk'][1]
c0a = mesh['c0a']

num_v, _, _ = struct.unpack('<iii', data[skin_off:skin_off+12])
p_ptr = skin_off + 12

print("\n--- Deforming first 10 vertices of H_Male_Lod2 ---")
for v in range(10):
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
        master_b = c0a[local_b] if local_b < len(c0a) else local_b
        tx, ty, tz = transform_pos(orig, skin_mat[master_b])
        def_x += tx * weight
        def_y += ty * weight
        def_z += tz * weight
        tw += weight
        influences_str.append(f"[{bones[master_b]['name']}: {weight:.2f}]")
    if tw > 0:
        def_x /= tw; def_y /= tw; def_z /= tw
    diff = math.sqrt((def_x - x)**2 + (def_y - y)**2 + (def_z - z)**2)
    print(f"V{v:2d}: orig=({x:.3f}, {y:.3f}, {z:.3f}) -> def=({def_x:.3f}, {def_y:.3f}, {def_z:.3f}) diff={diff:.3f} | {' '.join(influences_str)}")
