using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Ultima;

namespace UoFiddler.Plugin.Granny3D
{
    // A VD target ("what the animation is exported as"): the category selected in
    // UOFiddler's tree-view "Export -> To vd.." menu (Animal/Monster/Sea/Equipment
    // mul + Creatures uop). Mirrors AnimationEditForm.GetVdTargetType / action names.
    public sealed class VdTargetDef
    {
        public string Key;
        public string Label;
        public short AnimType;      // 2nd short written in the .vd header
        public int ActionCount;     // number of actions (a.k.a. vdLength)
        public bool Uop;            // true = UOP "per-frame palette" flavour
        public string[] ActionNames;
        // Action slot -> source keyword, when the name doesn't match (e.g. human
        // action 05 "Idle_01" must use the "Impatient" clip, 32 "Bow_Lesser_01"
        // the "Bow_Formal" clip).
        public System.Collections.Generic.Dictionary<int, string> Aliases;

        public VdTargetDef(string key, string label, short animType, int actionCount, bool uop, string[] names)
        {
            Key = key; Label = label; AnimType = animType; ActionCount = actionCount; Uop = uop; ActionNames = names;
        }

        public override string ToString() => Label;
    }

    // One (action, direction) slot: a 256-colour palette plus the encoded frames.
    public sealed class VdAnimBlock
    {
        public ushort[] Palette;
        public List<FrameEdit> Frames;
    }

    // Encodes baked 32bpp bitmaps into UO animation frames and writes a .vd file.
    // The on-disk layout matches Ultima.AnimationEdit.ExportToVD / AnimIdx.ExportToVD.
    public static class VdExport
    {
        public const int DirCount = 5;

        public static readonly string[] AnimalActions =
        {
            "Walk","Run","Idle","Eat","Alert","Attack1","Attack2","GetHit","Die1","Idle","Fidget","LieDown","Die2"
        };

        public static readonly string[] MonsterActions =
        {
            "Walk","Idle","Die1","Die2","Attack1","Attack2","Attack3","AttackBow","AttackCrossBow","AttackThrow",
            "GetHit","Pillage","Stomp","Cast2","Cast3","BlockRight","BlockLeft","Idle","Fidget","Fly","TakeOff","GetHitInAir"
        };

        public static readonly string[] HumanActions =
        {
            "Walk_01","WalkStaff_01","Run_01","RunStaff_01","Idle_01","Idle_01","Fidget_Yawn_Stretch_01","CombatIdle1H_01",
            "CombatIdle1H_01","AttackSlash1H_01","AttackPierce1H_01","AttackBash1H_01","AttackBash2H_01","AttackSlash2H_01",
            "AttackPierce2H_01","CombatAdvance_1H_01","Spell1","Spell2","AttackBow_01","AttackCrossbow_01","GetHit_Fr_Hi_01",
            "Die_Hard_Fwd_01","Die_Hard_Back_01","Horse_Walk_01","Horse_Run_01","Horse_Idle_01","Horse_Attack1H_SlashRight_01",
            "Horse_AttackBow_01","Horse_AttackCrossbow_01","Horse_Attack2H_SlashRight_01","Block_Shield_Hard_01",
            "Punch_Punch_Jab_01","Bow_Lesser_01","Salute_Armed1h_01","Ingest_Eat_01"
        };

        // Creature / UOP action names (32 actions), from UOFiddler GetCreatureActionNames().
        public static readonly string[] CreatureActions =
        {
            "Walk Combat","Idle Combat","Die Backward","Die Forward","Attack 1","Attack 2","Attack 3","AttackBow",
            "AttackCrossBow","AttackThrow","Get Hit","Rummage","Spellcast","Spellcast2","Spellcast3","BlockRight",
            "BlockLeft","Idle","Fidget","Fly","TakeOff","GetHitInAir","Walk","Special","Run","Idle","Fidget","Roar",
            "Peace to Combat","Mounted - Walk","Mounted - Run","Mounted - Idle"
        };

        public static List<VdTargetDef> Targets()
        {
            var humans = new VdTargetDef("equipment_mul", "EQUIPMENT / HUMANS (mul)", 2, 35, false, HumanActions)
            {
                Aliases = new Dictionary<int, string>
                {
                    { 5,  "Impatient_01" },   // 2nd "Idle_01" -> H_*_Impatient_01
                    { 32, "Bow_Formal_01" },  // "Bow_Lesser_01" -> H_*_Bow_Formal_01
                }
            };
            return new List<VdTargetDef>
            {
                new VdTargetDef("monster_mul",      "MONSTER (mul)",          0, 22, false, MonsterActions),
                new VdTargetDef("animal_mul",       "ANIMAL (mul)",           1, 13, false, AnimalActions),
                new VdTargetDef("sea_monster_mul",  "SEA-MONSTER (mul)",      1, 13, false, AnimalActions),
                humans,
                new VdTargetDef("creatures_uop",    "CREATURES (UOP)",        4, 32, true,  CreatureActions),
            };
        }

