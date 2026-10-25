import subprocess

code = '''using System;
using UoFiddler.Plugin.Granny3D;

var parser = new GrannyFormatParser();
if (parser.Load(@"d:\\Games\\sphere\\PROGRAMMES\\3D_UO\\Models\\Animals\\Equines_Horse_Dappled_Brown_Lod2.grn"))
{
    Console.WriteLine("Bone count: " + parser.Bones.Count);
    for (int i = 0; i < parser.Bones.Count; i++)
    {
        Console.WriteLine($"{i}: {parser.Bones[i].Name}");
    }
}
'''

target_cs = r'C:\Users\User\.gemini\antigravity-ide\brain\b0bb7262-3b7e-4716-9e85-74417cb1f119\scratch\Program.cs'
target_proj = r'C:\Users\User\.gemini\antigravity-ide\brain\b0bb7262-3b7e-4716-9e85-74417cb1f119\scratch\test_blackthorn.csproj'

with open(target_cs, 'w', encoding='utf-8') as f:
    f.write(code)

res = subprocess.run(['dotnet', 'run', '--project', target_proj], capture_output=True, text=True)
print(res.stdout)
if res.stderr:
    print('ERR:', res.stderr)
