from scratch_test14 import get_obj_names
import struct

objs_m, data_m, chunks_m = get_obj_names(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn')
objs_a, data_a, chunks_a = get_obj_names(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn')

b00_m = [struct.unpack('<i', data_m[c[1]:c[1]+4])[0] for c in chunks_m if c[0] == 0xCA5E0B00]
b00_a = [struct.unpack('<i', data_a[c[1]:c[1]+4])[0] for c in chunks_a if c[0] == 0xCA5E0B00]

model_bone_names = [objs_m[o1 - 1]['name'] for o1 in b00_m]
anim_track_names = [objs_a[o1 - 1]['name'] for o1 in b00_a]

print("Model bones count:", len(model_bone_names))
print("Anim tracks count:", len(anim_track_names))

print("\n--- Matching Test ---")
common = set(model_bone_names).intersection(set(anim_track_names))
print(f"Common bone names: {len(common)} / {len(model_bone_names)}")
for b_idx, m_name in enumerate(model_bone_names):
    in_anim = m_name in anim_track_names
    t_idx = anim_track_names.index(m_name) if in_anim else -1
    print(f"Model Bone {b_idx:2d}: '{m_name:20s}' -> Anim Track {t_idx:2d} ({'MATCH' if in_anim else 'MISSING'})")
