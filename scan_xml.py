import re

with open(r'c:\Games\Sphere\IrisGrannyViewer\xml\granny_aos.xml', 'r', encoding='latin1') as f:
    text = f.read()

tags = set(re.findall(r'<([A-Za-z0-9_]+)>', text))
print("Tags in granny_aos.xml:", tags)

# Search for "H_Male"
matches = re.findall(r'.*H_Male.*', text)
print(f"H_Male occurrences: {len(matches)}")
for m in matches[:10]:
    print("  ", m.strip())
