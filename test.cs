using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using OpenTK.Mathematics;

namespace UoFiddler.Plugin.Granny3D
{
    public class Program
    {
        public static void Run()
        {
            var parser = new GrannyFormatParser();
            if (parser.Load(@"d:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_Torso_V2_LOD2.grn"))
            {
                foreach(var m in parser.Meshes) {
                    Console.WriteLine("Verts: " + m.Vertices.Count + " UVs: " + m.UVs.Count + " UVInd: " + m.UVIndices.Count + " Ind: " + m.Indices.Count);
                    for(int i=0; i<Math.Min(10, m.UVs.Count); i++) {
                        Console.WriteLine("UV " + i + ": " + m.UVs[i].X + ", " + m.UVs[i].Y);
                    }
                }
            }
        }
    }
}
