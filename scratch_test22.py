from scratch_test14 import get_obj_names
import struct

objs_a, data_a, chunks_a = get_obj_names(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn')
b00_a = [struct.unpack('<i', data_a[c[1]:c[1]+4])[0] for c in chunks_a if c[0] == 0xCA5E0B00]

# For Anim Bones that we know by their exact geometric transform:
# 2: Pelvis
# 4: Spine
# 5: Spine1
# 6: Spine2
# 7: Chest
# 8: Neck
# 9: Head
# 10: R Clavicle
# 21: L Clavicle
# 32: R Thigh
# 37: L Thigh

known = {
    2: "Pelvis",
    4: "Spine",
    5: "Spine1",
    6: "Spine2",
    7: "Chest",
    8: "Neck",
    9: "Head",
    10: "R Clavicle",
    21: "L Clavicle",
    32: "R Thigh",
    37: "L Thigh"
}

print("Anim Bone -> b00[bone] -> Object Name:")
for b_idx, label in known.items():
    obj_idx_1 = b00_a[b_idx]
    obj_name = objs_a[obj_idx_1 - 1]['name']
    print(f"Anim Bone {b_idx:2d} ({label:10s}): b00={obj_idx_1:2d} -> '{obj_name}'")
