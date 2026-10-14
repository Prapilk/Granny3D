import struct
from test_full_pipeline import parse_granny_file

mesh = parse_granny_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn')
data = mesh['data']

pts_off = mesh['pts_chunk'][1]
# How many floats in pts_chunk?
# In Granny, chunk size:
print("Skin chunk num_v:", struct.unpack('<iii', data[mesh['skin_chunk'][1]:mesh['skin_chunk'][1]+12]))
