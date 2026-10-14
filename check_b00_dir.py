import struct
from parse_objects_clean import objects, strings, chunks, data

b00 = [struct.unpack('<i', data[c[1]:c[1]+4])[0] for c in chunks if c[0] == 0xCA5E0B00]
bone_chunks = [c for c in chunks if c[0] == 0xCA5E0506]

# Non-bone objects are 8, 37, 42 (1-based)
# What if b00 maps bone -> object?
# What if the inverse maps object -> bone?

# Let's check which bones are 0, 1, 2, 3, 4, 5, 6, 7, 8 in tree:
# 0 -> 1 -> 2 -> 3 -> 4 -> 5 -> 6 -> 7 -> 8
# If Bone 0 is Root, Bone 1 is Bip01, Bone 2 is Pelvis, Bone 3 is Spine, Bone 4 is Spine1...
# Let's see what object index each bone has:
for i in range(10):
    p, = struct.unpack('<i', data[bone_chunks[i][1]:bone_chunks[i][1]+4])
    obj_idx_1 = b00[i]
    obj_name = "NONE"
    for prop in objects[obj_idx_1 - 1]['props']:
        if prop[0] == 4: obj_name = strings[prop[2]]
    print(f"Bone {i} (p={p}): obj_1={obj_idx_1} -> name='{obj_name}'")

print("\nWhat if b00 contains bone indices?")
# b00 has values 1..46 (with 8, 37, 42 missing).
# There are only 43 bones (0..42).
# So b00 values cannot be 0-based bone indices (they go up to 46, which is num_objects!)
