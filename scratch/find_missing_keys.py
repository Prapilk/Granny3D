import os
import re
import json

fr_path = r'c:\Games\Sphere\IrisGrannyViewer\UoFiddler.Plugin.Granny3D\Languages\fr.json'
en_path = r'c:\Games\Sphere\IrisGrannyViewer\UoFiddler.Plugin.Granny3D\Languages\en.json'

with open(fr_path, 'r', encoding='utf-8') as f:
    fr = json.load(f)
with open(en_path, 'r', encoding='utf-8') as f:
    en = json.load(f)

cs_dir = r'c:\Games\Sphere\IrisGrannyViewer\UoFiddler.Plugin.Granny3D'
tr_regex = re.compile(r'Localization\.Tr(?:Fmt)?\(\s*"([^"]+)"(?:\s*,\s*"([^"]*)")?')

code_keys = {}
for root, dirs, files in os.walk(cs_dir):
    for f in files:
        if f.endswith('.cs'):
            with open(os.path.join(root, f), 'r', encoding='utf-8', errors='ignore') as cs_f:
                content = cs_f.read()
                matches = tr_regex.findall(content)
                for k, default_text in matches:
                    if k not in code_keys:
                        code_keys[k] = default_text

missing_in_fr = {k: default_text for k, default_text in code_keys.items() if k not in fr}
missing_in_en = {k: default_text for k, default_text in code_keys.items() if k not in en}

with open(r'c:\Games\Sphere\IrisGrannyViewer\scratch\keys_report.txt', 'w', encoding='utf-8') as out:
    out.write(f'Total keys in code: {len(code_keys)}\n')
    out.write(f'Keys in code missing in fr.json: {len(missing_in_fr)}\n')
    for k, def_txt in missing_in_fr.items():
        out.write(f'  {k} => {def_txt}\n')
    out.write(f'\nKeys in code missing in en.json: {len(missing_in_en)}\n')
    for k, def_txt in missing_in_en.items():
        out.write(f'  {k} => {def_txt}\n')

print(f'Done. Total keys in code: {len(code_keys)}, Missing in FR: {len(missing_in_fr)}, Missing in EN: {len(missing_in_en)}')
