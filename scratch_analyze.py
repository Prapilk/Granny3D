import sys
import scratch_spell as S

# re-parse a different anim by re-pointing S.ANIM is not enough (already loaded);
# instead re-load here using S's helpers.
path = sys.argv[1] if len(sys.argv) > 1 else S.ANIM

adata, aflat = S.inspect_file(path)
astr = S.parse_strings(adata, aflat)
aobjs = S.object_names(adata, aflat, astr)
ab00, aptrs2 = S.build_pointers(adata, aflat)

tracks = {}
for i, c in enumerate([c for c in aflat if c[0] == 0xCA5E1204]):
    off = c[1]
    h0, = struct.unpack('<i', adata[off:off+4]) if False else (__import__('struct').unpack('<i', adata[off:off+4])[0],)
    import struct as _st
    h0, = _st.unpack('<i', adata[off:off+4])
    cP, cR, cS = _st.unpack('<iii', adata[off+24:off+36])
    p = off + 52
    timesP = _st.unpack('<%df' % cP, adata[p:p+cP*4]) if cP else ()
    p += cP*4
    timesR = _st.unpack('<%df' % cR, adata[p:p+cR*4]) if cR else ()
    p += cR*4
    p += cS*4
    pos = [_st.unpack('<fff', adata[p+k*12:p+k*12+12]) for k in range(cP)]
    p += cP*12
    rot = [_st.unpack('<ffff', adata[p+k*16:p+k*16+16]) for k in range(cR)]
    nm = S.plugin_name(i, ab00, aptrs2, aobjs, f"Track_{i}")
    tracks[nm] = {'timesP': timesP, 'timesR': timesR, 'pos': pos, 'rot': rot}

import math
dur = 0
for tr in tracks.values():
    if tr['timesP']: dur = max(dur, max(tr['timesP']))
    if tr['timesR']: dur = max(dur, max(tr['timesR']))

print(f"=== {path.split(chr(92))[-1]}  dur={dur:.3f}s ===")
print("Root ('Bip01') position keys over time:")
bip = tracks.get('Bip01')
if bip:
    for t, p in zip(bip['timesP'], bip['pos']):
        print(f"  t={t:6.3f}  pos=({p[0]:+.3f},{p[1]:+.3f},{p[2]:+.3f})")
print("\nBones with non-constant position (>5mm):")
for nm, tr in sorted(tracks.items()):
    if not tr['pos'] or len(tr['pos']) < 2: continue
    mn = list(tr['pos'][0]); mx = list(tr['pos'][0])
    for p in tr['pos'][1:]:
        for k in range(3):
            mn[k] = min(mn[k], p[k]); mx[k] = max(mx[k], p[k])
    rng = math.sqrt(sum((mx[k]-mn[k])**2 for k in range(3)))
    if rng > 0.005:
        print(f"  {nm:20s} nP={len(tr['pos'])} range={rng:.3f}  Zmin={mn[2]:.3f} Zmax={mx[2]:.3f}")
