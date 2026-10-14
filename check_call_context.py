with open(r'c:\Games\Sphere\IrisGrannyViewer\GrannyViewer.exe', 'rb') as f:
    exe = f.read()

import struct

for call_site in [0x12677, 0x127e7, 0x12a89, 0x2c625]:
    print(f"\nContext around 0x{call_site:x} (VA 0x{call_site+0x400000:x}):")
    # check strings within 200 bytes
    for p in range(call_site - 100, call_site + 100):
        val = struct.unpack('<I', exe[p:p+4])[0]
        if 0x430000 <= val <= 0x435000:
            fo = val - 0x400000
            end = exe.find(b'\x00', fo)
            if 0 < end - fo < 100:
                s = exe[fo:end].decode('latin1', errors='ignore')
                print(f"  At 0x{p:x}: references '{s}'")
