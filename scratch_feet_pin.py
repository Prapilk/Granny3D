import scratch_feet_sim as S

idx = {b['name']: b['id'] for b in S.bones}
names_of_interest = ['Bip01 Pelvis', 'Bip01 L Foot', 'Bip01 L Toe0', 'Bip01 R Foot', 'Bip01 R Toe0']

def build_world(local):
    world = [None]*len(S.bones)
    def get(i):
        if world[i] is not None: return world[i]
        par = S.bones[i]['p']
        if par < 0 or par == i or par >= len(S.bones):
            world[i] = local[i]
        else:
            world[i] = S.mat_mul(local[i], get(par))
        return world[i]
    for i in range(len(S.bones)): get(i)
    return world

def eval_pos(t, times, vals):
    if not times or not vals: return None
    if len(times) == 1 or t <= times[0]: return vals[0]
    if t >= times[-1]: return vals[-1]
    i = 0
    while i < len(times)-1 and times[i+1] < t: i += 1
    f = (t - times[i])/(times[i+1]-times[i])
    return tuple(vals[i][k] + (vals[i+1][k]-vals[i][k])*f for k in range(3))

def eval_rot(t, tr):
    times, vals = tr['timesR'], tr['rot']
    if not times or not vals: return None
    if len(times) == 1 or t <= times[0]: return vals[0]
    if t >= times[-1]: return vals[-1]
    i = 0
    while i < len(times)-1 and times[i+1] < t: i += 1
    f = (t - times[i])/(times[i+1]-times[i])
    a, b = vals[i], vals[i+1]
    dot = sum(a[k]*b[k] for k in range(4))
    if dot < 0: b = tuple(-x for x in b); dot = -dot
    dot = max(-1.0, min(1.0, dot))
    if dot > 0.9995:
        q = tuple(a[k]+(b[k]-a[k])*f for k in range(4))
        n = __import__('math').sqrt(sum(x*x for x in q)) or 1.0
        return tuple(x/n for x in q)
    th = __import__('math').acos(dot); s = __import__('math').sin(th)
    s0 = __import__('math').sin((1-f)*th)/s; s1 = __import__('math').sin(f*th)/s
    return tuple(a[k]*s0+b[k]*s1 for k in range(4))

import math

def pose(t, pin_root):
    local = [S.local_bind[i] for i in range(len(S.bones))]
    for b in S.bones:
        tr = S.bind_track[b['id']]
        if tr is None: continue
        pos = eval_pos(t, tr['timesP'], tr['pos'])
        rot = eval_rot(t, tr)
        if rot is None: rot = b['rot']
        if pos is None: pos = b['pos']
        if pin_root and b['name'] == 'Bip01':
            pos = b['pos']  # pin root to bind position (strips vertical bounce)
        local[b['id']] = S.mat_mul(S.quat_to_mat(rot), S.trans_mat(pos))
    return build_world(local)

def gz(w, n): return w[idx[n]][3][2]

dur = 0.983
print("Root FREE (current plugin behavior) vs Root PINNED to bind:")
print(" t    | LFoot  LToe  RFoot  RToe (FREE)      | LFoot  LToe  RFoot  RToe (PINNED)")
for s in range(16+1):
    t = dur*s/16
    wf = pose(t, False)
    wp = pose(t, True)
    print(f"{t:4.2f} | {gz(wf,'Bip01 L Foot'):6.3f} {gz(wf,'Bip01 L Toe0'):6.3f} {gz(wf,'Bip01 R Foot'):6.3f} {gz(wf,'Bip01 R Toe0'):6.3f}      | "
          f"{gz(wp,'Bip01 L Foot'):6.3f} {gz(wp,'Bip01 L Toe0'):6.3f} {gz(wp,'Bip01 R Foot'):6.3f} {gz(wp,'Bip01 R Toe0'):6.3f}")
