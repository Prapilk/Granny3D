from scratch_test14 import get_obj_names
import struct

objs_t, data_t, chunks_t = get_obj_names(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_Armor_Leather_Tunic_V2_LOD2.grn')

b00_t = [struct.unpack('<i', data_t[c[1]:c[1]+4])[0] for c in chunks_t if c[0] == 0xCA5E0B00]
c0a_t = [struct.unpack('<i', data_t[c[1]:c[1]+4])[0] for c in chunks_t if c[0] == 0xCA5E0C0A]

print("Tunic b00:", b00_t)
print("Tunic c0a:", c0a_t)
for i, c_val in enumerate(c0a_t):
    # If c_val is b00 value:
    if c_val in b00_t:
        b_idx = b00_t.index(c_val)
        b_name = objs_t[c_val - 1]['name'] if c_val - 1 < len(objs_t) else "OOB"
        print(f"  c0a[{i}] = {c_val} -> matches b00[{b_idx}]: '{b_name}'")
    else:
        print(f"  c0a[{i}] = {c_val} -> NOT in b00")
