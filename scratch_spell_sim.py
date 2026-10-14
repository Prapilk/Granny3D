import scratch_spell as S

def eval_vec(t, times, vals):
    if not times or not vals: return None
    if len(times) == 1 or t <= times[0]: return vals[0]
    if t >= times[-1]: return vals[-1]
    i = 0
    while i < len(times)-1 and times[i+1] < t: i += 1
    f = (t - times[i])/(times[i+1]-times[i])
    return tuple(vals[i][k] + (vals[i+1][k]-vals[i][k])*f for k in range(3))

def eval_rot(t, times, vals):
    if not times or not vals: return None
    if len(times) == 1 or t <= times[0]: return vals[0]
    if t >= times[-1]: return vals[-1]
    i = 0
    while i < len(times)-1 and times[i+1] < t: i += 1
    f = (t - times[i])/(times[i+1]-times[i])
    a, b = vals[i], vals[i+1]
    import math
    dot = sum(a[k]*b[k] for k in range(4))
    if dot < 0: b = tuple(-x for x in b); dot = -dot
    dot = max(-1.0, min(1.0, dot))
    if dot > 0.9995:
        q = tuple(a[k]+(b[k]-a[k])*f for k in range(4))
        n = math.sqrt(sum(x*x for x in q)) or 1.0
        return tuple(x/n for x in q)
    th = math.acos(dot); s = math.sin(th)
    s0 = math.sin((1-f)*th)/s; s1 = math.sin(f*th)/s
    return tuple(a[k]*s0+b[k]*s1 for k in range(4))

local_bind = [S.mat_mul(S.quat_to_mat(b['rot']), S.trans_mat(b['pos'])) for b in S.bones]
bind_track = {b['id']: S.tracks.get(b['name']) for b in S.bones}

def pose(t, strip_root):
    local = list(local_bind)
    for b in S.bones:
        tr = bind_track[b['id']]
        if tr is None: continue
        pos = eval_vec(t, tr['timesP'], tr['pos'])
        rot = eval_rot(t, tr['timesR'], tr['rot'])
        if rot is None: rot = b['rot']
        if pos is None: pos = b['pos']
        if strip_root and b['name'] == 'Bip01':
            pos = b['pos']
        local[b['id']] = S.mat_mul(S.quat_to_mat(rot), S.trans_mat(pos))
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

idx = {b['name']: b['id'] for b in S.bones}
def wp(w, n):
    i = idx.get(n)
    if i is None: return None
    return (w[i][3][0], w[i][3][1], w[i][3][2])

dur = 2.317
print(" t    | PelvisZ  LFootZ  LFootX  RFootZ  RFootX  (STRIP) | PelvisZ  LFootZ  LFootX  RFootZ  RFootX  (FREE)")
for s in range(17):
    t = dur*s/16
    ws = pose(t, True)
    wf = pose(t, False)
    print(f"{t:4.2f} | {wp(ws,'Bip01 Pelvis')[2]:6.3f}  {wp(ws,'Bip01 L Foot')[2]:6.3f} {wp(ws,'Bip01 L Foot')[0]:6.3f} {wp(ws,'Bip01 R Foot')[2]:6.3f} {wp(ws,'Bip01 R Foot')[0]:6.3f}  | "
          f"{wp(wf,'Bip01 Pelvis')[2]:6.3f}  {wp(wf,'Bip01 L Foot')[2]:6.3f} {wp(wf,'Bip01 L Foot')[0]:6.3f} {wp(wf,'Bip01 R Foot')[2]:6.3f} {wp(wf,'Bip01 R Foot')[0]:6.3f}")
