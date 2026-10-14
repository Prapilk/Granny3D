from test_full_pipeline import parse_granny_file
import struct

with open(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn', 'rb') as f:
    data = f.read()

cid, _, data_off, _, _ = struct.unpack('<IIIII', data[0x74:0x74+20])
sub_count = struct.unpack('<I', data[data_off:data_off+4])[0]
list_pos = data_off + 16
chunks = []
for i in range(sub_count):
    lp = list_pos + i * 12
    scid, local_off, _ = struct.unpack('<III', data[lp:lp+12])
    chunks.append((scid, data_off + local_off))

str_chunk = [c for c in chunks if c[0] == 0xCA5E0200][0]
count, _ = struct.unpack('<ii', data[str_chunk[1]:str_chunk[1]+8])
pos = str_chunk[1] + 8
strings = []
for _ in range(count):
    end = data.find(b'\x00', pos)
    strings.append(data[pos:end].decode('latin1'))
    pos = end + 1

objects = []
current_obj = None
for scid, off in chunks:
    if scid == 0xCA5E0F00:
        current_obj = {'name': 'NO_NAME'}
        objects.append(current_obj)
    elif scid == 0xCA5E0F01 and current_obj is not None:
        v0, v1, v2, v3 = struct.unpack('<iiii', data[off:off+16])
        if v0 == 4 and v2 < len(strings):
            current_obj['name'] = strings[v2]

b00 = [struct.unpack('<i', data[c[1]:c[1]+4])[0] for c in chunks if c[0] == 0xCA5E0B00]

print("Hypothesis 1: Bone i has Name = objects[b00[i] - 1]")
# Bone 2: Pelvis
print("Hyp 1 Bone 2 name:", objects[b00[2] - 1]['name'])

print("\nHypothesis 2: Object i has Bone = b00[i] (1-based bone index)")
# Object for Pelvis:
pelvis_obj_idx = [i for i, o in enumerate(objects) if o['name'] == 'Bip01 Pelvis'][0]
print(f"'Bip01 Pelvis' is Object {pelvis_obj_idx} (1-based {pelvis_obj_idx+1})")
if pelvis_obj_idx < len(b00):
    bone_for_pelvis = b00[pelvis_obj_idx]
    print(f"b00[{pelvis_obj_idx}] = {bone_for_pelvis} -> Is Bone {bone_for_pelvis} or {bone_for_pelvis-1} Pelvis?")

print("\nHypothesis 3: b00[bone_idx] == 1-based object index, BUT which bone is which?")
# Let's test for all standard bones where they map under Hyp 1 vs Hyp 2:
test_names = ['Bip01 Pelvis', 'Bip01 Spine', 'Bip01 Spine1', 'Bip01 Spine2', 'Bip01 Neck', 'Bip01 Head', 'Bip01 R Thigh', 'Bip01 L Thigh']
for name in test_names:
    o_idx = [i for i, o in enumerate(objects) if o['name'] == name][0]
    # Hyp 1: find which bone has objects[b00[b] - 1] == name
    b_hyp1 = [b for b, o1 in enumerate(b00) if o1 - 1 == o_idx]
    # Hyp 2: b = b00[o_idx] - 1
    b_hyp2 = b00[o_idx] - 1 if o_idx < len(b00) else -1
    print(f"{name:15s} -> Object {o_idx:2d} | Hyp1 (b00[b]==obj): Bone {b_hyp1} | Hyp2 (b=b00[obj]): Bone {b_hyp2}")
