import struct

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

def sizes(flat):
    # replicate plugin DataSize: sorted by DataOffset, diff to next, last=0xFFFFFFFF
    s = sorted([c for c in flat if c[1] > 0], key=lambda c: c[1])
    out = []
    for i in range(len(s)):
        if i < len(s) - 1:
            out.append((s[i][0], s[i][1], s[i+1][1] - s[i][1]))
        else:
            out.append((s[i][0], s[i][1], 0xFFFFFFFF))
    return out

def read_list(data, cid, flat):
    # concatenate DataSize/4 ints from every chunk of this id (plugin reads count=DataSize/4)
    sz = sizes(flat)
    dsize = {}
    for c in flat:
        dsize.setdefault(c[0], {})[c[1]] = None
    out = []
    for s in sz:
        if s[0] == cid:
            n = (int(s[2]) // 4) if s[2] != 0xFFFFFFFF else 0
            for k in range(n):
                out.append(struct.unpack('<i', data[s[1] + k*4: s[1] + k*4 + 4])[0])
    return out

def analyze(path, label):
    data, flat = inspect_file(path)
    strings = parse_strings(data, flat)
    objs = object_names(data, flat, strings)
    # b00: 42 separate 4-byte chunks -> read 1 int each (plugin does readInt32 per chunk)
    b00 = [struct.unpack('<i', data[c[1]:c[1]+4])[0] for c in flat if c[0] == 0xCA5E0B00]
    b01 = [struct.unpack('<i', data[c[1]:c[1]+4])[0] for c in flat if c[0] == 0xCA5E0B01]
    ptr0 = read_list(data, 0xCA5E0C00, flat)
    ptr1 = read_list(data, 0xCA5E0C01, flat)
    ptrs2 = read_list(data, 0xCA5E0C02, flat)
    sz = sizes(flat)
    def dsize(cid):
        return [s[2] for s in sz if s[0] == cid]
    print(f"=== {label} ===")
    print(f" strings={len(strings)} objectNames={len(objs)} b00={len(b00)} b01={len(b01)} ptr0={len(ptr0)} ptr1={len(ptr1)} ptrs2={len(ptrs2)}")
    print(f" DataSize 0xB00={dsize(0xCA5E0B00)} 0xB01={dsize(0xCA5E0B01)} 0xC00={dsize(0xCA5E0C00)} 0xC01={dsize(0xCA5E0C01)} 0xC02={dsize(0xCA5E0C02)}")
    return data, flat, strings, objs, b00, ptrs2

# WALK
wdata, wflat, wstr, wobjs, wb00, wptrs2 = analyze(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn', 'WALK')

def plugin_name(idx, h0, b00, ptrs2, objs, fallback):
    objPtrB = (ptrs2[idx] - 1) if idx < len(ptrs2) else (h0 - 1)
    if 0 <= objPtrB < len(b00):
        objIdxB = b00[objPtrB] - 1
        if 0 <= objIdxB < len(objs):
            return objs[objIdxB]
    return fallback

tracks = [c for c in wflat if c[0] == 0xCA5E1204]
print("\n--- WALK track names (plugin logic) ---")
for i, c in enumerate(tracks):
    off = c[1]
    h0, = struct.unpack('<i', wdata[off:off+4])
    cP, cR, cS = struct.unpack('<iii', wdata[off+24:off+36])
    pos_off = off + 52 + (cP + cR + cS) * 4
    p0 = struct.unpack('<fff', wdata[pos_off:pos_off+12]) if cP > 0 else (0, 0, 0)
    nm = plugin_name(i, h0, wb00, wptrs2, wobjs, f"Track_{i}")
    raw = wstr[h0] if 0 <= h0 < len(wstr) else f"?str{h0}"
    print(f"T{i:2d}: h0={h0:2d} plugin='{nm:22s}' rawStr='{raw}'")

# MESH bone names
mdata, mflat, mstr, mobjs, mb00, mptrs2 = analyze(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn', 'MESH')
bone_chunks = [c for c in mflat if c[0] == 0xCA5E0506]
print(f"\n--- MESH bone names (plugin logic), {len(bone_chunks)} bones ---")
for i, c in enumerate(bone_chunks):
    p, = struct.unpack('<i', mdata[c[1]:c[1]+4])
    pos = struct.unpack('<fff', mdata[c[1]+4:c[1]+16])
    nm = plugin_name(i, i, mb00, mptrs2, mobjs, f"Bone_{i}")
    print(f"B{i:2d} p={p:2d} '{nm:22s}' pos=({pos[0]:6.3f},{pos[1]:6.3f},{pos[2]:6.3f})")
