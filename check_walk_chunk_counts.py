from print_walk_tracks_all import chunks
from collections import Counter

counts = Counter([hex(c[0]) for c in chunks])
print("Chunks in H_Male_Walk_01.grn:")
for cid, cnt in counts.most_common():
    print(f"  {cid}: {cnt}")
