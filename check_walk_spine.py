from test_full_pipeline import parse_granny_file

anim = parse_granny_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn')

for name in ['__Root', 'master_h_male', 'Bip01', 'Bip01 Pelvis', 'Bip01 Spine', 'Bip01 Spine1', 'Bip01 Spine2', 'Bip01 Neck', 'Bip01 Head']:
    if name in anim['tracks']:
        t = anim['tracks'][name]
        p = t['p0']
        r = t['r0']
        print(f"Track '{name}': pos=({p[0]:.3f}, {p[1]:.3f}, {p[2]:.3f}) rot=({r[0]:.3f}, {r[1]:.3f}, {r[2]:.3f}, {r[3]:.3f})")
    else:
        print(f"Track '{name}': NOT FOUND")
