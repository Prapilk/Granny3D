from parse_objects_clean import chunks

for i in range(490, 505):
    print(f"Chunk {i}: {hex(chunks[i][0])} at offset {hex(chunks[i][1])}")
