using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace TestGrannyParser {
    public class Vector2 { public float X, Y; }
    public class Vector3 { public float X, Y, Z; }
    public class Vector4 { public float X, Y, Z, W; }

    public class GrannyChunk
    {
        public uint Id { get; set; }
        public uint DataOffset { get; set; }
        public uint DataSize { get; set; }
        public uint NumChildren { get; set; }
    }

    public class GrannyMesh
    {
        public List<Vector3> Vertices { get; set; } = new List<Vector3>();
        public List<int> Indices { get; set; } = new List<int>();
        public List<Vector2> UVs { get; set; } = new List<Vector2>();
        public List<int> UVIndices { get; set; } = new List<int>();
    }

    public class GrannyFormatParser
    {
        public List<GrannyMesh> Meshes { get; private set; }
        private List<GrannyChunk> _flatChunks;

        public GrannyFormatParser()
        {
            Meshes = new List<GrannyMesh>();
            _flatChunks = new List<GrannyChunk>();
        }

        public bool Load(string filePath)
        {
            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
            using (BinaryReader reader = new BinaryReader(fs))
            {
                reader.BaseStream.Seek(0x40, SeekOrigin.Begin);
                uint magic = reader.ReadUInt32();
                if (magic != 0xCA5E0000) return false;
                uint numRootChunks = reader.ReadUInt32();
                for (int i = 0; i < 6; i++) reader.ReadUInt32(); 
                for (int i = 0; i < numRootChunks; i++)
                {
                    uint chunkId = reader.ReadUInt32(); reader.ReadUInt32();
                    uint dataOffset = reader.ReadUInt32(); reader.ReadUInt32(); reader.ReadUInt32();
                    long oldPos = reader.BaseStream.Position;
                    reader.BaseStream.Seek(dataOffset, SeekOrigin.Begin);
                    uint subChildrenCount = reader.ReadUInt32();
                    for (int skip = 0; skip < 3; skip++) reader.ReadUInt32();
                    ParseChunkList(reader, dataOffset, subChildrenCount);
                    reader.BaseStream.Seek(oldPos, SeekOrigin.Begin);
                }
                var sorted = _flatChunks.Where(c => c.DataOffset > 0).OrderBy(c => c.DataOffset).ToList();
                for (int i = 0; i < sorted.Count - 1; i++) sorted[i].DataSize = sorted[i+1].DataOffset - sorted[i].DataOffset;
                if (sorted.Count > 0) sorted[sorted.Count - 1].DataSize = 0xFFFFFFFF;
                ExtractData(reader);
            }
            return true;
        }

        private void ParseChunkList(BinaryReader reader, uint baseOffset, uint peers)
        {
            if (peers == 0) return;
            long listPos = reader.BaseStream.Position;
            for (int i = 0; i < peers; i++)
            {
                reader.BaseStream.Seek(listPos + i * 12, SeekOrigin.Begin);
                uint chunkId = reader.ReadUInt32();
                uint localOffset = reader.ReadUInt32();
                uint subChildren = reader.ReadUInt32();
                _flatChunks.Add(new GrannyChunk { Id = chunkId, DataOffset = baseOffset + localOffset, NumChildren = subChildren });
            }
        }

        private void ExtractData(BinaryReader reader)
        {
            var ptsChunks = _flatChunks.Where(c => c.Id == 0xCA5E0801).ToList();
            var polChunks = _flatChunks.Where(c => c.Id == 0xCA5E0901).ToList();
            var uvPtsChunks = _flatChunks.Where(c => c.Id == 0xCA5E0803).ToList();
            var uvPolChunks = _flatChunks.Where(c => c.Id == 0xCA5E0E06).ToList();

            int meshCount = Math.Max(ptsChunks.Count, polChunks.Count);
            for (int j = 0; j < meshCount; j++)
            {
                GrannyMesh m = new GrannyMesh();
                var ptsChunk = j < ptsChunks.Count ? ptsChunks[j] : null;
                var polChunk = j < polChunks.Count ? polChunks[j] : null;
                var uvPolChunk = j < uvPolChunks.Count ? uvPolChunks[j] : null;

                if (ptsChunk != null && ptsChunk.DataSize != 0xFFFFFFFF)
                {
                    reader.BaseStream.Seek(ptsChunk.DataOffset, SeekOrigin.Begin);
                    int count = (int)(ptsChunk.DataSize / 12);
                    for (int i = 0; i < count; i++) { m.Vertices.Add(new Vector3()); reader.ReadSingle(); reader.ReadSingle(); reader.ReadSingle(); }
                }
                
                if (polChunk != null && polChunk.DataSize != 0xFFFFFFFF)
                {
                    reader.BaseStream.Seek(polChunk.DataOffset, SeekOrigin.Begin);
                    int count = (int)(polChunk.DataSize / 24);
                    for (int i = 0; i < count; i++)
                    {
                        m.Indices.Add(reader.ReadInt32()); m.Indices.Add(reader.ReadInt32()); m.Indices.Add(reader.ReadInt32());
                        reader.ReadInt32(); reader.ReadInt32(); reader.ReadInt32();
                    }
                }

                int requiredUvs = 0;
                if (uvPolChunk != null && uvPolChunk.DataSize != 0xFFFFFFFF)
                {
                    reader.BaseStream.Seek(uvPolChunk.DataOffset, SeekOrigin.Begin);
                    uint numPolys = reader.ReadUInt32();
                    if (numPolys > 0)
                    {
                        int bytesPerPoly = (int)((uvPolChunk.DataSize - 4) / numPolys);
                        reader.BaseStream.Seek(uvPolChunk.DataOffset + 4, SeekOrigin.Begin);
                        for (int i = 0; i < numPolys; i++)
                        {
                            if (bytesPerPoly >= 28)
                            {
                                reader.ReadInt32(); 
                                int u1 = reader.ReadInt32(); reader.ReadInt32(); m.UVIndices.Add(u1);
                                int u2 = reader.ReadInt32(); reader.ReadInt32(); m.UVIndices.Add(u2);
                                int u3 = reader.ReadInt32(); reader.ReadInt32(); m.UVIndices.Add(u3);
                                if (u1 > requiredUvs) requiredUvs = u1; if (u2 > requiredUvs) requiredUvs = u2; if (u3 > requiredUvs) requiredUvs = u3;
                            }
                            else
                            {
                                reader.ReadInt32(); 
                                int u1 = reader.ReadInt32(); m.UVIndices.Add(u1);
                                int u2 = reader.ReadInt32(); m.UVIndices.Add(u2);
                                int u3 = reader.ReadInt32(); m.UVIndices.Add(u3);
                                if (u1 > requiredUvs) requiredUvs = u1; if (u2 > requiredUvs) requiredUvs = u2; if (u3 > requiredUvs) requiredUvs = u3;
                            }
                        }
                    }
                    requiredUvs += 1; 
                }

                GrannyChunk uvPtsChunk = null;
                if (requiredUvs > 0 && uvPtsChunks.Count > 0)
                {
                    uvPtsChunk = uvPtsChunks
                        .Where(c => ((c.DataSize - 4) / 12) >= requiredUvs)
                        .OrderBy(c => ((c.DataSize - 4) / 12))
                        .FirstOrDefault();
                    Console.WriteLine("Required UVs: " + requiredUvs);
                    if (uvPtsChunk != null) Console.WriteLine("Selected UV Chunk Size: " + uvPtsChunk.DataSize);
                }
                else if (j < uvPtsChunks.Count)
                {
                    uvPtsChunk = uvPtsChunks[j];
                }

                if (uvPtsChunk != null && uvPtsChunk.DataSize != 0xFFFFFFFF)
                {
                    reader.BaseStream.Seek(uvPtsChunk.DataOffset, SeekOrigin.Begin);
                    uint unkHeader = reader.ReadUInt32();
                    int count = (int)((uvPtsChunk.DataSize - 4) / 12);
                    for (int i = 0; i < count; i++)
                    {
                        m.UVs.Add(new Vector2 { X = reader.ReadSingle(), Y = reader.ReadSingle() });
                        reader.ReadUInt32();
                    }
                }
                
                Meshes.Add(m);
            }
        }
    }

    class Program
    {
        static void Main(string[] args)
        { TestGrannyParser.RunTest.Do(); return;
            var p = new GrannyFormatParser();
            p.Load(@"d:\Games\sphere\PROGRAMMES\3D_UO\Models\Others\H_Male_Torso_V2_LOD2.grn");
            foreach(var m in p.Meshes) {
                Console.WriteLine($"Verts: {m.Vertices.Count} Inds: {m.Indices.Count} UVs: {m.UVs.Count} UVInds: {m.UVIndices.Count}");
                for(int i=0; i<Math.Min(10, m.UVs.Count); i++) {
                    Console.WriteLine($"UV {i}: {m.UVs[i].X}, {m.UVs[i].Y}");
                }
            }
        }
    }
}


