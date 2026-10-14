from scratch_test import parse_grn, mat_mul, mat_quat, mat_trans

male = parse_grn(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn')
bones = male['bones']
count = len(bones)
local_bind = [mat_mul(mat_quat(b['rot']), mat_trans(b['pos'])) for b in bones]
world_bind = [None] * count
for i in range(count):
    p = bones[i]['parent']
    if p == i or p < 0 or p >= i:
        world_bind[i] = local_bind[i]
    else:
        world_bind[i] = mat_mul(local_bind[i], world_bind[p])

print("H_Male_Lod2 bones world rest positions (translation row):")
for i, b in enumerate(bones):
    w = world_bind[i]
    pos = (w[3][0], w[3][1], w[3][2])
    print(f"[{i:2d}] {b['name']:20s} (p={b['parent']:2d}): ({pos[0]:.2f}, {pos[1]:.2f}, {pos[2]:.2f})")
