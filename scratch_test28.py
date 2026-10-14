import struct, math, os
from scratch_test import mat_mul, mat_quat, mat_trans, mat_inv_affine, mat_transform, bbox

def load_grn_full(path):
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

    # Strings
    str_chunks = [c for c in chunks if c[0] == 0xCA5E0200]
    sc = str_chunks[-1] if str_chunks else None
    strings = []
    if sc:
        count, _ = struct.unpack('<ii', data[sc[1]:sc[1]+8])
        pos = sc[1] + 8
        for _ in range(count):
            end = data.find(b'\x00', pos)
            strings.append(data[pos:end].decode('latin1'))
            pos = end + 1

    # Objects
    objects = []
    current_obj = None
    for scid, off in chunks:
        if scid == 0xCA5E0F00:
            current_obj = {'name': 'NO_NAME'}
            objects.append(current_obj)
        elif scid == 0xCA5E0F01 and current_obj is not None:
            v0, v1, v2, v3 = struct.unpack('<iiii', data[off:off+16])
            if v0 == 4 and v2 < len(strings):
                current_obj['name'] = strings[v2]

    b00 = [struct.unpack('<i', data[c[1]:c[1]+4])[0] for c in chunks if c[0] == 0xCA5E0B00]
    bone_chunks = [c for c in chunks if c[0] == 0xCA5E0506]
    bones = []
    for i, c in enumerate(bone_chunks):
        p, = struct.unpack('<i', data[c[1]:c[1]+4])
        pos = struct.unpack('<fff', data[c[1]+4:c[1]+16])
        rot = struct.unpack('<ffff', data[c[1]+16:c[1]+32])
        name = f"Bone_{i}"
        if i < len(b00) and 0 <= b00[i] - 1 < len(objects):
            name = objects[b00[i] - 1]['name']
        bones.append({'id': i, 'parent': p, 'pos': pos, 'rot': rot, 'name': name})

    # Mesh & Skin
    c0a = [struct.unpack('<i', data[c[1]:c[1]+4])[0] for c in chunks if c[0] == 0xCA5E0C0A]
    bone_map = [b00.index(x) if x in b00 else (x if x < len(bones) else 0) for x in c0a]

    pts_chunk = [c for c in chunks if c[0] == 0xCA5E0801]
    skin_chunk = [c for c in chunks if c[0] == 0xCA5E0702]

    verts = []
    skin = []
    if pts_chunk and skin_chunk:
        poff = pts_chunk[0][1]
        soff = skin_chunk[0][1]
        num_v, _, _ = struct.unpack('<iii', data[soff:soff+12])
        verts = [struct.unpack('<fff', data[poff+v*12:poff+(v+1)*12]) for v in range(num_v)]
        spos = soff + 12
        for v in range(num_v):
            inf_count, = struct.unpack('<i', data[spos:spos+4])
            spos += 4
            infs = []
            for k in range(inf_count):
                local_b, w = struct.unpack('<if', data[spos:spos+8])
                spos += 8
                b_idx = bone_map[local_b] if local_b < len(bone_map) else local_b
                infs.append((b_idx, w))
            skin.append(infs)

    # Tracks
    track_chunks = [c for c in chunks if c[0] == 0xCA5E1204]
    tracks = []
    for i, tc in enumerate(track_chunks):
        off = tc[1]
        cP, cR, cS = struct.unpack('<iii', data[off+24:off+36])
        tpos = off + 52 + (cP + cR + cS) * 4
        p0 = struct.unpack('<fff', data[tpos:tpos+12]) if cP > 0 else (0,0,0)
        rot_off = tpos + cP * 12
        r0 = struct.unpack('<ffff', data[rot_off:rot_off+16]) if cR > 0 else (0,0,0,1)
        name = f"Track_{i}"
        if i < len(b00) and 0 <= b00[i] - 1 < len(objects):
            name = objects[b00[i] - 1]['name']
        tracks.append({'id': i, 'name': name, 'p0': p0, 'r0': r0})

    return {'bones': bones, 'verts': verts, 'skin': skin, 'tracks': tracks}

# Load anim
anim = load_grn_full(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn')

def test_model(path):
    fname = os.path.basename(path)
    model = load_grn_full(path)
    if not model['verts']:
        print(f"Skipping {fname} (no verts)")
        return

    # Match each bone to anim track
    # Dual match:
    # 1. Exact Name match
    # 2. Geometric match (p_dist * 2.0 + r_dist)
    anim_by_name = {t['name']: t for t in anim['tracks']}
    
    tb = model['bones']
    count = len(tb)
    bone_to_track = []
    for i, b in enumerate(tb):
        if b['name'] in anim_by_name:
            bone_to_track.append(anim_by_name[b['name']])
        else:
            # find best geometric match
            best_t = None
            best_score = 999.0
            for t in anim['tracks']:
                # compare t['p0'] to b['pos']
                pd = math.sqrt(sum((x-y)**2 for x,y in zip(b['pos'], t['p0'])))
                rd = math.sqrt(sum((x-y)**2 for x,y in zip(b['rot'], t['r0'])))
                score = pd * 2.0 + rd
                if score < best_score:
                    best_score = score
                    best_t = t
            if best_score < 0.2:
                bone_to_track.append(best_t)
            else:
                bone_to_track.append(None)

    # Bind matrices
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
        tr = bone_to_track[i]
        if tr:
            pos = tr['p0']
            rot = tr['r0']
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

    # Deform
    deformed = []
    diffs = []
    for v in range(len(model['verts'])):
        orig = model['verts'][v]
        infs = model['skin'][v]
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
        diffs.append(math.sqrt(sum((x-y)**2 for x,y in zip(orig, p_def))))

    avg_d = sum(diffs)/len(diffs)
    max_d = max(diffs)
    print(f"{fname:38s} | Verts: {len(model['verts']):4d} | Rest: {bbox(model['verts'])} -> Deformed: {bbox(deformed)} | AvgDiff: {avg_d:.3f}m MaxDiff: {max_d:.3f}m")

# Test all parts of human male
parts = [
    r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn',
    r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_Torso_V2_LOD2.grn',
    r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_UArms_V2_LOD2.grn',
    r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_FArms_V2_LOD2.grn',
    r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_Hands_V2_LOD2.grn',
    r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_ULegs_V2_LOD2.grn',
    r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_LLegs_V2_LOD2.grn',
    r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_Head_V2_LOD2.grn',
    r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_Neck_V2_LOD2.grn',
    r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_Pelvis_V2_LOD2.grn',
    r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_Feet_V2_LOD2.grn',
    r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_Armor_Leather_Tunic_V2_LOD2.grn'
]

print("=== BATCH DEFORMATION VALIDATION ===")
for p in parts:
    test_model(p)
