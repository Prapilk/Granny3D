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
    return [[1-2*(yy+zz), 2*(xy+wz), 2*(xz-wy), 0],
            [2*(xy-wz), 1-2*(xx+zz), 2*(yz+wx), 0],
            [2*(xz+wy), 2*(yz-wx), 1-2*(xx+yy), 0],
            [0, 0, 0, 1]]

def trans_mat(p):
    return [[1,0,0,0],[0,1,0,0],[0,0,1,0],[p[0],p[1],p[2],1]]

def mat_mul(a, b):
    c = [[0.0]*4 for _ in range(4)]
    for i in range(4):
        for j in range(4):
            c[i][j] = sum(a[i][k]*b[k][j] for k in range(4))
    return c

MESH = r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn'
ANIM = r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Spell_Summon_2h_L2_01.grn'

mdata, mflat = inspect_file(MESH)
mstr = parse_strings(mdata, mflat)
mobjs = object_names(mdata, mflat, mstr)
mb00, mptrs2 = build_pointers(mdata, mflat)

adata, aflat = inspect_file(ANIM)
astr = parse_strings(adata, aflat)
aobjs = object_names(adata, aflat, astr)
ab00, aptrs2 = build_pointers(adata, aflat)

bones = []
for i, c in enumerate([c for c in mflat if c[0] == 0xCA5E0506]):
    p, = struct.unpack('<i', mdata[c[1]:c[1]+4])
    pos = struct.unpack('<fff', mdata[c[1]+4:c[1]+16])
    rot = struct.unpack('<ffff', mdata[c[1]+16:c[1]+32])
    nm = plugin_name(i, mb00, mptrs2, mobjs, f"Bone_{i}")
    bones.append({'id': i, 'p': p, 'pos': pos, 'rot': rot, 'name': nm})

tracks = {}
track_chunks = [c for c in aflat if c[0] == 0xCA5E1204]
for i, c in enumerate(track_chunks):
    off = c[1]
    h0, = struct.unpack('<i', adata[off:off+4])
    cP, cR, cS = struct.unpack('<iii', adata[off+24:off+36])
    p = off + 52
    timesP = struct.unpack('<%df' % cP, adata[p:p+cP*4]) if cP else ()
    p += cP*4
    timesR = struct.unpack('<%df' % cR, adata[p:p+cR*4]) if cR else ()
    p += cR*4
    p += cS*4
    pos = [struct.unpack('<fff', adata[p+k*12:p+k*12+12]) for k in range(cP)]
    p += cP*12
    rot = [struct.unpack('<ffff', adata[p+k*16:p+k*16+16]) for k in range(cR)]
    nm = plugin_name(i, ab00, aptrs2, aobjs, f"Track_{i}")
    tracks[nm] = {'timesP': timesP, 'timesR': timesR, 'pos': pos, 'rot': rot, 'idx': i}

def pos_range(pos):
    if not pos: return (None, None, 0.0)
    mn = list(pos[0]); mx = list(pos[0])
    for p in pos[1:]:
        for k in range(3):
            mn[k] = min(mn[k], p[k]); mx[k] = max(mx[k], p[k])
    rng = math.sqrt(sum((mx[k]-mn[k])**2 for k in range(3)))
    return (mn, mx, rng)

print(f"Anim tracks: {len(tracks)}, duration: (see below)")
dur = 0
for tr in tracks.values():
    if tr['timesP']: dur = max(dur, max(tr['timesP']))
    if tr['timesR']: dur = max(dur, max(tr['timesR']))
print(f"Duration = {dur:.3f}s\n")

print("=== Tracks with NON-CONSTANT position (root-motion candidates) ===")
for nm, tr in sorted(tracks.items(), key=lambda kv: -kv[1]['idx']):
    mn, mx, rng = pos_range(tr['pos'])
    if rng > 0.005:
        print(f"  {nm:20s} nP={len(tr['pos']):2d} nR={len(tr['rot']):3d} posRange={rng:.3f}  min={tuple(round(x,3) for x in mn)} max={tuple(round(x,3) for x in mx)}")

print("\n=== All track names ===")
for nm in sorted(tracks.keys()):
    print(f"  {nm}")
