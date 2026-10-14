from test_clean_compare import bone_chunks, bone_names, data
import struct

for i, c in enumerate(bone_chunks):
    p, = struct.unpack('<i', data[c[1]:c[1]+4])
    p_name = bone_names[p] if 0 <= p < len(bone_names) else "NONE"
    print(f"Bone {i:2d} '{bone_names[i]}' -> Parent {p:2d} '{p_name}'")
