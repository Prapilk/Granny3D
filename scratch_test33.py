import struct, math, os
from scratch_test import mat_mul, mat_quat, mat_trans, mat_inv_affine, mat_transform, bbox

def get_grn_data(path):
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

    b00 = [struct.unpack('<i', data[c[1]:c[1]+4])[0] for c in chunks if c[0] == 0xCA5E0B00]
    bone_chunks = [c for c in chunks if c[0] == 0xCA5E0506]
    bones = []
    for i, c in enumerate(bone_chunks):
        p, = struct.unpack('<i', data[c[1]:c[1]+4])
        pos = struct.unpack('<fff', data[c[1]+4:c[1]+16])
        rot = struct.unpack('<ffff', data[c[1]+16:c[1]+32])
        bones.append({'id': i, 'parent': p, 'pos': pos, 'rot': rot})

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

    # Tracks (chunk i is Track i, corresponding 1-to-1 with anim bone i)
    track_chunks = [c for c in chunks if c[0] == 0xCA5E1204]
    tracks = []
    for i, tc in enumerate(track_chunks):
        off = tc[1]
        cP, cR, cS = struct.unpack('<iii', data[off+24:off+36])
        tpos = off + 52 + (cP + cR + cS) * 4
        p0 = struct.unpack('<fff', data[tpos:tpos+12]) if cP > 0 else (0,0,0)
        rot_off = tpos + cP * 12
        r0 = struct.unpack('<ffff', data[rot_off:rot_off+16]) if cR > 0 else (0,0,0,1)
        tracks.append({'id': i, 'p0': p0, 'r0': r0})

    return {'bones': bones, 'verts': verts, 'skin': skin, 'tracks': tracks}

anim = get_grn_data(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn')
print(f"Loaded anim with {len(anim['bones'])} bones and {len(anim['tracks'])} tracks")

def test_deform(path):
    fname = os.path.basename(path)
    m = get_grn_data(path)
    if not m['verts']: return

    # Match each model bone to anim bone
    # Match criteria:
    # Pelvis (local_pos.z > 0.8) matches Anim Bone 2
    # Others match by closest local_pos
    model_to_anim = []
    for i, b in enumerate(m['bones']):
        if b['pos'][2] > 0.8 and b['parent'] <= 1:
            model_to_anim.append(2) # Pelvis
            continue
        matches = []
        for j, ab in enumerate(anim['bones']):
            pd = math.sqrt(sum((x-y)**2 for x,y in zip(b['pos'], ab['pos'])))
            rd = math.sqrt(sum((x-y)**2 for x,y in zip(b['rot'], ab['rot'])))
            score = pd * 2.0 + rd
            matches.append((score, pd, j))
        matches.sort(key=lambda x: x[0])
        best = matches[0]
        if best[1] < 0.15:
            model_to_anim.append(best[2])
        else:
            model_to_anim.append(-1)

    count = len(m['bones'])
    local_bind = [mat_mul(mat_quat(b['rot']), mat_trans(b['pos'])) for b in m['bones']]
    world_bind = [None] * count
    inv_bind = [None] * count
    for i in range(count):
        p = m['bones'][i]['parent']
        if p == i or p < 0 or p >= i:
            world_bind[i] = local_bind[i]
        else:
            world_bind[i] = mat_mul(local_bind[i], world_bind[p])
        inv_bind[i] = mat_inv_affine(world_bind[i])

    local_anim = []
    matched = 0
    for i in range(count):
        b = m['bones'][i]
        pos = b['pos']
        rot = b['rot']
        a_idx = model_to_anim[i]
        if 0 <= a_idx < len(anim['tracks']):
            tr = anim['tracks'][a_idx]
            pos = tr['p0']
            rot = tr['r0']
            matched += 1
        local_anim.append(mat_mul(mat_quat(rot), mat_trans(pos)))

    world_anim = [None] * count
    skin_mat = [None] * count
    for i in range(count):
        p = m['bones'][i]['parent']
        if p == i or p < 0 or p >= i:
            world_anim[i] = local_anim[i]
        else:
            world_anim[i] = mat_mul(local_anim[i], world_anim[p])
        skin_mat[i] = mat_mul(inv_bind[i], world_anim[i])

    deformed = []
    diffs = []
    for v in range(len(m['verts'])):
        orig = m['verts'][v]
        infs = m['skin'][v]
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
    print(f"{fname:38s} | Matched: {matched:2d}/{count:2d} | Rest: {bbox(m['verts'])} -> Deformed: {bbox(deformed)} | AvgDiff: {avg_d:.3f}m MaxDiff: {max_d:.3f}m")

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

print("\n=== FINAL VALIDATION: TRACK i = ANIM BONE i ===")
for p in parts:
    test_deform(p)
