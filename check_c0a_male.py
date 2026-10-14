from test_clean_deform import mesh

print("c0a in H_Male_Lod2:", mesh['c0a'])
for local_b, master_b in enumerate(mesh['c0a']):
    bone_name = mesh['bones'][master_b]['name']
    print(f"  local {local_b} -> bone {master_b}: '{bone_name}'")
