import struct
from parse_objects_clean import objects, strings, chunks, data

b00 = [struct.unpack('<i', data[c[1]:c[1]+4])[0] for c in chunks if c[0] == 0xCA5E0B00]
bone_chunks = [c for c in chunks if c[0] == 0xCA5E0506]

bone_names = []
for i, obj_idx_1 in enumerate(b00):
    obj_idx = obj_idx_1 - 1
    name = "NO_NAME"
    if 0 <= obj_idx < len(objects):
        for p in objects[obj_idx]['props']:
            if p[0] == 4:
                name = strings[p[2]]
                break
    bone_names.append(name)

# Check all parent values
parents = [struct.unpack('<i', data[c[1]:c[1]+4])[0] for c in bone_chunks]
print(f"Parent values: {parents}")
print(f"Min parent: {min(parents)}, Max parent: {max(parents)}")

# Hypothesis A: p is a bone index 0..42
# Hypothesis B: p is an object index (1-based)
# Hypothesis C: p is an object index (0-based)

# Let's test if p is an object index:
# If p is an object index, what object does it point to?
print("\nIf p is bone index:")
for i in range(10):
    p = parents[i]
    p_name = bone_names[p] if 0 <= p < len(bone_names) else f"?({p})"
    print(f"  Bone {i} '{bone_names[i]}' -> parent {p} '{p_name}'")

print("\nIf p is 1-based object index:")
for i in range(10):
    p = parents[i]
    # what is object p-1?
    p_name = "ROOT"
    if 1 <= p <= len(objects):
        for prop in objects[p-1]['props']:
            if prop[0] == 4: p_name = strings[prop[2]]
    print(f"  Bone {i} '{bone_names[i]}' -> parent {p} '{p_name}'")

print("\nIf p is 0-based object index:")
for i in range(10):
    p = parents[i]
    p_name = "ROOT"
    if 0 <= p < len(objects):
        for prop in objects[p]['props']:
            if prop[0] == 4: p_name = strings[prop[2]]
    print(f"  Bone {i} '{bone_names[i]}' -> parent {p} '{p_name}'")
