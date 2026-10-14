with open(r'c:\Games\Sphere\IrisGrannyViewer\GrannyViewer.exe', 'rb') as f:
    exe = f.read()

data = exe[0x1e070:0x1e0c0]
for i in range(0, len(data), 16):
    line = data[i:i+16]
    hex_str = ' '.join(f'{b:02x}' for b in line)
    print(f"0x{0x1e070 + i:05x}: {hex_str}")