        // ---- palette / frame encoding ------------------------------------------

        private static ushort To555(int r, int g, int b)
            => (ushort)(0x8000 | ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3));

        // Build one block (palette + frames) from a list of already-rendered 32bppArgb
        // bitmaps. Transparent pixels (alpha < 128) become index 0. Colours are quantised
        // to at most 255 opaque entries (RGB555).
        public static VdAnimBlock BuildBlock(IReadOnlyList<Bitmap> argbFrames, int centerX, int centerY)
        {
            var hist = new Dictionary<ushort, int>();
            for (int f = 0; f < argbFrames.Count; f++)
                Accumulate(argbFrames[f], hist);

            // palette[0] = transparent, then the chosen colours.
            var palette = new ushort[256];
            var chosen = new List<ushort>();
            if (hist.Count <= 255)
            {
                foreach (var kv in hist) chosen.Add(kv.Key);
            }
            else
            {
                var all = new List<KeyValuePair<ushort, int>>(hist);
                all.Sort((a, b) => b.Value.CompareTo(a.Value));
                for (int i = 0; i < 255; i++) chosen.Add(all[i].Key);
            }
            for (int i = 0; i < chosen.Count && i < 255; i++) palette[i + 1] = chosen[i];

            // Fast colour -> index lookup (exact) + nearest fallback.
            var exact = new Dictionary<ushort, byte>();
            for (int i = 1; i < 256; i++) if (palette[i] != 0) exact[palette[i]] = (byte)i;

            var block = new VdAnimBlock { Palette = palette, Frames = new List<FrameEdit>(argbFrames.Count) };
            for (int f = 0; f < argbFrames.Count; f++)
                block.Frames.Add(EncodeFrame(argbFrames[f], palette, exact, chosen, centerX, centerY));
            return block;
        }

