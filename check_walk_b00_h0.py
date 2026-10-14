import struct
from print_walk_tracks_all import chunks, d

b00_walk = [struct.unpack('<i', d[c[1]:c[1]+4])[0] for c in chunks if c[0] == 0xCA5E0B00]
print(f"b00 in Walk anim: {b00_walk}")

tracks = [c for c in chunks if c[0] == 0xCA5E1204]
h0_list = [struct.unpack('<i', d[c[1]:c[1]+4])[0] for c in tracks]
print(f"h0 in Walk tracks: {h0_list}")

# Compare b00 vs h0
print("Does b00 == h0?", b00_walk == h0_list)
# Are the values in h0 pointing to bone indices, or is track i for bone i?
print(f"Sorted h0: {sorted(h0_list)}")
print(f"Sorted b00: {sorted(b00_walk)}")
