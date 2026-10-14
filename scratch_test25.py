import struct
from scratch_test14 import get_obj_names

objs_a, data_a, chunks_a = get_obj_names(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn')

track_chunks = [c for c in chunks_a if c[0] == 0xCA5E1204]
print(f"Total track chunks: {len(track_chunks)}")
for i, tc in enumerate(track_chunks):
    off = tc[1]
    h0, = struct.unpack('<i', data_a[off:off+4])
    cP, cR, cS = struct.unpack('<iii', data_a[off+24:off+36])
    tpos = off + 52 + (cP + cR + cS) * 4
    p0 = struct.unpack('<fff', data_a[tpos:tpos+12]) if cP > 0 else (0,0,0)
    obj_name = objs_a[h0 - 1]['name'] if 0 <= h0 - 1 < len(objs_a) else "OOB"
    print(f"Track {i:2d}: h0={h0:2d} -> Name='{obj_name:20s}' | p0=({p0[0]:.3f}, {p0[1]:.3f}, {p0[2]:.3f})")
