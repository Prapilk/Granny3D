from scratch_test33 import get_grn_data, mat_mul, mat_quat, mat_trans, mat_inv_affine, mat_transform
import math

def match_skeleton_by_tree(m_bones, anim_bones):
    count = len(m_bones)
    m_to_anim = [-1] * count

    m_pelvis = -1
    for i, b in enumerate(m_bones):
        if b['pos'][2] > 0.8 and b['parent'] <= 1:
            m_pelvis = i
            break

    a_pelvis = -1
    for j, ab in enumerate(anim_bones):
        if ab['pos'][2] > 0.8 and ab['parent'] <= 1:
            a_pelvis = j
            break

    if m_pelvis >= 0 and a_pelvis >= 0:
        m_to_anim[m_pelvis] = a_pelvis
        match_children(m_bones, m_pelvis, anim_bones, a_pelvis, m_to_anim)

    for i in range(count):
        if m_to_anim[i] >= 0: continue
        b = m_bones[i]
        best_j = -1
        best_dist = 1e9
        for j, ab in enumerate(anim_bones):
            pd = math.sqrt(sum((x-y)**2 for x,y in zip(b['pos'], ab['pos'])))
            if pd < best_dist:
                best_dist = pd
                if pd < 0.05:
                    best_j = j
        m_to_anim[i] = best_j

    return m_to_anim

def match_children(m_bones, m_parent, anim_bones, a_parent, m_to_anim):
    m_children = [i for i, b in enumerate(m_bones) if b['parent'] == m_parent and i != m_parent]
    a_children = [j for j, ab in enumerate(anim_bones) if ab['parent'] == a_parent and j != a_parent]

    matched_a = set()
    for mc in m_children:
        mb = m_bones[mc]
        best_ac = -1
        best_d = 1e9
        for ac in a_children:
            if ac in matched_a: continue
            ab = anim_bones[ac]
            pd = math.sqrt(sum((x-y)**2 for x,y in zip(mb['pos'], ab['pos'])))
            rd = math.sqrt(sum((x-y)**2 for x,y in zip(mb['rot'], ab['rot'])))
            score = pd * 2.0 + rd
            if score < best_d:
                best_d = score
                if pd < 0.05:
                    best_ac = ac
        if best_ac >= 0:
            m_to_anim[mc] = best_ac
            matched_a.add(best_ac)
            match_children(m_bones, mc, anim_bones, best_ac, m_to_anim)

m = get_grn_data(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn')
anim = get_grn_data(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn')

model_to_anim = match_skeleton_by_tree(m['bones'], anim['bones'])

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
for i in range(count):
    b = m['bones'][i]
    pos = b['pos']
    rot = b['rot']
    a_idx = model_to_anim[i]
    if 0 <= a_idx < len(anim['tracks']):
        tr = anim['tracks'][a_idx]
        pos = tr['p0']
        rot = tr['r0']
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

for i in range(count):
    w_rest = (world_bind[i][3][0], world_bind[i][3][1], world_bind[i][3][2])
    w_def = mat_transform(w_rest, skin_mat[i])
    d = math.sqrt(sum((x-y)**2 for x,y in zip(w_rest, w_def)))
    ai = model_to_anim[i]
    print('bone %2d (p=%2d) -> AnimBone %2d | RestPos: (%5.2f,%5.2f,%5.2f) -> DefPos: (%5.2f,%5.2f,%5.2f) | Diff: %.3fm' % (
        i, m['bones'][i]['parent'], ai, w_rest[0], w_rest[1], w_rest[2], w_def[0], w_def[1], w_def[2], d
    ))
