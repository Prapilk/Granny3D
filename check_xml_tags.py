import xml.etree.ElementTree as ET

tree = ET.parse(r'c:\Games\Sphere\IrisGrannyViewer\xml\granny_aos.xml')
root = tree.getroot()
print("Root tag:", root.tag)
child_tags = set(child.tag for child in root)
print("Child tags:", child_tags)

for tag in child_tags:
    elements = root.findall(tag)
    print(f"  {tag}: {len(elements)} elements")
    if elements:
        sample = ET.tostring(elements[0], encoding='utf-8').decode('utf-8')
        print(f"    sample: {sample[:150]}")
