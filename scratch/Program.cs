using System;
using UoFiddler.Plugin.Granny3D;

var parser = new GrannyFormatParser();
if (parser.Load(@"d:\Games\sphere\PROGRAMMES\3D_UO\Models\Animals\Equines_Horse_Dappled_Brown_Lod2.grn"))
{
    Console.WriteLine("Bone count: " + parser.Bones.Count);
    for (int i = 0; i < parser.Bones.Count; i++)
    {
        Console.WriteLine($"{i}: {parser.Bones[i].Name}");
    }
}
