with open(r'c:\Games\Sphere\IrisGrannyViewer\GrannyViewer.exe', 'rb') as f:
    data = f.read()

idx = 0
bip_strings = []
while True:
    pos = data.find(b'Bip01', idx)
    if pos == -1: break
    end = data.find(b'\x00', pos)
    bip_strings.append(data[pos:end].decode('latin1'))
    idx = pos + 1

print(f"Total Bip01 in GrannyViewer.exe: {len(bip_strings)}")
for s in bip_strings[:45]:
    print(f"  '{s}'")
