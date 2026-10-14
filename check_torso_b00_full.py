import struct
from check_torso_c0a_names import chunks, data, objects

b00 = [struct.unpack('<i', data[c[1]:c[1]+4])[0] for c in chunks if c[0] == 0xCA5E0B00]
print(f"b00 in Torso ({len(b00)} entries): {b00}")
for i, obj_idx_1 in enumerate(b00):
    name = objects[obj_idx_1 - 1]['name']
    print(f"  Torso Bone {i} -> obj {obj_idx_1}: '{name}'")
