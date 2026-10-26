using System;
using System.IO;
using UoFiddler.Plugin.Granny3D;

class Test {
    static void Main() {
        var p = new GrannyFormatParser();
        if (p.Load(@"D:\Games\sphere\PROGRAMMES\3D_UO\Models\Monsters\Centaurs_Centaur_Lod2.grn")) {
            for (int i = 0; i < p.Bones.Count; i++) {
                var b = p.Bones[i];
                string parentName = b.ParentId >= 0 && b.ParentId < p.Bones.Count ? p.Bones[b.ParentId].Name : "NONE";
                Console.WriteLine($"{i,2}: {b.Name} (Parent: {parentName}, Id: {b.ParentId})");
            }
        }
    }
}
