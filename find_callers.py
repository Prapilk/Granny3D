with open(r'c:\Games\Sphere\IrisGrannyViewer\GrannyViewer.exe', 'rb') as f:
    exe = f.read()

import struct

fn_va = 0x412320 # or near
for i in range(len(exe) - 4):
    v = struct.unpack('<I', exe[i:i+4])[0]
    if v == fn_va:
        print(f"Direct pointer to 0x412320 at file 0x{i:x} (VA 0x{i+0x400000:x})")
    # Also check CALL rel32
    if exe[i] == 0xe8:
        rel = struct.unpack('<i', exe[i+1:i+5])[0]
        if i + 5 + rel == 0x12320:
            print(f"CALL 0x412320 at file 0x{i:x} (VA 0x{i+0x400000:x})")
