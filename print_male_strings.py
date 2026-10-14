from parse_objects_clean import strings

print(f"Total strings in H_Male_Lod2: {len(strings)}")
for i, s in enumerate(strings):
    print(f"  [{i:2d}]: '{s}'")
