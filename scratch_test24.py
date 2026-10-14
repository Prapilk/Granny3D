import struct
from scratch_test14 import get_obj_names

objs_a, data_a, chunks_a = get_obj_names(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn')

# Check track at 0x7f18
off = 0x7f18
h0, = struct.unpack('<i', data_a[off:off+4])
cP, cR, cS = struct.unpack('<iii', data_a[off+24:off+36])
pos_off = off + 52 + (cP + cR + cS) * 4
p0 = struct.unpack('<fff', data_a[pos_off:pos_off+12]) if cP > 0 else (0,0,0)

print(f"Track at {hex(off)}: h0={h0} -> Object='{objs_a[h0 - 1]['name']}'")
print(f"  p0 = ({p0[0]:.3f}, {p0[1]:.3f}, {p0[2]:.3f})")
