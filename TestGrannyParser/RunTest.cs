using System;

namespace TestGrannyParser {
    class RunTest {
        public static void Do() {
            string d = @"d:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\";
            string[] parts = { "H_Male_Torso_V2_LOD2.grn", "H_Male_ULegs_V2_LOD2.grn", "H_Male_Head_V2_LOD2.grn", "H_Male_Shirt_Fancy_V2_LOD2.grn", "H_Male_Pants_Long_V2_LOD2.grn", "H_Male_Shoes_V2_LOD2.grn", "H_Female_Torso_V2_LOD2.grn", "H_Female_Shirt_Fancy_V2_LOD2.grn", "H_Female_Pants_Long_V2_LOD2.grn" };
            foreach(var f in parts) {
                var p = new GrannyFormatParser();
                if (!System.IO.File.Exists(d+f)) { Console.WriteLine(f+" missing"); continue; }
                p.Load(d + f);
                foreach(var m in p.Meshes) {
                    if (m.Vertices.Count==0) continue;
                    float a=1e9f,b=1e9f,c=1e9f,A=-1e9f,B=-1e9f,C=-1e9f; foreach (var v in m.Vertices){a=Math.Min(a,v.X);b=Math.Min(b,v.Y);c=Math.Min(c,v.Z);A=Math.Max(A,v.X);B=Math.Max(B,v.Y);C=Math.Max(C,v.Z);} Console.WriteLine($"BND {f} min=({a:F2},{b:F2},{c:F2}) max=({A:F2},{B:F2},{C:F2})");
                }
            }
        }
    }
}

