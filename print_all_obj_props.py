from parse_objects_clean import objects, strings

for i, obj in enumerate(objects):
    props_str = []
    for p in obj['props']:
        s = strings[p[2]] if p[2] < len(strings) else f"?({p[2]})"
        props_str.append(f"(type={p[0]}, str='{s}')")
    print(f"Object {i} (1-based {i+1}): {', '.join(props_str)}")
