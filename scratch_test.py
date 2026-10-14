import struct
import math

def mat_ident():
    return [[1.0 if i==j else 0.0 for j in range(4)] for i in range(4)]

def mat_mul(a, b):
    # a @ b (row-vector convention: v * (a * b) = (v * a) * b)
    res = [[0.0]*4 for _ in range(4)]
    for i in range(4):
        for j in range(4):
            res[i][j] = sum(a[i][k] * b[k][j] for k in range(4))
    return res

def mat_quat(q):
    x, y, z, w = q
    xx, xy, xz, xw = x*x, x*y, x*z, x*w
    yy, yz, yw = y*y, y*z, y*w
    zz, zw = z*z, z*w
    m = mat_ident()
    m[0][0] = 1.0 - 2.0 * (yy + zz)
    m[0][1] = 2.0 * (xy + zw)
    m[0][2] = 2.0 * (xz - yw)
    m[1][0] = 2.0 * (xy - zw)
    m[1][1] = 1.0 - 2.0 * (xx + zz)
    m[1][2] = 2.0 * (yz + xw)
    m[2][0] = 2.0 * (xz + yw)
    m[2][1] = 2.0 * (yz - xw)
    m[2][2] = 1.0 - 2.0 * (xx + yy)
    return m

def mat_trans(p):
    m = mat_ident()
    m[3][0] = p[0]
    m[3][1] = p[1]
    m[3][2] = p[2]
    return m

def mat_inv_affine(m):
    # Inverse of affine transformation matrix (Rotation R, Translation T)
    # [ R   0 ]^-1 = [ R^T     0 ]
    # [ T   1 ]      [ -T*R^T  1 ]
    # upper 3x3 transpose
    inv = mat_ident()
    for i in range(3):
        for j in range(3):
            inv[i][j] = m[j][i]
    # new translation = -T * R^T
    tx, ty, tz = m[3][0], m[3][1], m[3][2]
    inv[3][0] = -(tx * inv[0][0] + ty * inv[1][0] + tz * inv[2][0])
    inv[3][1] = -(tx * inv[0][1] + ty * inv[1][1] + tz * inv[2][1])
    inv[3][2] = -(tx * inv[0][2] + ty * inv[1][2] + tz * inv[2][2])
    return inv

def mat_transform(v, m):
    # v * m
    x = v[0]*m[0][0] + v[1]*m[1][0] + v[2]*m[2][0] + m[3][0]
    y = v[0]*m[0][1] + v[1]*m[1][1] + v[2]*m[2][1] + m[3][1]
    z = v[0]*m[0][2] + v[1]*m[1][2] + v[2]*m[2][2] + m[3][2]
    return (x, y, z)

