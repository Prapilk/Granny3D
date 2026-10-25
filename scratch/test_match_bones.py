import os
import subprocess

code = '''using System;
using System.IO;
using UoFiddler.Plugin.Granny3D;

string baseDir = @"d:\\Games\\sphere\\PROGRAMMES\\3D_UO\\Models";
string[] folders = new string[] { "Animals", "Monsters", "SeaCreatures" };

int targetCount = 37;
foreach (var folder in folders)
{
    string p = Path.Combine(baseDir, folder);
    if (!Directory.Exists(p)) continue;
    foreach (var f in Directory.GetFiles(p, "*_Lod2.grn"))
    {
        var parser = new GrannyFormatParser();
        if (parser.Load(f) && parser.Bones.Count == targetCount)
        {
            Console.WriteLine($"Matched 37 bones: {Path.GetFileName(f)} in {folder}");
        }
    }
}
'''

target_cs = r'C:\Users\User\.gemini\antigravity-ide\brain\b0bb7262-3b7e-4716-9e85-74417cb1f119\scratch\Program.cs'
target_proj = r'C:\Users\User\.gemini\antigravity-ide\brain\b0bb7262-3b7e-4716-9e85-74417cb1f119\scratch\test_blackthorn.csproj'

with open(target_cs, 'w', encoding='utf-8') as f:
    f.write(code)

res = subprocess.run(['dotnet', 'run', '--project', target_proj], capture_output=True, text=True)
print(res.stdout)
