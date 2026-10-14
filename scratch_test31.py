from scratch_test14 import get_obj_names
import struct

objs_m, data_m, chunks_m = get_obj_names(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn')
b00_m = [struct.unpack('<i', data_m[c[1]:c[1]+4])[0] for c in chunks_m if c[0] == 0xCA5E0B00]

# For each model bone i:
# name = objs_m[b00_m[i] - 1]['name']
for i in range(len(b00_m)):
    o1 = b00_m[i]
    name = objs_m[o1 - 1]['name']
    print(f"Model Bone {i:2d} (b00={o1:2d}) -> '{name}'")
