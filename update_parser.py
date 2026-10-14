code = r"""using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using OpenTK.Mathematics;

namespace UoFiddler.Plugin.Granny3D
{
    public class GrannyChunk
    {
        public uint Id { get; set; }
        public uint DataOffset { get; set; }
        public uint DataSize { get; set; }
        public uint NumChildren { get; set; }
    }

    public class GrannyBone
    {
        public int Id { get; set; }
        public int ParentId { get; set; }
        public Vector3 LocalPosition { get; set; }
        public Quaternion LocalRotation { get; set; }
        public Matrix4 InverseBindMatrix { get; set; }
    }

    public struct VertexInfluence
    {
        public int BoneId;
        public float Weight;
    }

    public class GrannyMesh
    {
        public List<Vector3> Vertices { get; set; } = new List<Vector3>();
        public List<int> Indices { get; set; } = new List<int>();
        public List<Vector2> UVs { get; set; } = new List<Vector2>();
        public List<int> UVIndices { get; set; } = new List<int>();
        public List<VertexInfluence[]> Skin { get; set; } = new List<VertexInfluence[]>();
    }

    public class GrannyTrack
    {
        public int BoneId;
        public float[] TimesP;
        public Vector3[] Pos;
        public float[] TimesR;
        public Quaternion[] Rot;

        public Vector3 EvaluatePos(float t)
        {
            if (TimesP == null || TimesP.Length == 0) return Vector3.Zero;
            if (TimesP.Length == 1 || t <= TimesP[0]) return Pos[0];
            if (t >= TimesP[TimesP.Length - 1]) return Pos[Pos.Length - 1];
            int i = 0;
            while (i < TimesP.Length - 1 && TimesP[i + 1] < t) i++;
            float factor = (t - TimesP[i]) / (TimesP[i + 1] - TimesP[i]);
            return Vector3.Lerp(Pos[i], Pos[i + 1], factor);
        }

        public Quaternion EvaluateRot(float t)
        {
            if (TimesR == null || TimesR.Length == 0) return Quaternion.Identity;
            if (TimesR.Length == 1 || t <= TimesR[0]) return Rot[0];
            if (t >= TimesR[TimesR.Length - 1]) return Rot[Rot.Length - 1];
            int i = 0;
            while (i < TimesR.Length - 1 && TimesR[i + 1] < t) i++;
            float factor = (t - TimesR[i]) / (TimesR[i + 1] - TimesR[i]);
            return Quaternion.Slerp(Rot[i], Rot[i + 1], factor);
        }
    }

    public class GrannyAnimation
    {
        public string Name { get; set; }
        public float Duration { get; set; }
        public Dictionary<int, GrannyTrack> Tracks { get; set; } = new Dictionary<int, GrannyTrack>();
    }

    public class GrannyFormatParser
    {
        public List<GrannyMesh> Meshes { get; private set; }
        public List<GrannyBone> Bones { get; private set; }
        public GrannyAnimation Animation { get; private set; }
        private List<GrannyChunk> _flatChunks;

        public GrannyFormatParser()
        {
            Meshes = new List<GrannyMesh>();
            Bones = new List<GrannyBone>();
            _flatChunks = new List<GrannyChunk>();
        }

        public bool Load(string filePath)
        {
            Meshes.Clear();
            Bones.Clear();
            Animation = null;
            _flatChunks.Clear();
            if (!File.Exists(filePath)) return false;
            
            try 
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
                        uint chunkId = reader.ReadUInt32();
                        reader.ReadUInt32();
                        uint dataOffset = reader.ReadUInt32();
                        reader.ReadUInt32();
                        reader.ReadUInt32();

                        long oldPos = reader.BaseStream.Position;
                        reader.BaseStream.Seek(dataOffset, SeekOrigin.Begin);
                        uint subChildrenCount = reader.ReadUInt32();
                        for (int skip = 0; skip < 3; skip++) reader.ReadUInt32();
                        
                        ParseChunkList(reader, dataOffset, subChildrenCount);
                        
                        reader.BaseStream.Seek(oldPos, SeekOrigin.Begin);
                    }

                    var sorted = _flatChunks.Where(c => c.DataOffset > 0).OrderBy(c => c.DataOffset).ToList();
                    for (int i = 0; i < sorted.Count - 1; i++)
                        sorted[i].DataSize = sorted[i+1].DataOffset - sorted[i].DataOffset;
                    if (sorted.Count > 0) sorted[sorted.Count - 1].DataSize = 0xFFFFFFFF;

                    ExtractData(reader);
                }
                return true;
            }
            catch
            {
                return false;
            }
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
                
                uint absoluteOffset = baseOffset + localOffset;
                GrannyChunk chunk = new GrannyChunk {
                    Id = chunkId,
                    DataOffset = absoluteOffset,
                    NumChildren = subChildren
                };
                _flatChunks.Add(chunk);
            }
        }

        private void ExtractData(BinaryReader reader)
        {
            // 1. Extract Skeleton Bones (0xCA5E0506)
            var boneChunks = _flatChunks.Where(c => c.Id == 0xCA5E0506).ToList();
            if (boneChunks.Count > 0)
            {
                Matrix4[] localBind = new Matrix4[boneChunks.Count];
                Matrix4[] worldBind = new Matrix4[boneChunks.Count];

                for (int i = 0; i < boneChunks.Count; i++)
                {
                    reader.BaseStream.Seek(boneChunks[i].DataOffset, SeekOrigin.Begin);
                    int parent = reader.ReadInt32();
                    Vector3 pos = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    Quaternion rot = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

                    GrannyBone gb = new GrannyBone
                    {
                        Id = i,
                        ParentId = parent,
                        LocalPosition = pos,
                        LocalRotation = rot
                    };
                    Bones.Add(gb);
                    localBind[i] = Matrix4.CreateFromQuaternion(rot) * Matrix4.CreateTranslation(pos);
                }

                for (int i = 0; i < boneChunks.Count; i++)
                {
                    int parent = Bones[i].ParentId;
                    if (parent == i || parent < 0 || parent >= i)
                        worldBind[i] = localBind[i];
                    else
                        worldBind[i] = localBind[i] * worldBind[parent];

                    Bones[i].InverseBindMatrix = Matrix4.Invert(worldBind[i]);
                }
            }

            // 2. Extract Meshes (Vertices 0xCA5E0801, Polygons 0xCA5E0901, UVs 0xCA5E0803, 0xCA5E0E06, Skin 0xCA5E0702)
            var ptsChunks = _flatChunks.Where(c => c.Id == 0xCA5E0801).ToList();
            var polChunks = _flatChunks.Where(c => c.Id == 0xCA5E0901).ToList();
            var uvPtsChunks = _flatChunks.Where(c => c.Id == 0xCA5E0803).ToList();
            var uvPolChunks = _flatChunks.Where(c => c.Id == 0xCA5E0E06).ToList();
            var skinChunks = _flatChunks.Where(c => c.Id == 0xCA5E0702).ToList();
            var c0aChunks = _flatChunks.Where(c => c.Id == 0xCA5E0C0A).ToList();

            int[] boneMap = new int[c0aChunks.Count];
            for (int i = 0; i < c0aChunks.Count; i++)
            {
                reader.BaseStream.Seek(c0aChunks[i].DataOffset, SeekOrigin.Begin);
                boneMap[i] = reader.ReadInt32();
            }

            int meshCount = Math.Max(ptsChunks.Count, polChunks.Count);

            for (int j = 0; j < meshCount; j++)
            {
                GrannyMesh m = new GrannyMesh();
                var ptsChunk = j < ptsChunks.Count ? ptsChunks[j] : null;
                var polChunk = j < polChunks.Count ? polChunks[j] : null;
                var uvPolChunk = j < uvPolChunks.Count ? uvPolChunks[j] : null;
                var skinChunk = j < skinChunks.Count ? skinChunks[j] : null;

                if (ptsChunk != null && ptsChunk.DataSize != 0xFFFFFFFF)
                {
                    reader.BaseStream.Seek(ptsChunk.DataOffset, SeekOrigin.Begin);
                    int count = (int)(ptsChunk.DataSize / 12);
                    for (int i = 0; i < count; i++)
                        m.Vertices.Add(new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
                }
                
                if (polChunk != null && polChunk.DataSize != 0xFFFFFFFF)
                {
                    reader.BaseStream.Seek(polChunk.DataOffset, SeekOrigin.Begin);
                    int count = (int)(polChunk.DataSize / 24);
                    for (int i = 0; i < count; i++)
                    {
                        m.Indices.Add(reader.ReadInt32());
                        m.Indices.Add(reader.ReadInt32());
                        m.Indices.Add(reader.ReadInt32());
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
                                reader.ReadInt32(); // ID
                                reader.ReadInt32(); int u1 = reader.ReadInt32(); m.UVIndices.Add(u1);
                                reader.ReadInt32(); int u2 = reader.ReadInt32(); m.UVIndices.Add(u2);
                                reader.ReadInt32(); int u3 = reader.ReadInt32(); m.UVIndices.Add(u3);
                                if (u1 > requiredUvs) requiredUvs = u1;
                                if (u2 > requiredUvs) requiredUvs = u2;
                                if (u3 > requiredUvs) requiredUvs = u3;
                            }
                            else
                            {
                                reader.ReadInt32(); // ID
                                int u1 = reader.ReadInt32(); m.UVIndices.Add(u1);
                                int u2 = reader.ReadInt32(); m.UVIndices.Add(u2);
                                int u3 = reader.ReadInt32(); m.UVIndices.Add(u3);
                                if (u1 > requiredUvs) requiredUvs = u1;
                                if (u2 > requiredUvs) requiredUvs = u2;
                                if (u3 > requiredUvs) requiredUvs = u3;
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
                        m.UVs.Add(new Vector2(reader.ReadSingle(), reader.ReadSingle()));
                        reader.ReadUInt32();
                    }
                }

                // Skin Weights (0xCA5E0702)
                if (skinChunk != null && skinChunk.DataSize != 0xFFFFFFFF)
                {
                    reader.BaseStream.Seek(skinChunk.DataOffset, SeekOrigin.Begin);
                    int numVerts = reader.ReadInt32();
                    reader.ReadInt32(); // h1
                    reader.ReadInt32(); // h2

                    for (int v = 0; v < numVerts; v++)
                    {
                        int infCount = reader.ReadInt32();
                        var influences = new VertexInfluence[infCount];
                        for (int k = 0; k < infCount; k++)
                        {
                            int localB = reader.ReadInt32();
                            float w = reader.ReadSingle();
                            int masterB = (localB >= 0 && localB < boneMap.Length) ? boneMap[localB] : localB;
                            influences[k] = new VertexInfluence { BoneId = masterB, Weight = w };
                        }
                        m.Skin.Add(influences);
                    }
                }
                
                if (m.Vertices.Count > 0 && m.Indices.Count > 0)
                {
                    Meshes.Add(m);
                }
            }

            // 3. Extract Animation Tracks (0xCA5E1204)
            var trackChunks = _flatChunks.Where(c => c.Id == 0xCA5E1204).ToList();
            if (trackChunks.Count > 0)
            {
                Animation = new GrannyAnimation();
                float maxDuration = 0;

                foreach (var tc in trackChunks)
                {
                    reader.BaseStream.Seek(tc.DataOffset, SeekOrigin.Begin);
                    int boneId = reader.ReadInt32();
                    for (int skip = 0; skip < 5; skip++) reader.ReadInt32();
                    int cP = reader.ReadInt32();
                    int cR = reader.ReadInt32();
                    int cS = reader.ReadInt32();
                    for (int skip = 0; skip < 4; skip++) reader.ReadInt32();

                    var track = new GrannyTrack { BoneId = boneId };
                    track.TimesP = new float[cP];
                    for (int i = 0; i < cP; i++)
                    {
                        track.TimesP[i] = reader.ReadSingle();
                        if (track.TimesP[i] > maxDuration) maxDuration = track.TimesP[i];
                    }

                    track.TimesR = new float[cR];
                    for (int i = 0; i < cR; i++)
                    {
                        track.TimesR[i] = reader.ReadSingle();
                        if (track.TimesR[i] > maxDuration) maxDuration = track.TimesR[i];
                    }

                    // Skip scale times if any
                    for (int i = 0; i < cS; i++) reader.ReadSingle();

                    track.Pos = new Vector3[cP];
                    for (int i = 0; i < cP; i++)
                        track.Pos[i] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

                    track.Rot = new Quaternion[cR];
                    for (int i = 0; i < cR; i++)
                        track.Rot[i] = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

                    Animation.Tracks[boneId] = track;
                }

                Animation.Duration = maxDuration;
            }
        }
    }
}
"""

with open(r"c:\Games\Sphere\Programmes\TestGemini\Axis2_WPF\UOFiddler-master\UoFiddler.Plugin.Granny3D\GrannyFormatParser.cs", "w", encoding="utf-8-sig") as f:
    f.write(code)
print("Updated GrannyFormatParser.cs successfully")