        private static void Accumulate(Bitmap bmp, Dictionary<ushort, int> hist)
        {
            var bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                unsafe
                {
                    byte* row = (byte*)bd.Scan0;
                    for (int y = 0; y < bmp.Height; y++)
                    {
                        byte* p = row + (long)y * bd.Stride;
                        for (int x = 0; x < bmp.Width; x++, p += 4)
                        {
                            int a = p[3];
                            if (a < 128) continue;
                            ushort c = To555(p[2], p[1], p[0]); // BGRA
                            hist.TryGetValue(c, out int n);
                            hist[c] = n + 1;
                        }
                    }
                }
            }
            finally { bmp.UnlockBits(bd); }
        }

        private static FrameEdit EncodeFrame(Bitmap bmp, ushort[] palette,
            Dictionary<ushort, byte> exact, List<ushort> chosen, int centerX, int centerY)
        {
            int w = bmp.Width, h = bmp.Height;
            var b1555 = new Bitmap(w, h, PixelFormat.Format16bppArgb1555);
            var bd = b1555.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format16bppArgb1555);
            var src = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                unsafe
                {
                    for (int y = 0; y < h; y++)
                    {
                        byte* sp = (byte*)src.Scan0 + (long)y * src.Stride;
                        ushort* dp = (ushort*)((byte*)bd.Scan0 + (long)y * bd.Stride);
                        for (int x = 0; x < w; x++, sp += 4)
                        {
                            int a = sp[3];
                            if (a < 128) { dp[x] = 0; continue; }
                            ushort c = To555(sp[2], sp[1], sp[0]);
                            if (!exact.TryGetValue(c, out byte idx))
                            {
                                idx = NearestIndex(chosen, c);
                                exact[c] = idx;
                            }
                            dp[x] = palette[idx];
                        }
                    }
                }
            }
            finally { bmp.UnlockBits(src); b1555.UnlockBits(bd); }

            return new FrameEdit(b1555, palette, centerX, centerY);
        }

        private static byte NearestIndex(List<ushort> chosen, ushort c)
        {
            int cr = (c >> 10) & 0x1F, cg = (c >> 5) & 0x1F, cb = c & 0x1F;
            int best = 0; long bestD = long.MaxValue;
            for (int i = 0; i < chosen.Count; i++)
            {
                ushort p = chosen[i];
                int dr = ((p >> 10) & 0x1F) - cr, dg = ((p >> 5) & 0x1F) - cg, db = (p & 0x1F) - cb;
                long d = (long)dr * dr + (long)dg * dg + (long)db * db;
                if (d < bestD) { bestD = d; best = i; }
            }
            return (byte)(best + 1);
        }

        // ---- file writing ------------------------------------------------------

        // byActionDir index = action * 5 + direction.
        public static void WriteMulVd(string path, short animType, int actionCount, VdAnimBlock[] byActionDir)
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var bin = new BinaryWriter(fs))
            {
                bin.Write((short)6);
                bin.Write(animType);
                long indexPos = bin.BaseStream.Position;
                long animPos = indexPos + 12L * actionCount * DirCount;
                for (int i = 0; i < actionCount * DirCount; i++)
                {
                    var blk = (i < byActionDir.Length) ? byActionDir[i] : null;
                    if (blk == null || blk.Frames == null || blk.Frames.Count == 0)
                    {
                        bin.BaseStream.Seek(indexPos, SeekOrigin.Begin);
                        bin.Write(-1); bin.Write(-1); bin.Write(-1);
                        indexPos = bin.BaseStream.Position;
                    }
                    else
                    {
                        WriteBlock(bin, blk, ref indexPos, ref animPos, perFramePalette: false);
                    }
                }
            }
        }

        // UOP "creatures" flavour: header animType 4, per-frame palette, table reserved = 1.
        public static void WriteUopVd(string path, short animType, int actionCount, VdAnimBlock[] byActionDir)
        {
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var bin = new BinaryWriter(fs))
            {
                bin.Write((short)6);
                bin.Write(animType);
                long tablePos = bin.BaseStream.Position;
                for (int i = 0; i < actionCount * DirCount; i++)
                {
                    bin.Write(0); bin.Write(0); bin.Write(0);
                }

                for (int i = 0; i < actionCount * DirCount; i++)
                {
                    var blk = (i < byActionDir.Length) ? byActionDir[i] : null;
                    int action = i / DirCount, dir = i % DirCount;
                    if (blk == null || blk.Frames == null || blk.Frames.Count == 0) continue;

                    long off = bin.BaseStream.Position;
                    long idxPos = tablePos + 12L * i;
                    long endPos = WriteBlockTo(bin, blk, perFramePalette: true);
                    long size = endPos - off;

                    long here = bin.BaseStream.Position;
                    bin.BaseStream.Seek(idxPos, SeekOrigin.Begin);
                    bin.Write((int)off); bin.Write((int)size); bin.Write(1);
                    bin.BaseStream.Seek(here, SeekOrigin.Begin);
                }
                bin.Flush();
            }
        }

        private static void WriteBlock(BinaryWriter bin, VdAnimBlock blk, ref long indexpos, ref long animpos, bool perFramePalette)
        {
            bin.BaseStream.Seek(indexpos, SeekOrigin.Begin);
            bin.Write((int)animpos);
            indexpos = bin.BaseStream.Position;
            bin.BaseStream.Seek(animpos, SeekOrigin.Begin);

            WriteFrames(bin, blk, perFramePalette);

            long length = bin.BaseStream.Position - animpos;
            animpos = bin.BaseStream.Position;
            bin.BaseStream.Seek(indexpos, SeekOrigin.Begin);
            bin.Write((int)length);
            bin.Write(0);
            indexpos = bin.BaseStream.Position;
        }

        private static long WriteBlockTo(BinaryWriter bin, VdAnimBlock blk, bool perFramePalette)
        {
            WriteFrames(bin, blk, perFramePalette);
            return bin.BaseStream.Position;
        }

        private static void WriteFrames(BinaryWriter bin, VdAnimBlock blk, bool perFramePalette)
        {
            long startPosition = bin.BaseStream.Position;

            if (!perFramePalette)
            {
                for (int i = 0; i < 256; i++)
                    bin.Write((ushort)((i < blk.Palette.Length ? blk.Palette[i] : (ushort)0) ^ 0x8000));
                startPosition = bin.BaseStream.Position;
            }

            bin.Write(blk.Frames.Count);
            long seek = bin.BaseStream.Position;
            long curr = bin.BaseStream.Position + 4L * blk.Frames.Count;

            foreach (var frame in blk.Frames)
            {
                bin.BaseStream.Seek(seek, SeekOrigin.Begin);
                bin.Write((int)(curr - startPosition));
                seek = bin.BaseStream.Position;
                bin.BaseStream.Seek(curr, SeekOrigin.Begin);

                if (perFramePalette)
                {
                    for (int i = 0; i < 256; i++)
                        bin.Write((ushort)((i < blk.Palette.Length ? blk.Palette[i] : (ushort)0) ^ 0x8000));
                }

                frame.Save(bin);
                curr = bin.BaseStream.Position;
            }
        }
    }
}
