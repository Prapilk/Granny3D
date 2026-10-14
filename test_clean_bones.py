import struct
from parse_objects_clean import objects, chunks, data

b00 = [struct.unpack('<i', data[c[1]:c[1]+4])[0] for c in chunks if c[0] == 0xCA5E0B00]
print(f"b00 has {len(b00)} entries:")

bone_names = []
for i, obj_idx_1 in enumerate(b00):
    obj_idx = obj_idx_1 - 1
    name = "UNKNOWN"
    if 0 <= obj_idx < len(objects):
        # find prop where v0 == 4
        # wait, let's also import strings
        from parse_objects_clean import strings
        for p in objects[obj_idx]['props']:
            if p[0] == 4:
                name = strings[p[2]]
                break
    bone_names.append(name)
    print(f"  Bone {i} -> Object {obj_idx} (1-based {obj_idx_1}): '{name}'")