def parse_grn(path):
    with open(path, "rb") as f:
        data = f.read()
    magic, num_root = struct.unpack("<II", data[0x40:0x48])
    pos = 0x48 + 24
    flat = []
    for i in range(num_root):
        cid, _, data_off, _, _ = struct.unpack("<IIIII", data[pos:pos+20])
        pos += 20
        sub_c, = struct.unpack("<I", data[data_off:data_off+4])
        list_pos = data_off + 16
        for j in range(sub_c):
            p = list_pos + j * 12
            ccid, local_off, sub_child = struct.unpack("<III", data[p:p+12])
            flat.append((ccid, data_off + local_off, sub_child))

    sorted_chunks = sorted([c for c in flat if c[1] > 0], key=lambda x: x[1])
    chunk_sizes = {}
    for i in range(len(sorted_chunks)-1):
        chunk_sizes[sorted_chunks[i][1]] = sorted_chunks[i+1][1] - sorted_chunks[i][1]
    if sorted_chunks:
        chunk_sizes[sorted_chunks[-1][1]] = len(data) - sorted_chunks[-1][1]

    # Strings
    str_chunks = [c for c in flat if c[0] == 0xca5e0200]
    s_list = []
    if str_chunks:
        sc = str_chunks[-1]
        off = sc[1]
        count, tot_len = struct.unpack("<II", data[off:off+8])
        raw_str = data[off+8:off+8+tot_len]
        s_list = [s.decode("utf-8", errors="ignore") for s in raw_str.split(b"\x00")]

    props = [c for c in flat if c[0] == 0xca5e0f01]
    obj_names = []
    for p in props:
        pdata = struct.unpack("<IIII", data[p[1]:p[1]+16])
        if pdata[0] == 4:
            obj_names.append(s_list[pdata[2]] if pdata[2] < len(s_list) else "")

    bone_chunks = [c for c in flat if c[0] == 0xca5e0506]
    b00_chunks = [c for c in flat if c[0] == 0xca5e0b00]
    bones = []
    for i in range(len(bone_chunks)):
        off = bone_chunks[i][1]
        parent, = struct.unpack("<i", data[off:off+4])
        pos = struct.unpack("<fff", data[off+4:off+16])
        rot = struct.unpack("<ffff", data[off+16:off+32])
        bname = f"Bone_{i}"
        if i < len(b00_chunks):
            target_obj_idx, = struct.unpack("<I", data[b00_chunks[i][1]:b00_chunks[i][1]+4])
            if 0 <= target_obj_idx - 1 < len(obj_names):
                bname = obj_names[target_obj_idx - 1]
        bones.append({
            "id": i, "name": bname, "parent": parent,
            "pos": pos, "rot": rot
        })

    pts_chunks = [c for c in flat if c[0] == 0xca5e0801]
    skin_chunks = [c for c in flat if c[0] == 0xca5e0702]
    c0a_chunks = [c for c in flat if c[0] == 0xca5e0c0a]
    bone_map = []
    for c in c0a_chunks:
        val, = struct.unpack("<I", data[c[1]:c[1]+4])
        bone_map.append(val)

    meshes = []
    for j in range(len(pts_chunks)):
        pts_chunk = pts_chunks[j]
        sz = chunk_sizes.get(pts_chunk[1], 0)
        num_v = sz // 12
        verts = []
        off = pts_chunk[1]
        for v in range(num_v):
            verts.append(struct.unpack("<fff", data[off+v*12:off+v*12+12]))

        skin = []
        if j < len(skin_chunks):
            soff = skin_chunks[j][1]
            num_verts, _, _ = struct.unpack("<iii", data[soff:soff+12])
            spos = soff + 12
            for v in range(num_verts):
                inf_count, = struct.unpack("<i", data[spos:spos+4])
                spos += 4
                infs = []
                for k in range(inf_count):
                    local_b, w = struct.unpack("<if", data[spos:spos+8])
                    spos += 8
                    master_b = bone_map[local_b] if 0 <= local_b < len(bone_map) else local_b
                    infs.append((master_b, w))
                skin.append(infs)

        meshes.append({"verts": verts, "skin": skin})

    track_chunks = [c for c in flat if c[0] == 0xca5e1204]
    tracks = {}
    for tc in track_chunks:
        off = tc[1]
        h0, = struct.unpack("<I", data[off:off+4])
        cP, cR, cS = struct.unpack("<III", data[off+24:off+36])
        tpos = off + 52
        times_p = struct.unpack(f"<{cP}f", data[tpos:tpos+cP*4]) if cP else []
        tpos += cP * 4
        times_r = struct.unpack(f"<{cR}f", data[tpos:tpos+cR*4]) if cR else []
        tpos += cR * 4
        tpos += cS * 4
        pos_keys = []
        for p in range(cP):
            pos_keys.append(struct.unpack("<fff", data[tpos:tpos+12]))
            tpos += 12
        rot_keys = []
        for r in range(cR):
            rot_keys.append(struct.unpack("<ffff", data[tpos:tpos+16]))
            tpos += 16
        tname = s_list[h0] if h0 < len(s_list) else ""
        tracks[tname] = {
            "times_p": times_p, "pos": pos_keys,
            "times_r": times_r, "rot": rot_keys
        }

    return {"bones": bones, "meshes": meshes, "tracks": tracks}

