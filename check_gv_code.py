with open(r'c:\Games\Sphere\IrisGrannyViewer\GrannyViewer.exe', 'rb') as f:
    data = f.read()

# Find the offset of 'Bip01 R Foot'
pos = data.find(b'Bip01 R Foot')
print(f"Bip01 list starts at {hex(pos)}")

# Check references to this offset or surrounding pointers
struct_pos = pos
# Let's inspect 500 bytes before and after
context = data[pos-100:pos+500]
print("Context around Bip01 list:")
print(context)
