from parse_objects_clean import chunks

b00_indices = [i for i, c in enumerate(chunks) if c[0] == 0xCA5E0B00]
b506_indices = [i for i, c in enumerate(chunks) if c[0] == 0xCA5E0506]

print(f"Total chunks in Root 1: {len(chunks)}")
print(f"b00 count: {len(b00_indices)}, first={b00_indices[0]}, last={b00_indices[-1]}")
print(f"506 count: {len(b506_indices)}, first={b506_indices[0]}, last={b506_indices[-1]}")

# What is around b00?
for idx in b00_indices[:5]:
    surrounding = [hex(chunks[j][0]) for j in range(max(0, idx-2), min(len(chunks), idx+3))]
    print(f"At {idx}: {surrounding}")
