import re

with open(r'c:\Games\Sphere\IrisGrannyViewer\xml\granny_aos.xml', 'r', encoding='latin1') as f:
    text = f.read()

chars = re.findall(r'<CHARACTER>(.*?)</CHARACTER>', text, re.DOTALL)
print(f"Total CHARACTER elements: {len(chars)}")
for i, c in enumerate(chars[:3]):
    print(f"Character {i}:")
    print(c.strip())

chars_aos = re.findall(r'<CHARACTER_AOS>(.*?)</CHARACTER_AOS>', text, re.DOTALL)
print(f"\nTotal CHARACTER_AOS elements: {len(chars_aos)}")
for i, c in enumerate(chars_aos[:3]):
    print(f"Character_AOS {i}:")
    print(c.strip())