def bbox(verts):
    min_x = min(v[0] for v in verts)
    max_x = max(v[0] for v in verts)
    min_y = min(v[1] for v in verts)
    max_y = max(v[1] for v in verts)
    min_z = min(v[2] for v in verts)
    max_z = max(v[2] for v in verts)
    return f"X:[{min_x:.1f}, {max_x:.1f}] Y:[{min_y:.1f}, {max_y:.1f}] Z:[{min_z:.1f}, {max_z:.1f}]"

model = parse_grn(r"d:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn")
torso = parse_grn(r"d:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_Torso_V2_LOD2.grn")
anim = parse_grn(r"d:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn")

print("Rest H_Male_Lod2 bbox:", bbox(model["meshes"][0]["verts"]))
print("Rest Torso bbox:", bbox(torso["meshes"][0]["verts"]))

def deform_model(m_data, use_name_matching):
    bones = m_data["bones"]
    count = len(bones)
    # Bind matrices
    local_bind = [mat_mul(mat_quat(b["rot"]), mat_trans(b["pos"])) for b in bones]
    world_bind = [None] * count
    inv_bind = [None] * count
    for i in range(count):
        p = bones[i]["parent"]
        if p == i or p < 0 or p >= i:
            world_bind[i] = local_bind[i]
        else:
            world_bind[i] = mat_mul(local_bind[i], world_bind[p])
        inv_bind[i] = mat_inv_affine(world_bind[i])

    # Anim matrices
    local_anim = []
    for i in range(count):
        b = bones[i]
        pos = b["pos"]
        rot = b["rot"]
        if use_name_matching:
            tr = anim["tracks"].get(b["name"])
        else:
            tr = list(anim["tracks"].values())[i] if i < len(anim["tracks"]) else None
        if tr and tr["pos"] and tr["rot"]:
            pos = tr["pos"][0]
            rot = tr["rot"][0]
        local_anim.append(mat_mul(mat_quat(rot), mat_trans(pos)))

    world_anim = [None] * count
    skin_m = [None] * count
    for i in range(count):
        p = bones[i]["parent"]
        if p == i or p < 0 or p >= i:
            world_anim[i] = local_anim[i]
        else:
            world_anim[i] = mat_mul(local_anim[i], world_anim[p])
        skin_m[i] = mat_mul(inv_bind[i], world_anim[i])

    deformed = []
    mesh = m_data["meshes"][0]
    verts = mesh["verts"]
    skin = mesh["skin"]
    for v in range(len(verts)):
        orig = verts[v]
        infs = skin[v] if v < len(skin) else []
        if not infs:
            deformed.append(orig)
            continue
        tot_w = 0.0
        acc_x, acc_y, acc_z = 0.0, 0.0, 0.0
        for b_id, w in infs:
            if 0 <= b_id < count:
                tp = mat_transform(orig, skin_m[b_id])
                acc_x += tp[0] * w
                acc_y += tp[1] * w
                acc_z += tp[2] * w
                tot_w += w
        if tot_w > 1e-4:
            deformed.append((acc_x/tot_w, acc_y/tot_w, acc_z/tot_w))
        else:
            deformed.append(orig)
    return deformed

print("\n--- H_Male_Lod2 ---")
def_m_old = deform_model(model, False)
print("Old deformed bbox:", bbox(def_m_old))
def_m_new = deform_model(model, True)
print("New deformed bbox:", bbox(def_m_new))

print("\n--- Torso (Modular Part) ---")
def_t_old = deform_model(torso, False)
print("Old deformed bbox:", bbox(def_t_old))
def_t_new = deform_model(torso, True)
print("New deformed bbox:", bbox(def_t_new))
