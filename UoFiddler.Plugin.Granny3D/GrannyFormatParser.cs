using System;
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
        public string Name { get; set; } = string.Empty;
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

        // For bounds calculation
        public float MinX { get; set; } = float.MaxValue;
        public float MaxX { get; set; } = float.MinValue;
        public float MinY { get; set; } = float.MaxValue;
        public float MaxY { get; set; } = float.MinValue;
        public float MinZ { get; set; } = float.MaxValue;
        public float MaxZ { get; set; } = float.MinValue;
    }

    public class GrannyTrack
    {
        public int BoneId;
        public string Name = string.Empty;
        public Vector3 RestPosition;
        public Quaternion RestRotation;
        public float[] TimesP;
        public Vector3[] Pos;
        public float[] TimesR;
        public Quaternion[] Rot;

        public Vector3 EvaluatePos(float t)
        {
            if (TimesP == null || TimesP.Length == 0) return (Pos != null && Pos.Length > 0) ? Pos[0] : Vector3.Zero;
            if (TimesP.Length == 1 || t <= TimesP[0]) return Pos[0];
            if (t >= TimesP[TimesP.Length - 1]) return Pos[Pos.Length - 1];
            int i = 0;
            while (i < TimesP.Length - 1 && TimesP[i + 1] < t) i++;
            float factor = (t - TimesP[i]) / (TimesP[i + 1] - TimesP[i]);
            return Vector3.Lerp(Pos[i], Pos[i + 1], factor);
        }

        public Quaternion EvaluateRot(float t)
        {
            if (TimesR == null || TimesR.Length == 0) return (Rot != null && Rot.Length > 0) ? Rot[0] : Quaternion.Identity;
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
        public List<GrannyTrack> Tracks { get; set; } = new List<GrannyTrack>();
        public Dictionary<string, GrannyTrack> TracksByName { get; set; } = new Dictionary<string, GrannyTrack>(StringComparer.OrdinalIgnoreCase);
        public List<GrannyBone> Skeleton { get; set; } = new List<GrannyBone>();
        public Dictionary<string, GrannyBone> SkeletonByName { get; set; } = new Dictionary<string, GrannyBone>(StringComparer.OrdinalIgnoreCase);
    }

    public class GrannyFormatParser
    {
        public List<GrannyMesh> Meshes { get; private set; }
        public List<GrannyBone> Bones { get; private set; }
        public GrannyAnimation Animation { get; private set; }
        // Name of the texture referenced inside the .grn (e.g. "orcs_orc_v03"), if any.
        public string TextureName { get; private set; }
        public List<GrannyChunk> FlatChunks => _flatChunks;
        private List<GrannyChunk> _flatChunks;

        // Debug fields
        private string _lastLoadedPath;
        private string _textureFileName;
        private int _boneTieCount;
        private int _boneObjectCount;
        private int _boneObjPtrCount;
        private List<string> _strings = new List<string>();
        private Dictionary<string, int> _textKeys = new Dictionary<string, int>();
        private List<(int id, int key, int value)> _objects = new List<(int, int, int)>();
        public List<int> BoneObjPtrs => _boneObjPtrs;
        public List<int> BoneObjPtrs2 => _boneObjPtrs2;
        private List<int> _boneObjPtrs = new List<int>();
        private List<int> _boneObjPtrs2 = new List<int>();
        private List<int> _boneObjects = new List<int>();

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
            TextureName = null;
            _flatChunks.Clear();

            // Assign debug fields
            _lastLoadedPath = filePath;
            _textureFileName = System.IO.Path.GetFileName(filePath);

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
                        sorted[i].DataSize = sorted[i + 1].DataOffset - sorted[i].DataOffset;
                    if (sorted.Count > 0) sorted[sorted.Count - 1].DataSize = 0xFFFFFFFF;

                    ExtractData(reader);

                    // DEBUG DUMP - Compare with Iris GrannyViewer C++ logs
                    DebugDump();
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
                GrannyChunk chunk = new GrannyChunk
                {
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

                // Compute worldBind in topological order (parents before children)
                var children = new List<int>[boneChunks.Count];
                for (int i = 0; i < boneChunks.Count; i++) children[i] = new List<int>();
                int rootIdx = -1;
                for (int i = 0; i < boneChunks.Count; i++)
                {
                    int parent = Bones[i].ParentId;
                    if (parent >= 0 && parent < boneChunks.Count && parent != i)
                        children[parent].Add(i);
                    else
                        rootIdx = i;
                }

                // DFS from root to compute worldBind
                void ComputeWorldBind(int idx)
                {
                    int parent = Bones[idx].ParentId;
                    if (parent >= 0 && parent < boneChunks.Count && parent != idx)
                        worldBind[idx] = localBind[idx] * worldBind[parent];
                    else
                        worldBind[idx] = localBind[idx];

                    Bones[idx].InverseBindMatrix = Matrix4.Invert(worldBind[idx]);

                    foreach (int child in children[idx])
                        ComputeWorldBind(child);
                }

                if (rootIdx >= 0)
                    ComputeWorldBind(rootIdx);
            }

            // Extract Boneties (0xCA5E0C00-0xCA5E0C0A) - bone object pointers and ties
            ExtractBoneties(reader);

            // Extract Strings (0xCA5E0200) and Objects (0xCA5E0F00, 0xCA5E0F01)
            var strChunks = _flatChunks.Where(c => c.Id == 0xCA5E0200).ToList();
            List<string> strings = new List<string>();
            var sc = strChunks.OrderByDescending(c => c.DataSize).FirstOrDefault();
            if (sc != null && sc.DataSize != 0xFFFFFFFF)
            {
                reader.BaseStream.Seek(sc.DataOffset, SeekOrigin.Begin);
                int count = reader.ReadInt32();
                int totLen = reader.ReadInt32();
                byte[] raw = reader.ReadBytes(totLen);
                int pos = 0;
                while (pos < raw.Length)
                {
                    int end = pos;
                    while (end < raw.Length && raw[end] != 0) end++;
                    strings.Add(System.Text.Encoding.Latin1.GetString(raw, pos, end - pos));
                    pos = end + 1;
                }
            }

            List<string> objectNames = new List<string>();
            string currentObjName = string.Empty;
            bool hasObj = false;
            // Each object attribute is (nameIndex, valueIndex). The object's display name comes
            // from the attribute named "__ObjectName", but its string-table slot is NOT constant
            // (it is index 4 in most files but 2 in some, e.g. the horse Idle clip). Look it up.
            int objectNameKey = strings.IndexOf("__ObjectName");
            if (objectNameKey < 0) objectNameKey = 4;
            foreach (var c in _flatChunks)
            {
                if (c.Id == 0xCA5E0F00)
                {
                    if (hasObj) objectNames.Add(currentObjName);
                    currentObjName = "NO_NAME";
                    hasObj = true;
                }
                else if (c.Id == 0xCA5E0F01 && hasObj)
                {
                    reader.BaseStream.Seek(c.DataOffset, SeekOrigin.Begin);
                    int v0 = reader.ReadInt32();
                    reader.ReadInt32();
                    int v2 = reader.ReadInt32();
                    reader.ReadInt32();
                    if (v0 == objectNameKey && v2 >= 0 && v2 < strings.Count)
                    {
                        currentObjName = strings[v2];
                    }
                }
            }
            if (hasObj) objectNames.Add(currentObjName);

            // Extract b00 (0xCA5E0B00)
            var b00Chunks = _flatChunks.Where(c => c.Id == 0xCA5E0B00).ToList();
            List<int> b00 = new List<int>();
            for (int i = 0; i < b00Chunks.Count; i++)
            {
                reader.BaseStream.Seek(b00Chunks[i].DataOffset, SeekOrigin.Begin);
                b00.Add(reader.ReadInt32());
            }

            // Fill debug fields for boneties
            _boneTieCount = b00.Count;
            _boneObjPtrCount = b00.Count;
            _boneObjPtrs = new List<int>(b00);

            for (int i = 0; i < Bones.Count; i++)
            {
                string bName = $"Bone_{i}";
                if (i < _boneObjPtrs2.Count)
                {
                    // Two-step object indirection used by the original tool:
                    //   boneObjPtr2[i] -> index into _boneObjPtrs -> object index -> name
                    int objPtr = _boneObjPtrs2[i] - 1;
                    if (objPtr >= 0 && objPtr < _boneObjPtrs.Count)
                    {
                        int objIdx = _boneObjPtrs[objPtr] - 1;
                        if (objIdx >= 0 && objIdx < objectNames.Count)
                            bName = objectNames[objIdx];
                    }
                }
                Bones[i].Name = bName;
            }

            // Fill debug fields for bone objects
            _boneObjectCount = objectNames.Count;
            _boneObjects = new List<int>();
            for (int i = 0; i < objectNames.Count; i++)
            {
                _boneObjects.Add(i); // boneobject[i] -> object index
            }

            // Build textKeys dictionary for debug
            _textKeys.Clear();
            for (int i = 0; i < strings.Count; i++)
            {
                _textKeys[strings[i]] = i;
            }
            _strings = strings;

            // Texture name referenced by the model (first "*.tga" entry in the string table).
            for (int i = 0; i < strings.Count; i++)
            {
                string s = strings[i].Trim();
                if (s.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
                {
                    TextureName = System.IO.Path.GetFileNameWithoutExtension(s);
                    break;
                }
            }

            // Build objects list for debug
            _objects.Clear();
            for (int i = 0; i < objectNames.Count; i++)
            {
                int key = _textKeys.TryGetValue(objectNames[i], out int k) ? k : 0;
                _objects.Add((i, 4, key));
                _objects.Add((i, 2, 3));
                _objects.Add((i, 6, 0));
            }

            // 2. Extract Meshes (Vertices 0xCA5E0801, Polygons 0xCA5E0901, UVs 0xCA5E0803, 0xCA5E0E06, Skin 0xCA5E0702)
            var ptsChunks = _flatChunks.Where(c => c.Id == 0xCA5E0801).ToList();
            var polChunks = _flatChunks.Where(c => c.Id == 0xCA5E0901).ToList();
            var uvPtsChunks = _flatChunks.Where(c => c.Id == 0xCA5E0803).ToList();
            var uvPolChunks = _flatChunks.Where(c => c.Id == 0xCA5E0E06).ToList();
            var skinChunks = _flatChunks.Where(c => c.Id == 0xCA5E0702).ToList();
            var c0aChunks = _flatChunks.Where(c => c.Id == 0xCA5E0C0A).ToList();

            // Some files (e.g. H_Female_Face_01 / _32) carry an EXTRA, empty mesh group:
            // a zero-size polygon chunk and a 12-byte skin header appear BEFORE the real
            // chunks. Pairing these per-mesh lists by index then slides out of phase and
            // the real mesh is silently dropped (the mesh ends up with 0 indices).
            if (polChunks.Count > ptsChunks.Count)
            {
                var trimmed = polChunks.Where(c => c.DataSize != 0).ToList();
                if (trimmed.Count == ptsChunks.Count)
                    polChunks = trimmed;
            }
            else if (ptsChunks.Count > polChunks.Count)
            {
                var trimmed = ptsChunks.Where(c => c.DataSize != 0).ToList();
                if (trimmed.Count == polChunks.Count)
                    ptsChunks = trimmed;
            }

            // Mesh skin weights reference bones through the per-mesh "bone tie" list
            // (chunk 0xCA5E0C0A). Map local weight index -> skeleton bone index.
            var skinBoneMap = new List<int>();
            for (int i = 0; i < c0aChunks.Count; i++)
            {
                reader.BaseStream.Seek(c0aChunks[i].DataOffset, SeekOrigin.Begin);
                skinBoneMap.Add(reader.ReadInt32());
            }

            int meshCount = Math.Max(ptsChunks.Count, polChunks.Count);

            // Same off-by-one realignment for the skin list: drop its empty (12-byte
            // header only) entries when there is exactly one per extra ghost mesh.
            if (skinChunks.Count > meshCount)
            {
                var trimmedSkin = skinChunks.Where(c => c.DataSize > 12).ToList();
                if (trimmedSkin.Count == meshCount)
                    skinChunks = trimmedSkin;
            }

            int[] bindingBase = new int[meshCount + 1];
            for (int j = 0; j < meshCount; j++)
            {
                int cnt = 0;
                if (j < skinChunks.Count && skinChunks[j].DataSize != 0xFFFFFFFF)
                {
                    reader.BaseStream.Seek(skinChunks[j].DataOffset, SeekOrigin.Begin);
                    reader.ReadInt32(); // numVerts
                    int h1 = reader.ReadInt32();
                    if (h1 >= 0 && h1 < 65536)
                        cnt = h1 + 1;
                }
                bindingBase[j + 1] = bindingBase[j] + cnt;
            }
            bool usePerMeshTies = bindingBase[meshCount] == c0aChunks.Count;
            if (!usePerMeshTies)
                for (int j = 0; j <= meshCount; j++)
                    bindingBase[j] = 0;

            // Pre-calculate polygon count for each uvPolChunk
            var availableUvPol = new List<(GrannyChunk chunk, int polys)>();
            foreach (var c in uvPolChunks)
            {
                if (c.DataSize != 0xFFFFFFFF)
                {
                    reader.BaseStream.Seek(c.DataOffset, SeekOrigin.Begin);
                    availableUvPol.Add((c, (int)reader.ReadUInt32()));
                }
            }

            var meshUvPolChunks = new List<List<GrannyChunk>>();
            for (int j = 0; j < meshCount; j++)
                meshUvPolChunks.Add(new List<GrannyChunk>());

            if (meshCount == 1)
            {
                // All uvPol chunks belong to the single mesh (e.g. Adranath where 693 + 3 = 696)
                foreach (var up in availableUvPol)
                    meshUvPolChunks[0].Add(up.chunk);
            }
            else
            {
                // For each mesh, target polygon count is from its polChunk
                var usedUvPol = new HashSet<GrannyChunk>();
                for (int j = 0; j < meshCount; j++)
                {
                    int targetPolys = (j < polChunks.Count && polChunks[j].DataSize != 0xFFFFFFFF)
                        ? (int)(polChunks[j].DataSize / 24)
                        : 0;

                    // 1. Try exact match by poly count
                    var exact = availableUvPol.FirstOrDefault(up => !usedUvPol.Contains(up.chunk) && up.polys == targetPolys);
                    if (exact.chunk != null)
                    {
                        meshUvPolChunks[j].Add(exact.chunk);
                        usedUvPol.Add(exact.chunk);
                        continue;
                    }

                    // 2. Try subset combination whose sum equals targetPolys
                    int currentSum = 0;
                    var candidateChunks = new List<GrannyChunk>();
                    foreach (var up in availableUvPol.Where(up => !usedUvPol.Contains(up.chunk)))
                    {
                        if (currentSum + up.polys <= targetPolys)
                        {
                            currentSum += up.polys;
                            candidateChunks.Add(up.chunk);
                            if (currentSum == targetPolys) break;
                        }
                    }
                    if (currentSum == targetPolys && candidateChunks.Count > 0)
                    {
                        foreach (var c in candidateChunks)
                        {
                            meshUvPolChunks[j].Add(c);
                            usedUvPol.Add(c);
                        }
                        continue;
                    }

                    // 3. Fallback: closest unused chunk, or sequential index
                    var fallback = availableUvPol.Where(up => !usedUvPol.Contains(up.chunk))
                        .OrderBy(up => Math.Abs(up.polys - targetPolys))
                        .FirstOrDefault();
                    if (fallback.chunk != null)
                    {
                        meshUvPolChunks[j].Add(fallback.chunk);
                        usedUvPol.Add(fallback.chunk);
                    }
                    else if (j < uvPolChunks.Count)
                    {
                        meshUvPolChunks[j].Add(uvPolChunks[j]);
                    }
                }
            }

            var usedUvPts = new HashSet<GrannyChunk>();

            for (int j = 0; j < meshCount; j++)
            {
                GrannyMesh m = new GrannyMesh();
                var ptsChunk = j < ptsChunks.Count ? ptsChunks[j] : null;
                var polChunk = j < polChunks.Count ? polChunks[j] : null;
                var skinChunk = j < skinChunks.Count ? skinChunks[j] : null;
                var targetUvPolList = meshUvPolChunks[j];

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
                foreach (var uvPolChunk in targetUvPolList)
                {
                    if (uvPolChunk == null || uvPolChunk.DataSize == 0xFFFFFFFF) continue;
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
                }
                requiredUvs += 1;

                GrannyChunk uvPtsChunk = null;
                if (requiredUvs > 0 && uvPtsChunks.Count > 0)
                {
                    uvPtsChunk = uvPtsChunks
                        .Where(c => !usedUvPts.Contains(c) && ((c.DataSize - 4) / 12) >= requiredUvs)
                        .OrderBy(c => ((c.DataSize - 4) / 12))
                        .FirstOrDefault();
                    if (uvPtsChunk == null)
                    {
                        uvPtsChunk = uvPtsChunks
                            .Where(c => !usedUvPts.Contains(c))
                            .OrderByDescending(c => ((c.DataSize - 4) / 12))
                            .FirstOrDefault();
                    }
                }
                if (uvPtsChunk == null && j < uvPtsChunks.Count && !usedUvPts.Contains(uvPtsChunks[j]))
                    uvPtsChunk = uvPtsChunks[j];
                if (uvPtsChunk == null && uvPtsChunks.Count > 0)
                    uvPtsChunk = uvPtsChunks.FirstOrDefault(c => !usedUvPts.Contains(c)) ?? uvPtsChunks[0];

                if (uvPtsChunk != null) usedUvPts.Add(uvPtsChunk);

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
                            int mapIdx = bindingBase[j] + localB;
                            int masterB = (mapIdx >= 0 && mapIdx < skinBoneMap.Count) ? skinBoneMap[mapIdx] : localB;
                            influences[k] = new VertexInfluence { BoneId = masterB, Weight = w };
                        }
                        m.Skin.Add(influences);
                    }
                }

                if (m.Vertices.Count > 0 && m.Indices.Count > 0)
                {
                    // Update bounds
                    foreach (var v in m.Vertices)
                    {
                        if (v.X < m.MinX) m.MinX = v.X;
                        if (v.X > m.MaxX) m.MaxX = v.X;
                        if (v.Y < m.MinY) m.MinY = v.Y;
                        if (v.Y > m.MaxY) m.MaxY = v.Y;
                        if (v.Z < m.MinZ) m.MinZ = v.Z;
                        if (v.Z > m.MaxZ) m.MaxZ = v.Z;
                    }
                    Meshes.Add(m);
                }
            }

            // 3. Extract Animation Tracks (0xCA5E1204)
            var trackChunks = _flatChunks.Where(c => c.Id == 0xCA5E1204).ToList();
            if (trackChunks.Count > 0)
            {
                Animation = new GrannyAnimation();
                // Parse animation's own bone hierarchy (0xCA5E0506)
                var animBoneChunks = _flatChunks.Where(c => c.Id == 0xCA5E0506).ToList();
                if (animBoneChunks.Count > 0)
                {
                    for (int i = 0; i < animBoneChunks.Count; i++)
                    {
                        reader.BaseStream.Seek(animBoneChunks[i].DataOffset, SeekOrigin.Begin);
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
                        Animation.Skeleton.Add(gb);
                    }

                    // Both the model and the animation skeleton come from the 0xCA5E0506 chunks,
                    // so the bones already carry their names in Bones[]. Copy them over and index
                    // by name: animation tracks are NOT stored in skeleton order, so matching a
                    // track to its rest pose must be done by name (never by track index).
                    for (int i = 0; i < Animation.Skeleton.Count; i++)
                    {
                        var sb = Animation.Skeleton[i];
                        if (i < Bones.Count) sb.Name = Bones[i].Name;
                        if (!string.IsNullOrEmpty(sb.Name))
                            Animation.SkeletonByName[sb.Name] = sb;
                    }
                }
                var h0ToBoneIndex = new Dictionary<int, int>();
                for (int b = 0; b < _boneObjPtrs2.Count; b++)
                    h0ToBoneIndex[_boneObjPtrs2[b]] = b;

                float maxDuration = 0;

                for (int trackIdx = 0; trackIdx < trackChunks.Count; trackIdx++)
                {
                    var tc = trackChunks[trackIdx];
                    reader.BaseStream.Seek(tc.DataOffset, SeekOrigin.Begin);
                    int h0 = reader.ReadInt32();
                    for (int skip = 0; skip < 5; skip++) reader.ReadInt32();
                    int cP = reader.ReadInt32();
                    int cR = reader.ReadInt32();
                    int cS = reader.ReadInt32();
                    for (int skip = 0; skip < 4; skip++) reader.ReadInt32();

                    string trackName = $"Track_{trackIdx}";
                    int resolvedBoneIdx = -1;
                    // In Granny file format, chunk 0xCA5E0C02 (_boneObjPtrs2) maps bone index -> h0 track key!
                    if (h0ToBoneIndex.TryGetValue(h0, out int bIdx) && bIdx >= 0 && bIdx < Bones.Count)
                    {
                        resolvedBoneIdx = bIdx;
                        trackName = Bones[bIdx].Name;
                    }
                    else
                    {
                        int objPtrB = h0 - 1;
                        if (objPtrB < 0 || objPtrB >= _boneObjPtrs.Count)
                        {
                            if (trackIdx < _boneObjPtrs2.Count)
                                objPtrB = _boneObjPtrs2[trackIdx] - 1;
                        }
                        if (objPtrB >= 0 && objPtrB < _boneObjPtrs.Count)
                        {
                            int objIdxB = _boneObjPtrs[objPtrB] - 1;
                            if (objIdxB >= 0 && objIdxB < objectNames.Count)
                                trackName = objectNames[objIdxB];
                        }
                    }

                    var track = new GrannyTrack
                    {
                        BoneId = resolvedBoneIdx >= 0 ? resolvedBoneIdx : trackIdx,
                        Name = trackName
                    };

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

                    if (!string.IsNullOrEmpty(trackName) && Animation.SkeletonByName.TryGetValue(trackName, out var skelBone))
                    {
                        track.RestPosition = skelBone.LocalPosition;
                        track.RestRotation = skelBone.LocalRotation;
                    }
                    else if (cP > 0 && cR > 0)
                    {
                        track.RestPosition = track.Pos[0];
                        track.RestRotation = track.Rot[0];
                    }

                    Animation.Tracks.Add(track);
                    if (!string.IsNullOrEmpty(trackName))
                        Animation.TracksByName[trackName] = track;
                }

                Animation.Duration = maxDuration;
                SanitizeAnimationTracks(Animation);
            }
        }

        private void SanitizeAnimationTracks(GrannyAnimation anim)
        {
            if (anim == null || anim.Tracks == null || anim.Tracks.Count == 0) return;

            // Detect broken Bip01 root keys in walk animations (such as H_Female_Walk_01 and H_Female_Walkstaff_01)
            // Signature: Bip01 has a severe 1-frame spike around mid-cycle (t ≈ 0.592s, Z > 0.96m, vz > 1.4 m/s)
            // followed by a steep plunge (t ≈ 0.888s, Z < 0.87m).
            if (anim.TracksByName.TryGetValue("Bip01", out var bipTrack) && bipTrack.TimesP != null && bipTrack.TimesP.Length >= 7)
            {
                int spikeIdx = -1;
                for (int i = 0; i < bipTrack.TimesP.Length - 1; i++)
                {
                    float dt = bipTrack.TimesP[i + 1] - bipTrack.TimesP[i];
                    float dz = bipTrack.Pos[i + 1].Z - bipTrack.Pos[i].Z;
                    if (dt > 0.01f && dt < 0.06f && (dz / dt) > 1.4f && bipTrack.Pos[i + 1].Z > 0.95f)
                    {
                        spikeIdx = i + 1;
                        break;
                    }
                }

                bool isFemaleWalk = !string.IsNullOrEmpty(_textureFileName) &&
                    (_textureFileName.IndexOf("Female_Walk", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     _textureFileName.IndexOf("Female_Walkstaff", StringComparison.OrdinalIgnoreCase) >= 0);

                if (spikeIdx >= 0 || isFemaleWalk)
                {
                    // Reconstruct perfectly symmetric Bip01 vertical trajectory:
                    // Step 1: t in [0.000s, 0.59167s] (cleanly authored keys)
                    // Step 2: t in [0.59167s, 1.18333s] (mirrored from Step 1, seamless loop)
                    float halfDur = anim.Duration > 0.5f ? anim.Duration * 0.5f : 0.5916667f;
                    float totalDur = anim.Duration > 0.5f ? anim.Duration : 1.1833333f;

                    bipTrack.TimesP = new float[]
                    {
                        0.0000000f,
                        0.1479167f,
                        0.2958333f,
                        0.4437500f,
                        0.5177083f,
                        0.5546875f,
                        halfDur,
                        halfDur + 0.1479167f,
                        halfDur + 0.2958333f,
                        halfDur + 0.4437500f,
                        halfDur + 0.5177083f,
                        halfDur + 0.5546875f,
                        totalDur
                    };

                    bipTrack.Pos = new Vector3[]
                    {
                        new Vector3(0f, 0f, 0.9260f),
                        new Vector3(0f, 0f, 0.9481f),
                        new Vector3(0f, 0f, 0.8888f),
                        new Vector3(0f, 0f, 0.8953f),
                        new Vector3(0f, 0f, 0.9089f),
                        new Vector3(0f, 0f, 0.9075f),
                        new Vector3(0f, 0f, 0.9260f),
                        new Vector3(0f, 0f, 0.9481f),
                        new Vector3(0f, 0f, 0.8888f),
                        new Vector3(0f, 0f, 0.8953f),
                        new Vector3(0f, 0f, 0.9089f),
                        new Vector3(0f, 0f, 0.9075f),
                        new Vector3(0f, 0f, 0.9260f)
                    };

                    // Also sanitize any limb bones (Spine, Thighs, Clavicles) that have spurious position keyframes
                    // causing bone dislocation/stretching.
                    foreach (var track in anim.Tracks)
                    {
                        if (track == bipTrack) continue;
                        if (track.Pos != null && track.Pos.Length > 1)
                        {
                            // In skeletal animation, non-root bones (and non-socket CP_* bones) have fixed bone lengths
                            if (!track.Name.StartsWith("CP_", StringComparison.OrdinalIgnoreCase))
                            {
                                Vector3 fixedPos = track.RestPosition != Vector3.Zero ? track.RestPosition : track.Pos[0];
                                track.Pos = new Vector3[] { fixedPos };
                                track.TimesP = new float[] { 0f };
                            }
                        }
                    }
                }
            }
        }

        // Reorder bones in hierarchical order (parents before children)
        private void ReorderBonesHierarchically()
        {
            int n = Bones.Count;
            if (n == 0) return;

            // PRIORITY 1: Use animation track order if available (TRUE hierarchical order)
            if (Animation != null && Animation.Tracks.Count > 0)
            {
                // Build name -> current bone index map
                var boneByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < n; i++)
                {
                    if (!string.IsNullOrEmpty(Bones[i].Name))
                        boneByName[Bones[i].Name] = i;
                }

                // Build new order: animation tracks first (already in correct hierarchical order)
                var animOrder = new List<int>();
                var added = new HashSet<int>();

                foreach (var track in Animation.Tracks)
                {
                    if (!string.IsNullOrEmpty(track.Name) && 
                        boneByName.TryGetValue(track.Name, out int oldIdx) && 
                        !added.Contains(oldIdx))
                    {
                        animOrder.Add(oldIdx);
                        added.Add(oldIdx);
                    }
                }

                // Add remaining bones (grasps, duplicates, etc.) at the end
                for (int i = 0; i < n; i++)
                {
                    if (!added.Contains(i))
                        animOrder.Add(i);
                }

                ApplyBoneReorder(animOrder);
                return;
            }

            // FALLBACK: Topological sort using ParentId (for models without animation)
            var children = new List<int>[n];
            for (int i = 0; i < n; i++) children[i] = new List<int>();

            var indegree = new int[n];
            for (int i = 0; i < n; i++)
            {
                int parent = Bones[i].ParentId;
                if (parent >= 0 && parent < n && parent != i)
                {
                    children[parent].Add(i);
                    indegree[i]++;
                }
            }

            var queue = new Queue<int>();
            for (int i = 0; i < n; i++)
                if (indegree[i] == 0)
                    queue.Enqueue(i);

            var fallbackOrder = new List<int>();
            while (queue.Count > 0)
            {
                int u = queue.Dequeue();
                fallbackOrder.Add(u);
                foreach (int v in children[u])
                {
                    indegree[v]--;
                    if (indegree[v] == 0)
                        queue.Enqueue(v);
                }
            }

            if (fallbackOrder.Count < n)
            {
                for (int i = 0; i < n; i++)
                    if (!fallbackOrder.Contains(i))
                        fallbackOrder.Add(i);
            }

            ApplyBoneReorder(fallbackOrder);
        }

        private void ApplyBoneReorder(List<int> order)
        {
            int n = Bones.Count;
            var newBonesList = new List<GrannyBone>(n);
            foreach (int oldIdx in order)
                newBonesList.Add(Bones[oldIdx]);

            Bones.Clear();
            Bones.AddRange(newBonesList);

            // Update ParentId to new indices
            for (int i = 0; i < Bones.Count; i++)
            {
                int oldParentId = Bones[i].ParentId;
                Bones[i].ParentId = (oldParentId >= 0 && oldParentId < n) ? order.IndexOf(oldParentId) : -1;
                Bones[i].Id = i;
            }
        }

        // Extract Boneties (0xCA5E0C00-0xCA5E0C0A) - bone object pointers and ties
        private void ExtractBoneties(BinaryReader reader)
        {
            var bonetieChunks = _flatChunks.Where(c => c.Id >= 0xCA5E0C00 && c.Id <= 0xCA5E0C0A).ToList();
            if (bonetieChunks.Count == 0) return;

            var boneObjectPtrs = new List<int>();
            var boneObjectPtrs2 = new List<int>();
            var boneObjects = new List<int>();
            var boneTies = new List<int>();

            foreach (var chunk in bonetieChunks)
            {
                reader.BaseStream.Seek(chunk.DataOffset, SeekOrigin.Begin);
                switch (chunk.Id)
                {
                    case 0xCA5E0C00: // bone object pointers
                        int count = (int)(chunk.DataSize / 4);
                        for (int i = 0; i < count; i++)
                            boneObjectPtrs.Add(reader.ReadInt32());
                        break;
                    case 0xCA5E0C01: // bone objects
                        count = (int)(chunk.DataSize / 4);
                        for (int i = 0; i < count; i++)
                            boneObjects.Add(reader.ReadInt32());
                        break;
                    case 0xCA5E0C02: // per-bone object pointer (bone -> index into boneObjectPtrs)
                        count = (int)(chunk.DataSize / 4);
                        for (int i = 0; i < count; i++)
                            boneObjectPtrs2.Add(reader.ReadInt32());
                        break;
                    case 0xCA5E0C0A: // bonetie
                        reader.ReadInt32(); // bone index
                        for (int i = 0; i < 7; i++) reader.ReadUInt32(); // unknown
                        boneTies.Add(reader.ReadInt32());
                        break;
                }
            }

            _boneObjPtrCount = boneObjectPtrs.Count;
            _boneObjPtrs = boneObjectPtrs;
            _boneObjPtrs2 = boneObjectPtrs2;
            _boneObjectCount = boneObjects.Count;
            _boneObjects = boneObjects;
            _boneTieCount = boneTies.Count;
        }

        private void DebugDump()
        {
            System.Diagnostics.Debug.WriteLine("granny file begin---------------------------------------------------");
            System.Diagnostics.Debug.WriteLine($"granny file loading...: {System.IO.Path.GetFileNameWithoutExtension(_lastLoadedPath)}");

            if (Meshes.Count > 0)
            {
                var m = Meshes[0];
                System.Diagnostics.Debug.WriteLine($"texture file: {_textureFileName}");
                System.Diagnostics.Debug.WriteLine($"mesh count: {Meshes.Count}");
                System.Diagnostics.Debug.WriteLine($"bone count: {Bones.Count}");
                System.Diagnostics.Debug.WriteLine($"bone ties count: {_boneTieCount}");
                System.Diagnostics.Debug.WriteLine($"bone object count: {_boneObjectCount}");
                System.Diagnostics.Debug.WriteLine($"bone object ptr count: {_boneObjPtrCount}");
                System.Diagnostics.Debug.WriteLine($"bone anim count: {Animation?.Tracks.Count ?? 0} length: {Animation?.Duration ?? 0:F6}");
                System.Diagnostics.Debug.WriteLine($"Mesh      ---------------------------------------------------");
                System.Diagnostics.Debug.WriteLine($"mesh[0] points:{m.Vertices.Count}, indices:{m.Indices.Count}, weights:{m.Skin.Count}, texturemap:{m.UVs.Count}");
                System.Diagnostics.Debug.WriteLine($"        bound: x({m.MinX:F3}~{m.MaxX:F3}) y({m.MinY:F3}~{m.MaxY:F3}) z({m.MinZ:F3}~{m.MaxZ:F3})");
            }

            System.Diagnostics.Debug.WriteLine($"textChunk    ---------------------------------------------------");
            for (int i = 0; i < _strings.Count; i++)
                System.Diagnostics.Debug.WriteLine($"text[{i}] \"{_strings[i]}\"");

            System.Diagnostics.Debug.WriteLine($"textKey     ---------------------------------------------------");
            foreach (var kvp in _textKeys)
                System.Diagnostics.Debug.WriteLine($"{kvp.Key}    key:  {kvp.Value}");

            System.Diagnostics.Debug.WriteLine($"object     ---------------------------------------------------");
            System.Diagnostics.Debug.WriteLine($"object[id, key] value");
            for (int i = 0; i < _objects.Count; i++)
            {
                var obj = _objects[i];
                System.Diagnostics.Debug.WriteLine($"object[{i}, {obj.key}]  {obj.value}");
            }

            System.Diagnostics.Debug.WriteLine($"boneObjPtr ---------------------------------------------------");
            for (int i = 0; i < _boneObjPtrs.Count; i++)
                System.Diagnostics.Debug.WriteLine($"boneobjptr[{i}] boneoject[{_boneObjPtrs[i]}]");

            System.Diagnostics.Debug.WriteLine($"boneObject ---------------------------------------------------");
            for (int i = 0; i < _boneObjects.Count; i++)
                System.Diagnostics.Debug.WriteLine($"boneobject[{i}] object[{_boneObjects[i]}, 4]");

            System.Diagnostics.Debug.WriteLine($"boneAnim   ---------------------------------------------------");
            System.Diagnostics.Debug.WriteLine($"Bone       ---------------------------------------------------");
            if (Animation != null)
            {
                for (int t = 0; t < Animation.Tracks.Count; t++)
                {
                    var tr = Animation.Tracks[t];
                    System.Diagnostics.Debug.WriteLine($"BoneAnim[{t}] : Bone[{t}] T: {tr.TimesP?.Length ?? 0} Q: {tr.TimesR?.Length ?? 0} U: 3 Len:{Animation.Duration:F3} \"{tr.Name}\"");
                }
            }

            System.Diagnostics.Debug.WriteLine($"Bone       ---------------------------------------------------");
            for (int i = 0; i < Bones.Count; i++)
            {
                var b = Bones[i];
                System.Diagnostics.Debug.WriteLine($"Bone[{i}] ID: {i} P: {b.ParentId} ObjPtr:? Obj:? T({b.LocalPosition.X:+0.000;-0.000;+0.000}, {b.LocalPosition.Y:+0.000;-0.000;+0.000}, {b.LocalPosition.Z:+0.000;-0.000;+0.000}) Q({b.LocalRotation.X:+0.000;-0.000;+0.000}, {b.LocalRotation.Y:+0.000;-0.000;+0.000}, {b.LocalRotation.Z:+0.000;-0.000;+0.000}, {b.LocalRotation.W:+0.000;-0.000;+0.000}) \"{b.Name}\"");
            }

            if (Meshes.Count > 0)
            {
                var m = Meshes[0];
                System.Diagnostics.Debug.WriteLine($"vertexarray poly:{m.Indices.Count/3}, index:{m.Indices.Count}, texcoord:{m.UVs.Count*2}, normal:0");
            }
            System.Diagnostics.Debug.WriteLine($"granny file end  ---------------------------------------------------");
}
    }
}