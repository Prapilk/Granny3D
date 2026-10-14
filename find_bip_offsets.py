with open(r'd:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn', 'rb') as f:
    data = f.read()

idx = 0
offsets = []
while True:
    pos = data.find(b'Bip01', idx)
    if pos == -1: break
    offsets.append(pos)
    idx = pos + 1

print(f"Found {len(offsets)} occurrences of 'Bip01'")
for o in offsets[:10]:
    # read string until null
    end = data.find(b'\x00', o)
    s = data[o:end].decode('latin1')
    print(f"  At offset {hex(o)}: '{s}'")
