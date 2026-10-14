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
    chunks = [c for c in flat if c[0] == 0xCA5E0200]
    if not chunks: return []
    off = chunks[-1][1]
    count, tot = struct.unpack('<ii', data[off:off+8])
    p = off + 8
    strings = []
    for _ in range(count):
        end = data.find(b'\x00', p)
        strings.append(data[p:end].decode('latin1'))
        p = end + 1
    return strings

def object_names(data, flat, strings):
    objectNames = []
    cur = ''; has = False
    for c in flat:
        if c[0] == 0xCA5E0F00:
            if has: objectNames.append(cur)
            cur = 'NO_NAME'; has = True
        elif c[0] == 0xCA5E0F01 and has:
            v0, v1, v2, v3 = struct.unpack('<iiii', data[c[1]:c[1]+16])
            if v0 == 4 and 0 <= v2 < len(strings):
                cur = strings[v2]
    if has: objectNames.append(cur)
    return objectNames

def chunk_sizes(data, flat):
    s = sorted([c for c in flat if c[1] > 0], key=lambda c: c[1])
    out = []
    for i in range(len(s)):
        nxt = s[i+1][1] if i < len(s)-1 else 0xFFFFFFFF
        out.append((s[i][0], s[i][1], nxt - s[i][1]))
    return out

def build_pointers(data, flat):
    sizes = chunk_sizes(data, flat)
    b00 = [struct.unpack('<i', data[c[1]:c[1]+4])[0] for c in flat if c[0] == 0xCA5E0B00]
    ptrs2 = []
    for s in sizes:
        if s[0] == 0xCA5E0C02 and s[2] != 0xFFFFFFFF:
            n = s[2] // 4
            for k in range(n):
                ptrs2.append(struct.unpack('<i', data[s[1]+k*4:s[1]+k*4+4])[0])
    return b00, ptrs2

def plugin_name(idx, b00, ptrs2, objs, fallback):
    objPtrB = (ptrs2[idx] - 1) if idx < len(ptrs2) else -1
    if 0 <= objPtrB < len(b00):
        objIdxB = b00[objPtrB] - 1
        if 0 <= objIdxB < len(objs):
            return objs[objIdxB]
    return fallback

def quat_to_mat(q):
    x, y, z, w = q
    xx, yy, zz = x*x, y*y, z*z
    xy, xz, yz = x*y, x*z, y*z
    wx, wy, wz = w*x, w*y, w*z
    return [
        [1-2*(yy+zz), 2*(xy+wz), 2*(xz-wy), 0],
        [2*(xy-wz), 1-2*(xx+zz), 2*(yz+wx), 0],
        [2*(xz+wy), 2*(yz-wx), 1-2*(xx+yy), 0],
        [0, 0, 0, 1]
    ]

def trans_mat(p):
    return [[1,0,0,0],[0,1,0,0],[0,0,1,0],[p[0],p[1],p[2],1]]

def mat_mul(a, b):
    c = [[0.0]*4 for _ in range(4)]
    for i in range(4):
        for j in range(4):
            c[i][j] = sum(a[i][k]*b[k][j] for k in range(4))
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

MESH = r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn'
WALK = r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn'

mdata, mflat = inspect_file(MESH)
mstr = parse_strings(mdata, mflat)
mobjs = object_names(mdata, mflat, mstr)
mb00, mptrs2 = build_pointers(mdata, mflat)

wdata, wflat = inspect_file(WALK)
wstr = parse_strings(wdata, wflat)
wobjs = object_names(wdata, wflat, wstr)
wb00, wptrs2 = build_pointers(wdata, wflat)

# mesh bones
bones = []
for i, c in enumerate([c for c in mflat if c[0] == 0xCA5E0506]):
    p, = struct.unpack('<i', mdata[c[1]:c[1]+4])
    pos = struct.unpack('<fff', mdata[c[1]+4:c[1]+16])
    rot = struct.unpack('<ffff', mdata[c[1]+16:c[1]+32])
    nm = plugin_name(i, mb00, mptrs2, mobjs, f"Bone_{i}")
    bones.append({'id': i, 'p': p, 'pos': pos, 'rot': rot, 'name': nm})

