with open(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_Torso_V2_LOD2.grn', 'rb') as f:
    data_torso = f.read()

from check_c0a import inspect_file, struct

_, flat_torso = inspect_file(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_Torso_V2_LOD2.grn')

for i, (cid, off) in enumerate([c for c in flat_torso if c[0] == 0xCA5E0C0A]):
    chunk_data = data_torso[off:off+32]
    first_int, = struct.unpack('<i', chunk_data[:4])
    floats = struct.unpack('<7f', chunk_data[4:32])
    print(f"Torso c0a[{i}]: int={first_int}, floats={[round(f, 4) for f in floats]}")
