import struct
from parse_objects_clean import objects, strings, chunks, data

b00 = [struct.unpack('<i', data[c[1]:c[1]+4])[0] for c in chunks if c[0] == 0xCA5E0B00]
bone_chunks = [c for c in chunks if c[0] == 0xCA5E0506]

# Non-bone objects were 8, 37, 42 (1-based: Object03, Hue_Chip, Material #30)
bone_objects = [obj for i, obj in enumerate(objects) if (i+1) not in (8, 37, 42)]
print(f"Bone objects count: {len(bone_objects)}")

# What if b00 maps bone_object[i] -> bone index?
# Or b00[i] -> object?
# Let's inspect object names:
obj_names = []
for obj in objects:
    name = "NONE"
    for p in obj['props']:
        if p[0] == 4: name = strings[p[2]]
    obj_names.append(name)

print("\nHypothesis: For bone i, which object has obj_names == expected bone?")
# Bone 2: Pelvis
# Where is 'Bip01 Pelvis' in obj_names?
print("'Bip01 Pelvis' is object index:", [i for i, n in enumerate(obj_names) if n == 'Bip01 Pelvis'])
# Bone 3: Spine
print("'Bip01 Spine' is object index:", [i for i, n in enumerate(obj_names) if n == 'Bip01 Spine'])
# Bone 4: Spine1
print("'Bip01 Spine1' is object index:", [i for i, n in enumerate(obj_names) if n == 'Bip01 Spine1'])
# Bone 5: Spine2
print("'Bip01 Spine2' is object index:", [i for i, n in enumerate(obj_names) if n == 'Bip01 Spine2'])
# Bone 6: Neck
print("'Bip01 Neck' is object index:", [i for i, n in enumerate(obj_names) if n == 'Bip01 Neck'])
# Bone 7: Head
print("'Bip01 Head' is object index:", [i for i, n in enumerate(obj_names) if n == 'Bip01 Head'])
# Bone 8: HeadNub
print("'Bip01 HeadNub' is object index:", [i for i, n in enumerate(obj_names) if n == 'Bip01 HeadNub'])

pelvis_obj_1 = [i+1 for i, n in enumerate(obj_names) if n == 'Bip01 Pelvis'][0]
spine_obj_1 = [i+1 for i, n in enumerate(obj_names) if n == 'Bip01 Spine'][0]
spine1_obj_1 = [i+1 for i, n in enumerate(obj_names) if n == 'Bip01 Spine1'][0]
spine2_obj_1 = [i+1 for i, n in enumerate(obj_names) if n == 'Bip01 Spine2'][0]
neck_obj_1 = [i+1 for i, n in enumerate(obj_names) if n == 'Bip01 Neck'][0]
head_obj_1 = [i+1 for i, n in enumerate(obj_names) if n == 'Bip01 Head'][0]
headnub_obj_1 = [i+1 for i, n in enumerate(obj_names) if n == 'Bip01 HeadNub'][0]

print("\n1-based object indices:")
print(f"Pelvis: {pelvis_obj_1}")
print(f"Spine: {spine_obj_1}")
print(f"Spine1: {spine1_obj_1}")
print(f"Spine2: {spine2_obj_1}")
print(f"Neck: {neck_obj_1}")
print(f"Head: {head_obj_1}")
print(f"HeadNub: {headnub_obj_1}")

print("\nWhere do these 1-based object indices appear in b00?")
print(f"b00.index(Pelvis={pelvis_obj_1}):", b00.index(pelvis_obj_1) if pelvis_obj_1 in b00 else -1)
print(f"b00.index(Spine={spine_obj_1}):", b00.index(spine_obj_1) if spine_obj_1 in b00 else -1)
print(f"b00.index(Spine1={spine1_obj_1}):", b00.index(spine1_obj_1) if spine1_obj_1 in b00 else -1)
print(f"b00.index(Spine2={spine2_obj_1}):", b00.index(spine2_obj_1) if spine2_obj_1 in b00 else -1)
print(f"b00.index(Neck={neck_obj_1}):", b00.index(neck_obj_1) if neck_obj_1 in b00 else -1)
print(f"b00.index(Head={head_obj_1}):", b00.index(head_obj_1) if head_obj_1 in b00 else -1)
print(f"b00.index(HeadNub={headnub_obj_1}):", b00.index(headnub_obj_1) if headnub_obj_1 in b00 else -1)
