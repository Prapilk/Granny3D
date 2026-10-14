import struct, math

# Let's inspect Torso vertices and find which bones they SHOULD be attached to
# Torso vertices rest bounds: X:[-0.2, 0.2] Y:[-0.1, 0.1] Z:[0.9, 1.5]
# Z=0.9 is Pelvis/Waist, Z=1.1 is Spine, Z=1.3 is Spine1/Spine2, Z=1.4-1.5 is Neck/Shoulders
# Let's check rest distance of each Torso bone world position to each Torso vertex!

from scratch_test import parse_grn, mat_mul, mat_quat, mat_trans, mat_inv_affine, mat_transform

torso = parse_grn(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_Torso_V2_LOD2.grn')
anim = parse_grn(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Walk_01.grn')

bones = torso['bones']
# Compute world rest positions of all bones in Torso
count = len(bones)
local_bind = [mat_mul(mat_quat(b['rot']), mat_trans(b['pos'])) for b in bones]
world_bind = [None] * count
for i in range(count):
    p = bones[i]['parent']
    if p == i or p < 0 or p >= i:
        world_bind[i] = local_bind[i]
    else:
        world_bind[i] = mat_mul(local_bind[i], world_bind[p])

print("Torso bones world rest positions (translation row):")
for i, b in enumerate(bones):
    w = world_bind[i]
    pos = (w[3][0], w[3][1], w[3][2])
    print(f"[{i:2d}] {b['name']:20s} (p={b['parent']:2d}): ({pos[0]:.2f}, {pos[1]:.2f}, {pos[2]:.2f})")

# Now check skin data in Torso
mesh = torso['meshes'][0]
skin = mesh['skin']
print("\nFirst 10 vertices skin local_b values:")
for v in range(min(10, len(skin))):
    print(f"V{v}: {skin[v]}")