# walk tracks
tracks = {}
track_chunks = [c for c in wflat if c[0] == 0xCA5E1204]
for i, c in enumerate(track_chunks):
    off = c[1]
    h0, = struct.unpack('<i', wdata[off:off+4])
    cP, cR, cS = struct.unpack('<iii', wdata[off+24:off+36])
    p = off + 52
    timesP = struct.unpack('<%df' % cP, wdata[p:p+cP*4]) if cP else ()
    p += cP*4
    timesR = struct.unpack('<%df' % cR, wdata[p:p+cR*4]) if cR else ()
    p += cR*4
    p += cS*4  # scale times
    pos = [struct.unpack('<fff', wdata[p+k*12:p+k*12+12]) for k in range(cP)]
    p += cP*12
    rot = [struct.unpack('<ffff', wdata[p+k*16:p+k*16+16]) for k in range(cR)]
    nm = plugin_name(i, wb00, wptrs2, wobjs, f"Track_{i}")
    tracks[nm] = {'timesP': timesP, 'timesR': timesR, 'pos': pos, 'rot': rot, 'idx': i}

# bind mesh bones to tracks by name
bind_track = {}
for b in bones:
    bind_track[b['id']] = tracks.get(b['name'])

# local bind matrices
local_bind = [mat_mul(quat_to_mat(b['rot']), trans_mat(b['pos'])) for b in bones]

def eval_vec(t, times, vals):
    if not times or not vals: return None
    if len(times) == 1 or t <= times[0]: return vals[0]
    if t >= times[-1]: return vals[-1]
    i = 0
    while i < len(times)-1 and times[i+1] < t: i += 1
    f = (t - times[i])/(times[i+1]-times[i])
    return tuple(vals[i][k] + (vals[i+1][k]-vals[i][k])*f for k in range(len(vals[i])))

def eval_quat(t, times, vals):
    if not times or not vals: return None
    if len(times) == 1 or t <= times[0]: return vals[0]
    if t >= times[-1]: return vals[-1]
    i = 0
    while i < len(times)-1 and times[i+1] < t: i += 1
    f = (t - times[i])/(times[i+1]-times[i])
    a, b = vals[i], vals[i+1]
    # slerp
    dot = sum(a[k]*b[k] for k in range(4))
    if dot < 0: b = tuple(-x for x in b); dot = -dot
    dot = max(-1.0, min(1.0, dot))
    if dot > 0.9995:
        q = tuple(a[k] + (b[k]-a[k])*f for k in range(4))
        norm = math.sqrt(sum(x*x for x in q)) or 1.0
        return tuple(x/norm for x in q)
    theta = math.acos(dot)
    s = math.sin(theta)
    s0 = math.sin((1-f)*theta)/s
    s1 = math.sin(f*theta)/s
    return tuple(a[k]*s0 + b[k]*s1 for k in range(4))

def pose(t):
    local = [local_bind[i] for i in range(len(bones))]
    for b in bones:
        tr = bind_track[b['id']]
        if tr is None: continue
        pos = eval_vec(t, tr['timesP'], tr['pos'])
        rot = eval_quat(t, tr['timesR'], tr['rot'])
        if rot is None: rot = b['rot']
        if pos is None: pos = b['pos']
        local[b['id']] = mat_mul(quat_to_mat(rot), trans_mat(pos))
    # world (topological; bones may not be ordered parents-first)
    world = [None]*len(bones)
    def get(i):
        if world[i] is not None: return world[i]
        par = bones[i]['p']
        if par < 0 or par == i or par >= len(bones):
            world[i] = local[i]
        else:
            world[i] = mat_mul(local[i], get(par))
        return world[i]
    for i in range(len(bones)): get(i)
    return world

def wpos(i, world):
    m = world[i]
    return (m[3][0], m[3][1], m[3][2])

# sample
dur = 0
for tr in tracks.values():
    if tr['timesP']: dur = max(dur, max(tr['timesP']))
    if tr['timesR']: dur = max(dur, max(tr['timesR']))
print(f"Walk duration ~ {dur:.3f}s")

names_of_interest = ['Bip01 Pelvis', 'Bip01 L Foot', 'Bip01 L Toe0', 'Bip01 R Foot', 'Bip01 R Toe0',
                     'Bip01 L Calf', 'Bip01 R Calf']
idx = {}
for b in bones:
    if b['name'] in names_of_interest:
        idx[b['name']] = b['id']
print("bone ids:", {k: idx[k] for k in names_of_interest if k in idx})

print("\n t    PelvisZ  LFootZ  LToeZ   RFootZ  RToeZ   LFootX   RFootX")
steps = 16
for s in range(steps+1):
    t = dur * s / steps
    w = pose(t)
    def gz(n): return wpos(idx[n], w)[2]
    def gx(n): return wpos(idx[n], w)[0]
    print(f"{t:4.2f}  {gz('Bip01 Pelvis'):6.3f}  {gz('Bip01 L Foot'):6.3f}  {gz('Bip01 L Toe0'):6.3f}  "
          f"{gz('Bip01 R Foot'):6.3f}  {gz('Bip01 R Toe0'):6.3f}  {gx('Bip01 L Foot'):7.3f}  {gx('Bip01 R Foot'):7.3f}")
