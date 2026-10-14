from scratch_test14 import get_obj_names
import struct

objs_t, data_t, chunks_t = get_obj_names(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_Torso_V2_LOD2.grn')
objs_a, data_a, chunks_a = get_obj_names(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn')

b00_t = [struct.unpack('<i', data_t[c[1]:c[1]+4])[0] for c in chunks_t if c[0] == 0xCA5E0B00]
b00_a = [struct.unpack('<i', data_a[c[1]:c[1]+4])[0] for c in chunks_a if c[0] == 0xCA5E0B00]

torso_bone_names = [objs_t[o1 - 1]['name'] for o1 in b00_t]
anim_track_names = [objs_a[o1 - 1]['name'] for o1 in b00_a]

print(f"Torso bones: {len(torso_bone_names)}")
common = set(torso_bone_names).intersection(set(anim_track_names))
print(f"Common bone names with Torso: {len(common)} / {len(torso_bone_names)}")
for b_idx, t_name in enumerate(torso_bone_names):
    in_anim = t_name in anim_track_names
    print(f"Torso Bone {b_idx:2d}: '{t_name:20s}' -> {'MATCH' if in_anim else 'REST'}")
