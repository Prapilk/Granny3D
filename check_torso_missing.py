from check_torso_c0a_names import objects

missing = [i for i in range(1, 29) if i not in [1, 2, 9, 4, 27, 5, 6, 8, 14, 12, 11, 3, 10, 13, 15, 16, 17, 19, 22, 20, 21, 23, 24, 26, 28]]
print("Missing from 1..28 in Torso:", missing)
for m in missing:
    print(f"  Obj {m}: '{objects[m-1]['name']}'")
