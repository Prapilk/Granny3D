import struct
from test_clean_deform import parse_granny_file

mesh = parse_granny_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_Torso_V2_LOD2.grn')
print("Raw c0a in Torso:", mesh['c0a'])
