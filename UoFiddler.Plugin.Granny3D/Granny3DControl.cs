using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using OpenTK.WinForms;
using Ultima;

namespace UoFiddler.Plugin.Granny3D
{
    // Role of a mesh in the UO-style layered "cutout" export. The body is the
    // export target; accessories and mounts are "occluders" used only to carve
    // away the parts of the body they cover (depth occlusion), and are not
    // themselves exported in the cutout pass.
    public enum MeshCutoutRole
    {
        Body = 0,      // export target (the visible character/creature)
        Accessory = 1, // armor/weapon layered on top -> occluder
        Mount = 2      // mount the rider sits on -> occluder (only for mounted actions)
    }

    public class LoadedMesh
    {
        public string Name = string.Empty;
        public string MaterialName = string.Empty;
        public string SourcePath = string.Empty;
        public GrannyMesh SourceMesh;
        public List<GrannyBone> Bones = new List<GrannyBone>();
        public VertexInfluence[][] Skin;
        public Vector3[] BaseVertices;
        public int[] Indices;
        public int PartIndex = -1;

        // De-indexed (per triangle corner) buffers. Corner c corresponds to Indices[c].
        public int[] CornerVertex;
        public float[] Interleaved;      // per corner: pos(3) + normal(3) + uv(2)
        public Vector3[] BaseCornerNormals;
        public Vector2[] CornerUVs;

        public int Vbo;
        public int Vao;
        public int TextureId = -1;
        public string TextureName = string.Empty;
        public bool HasTexture = false;
        public Vector3 MeshColor = new Vector3(0.72f, 0.66f, 0.55f);

        // Support de teinte (hues.mul)
        public int BaseTextureId = -1;
        public Bitmap BaseTextureBmp = null;
        public Vector3 BaseMeshColor = new Vector3(0.72f, 0.66f, 0.55f);
        public string TextureFilePath = string.Empty;
        public int HueId = 0;
        public bool PartialHue = true;
        public bool Desaturate = false;

        public int[] BoneToAnimTrack;
        public Matrix4[] LocalMatrices;
        public Matrix4[] WorldMatrices;
        public Matrix4[] SkinMatrices;
        public Vector3[] DeformedCache;

        // When AttachBoneName is set the mesh is rigidly driven by that bone of
        // the host skeleton (e.g. a weapon attached to "Bip01 R Hand"), so it
        // follows the animation instead of using its own skin/bind pose.
        public string AttachBoneName;
        public Matrix4 AttachMatrix = Matrix4.Identity;
        public bool HasAttachMatrix;
        // True when the mesh's vertices are authored in the host's bind space
        // (unskinned character accessories, e.g. Dawn_Girl's sword): drive it with
        // the bone's SKIN matrix. False = rigid local space (Weapon_* files): use
        // the bone's WORLD matrix.
        public bool AttachInBindSpace;

        // Manual placement tweak applied (in the item's own space) before the bone
        // transform: mirror axes, rotation in degrees (X,Y,Z) and an offset.
        public bool MirrorX, MirrorY, MirrorZ;
        public bool MirrorArms; // Bilateral mirrored rotation/offset for arms/gloves/bracers
        public Vector3 AttachRotation;  // degrees
        public Vector3 AttachOffset;    // world units
        public Vector3 CentroidLeft;
        public Vector3 CentroidRight;
        public bool HasBilateralCentroids;

        // Multipart character slot this mesh belongs to ("face", "hair", "beard",
        // "torso", "head", ...). When a slot is replaced, all meshes sharing it are
        // removed and the new part is loaded in their place.
        public string CharSlot;

        // Embedded body-skin texture name of a multipart body part (e.g.
        // "UT256_H_Male_V2G"). Non-null only for parts that use the shared body skin,
        // so the skin selector can retarget them; face/hair/armor parts leave it null.
        public string SkinTexName;

        // Loaded for its skeleton/matrices only (e.g. the H_Male/H_Female placeholder
        // box which carries the full 43-bone skeleton, incl. CP_Grasp_* sockets) but
        // never drawn.
        public bool Hidden;

        // Cutout role: Body = export target; Accessory/Mount = occluders that carve
        // away the covered parts of the body in the layered UO cutout export.
        public MeshCutoutRole CutoutRole = MeshCutoutRole.Body;

        // Optional per-mesh animation override. Mounts are posed with their OWN clip
        // (synchronized frame-by-frame to the rider's clip) instead of the rider's
        // animation, because the horse skeleton/tracks differ from the human one.
        public GrannyAnimation AnimOverride;
        public int[] AnimOverrideTrack;   // bone -> track index within AnimOverride
        public float AnimOverrideTimeScale = 1f;  // mount time = rider time * this
        public float AnimOverrideTimeBase = 0f;    // added to the scaled time

        // World placement of a mount relative to the rider (applied to the mount's
        // root bone after posing): offset in world units + uniform scale.
        public Vector3 MountOffset = Vector3.Zero;
        public float MountScale = 1f;

        // Per-part placement tweak for SKINNED accessories (armor/pants/bracers):
        // a uniform scale about the part's bind centroid plus a world offset,
        // applied after skinning. Lets the user "puff out" or nudge a piece so the
        // body no longer pokes through it.
        public float PartScale = 1f;
        public Vector3 PartScaleVec = new Vector3(1f, 1f, 1f);
        public Vector3 BindCentroid;
        public bool HasBindCentroid;

        // Per-vertex rest normals (bind space), used to push a skinned piece OUTWARD
        // along its surface normal after skinning (thickness), so small deform
        // mismatches never let the body poke through.
        public Vector3[] BaseVertexNormals;
        public float Thickness = 0f;

        // ---- Real-time cloth (verlet) for capes / robes / skirts ----
        // The top band of the piece (near the shoulders) stays pinned to the
        // animated skeleton; the rest falls under gravity, is kept together by
        // distance constraints, and is pushed out of body bone capsules. This makes
        // the legs push the cloth instead of passing through it, looping with any anim.
        public bool IsCloth;
        public Vector3[] ClothPos;
        public Vector3[] ClothPrev;
        public bool[] ClothPin;
        public float[] ClothPinWeight;
        public int[] ClothEdgeA;
        public int[] ClothEdgeB;
        public float[] ClothRest;
        public int ClothSimIterations = 15;
        public float ClothGravity = 9.8f;

        // Position-weld map: vertices that sit at the same 3D position (double-sided
        // geometry / UV-seam duplicates) share one particle. Cloth Pos/Prev/Pin are
        // indexed by particle id (0..WeldCount-1); ClothRep[p] is a representative
        // original vertex (for skinning the pins). Without this the coincident copies
        // drift apart and the piece tears.
        public int[] Weld;
        public int WeldCount;
        public int[] ClothRep;
        public Vector3[] ClothPos0;   // rest positions per particle (bind space)
        public Vector3[] ClothOffset0; // Per-particle repousser offset preserving clearance
        public Matrix4[] BindWorldMatrices; // Cached bind-world bone matrices

        // Pre-computed (baked) cloth loop: particle positions recorded ONCE for every
        // frame of the animation, so playback just interpolates a cache instead of
        // re-solving. Baking runs two full cycles and keeps the second (the driven
        // system has settled into a periodic state) -> the loop is seamless.
        public Vector3[][] ClothBake;         // [frame][particle]
        public Vector3[][] ClothBakeRaw;      // baked before loop-blend smoothing
        public int ClothBakeFrames;
        public float ClothBakeDt;             // seconds between frames
        public GrannyAnimation ClothBakeAnim; // animation the cache was baked for
        public float ClothBakeGravity;        // gravity used (invalidate if changed)

        // Skin-wrap: when set, this mesh borrows the SKIN WEIGHTS of the nearest
        // vertices of WrapSource (a properly-skinned body mesh), so it deforms
        // exactly like the body surface instead of relying on its own (often
        // path-named/mis-bound) skeleton. WrapSkin[v] = WrapSource.Skin[nearest].
        public LoadedMesh WrapSource;
        public VertexInfluence[][] WrapSkin;

        // Non-destructive preview of the advanced skin-wrap panel: while a preview is
        // active the live WrapSkin/WrapSource above are the PREVIEW values and the
        // committed ones are saved here. "Appliquer" drops the backup, "Annuler"
        // restores it.
        public LoadedMesh PreviewSavedWrapSource;
        public VertexInfluence[][] PreviewSavedWrapSkin;
        public bool PreviewActive;

        // DEBUG/test: render straight from BaseVertices, bypassing skin/wrap, to tell
        // whether a visible pose lives in the raw geometry or in the skinning step.
        public bool ForceBaseDisplay;

        // Backup of original skeleton, skin weights and attachment state before Skin Wrap,
        // restored when the mesh is detached so it doesn't distort or explode.
        public List<GrannyBone> OriginalBones;
        public VertexInfluence[][] OriginalSkin;
        public string OriginalAttachBoneName;
        public bool OriginalHasAttachMatrix;
        public Matrix4 OriginalAttachMatrix = Matrix4.Identity;
        public MeshCutoutRole OriginalCutoutRole = MeshCutoutRole.Accessory;
        public bool HasOriginalWrapBackup;

        public bool PushOutOfBody;
        public bool ShrinkSew;
        public List<LoadedMesh> CollisionBody;
        public Vector3[] ClothPushPos;
        public Vector3[] ClothPushDir;
        public Vector3[] ClothPushBase; // last frame pushed position to dampen jitter
        public object ClothPushCapturedGrid;
        public List<Vector3[]> ClothPushCapturedBodyTris;
    }

    // Camera controller for orbit / pan / zoom.
    public class CameraController
    {
        public Vector3 Target = Vector3.Zero;
        public float Distance = 5f;
        public float Yaw = 0f;
        public float Pitch = 0f;
        public float MinDistance = 1f;
        public float MaxDistance = 200f;

        private Point _lastMousePos;
        private bool _isRotating;
        private bool _isPanning;

        public Matrix4 GetViewMatrix()
        {
            Vector3 offset = new Vector3(
                (float)(Distance * Math.Cos(Pitch) * Math.Sin(Yaw)),
                (float)(-Distance * Math.Cos(Pitch) * Math.Cos(Yaw)),
                (float)(Distance * Math.Sin(Pitch)));
            Vector3 eye = Target + offset;
            return Matrix4.LookAt(eye, Target, Vector3.UnitZ);
        }

        public void OnMouseDown(MouseEventArgs e)
        {
            _lastMousePos = e.Location;
            if (e.Button == MouseButtons.Left)
                _isRotating = true;
            else if (e.Button == MouseButtons.Middle)
                _isPanning = true;
        }

        public void OnMouseUp(MouseEventArgs e)
        {
            _isRotating = false;
            _isPanning = false;
        }

        public void OnMouseMove(MouseEventArgs e)
        {
            int dx = e.X - _lastMousePos.X;
            int dy = e.Y - _lastMousePos.Y;
            _lastMousePos = e.Location;

            if (e.Button == MouseButtons.Right)
                _isPanning = true;

            if (_isRotating)
            {
                Yaw += dx * 0.007f;
                Pitch = MathHelper.Clamp(Pitch + dy * 0.007f,
                    -MathHelper.PiOver2 + 0.01f, MathHelper.PiOver2 - 0.01f);
            }
            else if (_isPanning)
            {
                Vector3 forward = Vector3.Normalize(Target - EyePosition());
                Vector3 right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitZ));
                Vector3 up = Vector3.Cross(right, forward);
                float factor = Distance * 0.0015f;
                Target += (-right * dx + up * dy) * factor;
            }
        }

        public void OnMouseWheel(MouseEventArgs e)
        {
            Distance = MathHelper.Clamp(Distance * (1f - e.Delta * 0.001f), MinDistance, MaxDistance);
        }

        // ---- Standard orthographic-style views (3ds Max-style presets) ----
        // The character faces +Y (its "front"), so the Front camera sits at +Y.
        public void SetViewDegrees(float yawDeg, float pitchDeg)
        {
            Yaw = MathHelper.DegreesToRadians(yawDeg);
            Pitch = MathHelper.Clamp(MathHelper.DegreesToRadians(pitchDeg),
                -MathHelper.PiOver2 + 0.01f, MathHelper.PiOver2 - 0.01f);
        }
        public void ViewFront()  { SetViewDegrees(180f, 0f); }
        public void ViewBack()   { SetViewDegrees(0f, 0f); }
        public void ViewLeft()   { SetViewDegrees(-90f, 0f); }
        public void ViewRight()  { SetViewDegrees(90f, 0f); }
        public void ViewTop()    { SetViewDegrees(180f, 89.5f); }
        public void ViewBottom() { SetViewDegrees(180f, -89.5f); }
        public void ViewIso()    { SetViewDegrees(135f, 35f); }

        public Vector3 EyePosition()
        {
            Vector3 offset = new Vector3(
                (float)(Distance * Math.Cos(Pitch) * Math.Sin(Yaw)),
                (float)(-Distance * Math.Cos(Pitch) * Math.Cos(Yaw)),
                (float)(Distance * Math.Sin(Pitch)));
            return Target + offset;
        }
    }

    // Node payload for the category tree.
    public class TreeNodeInfo
    {
        public bool IsFolder;
        public bool Populated;       // children already enumerated (expand/collapse idempotent)
        public bool IsOthersCategory;
        public bool IsCustomMeshCategory;
        public string Folder;
        public string CreatureKey;
        public string MeshPath;      // creature's base/LOD mesh file
        public string LeafPath;      // full path of a leaf .grn file
        public string Display;       // short name shown in the UI
    }

    public class Granny3DControl : UserControl
    {
        public static string BasePath { get; set; } = @"d:\Games\sphere\PROGRAMMES\3D_UO\Models\";
        public static string MapsPath => Path.Combine(BasePath, "Maps");
        // Saved pose files (pose studio) shown in the "Poses" category of the animations tree.
        public static string PosesFolder => Path.Combine(BasePath, "Poses");

        private static string SettingsFilePath
        {
            get
            {
                string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UoFiddler");
                return Path.Combine(appData, "Granny3D.ini");
            }
        }

        public static void LoadSettings()
        {
            try
            {
                string file = SettingsFilePath;
                if (!File.Exists(file))
                {
                    string localFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Granny3D.ini");
                    if (File.Exists(localFile)) file = localFile;
                }

                if (File.Exists(file))
                {
                    foreach (var line in File.ReadAllLines(file))
                    {
                        var trim = line.Trim();
                        if (trim.StartsWith("BasePath=", StringComparison.OrdinalIgnoreCase))
                        {
                            string path = trim.Substring(9).Trim();
                            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                                BasePath = path;
                        }
                        else if (trim.StartsWith("MainSplit=", StringComparison.OrdinalIgnoreCase))
                        {
                            if (int.TryParse(trim.Substring(10).Trim(), out int v)) LayoutMainSplit = v;
                        }
                        else if (trim.StartsWith("LeftSplit=", StringComparison.OrdinalIgnoreCase))
                        {
                            if (int.TryParse(trim.Substring(10).Trim(), out int v)) LayoutLeftSplit = v;
                        }
                        else if (trim.StartsWith("AnimSplit=", StringComparison.OrdinalIgnoreCase))
                        {
                            if (int.TryParse(trim.Substring(10).Trim(), out int v)) LayoutAnimSplit = v;
                        }
                        else if (trim.StartsWith("Language=", StringComparison.OrdinalIgnoreCase))
                        {
                            string lang = trim.Substring(9).Trim();
                            if (!string.IsNullOrEmpty(lang))
                                Localization.SetLanguage(lang);
                        }
                    }
                }
            }
            catch { }

            // Double the left column width if it was the old default (< 550)
            if (LayoutMainSplit > 0 && LayoutMainSplit < 550)
                LayoutMainSplit = Math.Max(LayoutMainSplit * 2, 728);
            else if (LayoutMainSplit <= 0)
                LayoutMainSplit = 728;

            // Ensure Animations treeview has plenty of height (not squeezed by pnlModels)
            if (LayoutLeftSplit > 450 || LayoutLeftSplit < 180)
                LayoutLeftSplit = 320;

            // Ensure animSplit gives generous height to the Animations tree
            if (LayoutAnimSplit < 360)
                LayoutAnimSplit = 420;

            if (!Directory.Exists(BasePath))
            {
                string fallback1 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models");
                if (Directory.Exists(fallback1))
                    BasePath = fallback1;
                else if (Directory.Exists(@"d:\Games\sphere\PROGRAMMES\3D_UO\Models\"))
                    BasePath = @"d:\Games\sphere\PROGRAMMES\3D_UO\Models\";
            }
        }

        public static void SaveSettings()
        {
            try
            {
                string file = SettingsFilePath;
                string dir = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                File.WriteAllLines(file, new[]
                {
                    "BasePath=" + BasePath,
                    "Language=" + Localization.CurrentLanguage,
                    "MainSplit=" + LayoutMainSplit,
                    "LeftSplit=" + LayoutLeftSplit,
                    "AnimSplit=" + LayoutAnimSplit
                });
            }
            catch { }
        }

        // Persisted panel layout (splitter distances). -1 = not set yet (use defaults).
        public static int LayoutMainSplit = -1;
        public static int LayoutLeftSplit = -1;
        public static int LayoutAnimSplit = -1;

        // Category folder -> display name
        private static readonly string[] CategoryFolders =
        {
            "Humans", "Monsters", "Animals", "SeaCreatures", "Others", "Others", "Meshs custom"
        };
        private static readonly string[] CategoryDisplay =
        {
            "Humans", "Monsters", "Animals", "Sea Creatures", "Equipment", "Others", "Meshs custom"
        };

        // Action words used to split "<creature>_<action>" filenames (longest first).
        private static readonly string[] ActionTokens = new string[]
        {
            "AttackCrossbow", "AttackCrossbow_01", "CombatIdle2H", "CombatIdle1H",
            "AttackSlash1H", "AttackSlash2H", "AttackPierce1H", "AttackPierce2H",
            "AttackBash1H", "AttackBash2H", "CombatAdvance", "Combatadvance",
            "WalkStaff", "RunStaff", "GetHitInAir", "DieInAir", "Die_Hard_Back",
            "Die_Hard_Fwd", "Sit_To_Stand", "Stand_To_Sit", "Blow_Kiss", "Fold_Arms",
            "Cover_Ears", "Fidget_Yawn_Stretch", "GetHit", "AttackBow", "Attack",
            "Death", "Died", "Die1", "Die2", "Fidget", "Cast2", "Cast3", "Spell1",
            "Spell2", "Fight", "LieDown", "TakeOff", "BlockRight", "BlockLeft",
            "Pillage", "Stomp", "Turn", "Salute", "Dance", "Applause", "Yawn",
            "Stretch", "Punch", "Sneaky", "Victory", "Stand", "Attack1", "Attack2",
            "Attack3", "Gethit", "Walk", "Run", "Idle", "Eat", "Alert", "Fly",
            "Land", "Jump", "Cast", "Spell", "Die", "Block", "Bow", "Sit", "Wave",
            "Shake", "Nod", "Point"
        };

        // UI - left column (category tree + animation list)
        private TreeView tvModels;
        private Label lblModels;
        private TextBox txtModelFilter;
        private ListBox lbAnimations;
        private Label lblAnimations;
        private TreeView tvLoaded;
        private GroupBox grpLoaded;
        // Left/right layout splitters, kept as fields so their positions can be
        // saved and restored (the panels are resized by the user at runtime).
        private SplitContainer _mainSplit, _leftSplit, _animSplit;
        // Ctrl/Shift multi-selection for the "Meshes affichés" tree (the WinForms
        // TreeView has no built-in multi-select, and CheckBoxes suppress it further).
        private readonly HashSet<TreeNode> _treeMultiSel = new HashSet<TreeNode>();
        private TreeNode _treeSelAnchor;
        private ContextMenuStrip tvMenu;
        private ContextMenuStrip tvAnimMenu;
        private ContextMenuStrip lstLoadedMenu;
        private ContextMenuStrip glContextMenu;
        private bool _glRightDrag;
        private Point _glRightDownPos;
        private bool _populatingTree;
        // UI - animations panel
        private GroupBox grpAnimations;
        private Label lblAnimHeader;
        private TextBox txtAnimFilter;
        private TreeView tvAnims;
        private Button btnTPose;
        private Button btnImportAnim;
        private Button btnImportMesh;
        private Button btnImportModel;
        private Button btnApplyMixamo;
        // animation categories (Male / Female / Animals / ...)
        private class AnimCat
        {
            public string Label;
            public string Folder;
            public string Prefix;
            public bool IsAll;
            public bool Populated;
            public string Pattern = "*.grn";
        }
        // A selectable multipart part (display text + full .grn path).
        private class PartItem
        {
            public string Display;
            public string Path;
            public override string ToString() => Display;
        }

        // Item of the body-skin selector. Base = texture base name (null = keep the
        // piece's embedded skin).
        private class SkinItem
        {
            public string Display;
            public string Base;
            public override string ToString() => Display;
        }
        // UI - bone attach
        private Label lblBone;
        private ComboBox cbBones;
        private Button btnAttach;
        // UI - equipment placement controller
        private GroupBox grpEquip;
        private Label lblEquipTarget;
        private CheckBox cbMirrorX, cbMirrorY, cbMirrorZ, cbMirrorArms;
        private NumericUpDown nudRotX, nudRotY, nudRotZ, nudOffX, nudOffY, nudOffZ, nudScale, nudScaleX, nudScaleY, nudScaleZ, nudThickness, nudClothGrav;
        private Button btnEquipReset, btnDuplicateMirror, btnSkinWrap, btnSubdiv, btnClothBake, btnBakeModifs;
        // Advanced skin-wrap panel
        private ComboBox cmbWrapSource, cmbWrapAlgo, cmbWrapFalloff;
        private NumericUpDown nudWrapK, nudWrapRadius, nudWrapMaxBones;
        private CheckBox chkWrapAnatomical, chkWrapLive;
        private Button btnWrapPreview, btnWrapApply, btnWrapCancel;
        private Button btnForceBase;
        private Label lblWrapInfo;
        private bool _wrapPreviewBusy;
        private CheckBox chkCloth, chkShowColliders, chkShowBones, chkClothHeatmap;
        private NumericUpDown nudClothClearance;
        private bool _showColliders, _showBones, _showClothHeatmap;
        private float _clothClearance = 0.008f; // 0.8 cm collision margin by default
        private int _debugLinesVao, _debugLinesVbo;
        private NumericUpDown nudBakeFps, nudLoopBlend;
        private bool _suppressEquip;
        // ---- Pose studio ("Personnage (déplacement / pose)" tab) ----
        // Additive pose layer over the current animation / bind pose: extra local
        // rotation + translation per bone (keyed by name), plus a global root
        // displacement so the character can be aligned against a fixed equipment
        // mesh imported in a non T-pose. The mesh itself is never modified.
        private class BonePoseOffset
        {
            public Vector3 RotDeg;   // extra local rotation (Euler degrees)
            public Vector3 Pos;      // extra local translation
        }
        private readonly Dictionary<string, BonePoseOffset> _poseOffsets =
            new Dictionary<string, BonePoseOffset>(StringComparer.OrdinalIgnoreCase);
        // Absolute pose layer loaded from a v2 .pose file: per bone name, the exact
        // local transform to force (replaces the animated/bind local matrix) so a
        // reloaded pose reproduces the preview on screen. When non-empty it takes
        // precedence over _poseOffsets for the target meshes.
        private class BonePoseAbs
        {
            public Quaternion Rot;       // captured local pose rotation
            public Vector3 Pos;          // captured local pose translation
            public Quaternion BindRot;   // base local the pose was captured against (v3)
            public Vector3 BindPos;
            public bool HasBind;         // false for v2 files (absolute replacement)
        }
        private readonly Dictionary<string, BonePoseAbs> _poseAbsolute =
            new Dictionary<string, BonePoseAbs>(StringComparer.OrdinalIgnoreCase);
        private bool _poseHasAbsolute;   // true once a v2 pose is loaded
        private Vector3 _poseGlobalOffset;
        private float _poseGlobalYaw;
        private bool _poseGlobalBodyOnly = true;  // move only the character body, not free meshes
        private bool _suppressPose;
        // Pose target: when non-empty, the pose (bones + global) only affects the
        // meshes chosen here (indices into _loadedMeshes, matched from the "Meshes
        // affichés" selection/checkboxes). Empty = auto (character body only).
        private readonly HashSet<int> _poseTargets = new HashSet<int>();
        private int _poseTargetMode;              // 0 = auto (corps), 1 = sélection « Meshes affichés »
        private ComboBox cbPoseTarget;
        private TabControl tabEquip;
        private ComboBox cbPoseBones;
        private CheckBox chkPoseMirror, chkPoseBodyOnly;
        private NumericUpDown nudPoseRotX, nudPoseRotY, nudPoseRotZ;
        private NumericUpDown nudPoseOffX, nudPoseOffY, nudPoseOffZ;
        private NumericUpDown nudPoseGlobX, nudPoseGlobY, nudPoseGlobZ, nudPoseGlobYaw;
        private Label lblPoseInfo;
        private Label lblPoseTargetInfo;
        private Button btnPoseResetBone, btnPoseResetAll, btnPoseSave;
        // ---- 3ds Max-style view cube (drawn in the top-left corner of the editor view) ----
        private int _viewCubeVao, _viewCubeVbo, _viewCubeTex;
        private const int ViewCubePx = 88;
        private const int ViewCubeMargin = 12;
        // UI - multipart character menu (Visage / Coiffure / Barbe / Torse)
        private GroupBox grpChar;
        private GroupBox grpCamera;
        private Label lblCharInfo;
        private ComboBox cbCharFace, cbCharHair, cbCharBeard, cbCharTorso, cbCharSkin;
        private Button btnSaveChar, btnLoadChar, btnResetChar, btnExportCharFile;
        private Button btnPresetPerso, btnPresetMonstre, btnResetCamera, btnSaveCamera, btnLoadCamera;
        private bool _suppressChar;
        private int _charGender;           // 0=none, 1=male, 2=female
        private string _skinOverride;      // body-skin texture base name (e.g. "UT256_H_Male_V2c"); null = embedded default
        // state - animation list
        private TreeNodeInfo _currentCreature;
        private bool _suppressAnimSelect;

        // UI - viewport
        private GLControl glControl;

        // UI - playback panel
        private Button btnPlay;
        private Button btnPause;
        private Button btnStop;
        private Button btnExportVd;
        // Cutout export (UO-style layered body) + optional mount.
        private Button btnExportCutout;
        private Button btnExportCutoutAcc;
        private ComboBox cmbMount;
        private NumericUpDown nudMountOffX, nudMountOffY, nudMountOffZ, nudMountScale;
        // Currently loaded mount mesh (CutoutRole == Mount) and its chosen clip path.
        private LoadedMesh _mountMesh;
        private string _mountClipPath;
        // Manual cutout role for the selected mesh (Body / Accessory / Mount).
        private ComboBox cmbCutoutRole;
        private CheckBox chkLoop;
        private TrackBar trackSpeed;
        private Label lblSpeed;
        private TrackBar trackTime;
        private Label lblTime;
        private Label lblDuration;
        private Label lblModelInfo;

        // UI - BasePath selection
        private Label lblBasePath;
        private Button btnSelectBasePath;
        private Button btnReloadBasePath;
        private ComboBox cmbLanguage;
        private GroupBox grpInfo;
        private GroupBox grpHue;
        private GroupBox grpPlayback;
        private TabPage tabEquipment;
        private TabPage tabPose;
        private ToolTip _toolTip;

        // UI - Hue selection (hues.mul)
        private NumericUpDown nudHue;
        private CheckBox chkPartialHue;
        private CheckBox chkDesaturate;
        private Button btnPickHue;
        private Button btnResetHue;
        private Label lblHueName;
        private ComboBox cmbHueTarget;
        private PictureBox picHueBar;
        private bool _suppressHueEvents;

        // UI - UO render bar
        private CheckBox chkUoView, chkUoFeet, chkUoRef;
        private ComboBox cmbUoMode, cmbUoDir;
        private NumericUpDown nudUoZoom, nudUoElev, nudUoVert, nudUoOffX, nudUoOffY;
        private NumericUpDown nudUoYaw;
        private NumericUpDown nudUoGlobal, nudUoBright;
        private NumericUpDown nudUoRefAction, nudUoRefFrame, nudUoRefScale, nudUoRefOpacity;

        private readonly CameraController _camera = new CameraController();

        // Data
        private GrannyFormatParser _modelParser;
        private GrannyFormatParser _animParser;
        private readonly List<LoadedMesh> _loadedMeshes = new List<LoadedMesh>();
        private readonly HashSet<string> _sceneExtras = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private GrannyAnimation _currentAnimation;
        private string _currentAnimPath;
        private float _currentTime;
        private float _clothDt = 1f / 60f;
        private int _clothDbg;
        private float _pushOutMargin = 0.005f;                                                              
        private float _sewShrinkForce = 0.6f;                                                               
        private NumericUpDown _nudPushMargin;
        private NumericUpDown _nudSewForce;
        private Button _btnPushOutBody;
        private Button _btnShrinkSew;
        private float _playbackSpeed = 1f;
        private bool _playing;
        private bool _looping = true;
        private DateTime _lastFrameTime;
        private Vector3 _refCenter = Vector3.Zero;
        private float _refScale = 1f;
        private Vector3 _normMin = Vector3.Zero;   // model bbox in normalized space
        private Vector3 _normMax = Vector3.Zero;
        private string _currentModelPath;

        private System.Windows.Forms.Timer _renderTimer;
        private bool _glReady;
        private int _dbgFrame;
        private int _shaderProg;
        private int _uMvp, _uHasTex, _uColor, _uTex, _uLightDir, _uAlphaCut, _uUnlit, _uOpacity, _uBrightness;
        private bool _initialLoadPending;

        // UO render view (right half of the viewport): orthographic 2:1 isometric,
        // 5 stored directions (world N,NE,E,SE,S), flat unlit shading, no lights.
        private bool _uoView = true;
        private bool _uoSheet;              // false = one direction, true = sheet of all 5
        private int _uoDirection = 4;       // 0..4 => world N,NE,E,SE,S (4 = S faces camera)
        private float _uoSplit = 0.5f;      // x fraction of the editor | UO split
        private float _uoZoom = 0.27f;
        private float _uoElevationDeg = 26.565f;
        private float _uoYawOffsetDeg = 180f;   // 180 so our camera matches UOFiddler's facing
        private float _uoVertScale = 1f;
        private float _uoOffX = 0f, _uoOffY = -0.080f;  // screen-space offset (normalized units)
        private bool _uoFeetOnLine = true;         // put the model's bottom on the red line
        private float _uoGlobalZoom = 1f;          // global magnification (model + reference)
        private float _uoBrightness = 0.8f;          // model brightness correction

        // Reference overlay: UOFiddler animation frame (anim.mul body 400 = c_man),
        // drawn on top of the model so size / camera tilt can be compared.
        private bool _uoRefShow;
        private int _uoRefBody = 400, _uoRefAction = 0, _uoRefFrame = 0;
        private float _uoRefScale = 1f;
        private float _uoRefOpacity = 0.55f;
        private readonly int[] _uoRefTex = new int[5];
        private readonly int[] _uoRefW = new int[5];
        private readonly int[] _uoRefH = new int[5];
        private readonly Point[] _uoRefPivot = new Point[5];
        private int _uoRefKey = int.MinValue;
        private int _quadVao, _quadVbo;
        private bool _uoDragSplit;
        // UO preview background (matches the UOFiddler animation preview: plain light back).
        private float _uoBgR = 0.94f, _uoBgG = 0.94f, _uoBgB = 0.94f;
        private int _uoDbg;

        private static readonly string LogFile = Path.Combine(Path.GetTempPath(), "Granny3D_plugin.log");

        private static void Log(string msg)
        {
            try
            {
                File.AppendAllText(LogFile, $"[{DateTime.Now:HH:mm:ss}] {msg}{Environment.NewLine}");
            }
            catch { }
        }

        public Granny3DControl()
        {
            Log("=== Granny3DControl ctor (build CHAR-MULTIPART-1 alpha) ===");
            InitializeComponent();
            InitializeOpenGL();
        }

        private void InitializeComponent()
        {
            LoadSettings();
            _toolTip = new ToolTip();
            SuspendLayout();
            Width = 1400;
            Height = 750;
            BackColor = Color.FromArgb(37, 37, 38);

            // ===== LEFT COLUMN: models tree (top) + animations (mid) + loaded meshes (bottom) =====
            var pnlLeft = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6) };

            // ---- Animations panel: header + filter + list ----
            grpAnimations = new GroupBox
            {
                Text = "Animations",
                Dock = DockStyle.Bottom,
                Height = 250,
                ForeColor = Color.Gainsboro,
                Padding = new Padding(4)
            };
            lblAnimHeader = new Label
            {
                Text = "Sélectionnez un modèle LOD2…",
                Dock = DockStyle.Top,
                Height = 32,
                ForeColor = Color.Gainsboro,
                Font = new Font("Segoe UI", 8, FontStyle.Regular)
            };
            txtAnimFilter = new TextBox
            {
                Dock = DockStyle.Top,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle
            };
            txtAnimFilter.TextChanged += (s, e) => ApplyAnimFilter();
            lbAnimations = new ListBox { Visible = false };
            tvAnims = new TreeView
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle,
                HideSelection = false,
                ShowLines = true,
                ShowPlusMinus = true,
                ShowRootLines = true
            };
            tvAnimMenu = new ContextMenuStrip();
            tvAnimMenu.Opening += TvAnimMenu_Opening;
            tvAnims.ContextMenuStrip = tvAnimMenu;
            tvAnims.NodeMouseClick += TvAnims_NodeMouseClick;
            tvAnims.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Right)
                {
                    var n = tvAnims.GetNodeAt(e.Location);
                    if (n != null)
                    {
                        _suppressAnimSelect = true;
                        try { tvAnims.SelectedNode = n; }
                        finally { _suppressAnimSelect = false; }
                    }
                }
            };
            tvAnims.AfterSelect += TvAnims_AfterSelect;
            tvAnims.BeforeExpand += TvAnims_BeforeExpand;
            tvAnims.KeyDown += (s, e) =>
            {
                if (tvAnims.SelectedNode != null && tvAnims.SelectedNode.Tag is string animPath)
                {
                    bool isCustom = animPath.EndsWith(".anim", StringComparison.OrdinalIgnoreCase) ||
                                    animPath.EndsWith(".pose", StringComparison.OrdinalIgnoreCase) ||
                                    (tvAnims.SelectedNode.Parent?.Tag as AnimCat)?.Pattern == "*.anim" ||
                                    (tvAnims.SelectedNode.Parent?.Tag as AnimCat)?.Pattern == "*.pose";

                    if (isCustom)
                    {
                        if (e.KeyCode == Keys.F2)
                        {
                            RenameCustomAnimation(tvAnims.SelectedNode, animPath);
                            e.Handled = true;
                        }
                        else if (e.KeyCode == Keys.Delete)
                        {
                            DeleteCustomAnimation(tvAnims.SelectedNode, animPath);
                            e.Handled = true;
                        }
                    }
                }
            };

            var pnlAnimTools = new Panel { Dock = DockStyle.Bottom, Height = 58 };
            btnTPose = new Button
            {
                Text = "T-Pose",
                Dock = DockStyle.Top,
                Height = 28,
                BackColor = Color.FromArgb(48, 48, 48),
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat
            };
            btnTPose.Click += BtnTPose_Click;

            btnImportAnim = new Button
            {
                Text = "Importer un FBX Mixamo…",
                Dock = DockStyle.Bottom,
                Height = 28,
                BackColor = Color.FromArgb(48, 48, 48),
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat
            };
            btnImportAnim.Click += BtnImportAnim_Click;
            pnlAnimTools.Controls.Add(btnImportAnim);
            pnlAnimTools.Controls.Add(btnTPose);

            grpAnimations.Controls.Add(tvAnims);
            grpAnimations.Controls.Add(pnlAnimTools);
            grpAnimations.Controls.Add(txtAnimFilter);
            grpAnimations.Controls.Add(lblAnimHeader);

            lblAnimations = new Label { Visible = false };

            // ---- Loaded meshes list + bone attach ----
            grpLoaded = new GroupBox
            {
                Text = "Meshes affichés (double-clic = retirer ; coches = sélection par lot)",
                Dock = DockStyle.Bottom,
                Height = 220,
                ForeColor = Color.Gainsboro,
                Padding = new Padding(4)
            };
            tvLoaded = new TreeView
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle,
                CheckBoxes = true,
                ShowPlusMinus = true,
                ShowLines = true,
                ShowRootLines = true,
                HideSelection = false
            };
            lstLoadedMenu = new ContextMenuStrip();
            lstLoadedMenu.Opening += LstLoadedMenu_Opening;
            tvLoaded.ContextMenuStrip = lstLoadedMenu;
            tvLoaded.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Right)
                {
                    var n = tvLoaded.GetNodeAt(e.Location);
                    if (n != null) tvLoaded.SelectedNode = n;
                }
            };
            tvLoaded.DoubleClick += TvLoaded_DoubleClick;
            tvLoaded.AfterSelect += (s, e) => TvLoaded_AfterSelect(s, e);
            tvLoaded.AfterCheck += (s, e) => TvLoaded_AfterCheck(s, e);
            tvLoaded.NodeMouseClick += TvLoaded_NodeMouseClick;
            tvLoaded.KeyDown += TvLoaded_KeyDown;
            tvLoaded.DrawMode = TreeViewDrawMode.OwnerDrawText;
            tvLoaded.DrawNode += TvLoaded_DrawNode;

            var pnlAttach = new Panel { Dock = DockStyle.Bottom, Height = 52 };
            lblBone = new Label
            {
                Text = "Attacher à l'os :",
                Location = new Point(2, 6),
                Width = 96,
                Height = 18,
                ForeColor = Color.Gainsboro
            };
            cbBones = new ComboBox
            {
                Location = new Point(100, 4),
                Width = 200,
                DropDownStyle = ComboBoxStyle.DropDownList,
                DropDownWidth = 320,
                MaxDropDownItems = 20,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.Gainsboro,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            btnAttach = new Button
            {
                Text = "Attacher / Détacher",
                Location = new Point(100, 26),
                Width = 200,
                Height = 22,
                BackColor = Color.FromArgb(60, 60, 62),
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            btnAttach.Click += (s, e) => AttachSelectedMeshToBone();
            pnlAttach.Controls.Add(cbBones);
            pnlAttach.Controls.Add(btnAttach);
            pnlAttach.Controls.Add(lblBone);
            grpLoaded.Controls.Add(tvLoaded);
            grpLoaded.Controls.Add(pnlAttach);

            lblModels = new Label
            {
                Text = "Modèles (LOD2)",
                Dock = DockStyle.Top,
                Height = 20,
                ForeColor = Color.Gainsboro,
                Font = new Font("Segoe UI", 9, FontStyle.Bold)
            };
            tvModels = new TreeView
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle,
                HideSelection = false,
                ShowLines = true
            };
            tvMenu = new ContextMenuStrip();
            tvMenu.Opening += TvMenu_Opening;
            tvModels.ContextMenuStrip = tvMenu;
            tvModels.AfterSelect += TvModels_AfterSelect;
            tvModels.BeforeExpand += TvModels_BeforeExpand;
            tvModels.DoubleClick += TvModels_DoubleClick;
            tvModels.NodeMouseClick += TvModels_NodeMouseClick;
            tvModels.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Right)
                {
                    var n = tvModels.GetNodeAt(e.Location);
                    if (n != null)
                    {
                        _suppressSelect = true;
                        try { tvModels.SelectedNode = n; }
                        finally { _suppressSelect = false; }
                    }
                }
            };
            tvModels.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.F2 && tvModels.SelectedNode != null)
                {
                    var info = tvModels.SelectedNode.Tag as TreeNodeInfo;
                    if (info != null && (info.IsOthersCategory || info.IsCustomMeshCategory ||
                        (tvModels.SelectedNode.Parent?.Tag as TreeNodeInfo)?.IsOthersCategory == true ||
                        (tvModels.SelectedNode.Parent?.Tag as TreeNodeInfo)?.IsCustomMeshCategory == true))
                    {
                        RenameCustomModel(tvModels.SelectedNode, info);
                        e.Handled = true;
                    }
                }
            };


            // Search box for the LOD2 models tree (same idea as the Animations filter).
            txtModelFilter = new TextBox
            {
                Dock = DockStyle.Top,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle
            };
            txtModelFilter.TextChanged += (s, e) => ApplyModelsFilter();

            // ---- Left column: models tree (top) / animations + meshes (bottom),
            //      separated by draggable splitters so every zone can be enlarged. ----
            var pnlModelTools = new Panel { Dock = DockStyle.Bottom, Height = 84 };
            btnApplyMixamo = new Button
            {
                Text = "⚡ Convert/create rig to UO…",
                Dock = DockStyle.Top,
                Height = 28,
                BackColor = Color.FromArgb(35, 75, 110),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font(Font, FontStyle.Bold)
            };
            btnApplyMixamo.Click += (s, e) =>
            {
                var info = tvModels.SelectedNode?.Tag as TreeNodeInfo;
                ApplyMixamoForUo(info?.MeshPath ?? info?.Folder ?? _currentModelPath);
            };

            btnImportMesh = new Button
            {
                Text = "Importer un mesh…",
                Dock = DockStyle.Top,
                Height = 28,
                BackColor = Color.FromArgb(48, 48, 48),
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat
            };
            btnImportMesh.Click += BtnImportMesh_Click;

            btnImportModel = new Button
            {
                Text = "Importer un Character Mixamo…",
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(48, 48, 48),
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat
            };
            btnImportModel.Click += BtnImportModel_Click;
            pnlModelTools.Controls.Add(btnImportModel);
            pnlModelTools.Controls.Add(btnImportMesh);
            pnlModelTools.Controls.Add(btnApplyMixamo);

            var pnlModels = new Panel { Dock = DockStyle.Fill };
            pnlModels.Controls.Add(tvModels);
            pnlModels.Controls.Add(pnlModelTools);
            pnlModels.Controls.Add(txtModelFilter);
            pnlModels.Controls.Add(lblModels);

            grpAnimations.Dock = DockStyle.Fill;
            grpLoaded.Dock = DockStyle.Fill;

            var animSplit = _animSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal
            };
            animSplit.Panel1.Controls.Add(grpAnimations);
            animSplit.Panel2.Controls.Add(grpLoaded);

            var leftSplit = _leftSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal
            };
            leftSplit.Panel1.Controls.Add(pnlModels);
            leftSplit.Panel2.Controls.Add(animSplit);

            pnlLeft.Controls.Add(leftSplit);

            // ===== RIGHT COLUMN: viewport (top) + playback (bottom) =====
            var pnlRight = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6) };

            glControl = new GLControl();
            glControl.Dock = DockStyle.None;
            glControl.BackColor = Color.Black;
            glControl.Paint += GlControl_Paint;
            glControl.Resize += GlControl_Resize;
            glControl.MouseDown += GlControl_MouseDown;
            glControl.MouseUp += GlControl_MouseUp;
            glControl.MouseMove += GlControl_MouseMove;
            glControl.MouseWheel += GlControl_MouseWheel;
            glControl.MouseEnter += (s, e) => glControl.Focus();
            glContextMenu = new ContextMenuStrip();
            glContextMenu.Opening += GlContextMenu_Opening;
            glControl.ContextMenuStrip = glContextMenu;

            // ---- Playback panel (bottom of right column) ----
            var pnlRightBottom = new Panel { Dock = DockStyle.Bottom, Height = 655, Padding = new Padding(0, 6, 0, 0) };

            grpPlayback = new GroupBox
            {
                Text = Localization.Tr("grp_playback", "Contrôle Animation"),
                Dock = DockStyle.Left,
                Width = 250,
                ForeColor = Color.Gainsboro,
                Padding = new Padding(8)
            };

            btnPlay = new Button { Text = "Play", Location = new Point(8, 24), Width = 72, Height = 30, BackColor = Color.LightGreen, Enabled = false };
            btnPlay.Click += BtnPlay_Click;

            btnPause = new Button { Text = "Pause", Location = new Point(86, 24), Width = 72, Height = 30, Enabled = false };
            btnPause.Click += BtnPause_Click;

            btnStop = new Button { Text = "Stop", Location = new Point(164, 24), Width = 72, Height = 30, Enabled = false };
            btnStop.Click += BtnStop_Click;

            // Export the baked 3D render as a UO animation (.vd) using the same
            // category/action mapping as UOFiddler's "Export -> To vd.." menu.
            btnExportVd = new Button { Text = "Exporter en .vd (mapping)", Location = new Point(8, 192), Width = 228, Height = 28, BackColor = Color.FromArgb(84, 96, 78) };
            btnExportVd.Click += (s, e) => ExportVdDialog(null, "Rendu complet (.vd)");

            // Cutout export: only the visible body is kept; armor/mount carve it away
            // (UO layered animation, body-only layer).
            btnExportCutout = new Button { Text = "Exporter cutout (corps)", Location = new Point(8, 226), Width = 110, Height = 28, BackColor = Color.FromArgb(78, 84, 96) };
            btnExportCutout.Click += (s, e) =>
            {
                foreach (var lm in _loadedMeshes)
                    Log($"cutout role: {lm.CutoutRole}  {lm.Name}");
                ExportVdDialog(BuildCutoutFilter(), "Cutout Corps (découpé par accessoires)");
            };

            // Inverse layer: export the accessory, carved by the body (+ mount).
            btnExportCutoutAcc = new Button { Text = "cutout accessoire", Location = new Point(126, 226), Width = 110, Height = 28, BackColor = Color.FromArgb(96, 84, 78) };
            btnExportCutoutAcc.Click += (s, e) =>
            {
                foreach (var lm in _loadedMeshes)
                    Log($"cutout role: {lm.CutoutRole}  {lm.Name}");
                ExportVdDialog(BuildAccessoryCutoutFilter(), "Cutout Accessoire (découpé par le corps)");
            };

            var lblMount = new Label { Text = "Monture :", Location = new Point(8, 262), AutoSize = true, ForeColor = Color.Gainsboro };
            cmbMount = new ComboBox { Location = new Point(8, 280), Width = 228, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(50, 50, 54), ForeColor = Color.Gainsboro };
            cmbMount.Items.Add("(aucune)");
            try { foreach (var m in GatherMountModels()) cmbMount.Items.Add(Path.GetFileName(m)); } catch { }
            cmbMount.SelectedIndex = 0;
            cmbMount.SelectedIndexChanged += (s, e) =>
            {
                int i = cmbMount.SelectedIndex;
                if (i <= 0) { RemoveMount(); return; }
                var models = GatherMountModels();
                int mi = i - 1;
                if (mi < 0 || mi >= models.Count) return;
                LoadMount(models[mi]);
            };

            var lblMountPos = new Label { Text = "Offset X/Y/Z + echelle :", Location = new Point(8, 306), AutoSize = true, ForeColor = Color.Gainsboro };
            nudMountOffX = new NumericUpDown { Location = new Point(8, 326), Width = 54, Minimum = -500, Maximum = 500, DecimalPlaces = 2, Increment = 0.5m, BackColor = Color.FromArgb(50, 50, 54), ForeColor = Color.Gainsboro };
            nudMountOffY = new NumericUpDown { Location = new Point(66, 326), Width = 54, Minimum = -500, Maximum = 500, DecimalPlaces = 2, Increment = 0.5m, BackColor = Color.FromArgb(50, 50, 54), ForeColor = Color.Gainsboro };
            nudMountOffZ = new NumericUpDown { Location = new Point(124, 326), Width = 54, Minimum = -500, Maximum = 500, DecimalPlaces = 2, Increment = 0.5m, BackColor = Color.FromArgb(50, 50, 54), ForeColor = Color.Gainsboro };
            nudMountScale = new NumericUpDown { Location = new Point(182, 326), Width = 54, Minimum = 0.05m, Maximum = 10, DecimalPlaces = 2, Increment = 0.05m, Value = 1, BackColor = Color.FromArgb(50, 50, 54), ForeColor = Color.Gainsboro };
            EventHandler mountTweak = (s, e) =>
            {
                if (_mountMesh == null) return;
                var off = new Vector3((float)nudMountOffX.Value, (float)nudMountOffY.Value, (float)nudMountOffZ.Value);
                float sc = (float)nudMountScale.Value;
                foreach (var lm in _loadedMeshes)
                    if (lm.CutoutRole == MeshCutoutRole.Mount) { lm.MountOffset = off; lm.MountScale = sc; }
                if (_glReady && _currentAnimation != null) { try { ApplyAnimationPose(_currentTime); } catch { } }
            };
            nudMountOffX.ValueChanged += mountTweak;
            nudMountOffY.ValueChanged += mountTweak;
            nudMountOffZ.ValueChanged += mountTweak;
            nudMountScale.ValueChanged += mountTweak;

            chkLoop = new CheckBox { Text = "Loop", Location = new Point(8, 62), AutoSize = true, Checked = true, ForeColor = Color.Gainsboro };
            chkLoop.CheckedChanged += (s, e) => _looping = chkLoop.Checked;

            lblSpeed = new Label { Text = "Speed: 1.0x", Location = new Point(90, 62), AutoSize = true, ForeColor = Color.Gainsboro };
            trackSpeed = new TrackBar { Location = new Point(8, 84), Width = 228, Height = 34, Minimum = 10, Maximum = 300, Value = 100, TickFrequency = 50, SmallChange = 10 };
            trackSpeed.Scroll += (s, e) => { _playbackSpeed = trackSpeed.Value / 100f; lblSpeed.Text = $"Speed: {_playbackSpeed:F1}x"; };

            // Timeline scrubber for the current animation.
            trackTime = new TrackBar { Location = new Point(8, 120), Width = 228, Height = 34, Minimum = 0, Maximum = 1000, Value = 0, TickStyle = TickStyle.None };
            trackTime.Scroll += TrackTime_Scroll;

            lblTime = new Label { Text = "Time: 0.00s", Location = new Point(8, 160), AutoSize = true, ForeColor = Color.Gainsboro };
            lblDuration = new Label { Text = "/ 0.00s", Location = new Point(150, 160), AutoSize = true, ForeColor = Color.Gainsboro };

            grpPlayback.Controls.AddRange(new Control[] { btnPlay, btnPause, btnStop, btnExportVd, btnExportCutout, btnExportCutoutAcc, lblMount, cmbMount, lblMountPos, nudMountOffX, nudMountOffY, nudMountOffZ, nudMountScale, chkLoop, lblSpeed, trackSpeed, trackTime, lblTime, lblDuration });

            var pnlBottomSplit = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 1,
                BackColor = Color.FromArgb(40, 40, 44),
                Padding = new Padding(0)
            };
            pnlBottomSplit.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48f));
            pnlBottomSplit.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52f));
            pnlBottomSplit.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            grpInfo = new GroupBox
            {
                Text = Localization.Tr("grp_info", "Informations Modèle & Répertoire"),
                Dock = DockStyle.Fill,
                ForeColor = Color.Gainsboro,
                Padding = new Padding(6)
            };
            lblModelInfo = new Label
            {
                Text = "Bones: 0 | Tracks: 0 | Meshes: 0",
                Location = new Point(8, 20),
                Size = new Size(420, 32),
                ForeColor = Color.Gainsboro
            };
            lblBasePath = new Label
            {
                Text = "UO 3D : " + BasePath,
                Location = new Point(8, 54),
                Size = new Size(330, 18),
                ForeColor = Color.LightGray,
                AutoEllipsis = true
            };
            _toolTip?.SetToolTip(lblBasePath, BasePath);

            btnSelectBasePath = new Button
            {
                Text = Localization.Tr("btn_uo_folder", "📁 Dossier UO 3D..."),
                Location = new Point(8, 76),
                Width = 140,
                Height = 24,
                BackColor = Color.FromArgb(60, 60, 68),
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat
            };
            btnSelectBasePath.Click += (s, e) => ChangeBasePath();

            btnReloadBasePath = new Button
            {
                Text = Localization.Tr("btn_reload", "🔄 Recharger"),
                Location = new Point(154, 76),
                Width = 95,
                Height = 24,
                BackColor = Color.FromArgb(50, 60, 50),
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat
            };
            btnReloadBasePath.Click += (s, e) =>
            {
                LoadCategoryTree();
                Log("Rechargement des modèles et animations terminé.");
            };

            cmbLanguage = new ComboBox
            {
                Location = new Point(255, 76),
                Width = 110,
                Height = 24,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(32, 32, 36),
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8.5F, FontStyle.Regular)
            };
            cmbLanguage.Items.Add("🇫🇷 Français");
            cmbLanguage.Items.Add("🇬🇧 English");
            cmbLanguage.SelectedIndex = string.Equals(Localization.CurrentLanguage, "en", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            cmbLanguage.SelectedIndexChanged += (s, e) =>
            {
                string chosen = cmbLanguage.SelectedIndex == 1 ? "en" : "fr";
                if (!string.Equals(Localization.CurrentLanguage, chosen, StringComparison.OrdinalIgnoreCase))
                {
                    Localization.SetLanguage(chosen);
                    SaveSettings();
                }
            };

            grpInfo.Controls.AddRange(new Control[] { lblModelInfo, lblBasePath, btnSelectBasePath, btnReloadBasePath, cmbLanguage });

            grpHue = new GroupBox
            {
                Text = Localization.Tr("grp_hue", "Teintes & Couleurs (hues.mul)"),
                Dock = DockStyle.Fill,
                ForeColor = Color.Gainsboro,
                Padding = new Padding(6)
            };

            var lblHueLabel = new Label { Text = "Teinte :", Location = new Point(8, 22), AutoSize = true, ForeColor = Color.Gainsboro };

            nudHue = new NumericUpDown
            {
                Location = new Point(54, 20),
                Width = 56,
                Minimum = 0,
                Maximum = 2999,
                Value = 0,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle
            };

            btnPickHue = new Button
            {
                Text = "🎨 Palette...",
                Location = new Point(114, 19),
                Width = 84,
                Height = 24,
                BackColor = Color.FromArgb(60, 60, 68),
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat
            };
            btnPickHue.Click += (s, e) =>
            {
                try
                {
                    using var pop = new UoFiddler.Controls.Forms.HuePopUpForm(selectedHue =>
                    {
                        if (selectedHue >= 0 && selectedHue < 3000)
                        {
                            nudHue.Value = selectedHue;
                        }
                    }, (int)nudHue.Value);
                    pop.ShowDialog(this);
                }
                catch (Exception ex)
                {
                    Log("Erreur ouverture HuePopUpForm: " + ex.Message);
                }
            };

            chkPartialHue = new CheckBox
            {
                Text = "Partial Hue",
                Location = new Point(206, 21),
                Width = 88,
                Checked = true,
                ForeColor = Color.Gainsboro
            };

            chkDesaturate = new CheckBox
            {
                Text = "Désaturation",
                Location = new Point(298, 21),
                Width = 94,
                Checked = false,
                ForeColor = Color.Gainsboro
            };

            btnResetHue = new Button
            {
                Text = "Réinitialiser",
                Location = new Point(398, 19),
                Width = 82,
                Height = 24,
                BackColor = Color.FromArgb(60, 60, 62),
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat
            };
            btnResetHue.Click += (s, e) =>
            {
                nudHue.Value = 0;
                chkDesaturate.Checked = false;
                ApplyCurrentHue();
            };

            lblHueName = new Label
            {
                Text = "#0 (Original / Non teinté)",
                Location = new Point(8, 48),
                Size = new Size(390, 18),
                ForeColor = Color.LightSkyBlue,
                Font = new Font("Segoe UI", 8.25f, FontStyle.Bold),
                AutoEllipsis = true
            };

            var lblTargetLabel = new Label { Text = "Cible :", Location = new Point(8, 76), AutoSize = true, ForeColor = Color.Gainsboro };

            cmbHueTarget = new ComboBox
            {
                Location = new Point(48, 73),
                Width = 145,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(50, 50, 54),
                ForeColor = Color.Gainsboro
            };
            cmbHueTarget.Items.AddRange(new object[] { "Mesh sélectionné", "Tous les équipements", "Tous les meshes" });
            cmbHueTarget.SelectedIndex = 1;

            picHueBar = new PictureBox
            {
                Location = new Point(200, 73),
                Size = new Size(195, 23),
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(30, 30, 30)
            };
            picHueBar.Paint += PicHueBar_Paint;

            nudHue.ValueChanged += (s, e) =>
            {
                if (_suppressHueEvents) return;
                UpdateHueDisplay();
                ApplyCurrentHue();
            };

            chkPartialHue.CheckedChanged += (s, e) =>
            {
                if (_suppressHueEvents) return;
                ApplyCurrentHue();
            };

            chkDesaturate.CheckedChanged += (s, e) =>
            {
                if (_suppressHueEvents) return;
                ApplyCurrentHue();
            };

            cmbHueTarget.SelectedIndexChanged += (s, e) =>
            {
                if (_suppressHueEvents) return;
                ApplyCurrentHue();
            };

            grpHue.Controls.AddRange(new Control[] { lblHueLabel, nudHue, btnPickHue, chkPartialHue, chkDesaturate, btnResetHue, lblHueName, lblTargetLabel, cmbHueTarget, picHueBar });

            pnlBottomSplit.Controls.Add(grpInfo, 0, 0);
            pnlBottomSplit.Controls.Add(grpHue, 1, 0);

            // ---- Equipment controller: mirror / rotation / offset for an attached item ----
            grpEquip = new GroupBox
            {
                Text = Localization.Tr("grp_equip_adjust", "Ajustements & Retouches"),
                Dock = DockStyle.Fill,
                ForeColor = Color.Gainsboro,
                Padding = new Padding(8)
            };
            grpEquip.Controls.Add(new Label { Text = "Sélectionnez un mesh attaché dans « Meshes affiches »", Location = new Point(8, 20), Width = 380, ForeColor = Color.Gray });

            NumericUpDown MakeNud(int x, int y, decimal min, decimal max, decimal inc, int dec, int width = 90)
            {
                var n = new NumericUpDown
                {
                    Location = new Point(x, y),
                    Width = width,
                    Minimum = min,
                    Maximum = max,
                    Increment = inc,
                    DecimalPlaces = dec,
                    BackColor = Color.FromArgb(30, 30, 30),
                    ForeColor = Color.Gainsboro,
                    BorderStyle = BorderStyle.FixedSingle
                };
                n.ValueChanged += (s, e) => ApplyEquipControls();
                return n;
            }
            Button MakeRotBtn(string txt, int x, int y, NumericUpDown target, decimal delta)
            {
                var b = new Button { Text = txt, Location = new Point(x, y), Width = 36, Height = 22, BackColor = Color.FromArgb(60, 60, 62), ForeColor = Color.Gainsboro, FlatStyle = FlatStyle.Flat };
                b.Click += (s, e) => { target.Value = Math.Max(target.Minimum, Math.Min(target.Maximum, target.Value + delta)); };
                return b;
            }

            lblEquipTarget = new Label { Text = "Cible : (aucun)", Location = new Point(8, 40), Width = 380, Height = 18, ForeColor = Color.Gainsboro };

            grpEquip.Controls.Add(new Label { Text = "Miroir :", Location = new Point(8, 66), Width = 50, ForeColor = Color.Gainsboro });
            cbMirrorX = new CheckBox { Text = "X", Location = new Point(66, 64), Width = 42, ForeColor = Color.Gainsboro };
            cbMirrorY = new CheckBox { Text = "Y", Location = new Point(112, 64), Width = 42, ForeColor = Color.Gainsboro };
            cbMirrorZ = new CheckBox { Text = "Z", Location = new Point(158, 64), Width = 42, ForeColor = Color.Gainsboro };
            cbMirrorX.CheckedChanged += (s, e) => ApplyEquipControls();
            cbMirrorY.CheckedChanged += (s, e) => ApplyEquipControls();
            cbMirrorZ.CheckedChanged += (s, e) => ApplyEquipControls();

            cbMirrorArms = new CheckBox
            {
                Text = "Miroir sagittal D/G (Gants/Bras)",
                Location = new Point(206, 64),
                Width = 220,
                ForeColor = Color.LightSkyBlue,
                Font = new Font("Segoe UI", 8.25f, FontStyle.Bold)
            };
            cbMirrorArms.CheckedChanged += (s, e) => ApplyEquipControls();
            grpEquip.Controls.Add(cbMirrorArms);

            grpEquip.Controls.Add(new Label { Text = "Rotation (°)", Location = new Point(8, 92), Width = 90, ForeColor = Color.Gainsboro });
            grpEquip.Controls.Add(new Label { Text = "Décalage", Location = new Point(240, 92), Width = 90, ForeColor = Color.Gainsboro });

            nudRotX = MakeNud(30, 112, -360, 360, 5, 1);
            nudRotY = MakeNud(30, 138, -360, 360, 5, 1);
            nudRotZ = MakeNud(30, 164, -360, 360, 5, 1);
            grpEquip.Controls.Add(new Label { Text = "X", Location = new Point(10, 116), Width = 18, ForeColor = Color.Gainsboro });
            grpEquip.Controls.Add(new Label { Text = "Y", Location = new Point(10, 142), Width = 18, ForeColor = Color.Gainsboro });
            grpEquip.Controls.Add(new Label { Text = "Z", Location = new Point(10, 168), Width = 18, ForeColor = Color.Gainsboro });
            grpEquip.Controls.Add(MakeRotBtn("+90", 126, 112, nudRotX, 90));
            grpEquip.Controls.Add(MakeRotBtn("-90", 166, 112, nudRotX, -90));
            grpEquip.Controls.Add(MakeRotBtn("+90", 126, 138, nudRotY, 90));
            grpEquip.Controls.Add(MakeRotBtn("-90", 166, 138, nudRotY, -90));
            grpEquip.Controls.Add(MakeRotBtn("+90", 126, 164, nudRotZ, 90));
            grpEquip.Controls.Add(MakeRotBtn("-90", 166, 164, nudRotZ, -90));

            nudOffX = MakeNud(262, 112, -100m, 100m, 0.01m, 3);
            nudOffY = MakeNud(262, 138, -100m, 100m, 0.01m, 3);
            nudOffZ = MakeNud(262, 164, -100m, 100m, 0.01m, 3);
            grpEquip.Controls.Add(new Label { Text = "X", Location = new Point(242, 116), Width = 18, ForeColor = Color.Gainsboro });
            grpEquip.Controls.Add(new Label { Text = "Y", Location = new Point(242, 142), Width = 18, ForeColor = Color.Gainsboro });
            grpEquip.Controls.Add(new Label { Text = "Z", Location = new Point(242, 168), Width = 18, ForeColor = Color.Gainsboro });

            // Uniform scale + per-axis scale: inflate/deflate a piece so the body no
            // longer pokes through it (skinned armor) or resize a weapon.
            grpEquip.Controls.Add(new Label { Text = "Échelle (globale)", Location = new Point(8, 200), Width = 120, ForeColor = Color.Gainsboro });
            nudScale = MakeNud(140, 197, 0.001m, 100m, 0.01m, 3);

            grpEquip.Controls.Add(new Label { Text = "Échelle :", Location = new Point(8, 229), Width = 55, ForeColor = Color.Gainsboro });
            nudScaleX = MakeNud(80, 226, 0.001m, 100m, 0.01m, 3, 68);
            nudScaleY = MakeNud(168, 226, 0.001m, 100m, 0.01m, 3, 68);
            nudScaleZ = MakeNud(256, 226, 0.001m, 100m, 0.01m, 3, 68);
            grpEquip.Controls.Add(new Label { Text = "X", Location = new Point(66, 229), Width = 14, ForeColor = Color.Gray });
            grpEquip.Controls.Add(new Label { Text = "Y", Location = new Point(154, 229), Width = 14, ForeColor = Color.Gray });
            grpEquip.Controls.Add(new Label { Text = "Z", Location = new Point(242, 229), Width = 14, ForeColor = Color.Gray });

            // Thickness: push the piece outward along its normals (anti poke-through).
            grpEquip.Controls.Add(new Label { Text = "Épaisseur (normale)", Location = new Point(8, 258), Width = 130, ForeColor = Color.Gainsboro });
            nudThickness = MakeNud(140, 255, -1m, 5m, 0.002m, 3);
            grpEquip.Controls.Add(new Label { Text = "0 = aucune", Location = new Point(240, 258), Width = 120, ForeColor = Color.Gray });

            // Real-time cloth simulation (capes / robes / skirts).
            chkCloth = new CheckBox { Text = "Tissu (simulation)", Location = new Point(8, 284), Width = 135, ForeColor = Color.Gainsboro };
            chkCloth.CheckedChanged += (s, e) => { if (!_suppressEquip) SetClothSelected(chkCloth.Checked); };
            grpEquip.Controls.Add(chkCloth);

            grpEquip.Controls.Add(new Label { Text = "Grav.", Location = new Point(148, 286), Width = 34, ForeColor = Color.Gray });
            nudClothGrav = MakeNud(182, 281, 0m, 30m, 0.5m, 2);
            nudClothGrav.Width = 48;
            nudClothGrav.Value = 9.8m;

            grpEquip.Controls.Add(new Label { Text = "Marge (cm) :", Location = new Point(236, 286), Width = 68, ForeColor = Color.Gray });
            nudClothClearance = new NumericUpDown
            {
                Location = new Point(306, 282), Width = 55,
                Minimum = 0m, Maximum = 15m, Increment = 0.5m, DecimalPlaces = 1,
                Value = 0.8m,
                BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle
            };
            nudClothClearance.ValueChanged += (s, e) =>
            {
                _clothClearance = (float)nudClothClearance.Value / 100f;
                if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();
            };
            grpEquip.Controls.Add(nudClothClearance);

            chkShowColliders = new CheckBox { Text = "👁 Colliders", Location = new Point(8, 312), Width = 100, ForeColor = Color.LightGreen };
            chkShowColliders.CheckedChanged += (s, e) => { _showColliders = chkShowColliders.Checked; if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate(); };
            grpEquip.Controls.Add(chkShowColliders);

            chkShowBones = new CheckBox { Text = "🦴 Squelette", Location = new Point(112, 312), Width = 105, ForeColor = Color.Gold };
            chkShowBones.CheckedChanged += (s, e) => { _showBones = chkShowBones.Checked; if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate(); };
            grpEquip.Controls.Add(chkShowBones);

            chkClothHeatmap = new CheckBox { Text = "🎨 Zones Tissu", Location = new Point(222, 312), Width = 145, ForeColor = Color.Cyan };
            chkClothHeatmap.CheckedChanged += (s, e) => { _showClothHeatmap = chkClothHeatmap.Checked; if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate(); };
            grpEquip.Controls.Add(chkClothHeatmap);

            // ---- Advanced Skin Wrap panel (moved to the free space right of the mirror
            //      checkbox, where the old "Recaler" button was). "Skin wrap" applies the
            //      committed wrap; the options below build a LIVE preview (non-destructive)
            //      over 3 algorithms, so the best fit can be chosen visually. ----
            btnSkinWrap = new Button { Text = "Skin wrap (copier la peau du corps)", Location = new Point(440, 44), Width = 320, Height = 40, BackColor = Color.FromArgb(70, 70, 90), ForeColor = Color.Gainsboro, FlatStyle = FlatStyle.Flat };
            btnSkinWrap.Click += (s, e) => SkinWrapSelected();
            grpEquip.Controls.Add(btnSkinWrap);

            grpEquip.Controls.Add(new Label { Text = "Portée :", Location = new Point(440, 92), Width = 70, ForeColor = Color.Gainsboro });
            cmbWrapSource = new ComboBox { Location = new Point(510, 89), Width = 250, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(50, 50, 54), ForeColor = Color.Gainsboro };
            cmbWrapSource.Items.AddRange(new object[] { "Corps entier (auto)", "Parties proches du mesh", "Meshes cochés comme source" });
            cmbWrapSource.SelectedIndex = 0;
            cmbWrapSource.SelectedIndexChanged += (s, e) => RebuildWrapPreview();
            grpEquip.Controls.Add(cmbWrapSource);

            grpEquip.Controls.Add(new Label { Text = "Algorithme :", Location = new Point(440, 120), Width = 80, ForeColor = Color.Gainsboro });
            cmbWrapAlgo = new ComboBox { Location = new Point(520, 117), Width = 240, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(50, 50, 54), ForeColor = Color.Gainsboro };
            cmbWrapAlgo.Items.AddRange(new object[] { "Voisins (sommets)", "Os (capsules)", "Projection surface" });
            cmbWrapAlgo.SelectedIndex = 0;
            cmbWrapAlgo.SelectedIndexChanged += (s, e) => RebuildWrapPreview();
            grpEquip.Controls.Add(cmbWrapAlgo);

            grpEquip.Controls.Add(new Label { Text = "Voisins K", Location = new Point(440, 148), Width = 62, ForeColor = Color.Gray });
            nudWrapK = MakeNud(504, 145, 1m, 8m, 1m, 0, 46);
            nudWrapK.Value = 4m;
            nudWrapK.ValueChanged += (s, e) => RebuildWrapPreview();
            grpEquip.Controls.Add(nudWrapK);
            grpEquip.Controls.Add(new Label { Text = "Rayon", Location = new Point(560, 148), Width = 42, ForeColor = Color.Gray });
            nudWrapRadius = MakeNud(604, 145, 0m, 50m, 0.01m, 2, 56);
            nudWrapRadius.Value = 0m;
            nudWrapRadius.ValueChanged += (s, e) => RebuildWrapPreview();
            grpEquip.Controls.Add(nudWrapRadius);
            grpEquip.Controls.Add(new Label { Text = "Max os", Location = new Point(668, 148), Width = 48, ForeColor = Color.Gray });
            nudWrapMaxBones = MakeNud(716, 145, 1m, 8m, 1m, 0, 44);
            nudWrapMaxBones.Value = 4m;
            nudWrapMaxBones.ValueChanged += (s, e) => RebuildWrapPreview();
            grpEquip.Controls.Add(nudWrapMaxBones);

            grpEquip.Controls.Add(new Label { Text = "Pondération :", Location = new Point(440, 176), Width = 84, ForeColor = Color.Gainsboro });
            cmbWrapFalloff = new ComboBox { Location = new Point(524, 173), Width = 236, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(50, 50, 54), ForeColor = Color.Gainsboro };
            cmbWrapFalloff.Items.AddRange(new object[] { "Inverse distance", "Uniforme", "Gaussien", "Distance au carré" });
            cmbWrapFalloff.SelectedIndex = 0;
            cmbWrapFalloff.SelectedIndexChanged += (s, e) => RebuildWrapPreview();
            grpEquip.Controls.Add(cmbWrapFalloff);

            chkWrapAnatomical = new CheckBox { Text = "Filtre anatomique (jambes/bras D-G)", Location = new Point(440, 201), Width = 300, ForeColor = Color.Gainsboro };
            chkWrapAnatomical.Checked = true;
            chkWrapAnatomical.CheckedChanged += (s, e) => RebuildWrapPreview();
            grpEquip.Controls.Add(chkWrapAnatomical);

            chkWrapLive = new CheckBox { Text = "Aperçu temps réel", Location = new Point(440, 225), Width = 160, ForeColor = Color.LightGreen };
            chkWrapLive.Checked = true;
            grpEquip.Controls.Add(chkWrapLive);

            btnWrapPreview = new Button { Text = "Aperçu", Location = new Point(600, 223), Width = 52, Height = 24, BackColor = Color.FromArgb(60, 70, 90), ForeColor = Color.Gainsboro, FlatStyle = FlatStyle.Flat };
            btnWrapPreview.Click += (s, e) => RebuildWrapPreview(true);
            grpEquip.Controls.Add(btnWrapPreview);
            btnWrapApply = new Button { Text = "Appliquer", Location = new Point(656, 223), Width = 52, Height = 24, BackColor = Color.FromArgb(45, 95, 55), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
            btnWrapApply.Click += (s, e) => ApplyWrapPreview();
            grpEquip.Controls.Add(btnWrapApply);
            btnWrapCancel = new Button { Text = "Annuler", Location = new Point(712, 223), Width = 48, Height = 24, BackColor = Color.FromArgb(90, 55, 55), ForeColor = Color.Gainsboro, FlatStyle = FlatStyle.Flat };
            btnWrapCancel.Click += (s, e) => CancelWrapPreview();
            grpEquip.Controls.Add(btnWrapCancel);

            lblWrapInfo = new Label { Text = "Aperçu : aucun", Location = new Point(440, 252), Width = 320, Height = 40, ForeColor = Color.LightSkyBlue, Font = new Font("Segoe UI", 7.5f) };
            grpEquip.Controls.Add(lblWrapInfo);

            btnForceBase = new Button { Text = "Afficher la géométrie brute (base, sans peau)", Location = new Point(440, 296), Width = 320, Height = 26, BackColor = Color.FromArgb(90, 70, 50), ForeColor = Color.Gainsboro, FlatStyle = FlatStyle.Flat };
            btnForceBase.Click += (s, e) => ToggleForceBaseDisplay();
            grpEquip.Controls.Add(btnForceBase);

            _btnPushOutBody = new Button { Text = "Repousser hors du corps (mesh-mesh)", Location = new Point(440, 326), Width = 320, Height = 26, BackColor = Color.FromArgb(90, 60, 60), ForeColor = Color.Gainsboro, FlatStyle = FlatStyle.Flat };
            _btnPushOutBody.Click += (s, e) => TogglePushOutOfBodySelected();
            grpEquip.Controls.Add(_btnPushOutBody);

            _btnShrinkSew = new Button { Text = "Rétrécir / Coudre (Shrink/Sew)", Location = new Point(440, 356), Width = 320, Height = 26, BackColor = Color.FromArgb(70, 80, 90), ForeColor = Color.Gainsboro, FlatStyle = FlatStyle.Flat };
            _btnShrinkSew.Click += (s, e) => ToggleShrinkSewSelected();
            grpEquip.Controls.Add(_btnShrinkSew);

            var lblPushMargin = new Label { Text = "Marge :", Location = new Point(440, 388), Width = 55, ForeColor = Color.Gray };
            grpEquip.Controls.Add(lblPushMargin);
            var nudPushMargin = new NumericUpDown { Location = new Point(500, 386), Width = 70, Minimum = 0m, Maximum = 0.15m, Increment = 0.001m, DecimalPlaces = 4, Value = 0.005m, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            nudPushMargin.ValueChanged += (s, e) => { _pushOutMargin = (float)nudPushMargin.Value; OnClothingEffectParameterChanged(); };
            grpEquip.Controls.Add(nudPushMargin);
            _nudPushMargin = nudPushMargin;

            var lblSewShrink = new Label { Text = "Force :", Location = new Point(585, 388), Width = 50, ForeColor = Color.Gray };
            grpEquip.Controls.Add(lblSewShrink);
            var nudSewForce = new NumericUpDown { Location = new Point(640, 386), Width = 50, Minimum = 0.05m, Maximum = 2.0m, Increment = 0.05m, DecimalPlaces = 2, Value = 0.3m, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            nudSewForce.ValueChanged += (s, e) => { _sewShrinkForce = (float)nudSewForce.Value; OnClothingEffectParameterChanged(); };
            grpEquip.Controls.Add(nudSewForce);
            _nudSewForce = nudSewForce;

            btnEquipReset = new Button { Text = "Réinitialiser", Location = new Point(30, 342), Width = 115, Height = 24, BackColor = Color.FromArgb(60, 60, 62), ForeColor = Color.Gainsboro, FlatStyle = FlatStyle.Flat };
            btnEquipReset.Click += (s, e) => ResetEquipControls();
            grpEquip.Controls.Add(btnEquipReset);

            btnDuplicateMirror = new Button { Text = "Dupliquer en miroir (D ↔ G)", Location = new Point(150, 342), Width = 195, Height = 24, BackColor = Color.FromArgb(50, 75, 100), ForeColor = Color.Gainsboro, FlatStyle = FlatStyle.Flat };
            btnDuplicateMirror.Click += (s, e) => DuplicateMirrorSelectedMesh();
            grpEquip.Controls.Add(btnDuplicateMirror);

            btnSubdiv = new Button { Text = "Subdiviser (1 → 4)  [plus de verts]", Location = new Point(30, 372), Width = 122, Height = 24, BackColor = Color.FromArgb(60, 70, 60), ForeColor = Color.Gainsboro, FlatStyle = FlatStyle.Flat };
            btnSubdiv.Click += (s, e) => SubdivideSelectedMesh(false);
            grpEquip.Controls.Add(btnSubdiv);

            var btnSubdivSmooth = new Button { Text = "Subdiviser + Arrondir", Location = new Point(158, 372), Width = 122, Height = 24, BackColor = Color.FromArgb(65, 75, 65), ForeColor = Color.Gainsboro, FlatStyle = FlatStyle.Flat };
            btnSubdivSmooth.Click += (s, e) => SubdivideSelectedMesh(true);
            grpEquip.Controls.Add(btnSubdivSmooth);

            // Bake the cloth once over the whole animation, then replay the cache.
            grpEquip.Controls.Add(new Label { Text = "Calcul : frames/s", Location = new Point(8, 404), Width = 120, ForeColor = Color.Gray });
            nudBakeFps = MakeNud(140, 401, 5m, 60m, 1m, 0);
            nudBakeFps.Value = 30m;
            grpEquip.Controls.Add(new Label { Text = "Fondu %", Location = new Point(238, 404), Width = 56, ForeColor = Color.Gray });
            nudLoopBlend = new NumericUpDown
            {
                Location = new Point(296, 401), Width = 56,
                Minimum = 0m, Maximum = 50m, Increment = 1m, DecimalPlaces = 0,
                Value = 10m,
                BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle
            };
            nudLoopBlend.ValueChanged += (s, e) => ReblendClothBakes();
            grpEquip.Controls.Add(nudLoopBlend);
            btnClothBake = new Button { Text = "Recalculer le tissu  (boucle sur l'action)", Location = new Point(30, 430), Width = 250, Height = 24, BackColor = Color.FromArgb(70, 60, 90), ForeColor = Color.Gainsboro, FlatStyle = FlatStyle.Flat };
            btnClothBake.Click += (s, e) => BakeCloth();
            grpEquip.Controls.Add(btnClothBake);

            // Manual cutout role for the selected mesh (auto-classified on load, editable
            // here). Body = export target; Accessory = occluder that carves the body; Mount = mount occluder.
            grpEquip.Controls.Add(new Label { Text = "Rôle cutout du mesh :", Location = new Point(8, 462), Width = 150, ForeColor = Color.Gainsboro });
            cmbCutoutRole = new ComboBox { Location = new Point(160, 459), Width = 175, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(50, 50, 54), ForeColor = Color.Gainsboro };
            cmbCutoutRole.Items.AddRange(new object[] { "Body (corps visible)", "Accessory (découpe le corps)", "Mount (monture / occluder)" });
            cmbCutoutRole.SelectedIndexChanged += CmbCutoutRole_SelectedIndexChanged;
            grpEquip.Controls.Add(cmbCutoutRole);

            btnBakeModifs = new Button
            {
                Text = "💾 Figer & Enregistrer les modifications",
                Location = new Point(440, 420),
                Width = 320,
                Height = 32,
                BackColor = Color.FromArgb(35, 95, 45),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font(Font.FontFamily, 9f, FontStyle.Bold)
            };
            btnBakeModifs.FlatAppearance.BorderColor = Color.FromArgb(60, 140, 70);
            btnBakeModifs.Click += (s, e) => BakeAndSaveSelectedMesh();
            grpEquip.Controls.Add(btnBakeModifs);

            grpEquip.Controls.AddRange(new Control[] { lblEquipTarget, cbMirrorX, cbMirrorY, cbMirrorZ, cbMirrorArms, nudRotX, nudRotY, nudRotZ, nudOffX, nudOffY, nudOffZ, nudScale, nudScaleX, nudScaleY,            nudScaleZ, nudThickness, chkCloth, nudClothGrav, btnSkinWrap, btnEquipReset, btnDuplicateMirror, btnSubdiv, 
           nudBakeFps, nudLoopBlend, btnClothBake, cmbCutoutRole, btnBakeModifs,
           cmbWrapSource, cmbWrapAlgo, cmbWrapFalloff, nudWrapK, nudWrapRadius, nudWrapMaxBones,
           chkWrapAnatomical, chkWrapLive, btnWrapPreview, btnWrapApply, btnWrapCancel, lblWrapInfo });

            // ---- Split the old "Équipement" panel into two tabs: equipment placement
            // | character pose/displacement (pose studio for non T-pose imports) ----
            tabEquip = new TabControl { Dock = DockStyle.Fill };
            tabEquipment = new TabPage(Localization.Tr("tab_equipment", "Équipement (attache)")) { BackColor = Color.FromArgb(45, 45, 48) };
            tabPose = new TabPage(Localization.Tr("tab_pose", "Personnage (déplacement / pose)")) { BackColor = Color.FromArgb(45, 45, 48), AutoScroll = true };
            tabEquipment.Controls.Add(grpEquip);   // grpEquip already Dock=Fill

            // ============================================================================
            // POSE STUDIO — styled like the AutoRigDialog / "Convert/create rig" panels:
            // accent-titled GroupBoxes stacked top-down inside a scrollable dark panel,
            // with flat tinted buttons, (22,22,24) NUDs, small bold section fonts.
            // ============================================================================
            var grpPose = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.FromArgb(30, 30, 32),
                Padding = new Padding(12)
            };

            Color cTextOn = Color.White;
            var fontSection = new Font("Segoe UI", 9.5f, FontStyle.Bold);
            var fontBody = new Font("Segoe UI", 8.75f, FontStyle.Regular);
            var fontSmall = new Font("Segoe UI", 8f, FontStyle.Regular);

            // Section container: accent-titled GroupBox, Dock=Top, fixed height.
            GroupBox MakePoseSection(string title, Color accent, int height)
            {
                return new GroupBox
                {
                    Text = title,
                    Dock = DockStyle.Top,
                    Height = height,
                    ForeColor = accent,
                    Font = fontSection,
                    Padding = new Padding(10)
                };
            }
            // Row panel with absolute placement (label then control).
            Panel PoseRow(int h)
            {
                return new Panel { Dock = DockStyle.Top, Height = h, Padding = new Padding(0, 2, 0, 2) };
            }
            // Styled NUD (matches AutoRigDialog: (22,22,24)/White).
            NumericUpDown MakePoseNud(decimal min, decimal max, decimal inc, int dec, EventHandler onChanged, int width = 84, Color? fore = null)
            {
                var n = new NumericUpDown
                {
                    Width = width,
                    Minimum = min,
                    Maximum = max,
                    Increment = inc,
                    DecimalPlaces = dec,
                    BackColor = Color.FromArgb(22, 22, 24),
                    ForeColor = fore ?? cTextOn,
                    TextAlign = HorizontalAlignment.Right,
                    Font = fontBody
                };
                n.ValueChanged += onChanged;
                return n;
            }
            // Flat tinted button (matches AutoRigDialog CreateTBtn).
            Button MakePoseBtn(string txt, Color back, EventHandler onClick, Font f = null, int h = 26, int w = 0)
            {
                var b = new Button
                {
                    Text = txt, Height = h, AutoSize = w <= 0, Width = w > 0 ? w : 0,
                    BackColor = back, ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat, Font = f ?? fontBody,
                    Margin = new Padding(3, 3, 3, 3)
                };
                b.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 75);
                b.Click += onClick;
                return b;
            }
            // Small axis stepper button.
            Button MakeAxisBtn(string txt, NumericUpDown target, decimal delta)
            {
                var b = new Button
                {
                    Text = txt, Size = new Size(44, 25),
                    BackColor = Color.FromArgb(45, 45, 48), ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat, Font = fontSmall,
                    Margin = new Padding(2, 2, 2, 2)
                };
                b.FlatAppearance.BorderColor = Color.FromArgb(65, 65, 70);
                b.Click += (s, e) => target.Value = Math.Max(target.Minimum, Math.Min(target.Maximum, target.Value + delta));
                return b;
            }

            // ---- Header / intro banner ----
            var pnlIntro = new Panel { Dock = DockStyle.Top, Height = 46, BackColor = Color.FromArgb(38, 40, 46) };
            pnlIntro.Controls.Add(new Label
            {
                Text = "🦴  Pose du rig — alignez le personnage sur un équipement importé.",
                Location = new Point(10, 6), AutoSize = true,
                ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            });
            pnlIntro.Controls.Add(new Label
            {
                Text = "Le mesh n'est jamais modifié : seul le squelette bouge (skinning intact).",
                Location = new Point(10, 25), AutoSize = true,
                ForeColor = Color.Gray, Font = fontSmall
            });

            // ================= Section 1 — CIBLE =================
            var secTarget = MakePoseSection("1.  Cible de la pose (meshes affectés)", Color.DeepSkyBlue, 96);
            var p1a = PoseRow(30);
            p1a.Controls.Add(new Label { Text = "Meshes affectés", Location = new Point(2, 6), AutoSize = true, ForeColor = Color.Gainsboro, Font = fontBody });
            cbPoseTarget = new ComboBox
            {
                Location = new Point(120, 3), Width = 430,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(22, 22, 24), ForeColor = cTextOn, Font = fontBody
            };
            cbPoseTarget.Items.AddRange(new object[]
            {
                "Auto — le personnage seulement (corps + sa peau)",
                "Sélection « Meshes affichés » — cochez les meshes à poser"
            });
            cbPoseTarget.SelectedIndex = 0;
            cbPoseTarget.SelectedIndexChanged += (s, e) =>
            {
                _poseTargetMode = cbPoseTarget.SelectedIndex;
                SyncPoseTargetsFromTree();
                Repose();
            };
            p1a.Controls.Add(cbPoseTarget);
            var p1b = PoseRow(24);
            lblPoseTargetInfo = new Label
            {
                Text = "Cible : (auto)", Location = new Point(122, 3), Width = 430, Height = 16,
                ForeColor = Color.LightSkyBlue, Font = fontSmall
            };
            p1b.Controls.Add(lblPoseTargetInfo);
            secTarget.Controls.Add(p1b);
            secTarget.Controls.Add(p1a);

            // ================= Section 2 — OS =================
            var secBone = MakePoseSection("2.  Os à poser + miroir", Color.LimeGreen, 92);
            var p2a = PoseRow(32);
            p2a.Controls.Add(new Label { Text = "Os", Location = new Point(2, 7), AutoSize = true, ForeColor = Color.Gainsboro, Font = fontBody });
            cbPoseBones = new ComboBox
            {
                Location = new Point(40, 4), Width = 340,
                DropDownStyle = ComboBoxStyle.DropDownList,
                DropDownWidth = 380, MaxDropDownItems = 20,
                BackColor = Color.FromArgb(22, 22, 24), ForeColor = cTextOn, Font = fontBody
            };
            cbPoseBones.SelectedIndexChanged += CbPoseBones_Changed;
            p2a.Controls.Add(cbPoseBones);
            chkPoseMirror = new CheckBox
            {
                Text = "Miroir L/R",
                Location = new Point(392, 6), Width = 110,
                ForeColor = Color.LightYellow,
                Font = new Font("Segoe UI", 8.75f, FontStyle.Bold)
            };
            p2a.Controls.Add(chkPoseMirror);
            var p2b = PoseRow(22);
            p2b.Controls.Add(new Label
            {
                Text = "Le miroir applique le même réglage à l'os opposé (ex. L UpperArm ↔ R UpperArm).",
                Location = new Point(40, 2), AutoSize = true,
                ForeColor = Color.Gray, Font = fontSmall
            });
            secBone.Controls.Add(p2b);
            secBone.Controls.Add(p2a);

            // ================= Section 3 — ROTATION =================
            var secRot = MakePoseSection("3.  Rotation locale de l'os (°)", Color.Orange, 168);
            nudPoseRotX = MakePoseNud(-360, 360, 1, 1, OnPoseRotChanged, 84, Color.Gold);
            nudPoseRotY = MakePoseNud(-360, 360, 1, 1, OnPoseRotChanged, 84);
            nudPoseRotZ = MakePoseNud(-360, 360, 1, 1, OnPoseRotChanged, 84);
            var rotNuds = new[] { nudPoseRotX, nudPoseRotY, nudPoseRotZ };
            var rotAxis = new[] { "X", "Y", "Z" };
            var rotSteps = new (string t, decimal d)[] { ("-90", -90), ("-15", -15), ("-5", -5), ("+5", 5), ("+15", 15), ("+90", 90) };
            for (int a = 0; a < 3; a++)   // X,Y,Z top to bottom (first Dock=Top stays on top)
            {
                var row = PoseRow(34);
                row.Controls.Add(new Label { Text = rotAxis[a], Location = new Point(2, 6), Width = 18, Height = 18, ForeColor = Color.Gainsboro, Font = fontBody });
                rotNuds[a].Location = new Point(22, 3);
                row.Controls.Add(rotNuds[a]);
                var flow = new FlowLayoutPanel { Location = new Point(114, 2), Size = new Size(300, 30), FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
                foreach (var st in rotSteps)
                    flow.Controls.Add(MakeAxisBtn(st.t, rotNuds[a], st.d));
                row.Controls.Add(flow);
                secRot.Controls.Add(row);
            }

            // ================= Section 4 — TRANSLATION =================
            var secTr = MakePoseSection("4.  Translation locale de l'os", Color.MediumOrchid, 138);
            nudPoseOffX = MakePoseNud(-5m, 5m, 0.005m, 3, OnPoseOffChanged, 110, Color.Gold);
            nudPoseOffY = MakePoseNud(-5m, 5m, 0.005m, 3, OnPoseOffChanged, 110);
            nudPoseOffZ = MakePoseNud(-5m, 5m, 0.005m, 3, OnPoseOffChanged, 110);
            var offNuds = new[] { nudPoseOffX, nudPoseOffY, nudPoseOffZ };
            for (int a = 0; a < 3; a++)   // X,Y,Z top to bottom
            {
                var row = PoseRow(32);
                row.Controls.Add(new Label { Text = rotAxis[a], Location = new Point(2, 6), Width = 18, Height = 18, ForeColor = Color.Gainsboro, Font = fontBody });
                offNuds[a].Location = new Point(22, 3);
                row.Controls.Add(offNuds[a]);
                if (a == 0)
                    row.Controls.Add(new Label { Text = "unités monde — déplacements fins", Location = new Point(140, 6), AutoSize = true, ForeColor = Color.Gray, Font = fontSmall });
                secTr.Controls.Add(row);
            }

            // ================= Section 5 — DÉPLACEMENT GLOBAL =================
            var secGlob = MakePoseSection("5.  Déplacement global du corps (racine)", Color.DeepSkyBlue, 150);
            nudPoseGlobX = MakePoseNud(-20m, 20m, 0.01m, 3, OnPoseGlobalChanged, 90, Color.Gold);
            nudPoseGlobY = MakePoseNud(-20m, 20m, 0.01m, 3, OnPoseGlobalChanged, 90);
            nudPoseGlobZ = MakePoseNud(-20m, 20m, 0.01m, 3, OnPoseGlobalChanged, 90);
            var globNuds = new[] { nudPoseGlobX, nudPoseGlobY, nudPoseGlobZ };
            for (int a = 0; a < 3; a++)   // X,Y,Z top to bottom
            {
                var row = PoseRow(32);
                row.Controls.Add(new Label { Text = rotAxis[a], Location = new Point(2, 6), Width = 18, Height = 18, ForeColor = Color.Gainsboro, Font = fontBody });
                globNuds[a].Location = new Point(22, 3);
                row.Controls.Add(globNuds[a]);
                if (a == 0)
                {
                    row.Controls.Add(new Label { Text = "Lacet (°)", Location = new Point(150, 6), AutoSize = true, ForeColor = Color.Gainsboro, Font = fontBody });
                    nudPoseGlobYaw = MakePoseNud(-180m, 180m, 1m, 1, OnPoseGlobalChanged, 90, Color.Gold);
                    nudPoseGlobYaw.Location = new Point(210, 3);
                    row.Controls.Add(nudPoseGlobYaw);
                }
                secGlob.Controls.Add(row);
            }
            var pGlobChk = PoseRow(26);
            chkPoseBodyOnly = new CheckBox
            {
                Text = "Corps seulement (l'équipement libre reste en place)",
                Location = new Point(4, 4), Width = 430, Checked = true,
                ForeColor = Color.LightYellow, Font = fontBody
            };
            chkPoseBodyOnly.CheckedChanged += OnPoseGlobalChanged;
            pGlobChk.Controls.Add(chkPoseBodyOnly);
            secGlob.Controls.Add(pGlobChk);

            // ================= Section 6 — ACTIONS / SAUVEGARDE =================
            var secAct = MakePoseSection("6.  Enregistrement de la pose", Color.White, 132);
            var flowAct = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 34, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            btnPoseResetBone = MakePoseBtn("↺  Réinitialiser l'os", Color.FromArgb(120, 70, 20), BtnPoseResetBone_Click);
            btnPoseResetAll = MakePoseBtn("⟲  Tout réinitialiser", Color.FromArgb(120, 50, 45), BtnPoseResetAll_Click);
            flowAct.Controls.Add(btnPoseResetBone);
            flowAct.Controls.Add(btnPoseResetAll);
            btnPoseSave = new Button
            {
                Text = "💾  Sauvegarder la pose sous...  (fichier .pose)",
                Dock = DockStyle.Bottom, Height = 40,
                BackColor = Color.FromArgb(24, 120, 56), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 10f, FontStyle.Bold)
            };
            btnPoseSave.FlatAppearance.BorderColor = Color.FromArgb(60, 170, 90);
            btnPoseSave.Click += BtnPoseSave_Click;
            var lblSaveHint = new Label
            {
                Dock = DockStyle.Bottom, Height = 34,
                Text = "La pose COMPLÈTE (tous les os + déplacement) est enregistrée dans Animations → « Poses ». Un clic la recharge à l'identique.",
                ForeColor = Color.Gray, Font = fontSmall
            };
            secAct.Controls.Add(lblSaveHint);
            secAct.Controls.Add(btnPoseSave);
            secAct.Controls.Add(flowAct);

            // Stack sections top-down. Dock=Top lays out in reverse z-order, so we add
            // them all then explicitly SendToBack from the BOTTOM section upward: the last
            // one sent to back ends up at the very top.
            grpPose.Controls.Add(secAct);
            grpPose.Controls.Add(secGlob);
            grpPose.Controls.Add(secTr);
            grpPose.Controls.Add(secRot);
            grpPose.Controls.Add(secBone);
            grpPose.Controls.Add(secTarget);
            grpPose.Controls.Add(pnlIntro);
            secAct.BringToFront();       // ensure final stacking is deterministic
            secGlob.BringToFront();
            secTr.BringToFront();
            secRot.BringToFront();
            secBone.BringToFront();
            secTarget.BringToFront();
            pnlIntro.BringToFront();

            tabPose.Controls.Add(grpPose);
            tabEquip.TabPages.Add(tabEquipment);
            tabEquip.TabPages.Add(tabPose);

            // ---- Multipart character menu (H_Male / H_Female parts) ----
            grpChar = new GroupBox
            {
                Text = "Personnage (multipartie)",
                Dock = DockStyle.Right,
                Width = 250,
                ForeColor = Color.Gainsboro,
                Padding = new Padding(8)
            };
            lblCharInfo = new Label { Text = "Chargez H_Male / H_Female", Location = new Point(8, 20), Width = 232, Height = 28, ForeColor = Color.Gray };

            ComboBox MakeSlotCombo(int y)
            {
                var cb = new ComboBox
                {
                    Location = new Point(8, y),
                    Width = 232,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    DropDownWidth = 260,
                    MaxDropDownItems = 20,
                    BackColor = Color.FromArgb(30, 30, 30),
                    ForeColor = Color.Gainsboro,
                    Enabled = false
                };
                cb.SelectedIndexChanged += CharCombo_Changed;
                return cb;
            }
            grpChar.Controls.Add(new Label { Text = "Visage", Location = new Point(8, 50), Width = 232, ForeColor = Color.Gainsboro });
            cbCharFace = MakeSlotCombo(68);
            grpChar.Controls.Add(new Label { Text = "Coiffure", Location = new Point(8, 96), Width = 232, ForeColor = Color.Gainsboro });
            cbCharHair = MakeSlotCombo(114);
            grpChar.Controls.Add(new Label { Text = "Barbe (homme)", Location = new Point(8, 142), Width = 232, ForeColor = Color.Gainsboro });
            cbCharBeard = MakeSlotCombo(160);
            grpChar.Controls.Add(new Label { Text = "Torse", Location = new Point(8, 188), Width = 232, ForeColor = Color.Gainsboro });
            cbCharTorso = MakeSlotCombo(206);
            grpChar.Controls.Add(new Label { Text = "Peau (corps)", Location = new Point(8, 234), Width = 232, ForeColor = Color.Gainsboro });
            cbCharSkin = MakeSlotCombo(252);
            cbCharSkin.SelectedIndexChanged += CharSkin_Changed;
            // Multipart character preset buttons (Save / Load / Reset / Unified .char export)
            btnSaveChar = new Button
            {
                Text = Localization.Tr("btn_char_save", "💾 Sauver personnage…"),
                Location = new Point(8, 286),
                Width = 232,
                Height = 28,
                BackColor = Color.FromArgb(40, 70, 95),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnSaveChar.Click += (s, e) => SaveCharacterPresetDialog();

            btnLoadChar = new Button
            {
                Text = Localization.Tr("btn_char_load", "📂 Charger personnage…"),
                Location = new Point(8, 318),
                Width = 232,
                Height = 28,
                BackColor = Color.FromArgb(45, 75, 55),
                ForeColor = Color.LightGreen,
                FlatStyle = FlatStyle.Flat
            };
            btnLoadChar.Click += (s, e) => LoadCharacterPresetDialog();

            btnResetChar = new Button
            {
                Text = Localization.Tr("btn_char_reset", "↺ Réinitialiser personnage"),
                Location = new Point(8, 350),
                Width = 232,
                Height = 26,
                BackColor = Color.FromArgb(60, 60, 62),
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat
            };
            btnResetChar.Click += (s, e) => ResetCharacterToDefault();

            btnExportCharFile = new Button
            {
                Text = Localization.Tr("btn_char_export", "📦 Exporter en .char unifié…"),
                Location = new Point(8, 380),
                Width = 232,
                Height = 28,
                BackColor = Color.FromArgb(85, 60, 45),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnExportCharFile.Click += (s, e) => ExportUnifiedCharModelDialog();

            var lblCharPresetInfo = new Label
            {
                Text = "Sauve & recharge : visage, coiffure, barbe, torse, peau, accessoires attachés (os, offsets, teintes, cutout).",
                Location = new Point(8, 414),
                Width = 232,
                Height = 44,
                ForeColor = Color.DarkGray,
                Font = new Font("Segoe UI", 7.5f)
            };

            grpChar.Controls.AddRange(new Control[] {
                lblCharInfo, cbCharFace, cbCharHair, cbCharBeard, cbCharTorso, cbCharSkin,
                btnSaveChar, btnLoadChar, btnResetChar, btnExportCharFile, lblCharPresetInfo
            });

            // ---- Camera / UO render options (docked right of the character menu) ----
            grpCamera = new GroupBox
            {
                Text = "Caméra (rendu UO)",
                Dock = DockStyle.Right,
                Width = 250,
                ForeColor = Color.Gainsboro,
                Padding = new Padding(8)
            };

            chkUoView = new CheckBox { Text = "Afficher le rendu UO", Location = new Point(8, 20), Width = 232, Checked = true, ForeColor = Color.Gainsboro };
            chkUoView.CheckedChanged += (s, e) => { _uoView = chkUoView.Checked; if (glControl != null) glControl.Invalidate(); };

            grpCamera.Controls.Add(new Label { Text = "Mode", Location = new Point(8, 44), Width = 110, Height = 15, ForeColor = Color.Gray });
            cmbUoMode = new ComboBox { Location = new Point(8, 62), Width = 110, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro };
            cmbUoMode.Items.AddRange(new object[] { "Vue 1 dir.", "Planche 5" });
            cmbUoMode.SelectedIndex = 0;
            cmbUoMode.SelectedIndexChanged += (s, e) => { _uoSheet = cmbUoMode.SelectedIndex == 1; if (glControl != null) glControl.Invalidate(); };

            grpCamera.Controls.Add(new Label { Text = "Direction", Location = new Point(124, 44), Width = 114, Height = 15, ForeColor = Color.Gray });
            cmbUoDir = new ComboBox { Location = new Point(124, 62), Width = 114, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro };
            cmbUoDir.Items.AddRange(new object[] { "N (0)", "NE (1)", "E (2)", "SE (3)", "S (4)" });
            cmbUoDir.SelectedIndex = 4;
            cmbUoDir.SelectedIndexChanged += (s, e) => { _uoDirection = cmbUoDir.SelectedIndex; if (glControl != null) glControl.Invalidate(); };

            grpCamera.Controls.Add(new Label { Text = "Zoom", Location = new Point(8, 90), Width = 110, Height = 15, ForeColor = Color.Gray });
            nudUoZoom = new NumericUpDown { Location = new Point(8, 108), Width = 110, Minimum = 0.05m, Maximum = 6m, Increment = 0.02m, DecimalPlaces = 2, Value = 0.27m, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            nudUoZoom.ValueChanged += (s, e) => { _uoZoom = (float)nudUoZoom.Value; if (glControl != null) glControl.Invalidate(); };

            grpCamera.Controls.Add(new Label { Text = "Inclinaison (°)", Location = new Point(124, 90), Width = 114, Height = 15, ForeColor = Color.Gray });
            nudUoElev = new NumericUpDown { Location = new Point(124, 108), Width = 114, Minimum = 5m, Maximum = 60m, Increment = 0.5m, DecimalPlaces = 2, Value = 26.565m, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            nudUoElev.ValueChanged += (s, e) => { _uoElevationDeg = (float)nudUoElev.Value; if (glControl != null) glControl.Invalidate(); };

            grpCamera.Controls.Add(new Label { Text = "Éch. verticale", Location = new Point(8, 136), Width = 110, Height = 15, ForeColor = Color.Gray });
            nudUoVert = new NumericUpDown { Location = new Point(8, 154), Width = 110, Minimum = 0.2m, Maximum = 3m, Increment = 0.05m, DecimalPlaces = 2, Value = 1m, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            nudUoVert.ValueChanged += (s, e) => { _uoVertScale = (float)nudUoVert.Value; if (glControl != null) glControl.Invalidate(); };

            grpCamera.Controls.Add(new Label { Text = "Luminosité", Location = new Point(124, 136), Width = 114, Height = 15, ForeColor = Color.Gray });
            nudUoBright = new NumericUpDown { Location = new Point(124, 154), Width = 114, Minimum = 0.2m, Maximum = 2m, Increment = 0.05m, DecimalPlaces = 2, Value = 0.8m, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            nudUoBright.ValueChanged += (s, e) => { _uoBrightness = (float)nudUoBright.Value; if (glControl != null) glControl.Invalidate(); };

            chkUoFeet = new CheckBox { Text = "Pieds sur la ligne rouge", Location = new Point(8, 184), Width = 232, Checked = true, ForeColor = Color.Gainsboro };
            chkUoFeet.CheckedChanged += (s, e) => { _uoFeetOnLine = chkUoFeet.Checked; if (glControl != null) glControl.Invalidate(); };

            grpCamera.Controls.Add(new Label { Text = "Décalage X", Location = new Point(8, 208), Width = 110, Height = 15, ForeColor = Color.Gray });
            nudUoOffX = new NumericUpDown { Location = new Point(8, 226), Width = 110, Minimum = -2m, Maximum = 2m, Increment = 0.02m, DecimalPlaces = 3, Value = 0m, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            nudUoOffX.ValueChanged += (s, e) => { _uoOffX = (float)nudUoOffX.Value; if (glControl != null) glControl.Invalidate(); };

            grpCamera.Controls.Add(new Label { Text = "Décalage Y", Location = new Point(124, 208), Width = 114, Height = 15, ForeColor = Color.Gray });
            nudUoOffY = new NumericUpDown { Location = new Point(124, 226), Width = 114, Minimum = -2m, Maximum = 2m, Increment = 0.02m, DecimalPlaces = 3, Value = -0.080m, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            nudUoOffY.ValueChanged += (s, e) => { _uoOffY = (float)nudUoOffY.Value; if (glControl != null) glControl.Invalidate(); };

            grpCamera.Controls.Add(new Label { Text = "Rotation (°)", Location = new Point(8, 254), Width = 110, Height = 15, ForeColor = Color.Gray });
            nudUoYaw = new NumericUpDown { Location = new Point(8, 272), Width = 110, Minimum = -180m, Maximum = 180m, Increment = 5m, DecimalPlaces = 0, Value = 180m, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            nudUoYaw.ValueChanged += (s, e) => { _uoYawOffsetDeg = (float)nudUoYaw.Value; if (glControl != null) glControl.Invalidate(); };

            grpCamera.Controls.Add(new Label { Text = "Zoom global %", Location = new Point(124, 254), Width = 114, Height = 15, ForeColor = Color.Gray });
            nudUoGlobal = new NumericUpDown { Location = new Point(124, 272), Width = 114, Minimum = 10m, Maximum = 500m, Increment = 25m, DecimalPlaces = 0, Value = 100m, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            nudUoGlobal.ValueChanged += (s, e) => { _uoGlobalZoom = (float)nudUoGlobal.Value / 100f; if (glControl != null) glControl.Invalidate(); };

            // Reference overlay (UOFiddler animation, body 400 = c_man).
            chkUoRef = new CheckBox { Text = "Réf. c_man (superposée)", Location = new Point(8, 302), Width = 232, Checked = false, ForeColor = Color.Gainsboro };
            chkUoRef.CheckedChanged += (s, e) => { _uoRefShow = chkUoRef.Checked; if (glControl != null) glControl.Invalidate(); };

            grpCamera.Controls.Add(new Label { Text = "Action réf.", Location = new Point(8, 326), Width = 110, Height = 15, ForeColor = Color.Gray });
            nudUoRefAction = new NumericUpDown { Location = new Point(8, 344), Width = 110, Minimum = 0m, Maximum = 34m, Increment = 1m, DecimalPlaces = 0, Value = 0m, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            nudUoRefAction.ValueChanged += (s, e) => { _uoRefAction = (int)nudUoRefAction.Value; _uoRefKey = int.MinValue; if (glControl != null) glControl.Invalidate(); };

            grpCamera.Controls.Add(new Label { Text = "Frame réf.", Location = new Point(124, 326), Width = 114, Height = 15, ForeColor = Color.Gray });
            nudUoRefFrame = new NumericUpDown { Location = new Point(124, 344), Width = 114, Minimum = 0m, Maximum = 40m, Increment = 1m, DecimalPlaces = 0, Value = 0m, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            nudUoRefFrame.ValueChanged += (s, e) => { _uoRefFrame = (int)nudUoRefFrame.Value; _uoRefKey = int.MinValue; if (glControl != null) glControl.Invalidate(); };

            grpCamera.Controls.Add(new Label { Text = "Éch. réf.", Location = new Point(8, 372), Width = 110, Height = 15, ForeColor = Color.Gray });
            nudUoRefScale = new NumericUpDown { Location = new Point(8, 390), Width = 110, Minimum = 0.05m, Maximum = 40m, Increment = 0.05m, DecimalPlaces = 2, Value = 1m, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            nudUoRefScale.ValueChanged += (s, e) => { _uoRefScale = (float)nudUoRefScale.Value; if (glControl != null) glControl.Invalidate(); };

            grpCamera.Controls.Add(new Label { Text = "Opacité %", Location = new Point(124, 372), Width = 114, Height = 15, ForeColor = Color.Gray });
            nudUoRefOpacity = new NumericUpDown { Location = new Point(124, 390), Width = 114, Minimum = 0m, Maximum = 100m, Increment = 5m, DecimalPlaces = 0, Value = 55m, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            nudUoRefOpacity.ValueChanged += (s, e) => { _uoRefOpacity = (float)nudUoRefOpacity.Value / 100f; if (glControl != null) glControl.Invalidate(); };

            // Camera Quick Presets (Perso / Monstre / Défaut)
            btnPresetPerso = new Button
            {
                Text = Localization.Tr("btn_cam_char", "Perso"),
                Location = new Point(8, 420),
                Width = 72,
                Height = 25,
                BackColor = Color.FromArgb(40, 70, 95),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8f)
            };
            btnPresetPerso.Click += (s, e) => ApplyCameraPreset(new UoCameraPreset { Zoom = 0.27f, ElevationDeg = 26.565f, VertScale = 1.0f, Brightness = 0.8f, FeetOnLine = true, OffX = 0f, OffY = -0.080f, YawOffsetDeg = 180f, GlobalZoom = 1.0f });

            btnPresetMonstre = new Button
            {
                Text = Localization.Tr("btn_cam_monster", "Monstre"),
                Location = new Point(84, 420),
                Width = 74,
                Height = 25,
                BackColor = Color.FromArgb(60, 50, 75),
                ForeColor = Color.LightSkyBlue,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8f)
            };
            btnPresetMonstre.Click += (s, e) => ApplyCameraPreset(new UoCameraPreset { Zoom = 0.18f, ElevationDeg = 26.565f, VertScale = 1.0f, Brightness = 0.8f, FeetOnLine = true, OffX = 0f, OffY = -0.050f, YawOffsetDeg = 180f, GlobalZoom = 1.0f });

            btnResetCamera = new Button
            {
                Text = Localization.Tr("btn_cam_default", "↺ Défaut"),
                Location = new Point(162, 420),
                Width = 76,
                Height = 25,
                BackColor = Color.FromArgb(60, 60, 62),
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8f)
            };
            btnResetCamera.Click += (s, e) => ApplyCameraPreset(new UoCameraPreset());

            // Camera Save / Load buttons
            btnSaveCamera = new Button
            {
                Text = Localization.Tr("btn_cam_save", "💾 Sauver caméra…"),
                Location = new Point(8, 450),
                Width = 112,
                Height = 26,
                BackColor = Color.FromArgb(45, 60, 75),
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8f)
            };
            btnSaveCamera.Click += (s, e) => SaveCameraPresetDialog();

            btnLoadCamera = new Button
            {
                Text = Localization.Tr("btn_cam_load", "📂 Charger caméra…"),
                Location = new Point(124, 450),
                Width = 114,
                Height = 26,
                BackColor = Color.FromArgb(45, 60, 75),
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8f)
            };
            btnLoadCamera.Click += (s, e) => LoadCameraPresetDialog();

            var lblCamInfo = new Label
            {
                Text = "Préréglages caméra & rendu UO",
                Location = new Point(8, 482),
                Width = 230,
                Height = 20,
                ForeColor = Color.DarkGray,
                Font = new Font("Segoe UI", 7.5f)
            };

            grpCamera.Controls.AddRange(new Control[] {
                chkUoView, cmbUoMode, cmbUoDir, nudUoZoom, nudUoElev, nudUoVert, nudUoBright,
                chkUoFeet, nudUoOffX, nudUoOffY, chkUoRef, nudUoRefAction, nudUoRefFrame,
                nudUoRefScale, nudUoRefOpacity, nudUoYaw, nudUoGlobal,
                btnPresetPerso, btnPresetMonstre, btnResetCamera, btnSaveCamera, btnLoadCamera, lblCamInfo
            });

            var pnlPlayRow = new Panel { Dock = DockStyle.Top, Height = 540 };
            pnlPlayRow.Controls.Add(tabEquip);     // Fill (tabs : Équipement | Personnage)
            pnlPlayRow.Controls.Add(grpCamera);     // Right (docked first -> further right)
            pnlPlayRow.Controls.Add(grpChar);       // Right
            pnlPlayRow.Controls.Add(grpPlayback);   // Left

            pnlRightBottom.Controls.Add(pnlBottomSplit); // Fill
            pnlRightBottom.Controls.Add(pnlPlayRow);  // Top

            pnlRight.Controls.Add(pnlRightBottom);
            pnlRight.Controls.Add(glControl);

            // Size the GL viewport explicitly above the bottom strip so it never
            // extends behind the controls (deterministic, independent of z-order).
            pnlRight.Resize += (s, e) => LayoutViewport(pnlRight, pnlRightBottom);
            LayoutViewport(pnlRight, pnlRightBottom);

            // ===== MAIN SPLIT: left tree | right viewport+playback =====
            var mainSplit = _mainSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                FixedPanel = FixedPanel.Panel1,
                BorderStyle = BorderStyle.FixedSingle,
                Orientation = Orientation.Vertical
            };
            mainSplit.Panel1.Controls.Add(pnlLeft);
            mainSplit.Panel2.Controls.Add(pnlRight);

            Controls.Add(mainSplit);
            ResumeLayout(false);

            // Initial splitter positions: restore the user's last layout if saved,
            // otherwise sensible defaults (models column doubled 364 -> 728, animations 420 high).
            SetSplitterDistance(mainSplit, LayoutMainSplit > 0 ? LayoutMainSplit : 728);
            SetSplitterDistance(leftSplit, LayoutLeftSplit > 0 ? LayoutLeftSplit : 320);
            SetSplitterDistance(animSplit, LayoutAnimSplit > 0 ? LayoutAnimSplit : 420);

            // Remember the layout when the user drags a splitter, and persist on close.
            mainSplit.SplitterMoved += (s, e) => LayoutMainSplit = mainSplit.SplitterDistance;
            leftSplit.SplitterMoved += (s, e) => LayoutLeftSplit = leftSplit.SplitterDistance;
            animSplit.SplitterMoved += (s, e) => LayoutAnimSplit = animSplit.SplitterDistance;

            LoadCategoryTree();
            Localization.LanguageChanged += UpdateUILanguage;
            UpdateUILanguage();
        }

        private void UpdateUILanguage()
        {
            if (grpInfo != null) grpInfo.Text = Localization.Tr("grp_info", "Informations Modèle & Répertoire");
            if (btnSelectBasePath != null) btnSelectBasePath.Text = Localization.Tr("btn_uo_folder", "📁 Dossier UO 3D...");
            if (btnReloadBasePath != null) btnReloadBasePath.Text = Localization.Tr("btn_reload", "🔄 Recharger");
            if (grpHue != null) grpHue.Text = Localization.Tr("grp_hue", "Teintes & Couleurs (hues.mul)");
            if (btnPickHue != null) btnPickHue.Text = Localization.Tr("btn_pick_hue", "Palette...");
            if (chkPartialHue != null) chkPartialHue.Text = Localization.Tr("chk_partial_hue", "Partial hue");
            if (chkDesaturate != null) chkDesaturate.Text = Localization.Tr("chk_desaturate", "Désaturer");
            if (btnResetHue != null) btnResetHue.Text = Localization.Tr("btn_reset_hue", "Reset");
            if (grpPlayback != null) grpPlayback.Text = Localization.Tr("grp_playback", "Contrôle Animation");
            if (btnPlay != null) btnPlay.Text = Localization.Tr("btn_play", "Play");
            if (btnPause != null) btnPause.Text = Localization.Tr("btn_pause", "Pause");
            if (btnStop != null) btnStop.Text = Localization.Tr("btn_stop", "Stop");
            if (chkLoop != null) chkLoop.Text = Localization.Tr("chk_loop", "Loop");
            if (btnExportVd != null) btnExportVd.Text = Localization.Tr("btn_export_vd", "Exporter en .vd (mapping)");
            if (btnExportCutout != null) btnExportCutout.Text = Localization.Tr("btn_export_cutout", "Exporter cutout (corps)");
            if (btnExportCutoutAcc != null) btnExportCutoutAcc.Text = Localization.Tr("btn_export_cutout_acc", "cutout accessoire");
            if (grpAnimations != null) grpAnimations.Text = Localization.Tr("grp_animations", "Animations UO 3D");
            if (btnTPose != null) btnTPose.Text = Localization.Tr("btn_tpose", "T-Pose");
            if (btnImportAnim != null) btnImportAnim.Text = Localization.Tr("btn_import_anim", "Importer Action (.anim)...");
            if (btnApplyMixamo != null) btnApplyMixamo.Text = Localization.Tr("btn_auto_rig", "⚡ Convert/create rig to UO…");
            if (grpLoaded != null) grpLoaded.Text = Localization.Tr("grp_loaded", "Meshes & Sous-objets affichés");
            if (btnAttach != null) btnAttach.Text = Localization.Tr("btn_attach", "Attacher...");
            if (btnImportMesh != null) btnImportMesh.Text = Localization.Tr("btn_import_mesh", "Importer Mesh (.grn)...");
            if (btnImportModel != null) btnImportModel.Text = Localization.Tr("btn_import_model", "Charger Modèle (.grn)...");
            if (tabEquipment != null) tabEquipment.Text = Localization.Tr("tab_equipment", "Équipement (attache)");
            if (tabPose != null) tabPose.Text = Localization.Tr("tab_pose", "Personnage (déplacement / pose)");
            if (grpCamera != null) grpCamera.Text = Localization.Tr("grp_camera", "Caméra (rendu UO)");
            if (chkUoView != null) chkUoView.Text = Localization.Tr("chk_uo_view", "Afficher le rendu UO");
            if (chkUoFeet != null) chkUoFeet.Text = Localization.Tr("chk_uo_feet", "Pieds sur la ligne rouge");
            if (chkUoRef != null) chkUoRef.Text = Localization.Tr("chk_uo_ref", "Réf. c_man (superposée)");
            if (grpChar != null) grpChar.Text = Localization.Tr("grp_char", "Personnage (multipartie)");
            if (grpEquip != null) grpEquip.Text = Localization.Tr("grp_equip_adjust", "Ajustements & Retouches");
            if (chkCloth != null) chkCloth.Text = Localization.Tr("chk_cloth_sim", "Tissu (simulation)");
            if (chkShowColliders != null) chkShowColliders.Text = Localization.Tr("chk_colliders", "Colliders");
            if (chkShowBones != null) chkShowBones.Text = Localization.Tr("chk_skeleton", "Squelette");
            if (chkClothHeatmap != null) chkClothHeatmap.Text = Localization.Tr("chk_cloth_zones", "Zones Tissu");
            if (btnSkinWrap != null) btnSkinWrap.Text = Localization.Tr("btn_skin_wrap", "Skin wrap (copier la peau du corps)");
            if (_btnPushOutBody != null) _btnPushOutBody.Text = Localization.Tr("btn_push_out", "Repousser hors du corps (mesh-mesh)");
            if (_btnShrinkSew != null) _btnShrinkSew.Text = Localization.Tr("btn_shrink_sew", "Rétrécir / Coudre (Shrink/Sew)");
            if (btnBakeModifs != null) btnBakeModifs.Text = Localization.Tr("btn_freeze_save", "💾 Figer & Enregistrer les modifications");
            if (btnDuplicateMirror != null) btnDuplicateMirror.Text = Localization.Tr("btn_duplicate_mirror", "Dupliquer en miroir (D ↔ G)");
            if (btnSubdiv != null) btnSubdiv.Text = Localization.Tr("btn_subdiv", "Subdiviser (1 → 4)");
            if (btnClothBake != null) btnClothBake.Text = Localization.Tr("btn_cloth_bake", "Recalculer le tissu");

            if (lblBone != null) lblBone.Text = Localization.Tr("lbl_attach_bone", "Attacher à l'os :");
            if (btnAttach != null) btnAttach.Text = Localization.Tr("btn_attach_toggle", "Attacher / Détacher");
            if (lblModels != null) lblModels.Text = Localization.Tr("lbl_models", "Modèles (LOD2)");
            if (btnForceBase != null) btnForceBase.Text = Localization.Tr("btn_force_base", "Afficher la géométrie brute (base, sans peau)");
            if (btnWrapPreview != null) btnWrapPreview.Text = Localization.Tr("btn_preview", "Aperçu");
            if (btnWrapCancel != null) btnWrapCancel.Text = Localization.Tr("btn_cancel", "Annuler");
            if (chkWrapLive != null) chkWrapLive.Text = Localization.Tr("chk_wrap_live", "Aperçu temps réel");
            if (btnSaveChar != null) btnSaveChar.Text = Localization.Tr("btn_char_save", "💾 Sauver personnage…");
            if (btnLoadChar != null) btnLoadChar.Text = Localization.Tr("btn_char_load", "📂 Charger personnage…");
            if (btnResetChar != null) btnResetChar.Text = Localization.Tr("btn_char_reset", "↺ Réinitialiser personnage");
            if (btnExportCharFile != null) btnExportCharFile.Text = Localization.Tr("btn_char_export", "📦 Exporter en .char unifié…");
            if (btnResetCamera != null) btnResetCamera.Text = Localization.Tr("btn_cam_default", "↺ Défaut");
            if (btnSaveCamera != null) btnSaveCamera.Text = Localization.Tr("btn_cam_save", "💾 Sauver caméra…");
            if (btnLoadCamera != null) btnLoadCamera.Text = Localization.Tr("btn_cam_load", "📂 Charger caméra…");
            if (btnPresetPerso != null) btnPresetPerso.Text = Localization.Tr("btn_cam_char", "Perso");
            if (btnPresetMonstre != null) btnPresetMonstre.Text = Localization.Tr("btn_cam_monster", "Monstre");
            if (btnPoseResetBone != null) btnPoseResetBone.Text = Localization.Tr("btn_pose_reset_bone", "↺  Réinitialiser l'os");
            if (btnPoseResetAll != null) btnPoseResetAll.Text = Localization.Tr("btn_pose_reset_all", "⟲  Tout réinitialiser");
            if (btnPoseSave != null) btnPoseSave.Text = Localization.Tr("btn_pose_save", "💾  Sauvegarder la pose sous...  (fichier .pose)");
            if (chkPoseBodyOnly != null) chkPoseBodyOnly.Text = Localization.Tr("chk_pose_body_only", "Corps seulement (l'équipement libre reste en place)");
            if (chkPoseMirror != null) chkPoseMirror.Text = Localization.Tr("chk_pose_mirror", "Miroir X automatique (D ↔ G)");

            if (cbBones != null && cbBones.Items.Count > 0 && cbBones.SelectedIndex == 0)
            {
                cbBones.Items[0] = Localization.Tr("cb_bone_none", "(non attaché)");
            }

            if (cmbLanguage != null)
            {
                int desired = string.Equals(Localization.CurrentLanguage, "en", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                if (cmbLanguage.SelectedIndex != desired)
                    cmbLanguage.SelectedIndex = desired;
            }
        }

        private bool _splittersConfigured;

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible && !_splittersConfigured)
            {
                _splittersConfigured = true;
                try { BeginInvoke((Action)(() => ApplySavedSplitters())); } catch { }
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (!_splittersConfigured && Width >= 800)
            {
                _splittersConfigured = true;
                try { BeginInvoke((Action)(() => ApplySavedSplitters())); } catch { }
            }
        }

        private void ApplySavedSplitters()
        {
            try
            {
                if (_mainSplit != null)
                {
                    int targetMain = LayoutMainSplit > 0 ? LayoutMainSplit : 728;
                    if (targetMain < 550) targetMain = 728;
                    SetSplitterDistance(_mainSplit, targetMain);
                }
                if (_leftSplit != null)
                {
                    int targetLeft = LayoutLeftSplit > 0 ? LayoutLeftSplit : 320;
                    if (targetLeft > 380 || targetLeft < 180) targetLeft = 320;
                    SetSplitterDistance(_leftSplit, targetLeft);
                }
                if (_animSplit != null)
                {
                    int targetAnim = LayoutAnimSplit > 0 ? LayoutAnimSplit : 420;
                    if (targetAnim < 360) targetAnim = 420;
                    SetSplitterDistance(_animSplit, targetAnim);
                }
            }
            catch { }
        }

        private static void SetSplitterDistance(SplitContainer sc, int distance)
        {
            if (sc == null) return;
            int span = sc.Orientation == Orientation.Vertical ? sc.Width : sc.Height;
            int lo = sc.Panel1MinSize;
            int hi = span - sc.Panel2MinSize - sc.SplitterWidth;
            if (hi < lo) hi = lo;
            try { sc.SplitterDistance = Math.Max(lo, Math.Min(distance, hi)); }
            catch { }
        }

        private void LayoutViewport(Control parent, Control bottom)
        {
            if (glControl == null || parent == null) return;
            const int pad = 6;
            int w = parent.ClientSize.Width - pad * 2;
            int h = parent.ClientSize.Height - (bottom != null ? bottom.Height : 0) - pad * 2;
            glControl.Bounds = new Rectangle(pad, pad, Math.Max(1, w), Math.Max(1, h));
        }

        private void InitializeOpenGL()
        {
            _renderTimer = new System.Windows.Forms.Timer { Interval = 16 };
            _renderTimer.Tick += RenderTimer_Tick;
            _lastFrameTime = DateTime.Now;
        }

        // ==================== TREE / LOADING ====================

        private void LoadCategoryTree()
        {
            RestoreModelsTree();

            // Defer auto-load until the OpenGL context is ready (first Paint).
            _initialLoadPending = tvModels.Nodes.Count > 0;
            BuildAnimCategories();
            if (_glReady)
                StartInitialLoad();
        }

        // Rebuild the normal category tree (Humans / Monsters / ... / Equipment) with
        // lazy "..." placeholders. Used at startup and when the model search is cleared.
        private void RestoreModelsTree()
        {
            if (tvModels == null) return;
            _suppressSelect = true;
            tvModels.BeginUpdate();
            try
            {
                tvModels.Nodes.Clear();
                if (!Directory.Exists(BasePath)) return;

                for (int i = 0; i < CategoryFolders.Length; i++)
                {
                    string folder = Path.Combine(BasePath, CategoryFolders[i]);
                    if (CategoryFolders[i] == "Meshs custom" && !Directory.Exists(folder))
                    {
                        try { Directory.CreateDirectory(folder); } catch { }
                    }

                    if (!Directory.Exists(folder))
                        continue;

                    bool isOthersCat = (i == 5);
                    bool isCustomMeshCat = (i == 6);
                    var info = new TreeNodeInfo
                    {
                        IsFolder = true,
                        Folder = folder,
                        Display = CategoryDisplay[i],
                        IsOthersCategory = isOthersCat,
                        IsCustomMeshCategory = isCustomMeshCat
                    };
                    var node = new TreeNode(CategoryDisplay[i]) { Tag = info };
                    node.Nodes.Add(new TreeNode("..."));
                    tvModels.Nodes.Add(node);
                }
            }
            finally
            {
                tvModels.EndUpdate();
                _suppressSelect = false;
            }
        }

        // Filter the LOD2 models tree by name. With an empty filter the normal
        // category tree is restored; otherwise a flat "Résultats" list of matching
        // creatures is shown (selecting one loads it exactly like the tree does).
        private void ApplyModelsFilter()
        {
            if (tvModels == null) return;
            string filter = txtModelFilter?.Text?.Trim() ?? string.Empty;
            if (filter.Length == 0) { RestoreModelsTree(); return; }

            _suppressSelect = true;
            tvModels.BeginUpdate();
            try
            {
                tvModels.Nodes.Clear();
                var results = new SortedDictionary<string, TreeNodeInfo>(StringComparer.OrdinalIgnoreCase);

                foreach (var folderName in CategoryFolders)
                {
                    string folder = Path.Combine(BasePath, folderName);
                    if (!Directory.Exists(folder)) continue;
                    try
                    {
                        foreach (var f in Directory.GetFiles(folder, "*.grn"))
                        {
                            string fileName = Path.GetFileNameWithoutExtension(f);
                            if (fileName.IndexOf("lod", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            string creature = DeriveCreature(fileName);
                            if (creature.Length == 0) creature = fileName;
                            if (creature.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                                fileName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;

                            if (!results.TryGetValue(creature, out var info))
                            {
                                results[creature] = new TreeNodeInfo
                                {
                                    IsFolder = false,
                                    Folder = folder,
                                    CreatureKey = creature,
                                    MeshPath = f,
                                    Display = creature
                                };
                            }
                            else if (info.MeshPath == null ||
                                     (f.IndexOf("lod2", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                      info.MeshPath.IndexOf("lod2", StringComparison.OrdinalIgnoreCase) < 0))
                            {
                                info.MeshPath = f;
                            }
                        }
                    }
                    catch { }
                }

                try
                {
                    foreach (var customDirName in new[] { "Others", "Meshs custom" })
                    {
                        string customFolder = Path.Combine(BasePath, customDirName);
                        if (Directory.Exists(customFolder))
                        {
                            var extraFiles = new List<string>();
                            extraFiles.AddRange(Directory.GetFiles(customFolder, "*.char", SearchOption.AllDirectories));
                            extraFiles.AddRange(Directory.GetFiles(customFolder, "*.fbx", SearchOption.AllDirectories));
                            extraFiles.AddRange(Directory.GetFiles(customFolder, "*.obj", SearchOption.AllDirectories));
                            foreach (var f in extraFiles)
                            {
                                string name = Path.GetFileNameWithoutExtension(f);
                                if (name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                                if (!results.ContainsKey(name))
                                {
                                    results[name] = new TreeNodeInfo
                                    {
                                        IsFolder = false,
                                        Folder = Path.GetDirectoryName(f),
                                        CreatureKey = name,
                                        MeshPath = f,
                                        Display = name,
                                        IsCustomMeshCategory = (customDirName == "Meshs custom"),
                                        IsOthersCategory = (customDirName == "Others")
                                    };
                                }
                            }
                        }
                    }
                }
                catch { }

                var root = new TreeNode($"Résultats ({results.Count})");
                foreach (var kv in results)
                    root.Nodes.Add(new TreeNode(kv.Key) { Tag = kv.Value });
                tvModels.Nodes.Add(root);
                root.Expand();
            }
            finally
            {
                tvModels.EndUpdate();
                _suppressSelect = false;
            }
        }

        private void TvModels_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            if (e.Node == null) return;
            var info = e.Node.Tag as TreeNodeInfo;
            if (info == null) return;

            try
            {
                if (_populatingTree) return;
                if (info.Populated) return;
                if (info.LeafPath != null) return;

                _populatingTree = true;
                try
                {
                    tvModels.BeginUpdate();
                    if (info.IsFolder)
                        AddCreatureNodes(e.Node, info.Folder);
                    else
                        AddAnimationLeafNodes(e.Node, info);
                }
                finally
                {
                    tvModels.EndUpdate();
                    _populatingTree = false;
                }
            }
            catch (Exception ex)
            {
                _populatingTree = false;
                Log($"BeforeExpand error: {ex.Message}\n{ex}");
            }
        }

        private static string StripLod(string name)
        {
            string n = name;
            int lod = n.IndexOf("lod", StringComparison.OrdinalIgnoreCase);
            if (lod > 0) n = n.Substring(0, lod);
            return n.TrimEnd('_', '-', '.');
        }

        private static bool IsTrailingSegment(string seg)
        {
            if (seg.Length == 0) return true; // "X_Walk" -> creature "X"
            foreach (char c in seg)
            {
                if (!char.IsDigit(c) && c != '_' && c != '!' && c != ' ')
                    return false;
            }
            return true;
        }

        // Split a model filename into its creature name:
        //   "H_Male_Walk_01"      -> "H_Male"
        //   "Birds_Eagle_Lod2"    -> "Birds_Eagle"
        //   "Orcs_Orc_Attack1"    -> "Orcs_Orc"
        //   "E_Female_Hair01V2"   -> "E_Female_Hair01V2" (equipment piece)
        public static string DeriveCreature(string fileName)
        {
            string n = StripLod(fileName);
            foreach (var tok in ActionTokens)
            {
                int i = n.LastIndexOf("_" + tok, StringComparison.OrdinalIgnoreCase);
                if (i < 0) continue;

                string after = n.Substring(i + 1 + tok.Length);
                if (IsTrailingSegment(after))
                    return n.Substring(0, i).TrimEnd('_', '-').Length > 0
                        ? n.Substring(0, i).TrimEnd('_', '-')
                        : n;
            }
            return n;
        }

        private void AddCreatureNodes(TreeNode folderNode, string folder)
        {
            var folderInfo = folderNode.Tag as TreeNodeInfo;
            if (folderInfo != null && folderInfo.Populated) return;

            // Clear any "..." placeholder before adding the real children.
            folderNode.Nodes.Clear();

            if (folderInfo != null && (folderInfo.IsOthersCategory || folderInfo.IsCustomMeshCategory))
            {
                try
                {
                    var modelFiles = new List<string>();
                    if (Directory.Exists(folder))
                    {
                        modelFiles.AddRange(Directory.GetFiles(folder, "*.char", SearchOption.AllDirectories));
                        modelFiles.AddRange(Directory.GetFiles(folder, "*.fbx", SearchOption.AllDirectories));
                        modelFiles.AddRange(Directory.GetFiles(folder, "*.obj", SearchOption.AllDirectories));
                    }
                    modelFiles.Sort(StringComparer.OrdinalIgnoreCase);

                    foreach (var f in modelFiles)
                    {
                        string name = Path.GetFileNameWithoutExtension(f);
                        var info = new TreeNodeInfo
                        {
                            IsFolder = false,
                            Folder = Path.GetDirectoryName(f),
                            CreatureKey = name,
                            MeshPath = f,
                            Display = name,
                            IsCustomMeshCategory = folderInfo.IsCustomMeshCategory,
                            IsOthersCategory = folderInfo.IsOthersCategory,
                            Populated = true
                        };
                        var node = new TreeNode(name) { Tag = info };
                        folderNode.Nodes.Add(node);
                    }
                }
                catch { }

                if (folderInfo != null) folderInfo.Populated = true;
                return;
            }

            var creatures = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            try
            {
                foreach (var f in Directory.GetFiles(folder, "*.grn"))
                {
                    string fileName = Path.GetFileNameWithoutExtension(f);
                    // Only real meshes live in the Models tree. Animations (.grn
                    // without an "LOD" token) are browsed from the Animations tree.
                    if (fileName.IndexOf("lod", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    string creature = DeriveCreature(fileName);
                    if (creature.Length == 0) creature = fileName;
                    if (!creatures.TryGetValue(creature, out var list))
                    {
                        list = new List<string>();
                        creatures[creature] = list;
                    }
                    list.Add(f);
                }
            }
            catch { }

            foreach (var kv in creatures)
            {
                kv.Value.Sort(StringComparer.OrdinalIgnoreCase);

                // Creature reference mesh: prefer the LOD/base mesh file.
                string mesh = kv.Value.Find(x => x.IndexOf("lod", StringComparison.OrdinalIgnoreCase) >= 0);
                if (mesh == null && kv.Value.Count > 0) mesh = kv.Value[0];

                var info = new TreeNodeInfo
                {
                    IsFolder = false,
                    Folder = folder,
                    CreatureKey = kv.Key,
                    MeshPath = mesh,
                    Display = kv.Key
                };
                var node = new TreeNode(kv.Key) { Tag = info };
                folderNode.Nodes.Add(node);
            }

            if (folderInfo != null) folderInfo.Populated = true;
        }

        private void AddAnimationLeafNodes(TreeNode creatureNode, TreeNodeInfo info)
        {
            if (info != null && info.Populated) return;

            creatureNode.Nodes.Clear();
            var leaves = new List<string>();

            try
            {
                foreach (var f in Directory.GetFiles(info.Folder, "*.grn"))
                {
                    string fileName = Path.GetFileNameWithoutExtension(f);
                    if (string.Equals(DeriveCreature(fileName), info.CreatureKey, StringComparison.OrdinalIgnoreCase))
                        leaves.Add(f);
                }
            }
            catch { }

            leaves.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var lf in leaves)
            {
                string shortName = Path.GetFileNameWithoutExtension(lf);
                var li = new TreeNodeInfo
                {
                    IsFolder = false,
                    Folder = info.Folder,
                    CreatureKey = info.CreatureKey,
                    LeafPath = lf,
                    Display = shortName
                };
                creatureNode.Nodes.Add(new TreeNode(shortName) { Tag = li });
            }

            if (info != null) info.Populated = true;
        }

        private void StartInitialLoad()
        {
            if (!_initialLoadPending) return;
            _initialLoadPending = false;

            if (tvModels.Nodes.Count == 0) return;

            // Expand the first category so its creatures become visible/selectable.
            tvModels.Nodes[0].Expand();
            TreeNode firstCreature = FindFirstCreature(tvModels.Nodes);
            if (firstCreature != null)
            {
                firstCreature.Expand();
                tvModels.SelectedNode = firstCreature;
                return;
            }

            TreeNode first = FindFirstLeaf(tvModels.Nodes);
            if (first != null)
                tvModels.SelectedNode = first;
        }

        private static TreeNode FindFirstCreature(TreeNodeCollection nodes)
        {
            foreach (TreeNode n in nodes)
            {
                if (n.Tag is TreeNodeInfo info && !info.IsFolder && info.LeafPath == null)
                    return n;
                TreeNode r = FindFirstCreature(n.Nodes);
                if (r != null) return r;
            }
            return null;
        }

        private static TreeNode FindFirstLeaf(TreeNodeCollection nodes)
        {
            foreach (TreeNode n in nodes)
            {
                if (n.Nodes.Count > 0)
                {
                    TreeNode r = FindFirstLeaf(n.Nodes);
                    if (r != null) return r;
                }
                if (n.Tag is TreeNodeInfo info && !info.IsFolder && info.LeafPath != null)
                    return n;
            }
            return null;
        }

        private bool _suppressSelect;

        private void TvModels_AfterSelect(object sender, TreeViewEventArgs e)
        {
            if (_suppressSelect || _populatingTree) return;

            var info = e.Node?.Tag as TreeNodeInfo;
            if (info == null) return;

            try
            {
                if (info.IsFolder)
                {
                    // Category selected: just expand it so its creatures appear.
                    // (No auto-jump to the first child: that re-entrant selection
                    //  was the main source of expand/collapse instability.)
                    if (!info.Populated)
                        AddCreatureNodes(e.Node, info.Folder);
                    if (!e.Node.IsExpanded)
                        e.Node.Expand();
                    return;
                }

                if (info.LeafPath != null)
                {
                    // Animation leaf selected directly: make sure the creature's
                    // base mesh is loaded (do NOT load the .grn anim as a model),
                    // then load the animation.
                    string leafPath = info.LeafPath;
                    var parentInfo = e.Node.Parent?.Tag as TreeNodeInfo;
                    if (_loadedMeshes.Count == 0 && parentInfo != null)
                        EnsureMeshLoaded(parentInfo.MeshPath);
                    LoadAnimation(leafPath);
                    return;
                }

                // Creature node selected: load the reference mesh and reveal the
                // matching category in the Animations tree.
                if (info.MeshPath != null)
                {
                    // Convenience: single-clicking an item from the Equipment
                    // category (the "Others" folder: E_*, Weapon_*, Shield_*,
                    // Staff_*, Wand_*, armor skins, ...) while a character is
                    // already in the scene layers it on the body instead of
                    // replacing the scene. Full NPC "Character_*" still replace.
                    // Layout: keeps the body's animation untouched.
                    bool isEquipCategory = !string.IsNullOrEmpty(info.Folder)
                        && (string.Equals(Path.GetFileName(info.Folder), "Others", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(Path.GetFileName(info.Folder), "Equipment", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(Path.GetFileName(info.Folder), "Meshs custom", StringComparison.OrdinalIgnoreCase)
                         || info.IsCustomMeshCategory
                         || info.IsOthersCategory);
                    bool looksCharacter = Path.GetFileName(info.MeshPath).StartsWith("Character_", StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileName(info.MeshPath).StartsWith("H_Male", StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileName(info.MeshPath).StartsWith("H_Female", StringComparison.OrdinalIgnoreCase);
                    bool isFullCharacter = looksCharacter;
                    if (!isFullCharacter && GrannyModelFile.IsModelFile(info.MeshPath))
                    {
                        var m = GrannyModelFile.Load(info.MeshPath);
                        if (m != null && m.Bones != null && m.Bones.Count >= 20)
                            isFullCharacter = true;
                    }

                    if (isEquipCategory && !isFullCharacter && _loadedMeshes.Count > 0)
                    {
                        AddToScene(info.MeshPath, MeshCutoutRole.Accessory);
                        return;
                    }
                    EnsureMeshLoaded(info.MeshPath);
                }
                _currentCreature = info;
                ExpandAnimCategoryFor(info);
            }
            catch (Exception ex)
            {
                Log($"AfterSelect error: {ex.Message}\n{ex}");
            }
        }

        private void TvModels_DoubleClick(object sender, EventArgs e)
        {
            try
            {
                var node = tvModels.SelectedNode;
                if (node == null || node.Tag is TreeNodeInfo == false) return;
                tvModels.BeginUpdate();
                try { node.Toggle(); }
                finally { tvModels.EndUpdate(); }
            }
            catch (Exception ex)
            {
                Log($"DoubleClick error: {ex.Message}\n{ex}");
            }
        }

        private void TvModels_NodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Button != MouseButtons.Right || e.Node == null) return;
            // Select the node the user right-clicked without triggering a heavy load.
            _suppressSelect = true;
            try { tvModels.SelectedNode = e.Node; }
            finally { _suppressSelect = false; }
        }

        private void TvMenu_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            tvMenu.Items.Clear();
            var node = tvModels.SelectedNode;
            var info = node?.Tag as TreeNodeInfo;

            // If right-clicked on empty area or no node selected
            if (node == null || info == null)
            {
                var applyMixamoGen = new ToolStripMenuItem("⚡ Convert/create rig to UO (Rig Studio)...", null, (s, a) =>
                {
                    ApplyMixamoForUo(null);
                });
                applyMixamoGen.Font = new Font(tvMenu.Font, FontStyle.Bold);
                tvMenu.Items.Add(applyMixamoGen);

                tvMenu.Items.Add(Localization.Tr("menu_import_mixamo", "Importer un Character Mixamo (FBX)..."), null, BtnImportModel_Click);
                tvMenu.Items.Add(Localization.Tr("menu_import_mesh", "Importer un mesh (FBX / OBJ)..."), null, BtnImportMesh_Click);
                tvMenu.Items.Add(Localization.Tr("menu_import_anim", "Importer une animation FBX Mixamo..."), null, BtnImportAnim_Click);
                tvMenu.Items.Add(new ToolStripSeparator());
                tvMenu.Items.Add(Localization.Tr("menu_refresh_models", "Rafraîchir la liste des modèles"), null, (s, a) => RestoreModelsTree());
                return;
            }

            if (info.IsFolder)
            {
                tvMenu.Items.Add(node.IsExpanded ? Localization.Tr("menu_collapse", "Réduire") : Localization.Tr("menu_expand", "Développer"), null, (s, a) =>
                {
                    if (!info.Populated) AddCreatureNodes(node, info.Folder);
                    node.Toggle();
                });

                tvMenu.Items.Add(new ToolStripSeparator());
                var applyMixamoFolder = new ToolStripMenuItem(Localization.Tr("menu_auto_rig", "⚡ Convert/create rig to UO (Rig Studio)..."), null, (s, a) =>
                {
                    ApplyMixamoForUo(info.Folder);
                });
                applyMixamoFolder.Font = new Font(tvMenu.Font, FontStyle.Bold);
                tvMenu.Items.Add(applyMixamoFolder);
            }
            else if (info.LeafPath != null)
            {
                tvMenu.Items.Add(Localization.Tr("menu_play_anim", "Lire l'animation"), null, (s, a) =>
                {
                    var parentInfo = node.Parent?.Tag as TreeNodeInfo;
                    EnsureMeshLoaded(parentInfo?.MeshPath);
                    LoadAnimation(info.LeafPath);
                });
                tvMenu.Items.Add(Localization.Tr("menu_load_creature_model", "Charger le modèle de la créature"), null, (s, a) =>
                {
                    var parentInfo = node.Parent?.Tag as TreeNodeInfo;
                    if (parentInfo?.MeshPath != null) LoadModel(parentInfo.MeshPath);
                });

                tvMenu.Items.Add(new ToolStripSeparator());
                var applyMixamoLeaf = new ToolStripMenuItem(Localization.Tr("menu_auto_rig", "⚡ Convert/create rig to UO (Rig Studio)..."), null, (s, a) =>
                {
                    var parentInfo = node.Parent?.Tag as TreeNodeInfo;
                    ApplyMixamoForUo(parentInfo?.MeshPath);
                });
                applyMixamoLeaf.Font = new Font(tvMenu.Font, FontStyle.Bold);
                tvMenu.Items.Add(applyMixamoLeaf);
            }
            else
            {
                tvMenu.Items.Add(Localization.Tr("menu_load_replace_scene", "Charger (remplacer la scène)"), null, (s, a) =>
                {
                    if (info.MeshPath != null) LoadModel(info.MeshPath);
                });
                tvMenu.Items.Add(Localization.Tr("menu_add_to_scene", "Ajouter à la scène (équipement)"), null, (s, a) =>
                {
                    if (info.MeshPath != null) AddToScene(info.MeshPath);
                });

                tvMenu.Items.Add(new ToolStripSeparator());
                var applyMixamoModel = new ToolStripMenuItem(Localization.Tr("menu_auto_rig", "⚡ Convert/create rig to UO (Rig Studio)..."), null, (s, a) =>
                {
                    ApplyMixamoForUo(info.MeshPath);
                });
                applyMixamoModel.Font = new Font(tvMenu.Font, FontStyle.Bold);
                tvMenu.Items.Add(applyMixamoModel);
            }

            bool isCustomChar = !string.IsNullOrEmpty(info.MeshPath) &&
                (info.MeshPath.EndsWith(".char", StringComparison.OrdinalIgnoreCase) ||
                 info.MeshPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) ||
                 info.MeshPath.EndsWith(".obj", StringComparison.OrdinalIgnoreCase) ||
                 (node.Parent?.Tag as TreeNodeInfo)?.IsOthersCategory == true ||
                 (node.Parent?.Tag as TreeNodeInfo)?.IsCustomMeshCategory == true);

            if (isCustomChar)
            {
                tvMenu.Items.Add(new ToolStripSeparator());

                tvMenu.Items.Add(Localization.Tr("menu_manage_textures", "Assigner / Modifier les textures..."), null, (s, a) =>
                {
                    OpenTextureManagerForModel(info.MeshPath);
                });

                tvMenu.Items.Add(Localization.Tr("menu_rename", "Renommer..."), null, (s, a) =>
                {
                    RenameCustomModel(node, info);
                });

                bool isCustomMeshCat = (node.Parent?.Tag as TreeNodeInfo)?.IsCustomMeshCategory == true || info.IsCustomMeshCategory;
                bool isOthersCat = (node.Parent?.Tag as TreeNodeInfo)?.IsOthersCategory == true || info.IsOthersCategory;

                if (isOthersCat)
                {
                    tvMenu.Items.Add(Localization.Tr("menu_move_to_custom", "Déplacer vers 'Meshs custom'"), null, (s, a) =>
                    {
                        MoveCustomModel(node, info, Path.Combine(BasePath, "Meshs custom"), "Meshs custom");
                    });
                }
                else if (isCustomMeshCat)
                {
                    tvMenu.Items.Add(Localization.Tr("menu_move_to_others", "Déplacer vers 'Others'"), null, (s, a) =>
                    {
                        MoveCustomModel(node, info, Path.Combine(BasePath, "Others"), "Others");
                    });
                }

                if (!string.IsNullOrEmpty(info.MeshPath) && info.MeshPath.EndsWith(GrannyModelFile.Extension, StringComparison.OrdinalIgnoreCase))
                {
                    tvMenu.Items.Add(Localization.Tr("menu_extract_parts", "Extraire des pièces vers 'Meshs custom'..."), null, (s, a) =>
                    {
                        OpenExtractPartsDialog(info.MeshPath);
                    });
                }

                string delLabel = isCustomMeshCat
                    ? Localization.Tr("menu_del_mesh", "Supprimer ce mesh")
                    : Localization.Tr("menu_del_char", "Supprimer ce character");
                var deleteItem = new ToolStripMenuItem(delLabel, null, (s, a) =>
                {
                    DeleteCustomCharacter(node, info);
                });
                deleteItem.ForeColor = Color.Crimson;
                tvMenu.Items.Add(deleteItem);
            }

            tvMenu.Items.Add(new ToolStripSeparator());
            tvMenu.Items.Add(Localization.Tr("menu_remove_last_mesh", "Retirer le dernier mesh"), null, (s, a) =>
                RemoveMeshAt(_loadedMeshes.Count - 1));
            tvMenu.Items.Add(Localization.Tr("menu_clear_scene", "Vider la scène"), null, (s, a) => ClearScene());
        }

        private void ApplyMixamoForUo(string pathOrFolder)
        {
            try
            {
                string sourceFile = null;
                string outFolder = null;

                if (!string.IsNullOrEmpty(pathOrFolder))
                {
                    if (Directory.Exists(pathOrFolder))
                    {
                        outFolder = pathOrFolder;
                    }
                    else if (File.Exists(pathOrFolder))
                    {
                        sourceFile = pathOrFolder;
                        outFolder = Path.GetDirectoryName(pathOrFolder);
                    }
                }

                // If no file found directly from pathOrFolder, check selected node in treeview
                if (string.IsNullOrEmpty(sourceFile) && tvModels.SelectedNode?.Tag is TreeNodeInfo selInfo)
                {
                    if (!string.IsNullOrEmpty(selInfo.MeshPath) && File.Exists(selInfo.MeshPath))
                    {
                        sourceFile = selInfo.MeshPath;
                        if (string.IsNullOrEmpty(outFolder)) outFolder = Path.GetDirectoryName(sourceFile);
                    }
                }

                // If still no file, check _currentModelPath
                if (string.IsNullOrEmpty(sourceFile) && !string.IsNullOrEmpty(_currentModelPath) && File.Exists(_currentModelPath))
                {
                    sourceFile = _currentModelPath;
                    if (string.IsNullOrEmpty(outFolder)) outFolder = Path.GetDirectoryName(sourceFile);
                }

                // If still no file, check loaded meshes in active preview
                if (string.IsNullOrEmpty(sourceFile) && _loadedMeshes != null && _loadedMeshes.Count > 0)
                {
                    foreach (var m in _loadedMeshes)
                    {
                        if (!string.IsNullOrEmpty(m.SourcePath) && File.Exists(m.SourcePath))
                        {
                            sourceFile = m.SourcePath;
                            if (string.IsNullOrEmpty(outFolder)) outFolder = Path.GetDirectoryName(sourceFile);
                            break;
                        }
                    }
                }

                string othersFolder = Path.Combine(BasePath, "Others");
                if (!Directory.Exists(othersFolder)) Directory.CreateDirectory(othersFolder);
                // Tout personnage ou rig (.char) doit être enregistré automatiquement dans "Others"
                outFolder = othersFolder;

                // Pass BOTH sourceFile AND active _loadedMeshes, as well as current active animation, so AutoRigDialog has full context
                using var dlg = new AutoRigDialog(sourceFile, BasePath, outFolder, _loadedMeshes, _currentAnimation, _currentAnimPath);
                if (dlg.ShowDialog(this) == DialogResult.OK && !string.IsNullOrEmpty(dlg.GeneratedCharPath))
                {
                    string charPath = dlg.GeneratedCharPath;
                    LoadCustomCharacter(charPath);
                    RestoreModelsTree();
                    RevealModelFile(charPath);
                }
            }
            catch (Exception ex)
            {
                Log($"Auto-Rig Studio error: {ex.Message}\n{ex}");
                MessageBox.Show("Erreur dans Auto-Rig Studio : " + ex.Message, "Convert/create rig to UO", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void RenameCustomModel(TreeNode node, TreeNodeInfo info)
        {
            if (info == null || string.IsNullOrEmpty(info.MeshPath) || !File.Exists(info.MeshPath)) return;

            string oldName = !string.IsNullOrEmpty(node.Text) ? node.Text : Path.GetFileNameWithoutExtension(info.MeshPath);
            using var dlg = new Form
            {
                Text = "Renommer le modèle",
                Size = new Size(380, 160),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.Gainsboro
            };

            var lbl = new Label { Text = "Nouveau nom :", Location = new Point(16, 16), AutoSize = true, ForeColor = Color.Gainsboro };
            var txt = new TextBox
            {
                Text = oldName,
                Location = new Point(16, 40),
                Width = 330,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle
            };
            txt.SelectAll();

            var btnOk = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Location = new Point(170, 80),
                Width = 80,
                Height = 28,
                BackColor = Color.FromArgb(48, 48, 48),
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat
            };
            var btnCancel = new Button
            {
                Text = "Annuler",
                DialogResult = DialogResult.Cancel,
                Location = new Point(266, 80),
                Width = 80,
                Height = 28,
                BackColor = Color.FromArgb(48, 48, 48),
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat
            };
            dlg.Controls.AddRange(new Control[] { lbl, txt, btnOk, btnCancel });
            dlg.AcceptButton = btnOk;
            dlg.CancelButton = btnCancel;

            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            string newName = MakeSafeFilename(txt.Text.Trim());
            if (string.IsNullOrEmpty(newName) || string.Equals(newName, oldName, StringComparison.OrdinalIgnoreCase)) return;

            string folder = Path.GetDirectoryName(info.MeshPath) ?? string.Empty;
            string ext = Path.GetExtension(info.MeshPath);
            string newPath = Path.Combine(folder, newName + ext);

            if (File.Exists(newPath))
            {
                MessageBox.Show($"Un fichier nommé '{newName}{ext}' existe déjà dans ce dossier.", "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                // If it's a .char model file, update internal Name and texture names
                if (info.MeshPath.EndsWith(GrannyModelFile.Extension, StringComparison.OrdinalIgnoreCase))
                {
                    var model = GrannyModelFile.Load(info.MeshPath);
                    if (model != null)
                    {
                        model.Name = newName;
                        for (int i = 0; i < model.Parts.Count; i++)
                        {
                            var part = model.Parts[i];
                            if (!string.IsNullOrEmpty(part.TextureName) && part.TextureName.StartsWith(oldName + "_", StringComparison.OrdinalIgnoreCase))
                            {
                                string oldTexFile = Path.Combine(folder, part.TextureName);
                                string newTexName = newName + "_" + part.TextureName.Substring(oldName.Length + 1);
                                string newTexFile = Path.Combine(folder, newTexName);
                                if (File.Exists(oldTexFile) && !File.Exists(newTexFile))
                                {
                                    try { File.Move(oldTexFile, newTexFile); } catch { }
                                }
                                part.TextureName = newTexName;
                            }
                        }
                        GrannyModelFile.Save(model, newPath);
                        try { File.Delete(info.MeshPath); } catch { }
                    }
                    else
                    {
                        File.Move(info.MeshPath, newPath);
                    }
                }
                else
                {
                    File.Move(info.MeshPath, newPath);
                }

                // Rename any other associated textures starting with oldName_
                string prefix = oldName + "_";
                foreach (var f in Directory.GetFiles(folder, prefix + "*.*"))
                {
                    try
                    {
                        string fn = Path.GetFileName(f);
                        string newFn = newName + "_" + fn.Substring(prefix.Length);
                        string target = Path.Combine(folder, newFn);
                        if (!File.Exists(target)) File.Move(f, target);
                    }
                    catch { }
                }

                // Update currently loaded path if applicable
                if (string.Equals(_currentModelPath, info.MeshPath, StringComparison.OrdinalIgnoreCase))
                {
                    _currentModelPath = newPath;
                }

                info.MeshPath = newPath;
                info.CreatureKey = newName;
                info.Display = newName;
                node.Text = newName;

                Log($"Modèle renommé : '{oldName}' -> '{newName}'");
            }
            catch (Exception ex)
            {
                Log($"Erreur renommage modèle: {ex.Message}");
                MessageBox.Show("Erreur lors du renommage : " + ex.Message);
            }
        }

        private void MoveCustomModel(TreeNode node, TreeNodeInfo info, string targetDir, string targetCatName)
        {
            if (info == null || string.IsNullOrEmpty(info.MeshPath) || !File.Exists(info.MeshPath)) return;
            if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);

            string oldFile = info.MeshPath;
            string fileName = Path.GetFileName(oldFile);
            string targetFile = Path.Combine(targetDir, fileName);

            if (File.Exists(targetFile))
            {
                var res = MessageBox.Show(
                    $"Un fichier '{fileName}' existe déjà dans '{targetCatName}'. Voulez-vous le remplacer ?",
                    "Remplacer le fichier",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (res != DialogResult.Yes) return;
                try { File.Delete(targetFile); } catch { }
            }

            try
            {
                // Move model file
                File.Move(oldFile, targetFile);

                // Move all associated textures (prefix + *.*)
                string srcFolder = Path.GetDirectoryName(oldFile) ?? string.Empty;
                string prefix = Path.GetFileNameWithoutExtension(oldFile) + "_";
                foreach (var f in Directory.GetFiles(srcFolder, prefix + "*.*"))
                {
                    string tPath = Path.Combine(targetDir, Path.GetFileName(f));
                    try
                    {
                        if (File.Exists(tPath)) File.Delete(tPath);
                        File.Move(f, tPath);
                    }
                    catch { }
                }

                // If the model was loaded, update current path
                if (string.Equals(_currentModelPath, oldFile, StringComparison.OrdinalIgnoreCase))
                {
                    _currentModelPath = targetFile;
                }

                Log($"Modèle déplacé vers '{targetCatName}' : {fileName}");

                RestoreModelsTree();
                RevealModelFile(targetFile);
            }
            catch (Exception ex)
            {
                Log($"Erreur déplacement modèle: {ex.Message}");
                MessageBox.Show("Erreur lors du déplacement : " + ex.Message);
            }
        }

        private void OpenExtractPartsDialog(string charPath)
        {
            if (string.IsNullOrEmpty(charPath) || !File.Exists(charPath)) return;
            try
            {
                var model = GrannyModelFile.Load(charPath);
                if (model == null || model.Parts.Count == 0)
                {
                    MessageBox.Show("Impossible de charger les données du modèle.", "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                if (model.Parts.Count <= 1)
                {
                    MessageBox.Show(
                        "Ce modèle ne contient qu'une seule pièce.\nPour déplacer le modèle entier, faites un clic droit dessus et choisissez 'Déplacer vers Meshs custom'.",
                        "Extraction impossible",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                using var dlg = new Form
                {
                    Text = $"Extraire des pièces vers 'Meshs custom' - {model.Name}",
                    Size = new Size(720, 480),
                    StartPosition = FormStartPosition.CenterParent,
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    MaximizeBox = false,
                    MinimizeBox = false,
                    BackColor = Color.FromArgb(36, 36, 36),
                    ForeColor = Color.Gainsboro
                };

                var pnlTop = new Panel { Dock = DockStyle.Top, Height = 45, Padding = new Padding(12, 10, 12, 0) };
                var lblIntro = new Label
                {
                    Text = "Cochez les pièces (armes, équipements, accessoires...) à extraire vers la catégorie 'Meshs custom' :",
                    Dock = DockStyle.Fill,
                    ForeColor = Color.Gainsboro,
                    Font = new Font("Segoe UI", 9.5f, FontStyle.Regular)
                };
                pnlTop.Controls.Add(lblIntro);

                var dgv = new DataGridView
                {
                    Dock = DockStyle.Fill,
                    BackgroundColor = Color.FromArgb(28, 28, 28),
                    GridColor = Color.FromArgb(50, 50, 50),
                    BorderStyle = BorderStyle.None,
                    AutoGenerateColumns = false,
                    AllowUserToAddRows = false,
                    AllowUserToDeleteRows = false,
                    SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                    RowHeadersVisible = false,
                    EnableHeadersVisualStyles = false,
                    Font = new Font("Segoe UI", 9f)
                };

                dgv.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(45, 45, 45);
                dgv.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
                dgv.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
                dgv.ColumnHeadersHeight = 30;

                dgv.DefaultCellStyle.BackColor = Color.FromArgb(32, 32, 32);
                dgv.DefaultCellStyle.ForeColor = Color.Gainsboro;
                dgv.DefaultCellStyle.SelectionBackColor = Color.FromArgb(60, 90, 140);
                dgv.DefaultCellStyle.SelectionForeColor = Color.White;

                var colCheck = new DataGridViewCheckBoxColumn { HeaderText = "Extraire", Width = 65 };
                var colIndex = new DataGridViewTextBoxColumn { HeaderText = "#", Width = 35, ReadOnly = true };
                var colPartName = new DataGridViewTextBoxColumn { HeaderText = "Pièce originale", Width = 130, ReadOnly = true };
                var colMat = new DataGridViewTextBoxColumn { HeaderText = "Matériau", Width = 110, ReadOnly = true };
                var colVerts = new DataGridViewTextBoxColumn { HeaderText = "Sommets", Width = 75, ReadOnly = true };
                var colNewName = new DataGridViewTextBoxColumn { HeaderText = "Nouveau nom dans 'Meshs custom'", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill };

                dgv.Columns.AddRange(colCheck, colIndex, colPartName, colMat, colVerts, colNewName);

                for (int i = 0; i < model.Parts.Count; i++)
                {
                    var p = model.Parts[i];
                    string pName = !string.IsNullOrEmpty(p.Name) ? p.Name : $"Mesh_{i + 1}";
                    string candidateName = pName;

                    if (pName.StartsWith("Object", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!string.IsNullOrEmpty(p.TextureName))
                        {
                            string texLower = p.TextureName.ToLowerInvariant();
                            if (texLower.Contains("crossbow") || texLower.Contains("bow")) candidateName = $"{model.Name}_CrossBow";
                            else if (texLower.Contains("spear") || texLower.Contains("lance")) candidateName = $"{model.Name}_Spear";
                            else if (texLower.Contains("sword") || texLower.Contains("weapon")) candidateName = $"{model.Name}_Weapon";
                            else if (texLower.Contains("shield")) candidateName = $"{model.Name}_Shield";
                            else candidateName = $"{model.Name}_{pName}";
                        }
                        else
                        {
                            candidateName = $"{model.Name}_{pName}";
                        }
                    }

                    bool preCheck = (i < model.Parts.Count - 1 && p.Vertices.Length < 5000);
                    dgv.Rows.Add(preCheck, i + 1, pName, p.MaterialName, p.Vertices.Length, candidateName);
                }

                var pnlBottom = new Panel { Dock = DockStyle.Bottom, Height = 80, Padding = new Padding(12, 8, 12, 10), BackColor = Color.FromArgb(30, 30, 30) };

                var chkRemove = new CheckBox
                {
                    Text = "Retirer les pièces extraites de ce modèle (ne garder que le character dans Others)",
                    Checked = true,
                    ForeColor = Color.Gold,
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                    Location = new Point(12, 10),
                    AutoSize = true
                };

                var btnExtract = new Button
                {
                    Text = "Extraire vers 'Meshs custom'",
                    DialogResult = DialogResult.OK,
                    Size = new Size(210, 32),
                    Location = new Point(350, 38),
                    BackColor = Color.FromArgb(35, 120, 75),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 9f, FontStyle.Bold)
                };

                var btnCancel = new Button
                {
                    Text = "Annuler",
                    DialogResult = DialogResult.Cancel,
                    Size = new Size(110, 32),
                    Location = new Point(570, 38),
                    BackColor = Color.FromArgb(50, 50, 50),
                    ForeColor = Color.Gainsboro,
                    FlatStyle = FlatStyle.Flat
                };

                pnlBottom.Controls.AddRange(new Control[] { chkRemove, btnExtract, btnCancel });
                dlg.Controls.Add(dgv);
                dlg.Controls.Add(pnlTop);
                dlg.Controls.Add(pnlBottom);
                dlg.AcceptButton = btnExtract;
                dlg.CancelButton = btnCancel;

                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                var checkedIndices = new List<int>();
                var targetNames = new Dictionary<int, string>();
                for (int r = 0; r < dgv.Rows.Count; r++)
                {
                    bool isChecked = Convert.ToBoolean(dgv.Rows[r].Cells[0].Value);
                    if (isChecked)
                    {
                        checkedIndices.Add(r);
                        string customName = MakeSafeFilename(dgv.Rows[r].Cells[5].Value?.ToString()?.Trim() ?? "");
                        if (string.IsNullOrEmpty(customName)) customName = $"{model.Name}_Part_{r + 1}";
                        targetNames[r] = customName;
                    }
                }

                if (checkedIndices.Count == 0)
                {
                    MessageBox.Show("Aucune pièce sélectionnée pour l'extraction.", "Information", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                if (checkedIndices.Count >= model.Parts.Count && chkRemove.Checked)
                {
                    MessageBox.Show("Vous ne pouvez pas retirer toutes les pièces du modèle (le modèle n'aurait plus aucun mesh). Laissez au moins une pièce ou décochez l'option de retrait.", "Attention", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                string targetDir = Path.Combine(BasePath, "Meshs custom");
                if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);
                string srcFolder = Path.GetDirectoryName(charPath) ?? string.Empty;

                int extractedCount = 0;
                foreach (int idx in checkedIndices)
                {
                    var part = model.Parts[idx];
                    string targetName = targetNames[idx];
                    string outCharPath = Path.Combine(targetDir, targetName + GrannyModelFile.Extension);

                    var pieceModel = new GrannyModelFile
                    {
                        Name = targetName,
                        Parts = new List<GrannyModelPart> { part },
                        Bones = model.Bones
                    };

                    if (!string.IsNullOrEmpty(part.TextureName))
                    {
                        string srcTex = Path.Combine(srcFolder, part.TextureName);
                        if (File.Exists(srcTex))
                        {
                            string ext = Path.GetExtension(part.TextureName);
                            string newTexName = $"{targetName}_{Path.GetFileNameWithoutExtension(part.TextureName)}{ext}";
                            string dstTex = Path.Combine(targetDir, newTexName);
                            try
                            {
                                File.Copy(srcTex, dstTex, true);
                                part.TextureName = newTexName;
                            }
                            catch { }
                        }
                    }

                    if (GrannyModelFile.Save(pieceModel, outCharPath))
                    {
                        extractedCount++;
                        Log($"Pièce extraite vers 'Meshs custom' : {Path.GetFileName(outCharPath)}");
                    }
                }

                if (chkRemove.Checked)
                {
                    checkedIndices.Sort((a, b) => b.CompareTo(a));
                    foreach (int idx in checkedIndices)
                    {
                        model.Parts.RemoveAt(idx);
                    }

                    GrannyModelFile.Save(model, charPath);
                    Log($"Pièces retirées de {Path.GetFileName(charPath)} (restant : {model.Parts.Count} mesh(es))");
                }

                RestoreModelsTree();

                if (string.Equals(_currentModelPath, charPath, StringComparison.OrdinalIgnoreCase))
                {
                    LoadModel(charPath);
                }

                MessageBox.Show(
                    $"{extractedCount} pièce(s) ont été extraites avec succès dans la catégorie 'Meshs custom' !" +
                    (chkRemove.Checked ? $"\nLe modèle original ne contient plus que {model.Parts.Count} mesh(es)." : ""),
                    "Extraction réussie",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log($"Erreur extraction pièces: {ex.Message}\n{ex}");
                MessageBox.Show("Erreur lors de l'extraction : " + ex.Message);
            }
        }

        private void DeleteCustomCharacter(TreeNode node, TreeNodeInfo info)
        {
            if (info == null || string.IsNullOrEmpty(info.MeshPath)) return;

            string charName = !string.IsNullOrEmpty(node.Text) ? node.Text : Path.GetFileNameWithoutExtension(info.MeshPath);
            var res = MessageBox.Show(
                $"Voulez-vous vraiment supprimer le character '{charName}' et ses textures associées ?",
                "Supprimer le character",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (res != DialogResult.Yes) return;

            try
            {
                if (string.Equals(_currentModelPath, info.MeshPath, StringComparison.OrdinalIgnoreCase))
                {
                    ClearScene();
                }

                if (File.Exists(info.MeshPath))
                {
                    File.Delete(info.MeshPath);
                }

                string folder = Path.GetDirectoryName(info.MeshPath) ?? string.Empty;
                if (Directory.Exists(folder))
                {
                    string prefix = Path.GetFileNameWithoutExtension(info.MeshPath) + "_";
                    foreach (var f in Directory.GetFiles(folder, prefix + "*.*"))
                    {
                        try { File.Delete(f); } catch { }
                    }
                }

                node.Remove();
                Log($"Character supprimé : {charName} ({info.MeshPath})");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erreur lors de la suppression du character : {ex.Message}", "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Log($"Erreur suppression character: {ex.Message}");
            }
        }


        // Append every mesh of a .grn file to the current scene (used to layer
        // equipment/extra pieces onto an already-loaded character). The base
        // model, camera and reference bounds are left untouched so the added
        // meshes stay aligned with the body. role controls the cutout layering
        // (equipment added here defaults to Accessory = an occluder).
        private void AddToScene(string path, MeshCutoutRole role = MeshCutoutRole.Accessory)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            if (_sceneExtras.Contains(path)) { Log($"AddToScene: {Path.GetFileName(path)} already in scene"); return; }
            if (!_glReady || glControl == null || !glControl.IsHandleCreated)
            {
                Log("AddToScene aborted: GL not ready");
                return;
            }

            glControl.MakeCurrent();

            if (GrannyModelFile.IsModelFile(path))
            {
                var model = GrannyModelFile.Load(path);
                if (model == null)
                {
                    Log($"AddToScene parse failed for .char: {path}");
                    return;
                }

                int addedChar = 0;
                for (int pIdx = 0; pIdx < model.Parts.Count; pIdx++)
                {
                    var part = model.Parts[pIdx];
                    if (part.Vertices.Length == 0 || part.Indices.Length == 0) continue;

                    var gmesh = new GrannyMesh();
                    gmesh.Vertices.AddRange(part.Vertices);
                    gmesh.Indices.AddRange(part.Indices);
                    if (part.UVs != null) gmesh.UVs.AddRange(part.UVs);
                    if (part.Skin != null) gmesh.Skin.AddRange(part.Skin);

                    string attachBone = !string.IsNullOrEmpty(part.AttachBoneName) ? part.AttachBoneName : null;
                    if (string.IsNullOrEmpty(attachBone) && role == MeshCutoutRole.Accessory && (model.Bones == null || model.Bones.Count < 5))
                    {
                        string fn = Path.GetFileName(path).ToLowerInvariant();
                        bool isLeftHand = fn.Contains("shield") || fn.Contains("buckler");
                        string[] prefs = isLeftHand
                            ? new[] { "CP_Grasp_Lhand", "Bip01 L Hand" }
                            : new[] { "CP_Grasp_Rhand", "Bip01 R Hand" };

                        foreach (var host in _loadedMeshes)
                        {
                            if (host.Bones != null && host.Bones.Count >= 20)
                            {
                                foreach (var pref in prefs)
                                {
                                    if (host.Bones.Exists(b => string.Equals(b.Name, pref, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        attachBone = pref;
                                        break;
                                    }
                                }
                                if (attachBone != null) break;
                            }
                        }
                    }

                    var lm = new LoadedMesh
                    {
                        PartIndex = pIdx,
                        Name = string.IsNullOrEmpty(part.Name) ? Path.GetFileNameWithoutExtension(path) : part.Name,
                        SourcePath = path,
                        SourceMesh = gmesh,
                        Bones = new List<GrannyBone>(model.Bones),
                        BaseVertices = part.Vertices,
                        Indices = part.Indices,
                        Skin = (part.Skin != null && part.Skin.Length > 0) ? part.Skin : null,
                        MeshColor = part.Color,
                        CutoutRole = !string.IsNullOrEmpty(attachBone) ? MeshCutoutRole.Accessory : (part.CutoutRole != 0 ? part.CutoutRole : role),
                        AttachBoneName = attachBone,
                        AttachInBindSpace = part.AttachInBindSpace
                    };

                    BuildCornerData(lm);
                    SetupMeshVBO(lm);
                    lm.TextureId = !string.IsNullOrEmpty(part.TextureName) ? TryLoadTexture(path, part.TextureName, out lm.TextureFilePath) : -1;
                    lm.HasTexture = lm.TextureId > 0;
                    lm.BaseTextureId = lm.TextureId;
                    lm.BaseMeshColor = lm.MeshColor;
                    _loadedMeshes.Add(lm);
                    addedChar++;
                }

                if (addedChar == 0)
                {
                    Log($"AddToScene: no meshes in {path}");
                    return;
                }

                RefreshLoadedList();
                PopulateBoneCombo();

                string attachToSelect = null;
                for (int i = _loadedMeshes.Count - addedChar; i < _loadedMeshes.Count; i++)
                {
                    if (!string.IsNullOrEmpty(_loadedMeshes[i].AttachBoneName))
                    {
                        attachToSelect = _loadedMeshes[i].AttachBoneName;
                        break;
                    }
                }
                SelectBoneByName(attachToSelect);

                if (_currentAnimation != null)
                {
                    EnsureAnimationBindings(_currentAnimation);
                }
                ApplyAnimationPose(_currentTime);
                RefreshVertexBuffers(true);

                _sceneExtras.Add(path);
                UpdateModelInfo();
                glControl.Invalidate();
                Log($"AddToScene done: +{addedChar} meshes, total={_loadedMeshes.Count} ({Path.GetFileName(path)})");
                return;
            }

            var parser = new GrannyFormatParser();
            if (!parser.Load(path))
            {
                Log($"AddToScene parse failed: {path}");
                return;
            }

            int added = 0;
            foreach (var mesh in parser.Meshes)
            {
                if (mesh.Vertices.Count == 0 || mesh.Indices.Count == 0) continue;

                bool isClothingOrBody = IsClothingOrSkinnedGrn(path, parser, mesh);
                string attachBone = null;
                if (!isClothingOrBody && role == MeshCutoutRole.Accessory)
                {
                    string fn = Path.GetFileName(path).ToLowerInvariant();
                    bool isLeftHand = fn.Contains("shield") || fn.Contains("buckler");
                    string[] prefs = isLeftHand
                        ? new[] { "CP_Grasp_Lhand", "Bip01 L Hand" }
                        : new[] { "CP_Grasp_Rhand", "Bip01 R Hand" };

                    foreach (var host in _loadedMeshes)
                    {
                        if (host.Bones != null && host.Bones.Count >= 20)
                        {
                            foreach (var pref in prefs)
                            {
                                if (host.Bones.Exists(b => string.Equals(b.Name, pref, StringComparison.OrdinalIgnoreCase)))
                                {
                                    attachBone = pref;
                                    break;
                                }
                            }
                            if (attachBone != null) break;
                        }
                    }
                }

                var lm = new LoadedMesh
                {
                    Name = $"{Path.GetFileName(path)} ({_loadedMeshes.Count})",
                    SourcePath = path,
                    SourceMesh = mesh,
                    Bones = new List<GrannyBone>(parser.Bones),
                    BaseVertices = mesh.Vertices.ToArray(),
                    Indices = mesh.Indices.ToArray(),
                    Skin = mesh.Skin.Count > 0 ? mesh.Skin.ToArray() : null,
                    CutoutRole = isClothingOrBody ? MeshCutoutRole.Accessory : role,
                    AttachBoneName = attachBone,
                    AttachInBindSpace = false
                };

                BuildCornerData(lm);
                SetupMeshVBO(lm);
                lm.TextureId = TryLoadTexture(path, parser.TextureName, out lm.TextureFilePath);
                lm.HasTexture = lm.TextureId > 0;
                lm.BaseTextureId = lm.TextureId;
                lm.BaseMeshColor = lm.MeshColor;
                _loadedMeshes.Add(lm);
                added++;
            }

            if (added == 0)
            {
                Log($"AddToScene: no meshes in {path}");
                return;
            }

            RefreshLoadedList();
            PopulateBoneCombo();
            if (added > 0 && !string.IsNullOrEmpty(_loadedMeshes[_loadedMeshes.Count - 1].AttachBoneName))
                SelectBoneByName(_loadedMeshes[_loadedMeshes.Count - 1].AttachBoneName);
            else
                SelectBoneByName(null);

            if (_currentAnimation != null)
            {
                EnsureAnimationBindings(_currentAnimation);
            }
            ApplyAnimationPose(_currentTime);
            RefreshVertexBuffers(true);

            _sceneExtras.Add(path);
            UpdateModelInfo();
            glControl.Invalidate();
            Log($"AddToScene done: +{added} meshes, total={_loadedMeshes.Count} ({Path.GetFileName(path)})");
        }

        private void ClearScene()
        {
            if (glControl == null || !glControl.IsHandleCreated) return;
            glControl.MakeCurrent();
            foreach (var lm in _loadedMeshes)
                DestroyMesh(lm);
            _loadedMeshes.Clear();
            _sceneExtras.Clear();
            ResetCharMenu();
            RefreshLoadedList();
            PopulateBoneCombo();
            _currentModelPath = null;
            UpdateModelInfo();
            glControl.Invalidate();
            Log("ClearScene done");
        }

        // ==================== MULTIPART CHARACTER ====================
        // H_Male/H_Female are skeletons + a placeholder box; the visible body is an
        // assembly of part files (Torso/Head/Face/... in "Others"), each a small
        // skinned mesh authored in the shared bind space and bound by bone name.

        // Does this .grn follow the H_Male/H_Female placeholder pattern?
        private static bool IsHumanPlaceholder(string path, out bool female)
        {
            female = false;
            string name = Path.GetFileNameWithoutExtension(path);
            if (name.Equals("H_Male_Lod2", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Equals("H_Female_Lod2", StringComparison.OrdinalIgnoreCase)) { female = true; return true; }
            return false;
        }

        private void ResetCharMenu()
        {
            _charGender = 0;
            _skinOverride = null;
            if (cbCharFace == null) return;
            _suppressChar = true;
            try
            {
                foreach (var cb in new[] { cbCharFace, cbCharHair, cbCharBeard, cbCharTorso, cbCharSkin })
                {
                    cb.Items.Clear();
                    cb.Enabled = false;
                }
            }
            finally { _suppressChar = false; }
            UpdateCharInfo();
        }

        // Load the base body parts for a human (one mesh per slot). The placeholder
        // box was skipped by the caller, so this is the whole visible body.
        private void AssembleHumanBody(bool female)
        {
            _charGender = female ? 2 : 1;
            string folder = Path.Combine(BasePath, "Others");
            string prefix = female ? "H_Female_" : "H_Male_";

            var parts = new (string slot, string file)[]
            {
                ("torso", prefix + "Torso_V2_LOD2.grn"),
                ("torsoupper", prefix + "Torso_Upper_V2_LOD2.grn"),
                ("neck", prefix + "Neck_V2_LOD2.grn"),
                ("head", prefix + "Head_V2_LOD2.grn"),
                ("ears", prefix + "Ears_V2_LOD2.grn"),
                ("uarms", prefix + "UArms_V2_LOD2.grn"),
                ("farms", prefix + "FArms_V2_LOD2.grn"),
                ("hands", prefix + "Hands_V2_LOD2.grn"),
                ("ulegs", prefix + "ULegs_V2_LOD2.grn"),
                ("llegs", prefix + "LLegs_V2_LOD2.grn"),
                ("feet", prefix + "Feet_V2_LOD2.grn"),
                ("pelvis", prefix + "Pelvis_V2_LOD2.grn"),
                ("face", prefix + "Face_01_V2_LOD2.grn"),
            };

            int total = 0;
            foreach (var part in parts)
            {
                string p = Path.Combine(folder, part.file);
                if (File.Exists(p)) total += LoadPartMeshes(p, part.slot);
                else Log($"AssembleHumanBody: missing {part.file}");
            }
            Log($"AssembleHumanBody female={female} meshes=+{total} meshes={_loadedMeshes.Count}");
            UpdateCharInfo();
        }

        // Append every mesh of a part file, tagging it with its character slot. No
        // refresh here (callers batch it): load many parts then refresh once.
        private int LoadPartMeshes(string path, string slot)
        {
            var parser = new GrannyFormatParser();
            if (!parser.Load(path)) { Log($"LoadPart parse failed: {path}"); return 0; }
            int added = 0;
            foreach (var mesh in parser.Meshes)
            {
                if (mesh.Vertices.Count == 0 || mesh.Indices.Count == 0) continue;
                var lm = new LoadedMesh
                {
                    Name = Path.GetFileName(path),
                    SourcePath = path,
                    SourceMesh = mesh,
                    Bones = new List<GrannyBone>(parser.Bones),
                    BaseVertices = mesh.Vertices.ToArray(),
                    Indices = mesh.Indices.ToArray(),
                    Skin = mesh.Skin.Count > 0 ? mesh.Skin.ToArray() : null,
                    CharSlot = slot
                };
                BuildCornerData(lm);
                SetupMeshVBO(lm);
                lm.SkinTexName = IsBodySkinTexture(parser.TextureName) ? parser.TextureName : null;
                string texName = (lm.SkinTexName != null && !string.IsNullOrEmpty(_skinOverride)) ? _skinOverride : parser.TextureName;
                lm.TextureId = TryLoadTexture(path, texName, out lm.TextureFilePath);
                lm.HasTexture = lm.TextureId > 0;
                lm.BaseTextureId = lm.TextureId;
                lm.BaseMeshColor = lm.MeshColor;
                _loadedMeshes.Add(lm);
                added++;
            }
            return added;
        }

        // Replace every mesh of a slot with the given part file.
        private void SetCharacterPart(string slot, string path)
        {
            if (string.IsNullOrEmpty(slot) || string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            if (!_glReady || glControl == null || !glControl.IsHandleCreated) return;
            glControl.MakeCurrent();

            for (int i = _loadedMeshes.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_loadedMeshes[i].CharSlot, slot, StringComparison.OrdinalIgnoreCase))
                {
                    var dead = _loadedMeshes[i];
                    DestroyMesh(dead);
                    _loadedMeshes.RemoveAt(i);
                    // Meshes wrapped to this part lose their borrowed weights.
                    for (int k = 0; k < _loadedMeshes.Count; k++)
                        if (_loadedMeshes[k].WrapSource == dead) { _loadedMeshes[k].WrapSource = null; _loadedMeshes[k].WrapSkin = null; }
                }
            }

            LoadPartMeshes(path, slot);
            Log($"SetCharacterPart {slot} = {Path.GetFileName(path)}");
            AfterCharacterChange();
        }

        // Rebuild derived state (bounds, animation binding, pose) after a part change.
        private void AfterCharacterChange()
        {
            RefreshLoadedList();
            PopulateBoneCombo();
            ComputeReferenceBounds();
            if (_currentAnimation != null)
            {
                EnsureAnimationBindings(_currentAnimation);
                ApplyAnimationPose(_currentTime);
            }
            else
            {
                RefreshVertexBuffers(true);
            }
            UpdateModelInfo();
            if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();
        }

        // Fill the Visage / Coiffure / Barbe / Torse combos from the "Others" folder
        // for the current gender, then pre-select the parts already loaded.
        private void PopulateCharCombos()
        {
            if (cbCharFace == null) return;
            _suppressChar = true;
            try
            {
                foreach (var cb in new[] { cbCharFace, cbCharHair, cbCharBeard, cbCharTorso, cbCharSkin })
                    cb.Items.Clear();

                string folder = Path.Combine(BasePath, "Others");
                if (_charGender == 0 || !Directory.Exists(folder))
                {
                    foreach (var cb in new[] { cbCharFace, cbCharHair, cbCharBeard, cbCharTorso, cbCharSkin })
                        cb.Enabled = false;
                    return;
                }

                bool male = _charGender == 1;
                string prefix = male ? "H_Male_" : "H_Female_";
                string eprefix = male ? "E_Male_" : "E_Female_";

                var files = new List<string>();
                try
                {
                    foreach (var f in Directory.GetFiles(folder, "*.grn"))
                    {
                        string fn = Path.GetFileName(f);
                        if (fn.IndexOf("lod", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        files.Add(fn);
                    }
                }
                catch { }
                files.Sort(StringComparer.OrdinalIgnoreCase);

                foreach (var fn in files)
                {
                    string low = fn.ToLowerInvariant();
                    bool isH = fn.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
                    bool isE = fn.StartsWith(eprefix, StringComparison.OrdinalIgnoreCase);

                    if (isH && low.Contains("_face") && !low.Contains("facial"))
                        cbCharFace.Items.Add(MakePart(fn));
                    if ((isH || isE) && low.Contains("hair") && !low.Contains("facial"))
                        cbCharHair.Items.Add(MakePart(fn));
                    if (male && isH && low.Contains("hair_facial"))
                        cbCharBeard.Items.Add(MakePart(fn));
                    if (isH && low.Contains("_torso") && !low.Contains("_torso_upper"))
                        cbCharTorso.Items.Add(MakePart(fn));
                }

                cbCharFace.Enabled = cbCharFace.Items.Count > 0;
                cbCharHair.Enabled = cbCharHair.Items.Count > 0;
                cbCharBeard.Enabled = male && cbCharBeard.Items.Count > 0;
                cbCharTorso.Enabled = cbCharTorso.Items.Count > 0;

                PreselectSlot(cbCharFace, "face");
                PreselectSlot(cbCharHair, "hair");
                PreselectSlot(cbCharBeard, "beard");
                PreselectSlot(cbCharTorso, "torso");
                PopulateSkinCombo(male);
            }
            finally { _suppressChar = false; }
            UpdateCharInfo();
        }

        private PartItem MakePart(string fileName) =>
            new PartItem { Display = BeautifyPart(fileName), Path = Path.Combine(BasePath, "Others", fileName) };

        private static string BeautifyPart(string fileName)
        {
            string n = fileName;
            if (n.EndsWith(".grn", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
            if (n.EndsWith("_V2_LOD2", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 8);
            if (n.StartsWith("H_Male_", StringComparison.OrdinalIgnoreCase)) n = n.Substring(7);
            else if (n.StartsWith("H_Female_", StringComparison.OrdinalIgnoreCase)) n = n.Substring(9);
            else if (n.StartsWith("E_Male_", StringComparison.OrdinalIgnoreCase)) n = n.Substring(7);
            else if (n.StartsWith("E_Female_", StringComparison.OrdinalIgnoreCase)) n = n.Substring(9);
            return n;
        }

        // Select the combo item matching the part currently loaded in a slot.
        private void PreselectSlot(ComboBox cb, string slot)
        {
            if (cb == null) return;
            string path = null;
            foreach (var lm in _loadedMeshes)
                if (string.Equals(lm.CharSlot, slot, StringComparison.OrdinalIgnoreCase)) { path = lm.SourcePath; break; }

            if (path == null) { cb.SelectedIndex = -1; return; }
            for (int i = 0; i < cb.Items.Count; i++)
                if (cb.Items[i] is PartItem pi && string.Equals(pi.Path, path, StringComparison.OrdinalIgnoreCase))
                { cb.SelectedIndex = i; return; }
            cb.SelectedIndex = -1;
        }

        private void CharCombo_Changed(object sender, EventArgs e)
        {
            if (_suppressChar) return;
            var cb = sender as ComboBox;
            if (cb == null || cb.SelectedIndex < 0) return;
            var item = cb.SelectedItem as PartItem;
            if (item == null) return;

            string slot = cb == cbCharFace ? "face"
                        : cb == cbCharHair ? "hair"
                        : cb == cbCharBeard ? "beard"
                        : cb == cbCharTorso ? "torso" : null;
            if (slot == null) return;
            SetCharacterPart(slot, item.Path);
        }

        private void UpdateCharInfo()
        {
            if (lblCharInfo == null) return;
            if (_charGender == 0) { lblCharInfo.Text = "Chargez H_Male / H_Female"; return; }
            int parts = 0;
            foreach (var lm in _loadedMeshes)
                if (!string.IsNullOrEmpty(lm.CharSlot)) parts++;
            lblCharInfo.Text = (_charGender == 2 ? "Femme" : "Homme") + $" — {parts} parties";
        }

        // True when a part's embedded texture is a shared human body skin
        // (UT256_H_Male_V2 / _V2c / _V2G… but NOT _Faces_V2, hair, armor…).
        private static bool IsBodySkinTexture(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            return System.Text.RegularExpressions.Regex.IsMatch(
                name.Trim(), @"^UT?256_H_(Male|Female)_V2[A-Za-z]?$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        // List the body-skin textures available in Maps for this gender (V2 family +
        // the Savage skins), with a "default" entry that keeps each part's embedded skin.
        private void PopulateSkinCombo(bool male)
        {
            if (cbCharSkin == null) return;
            cbCharSkin.Items.Clear();
            cbCharSkin.Items.Add(new SkinItem { Display = "Défaut (pièce)", Base = null });

            string token = male ? "Male" : "Female";
            string tokenRx = @"(?<![A-Za-z])" + token + @"(?![A-Za-z])";
            var bases = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(MapsPath))
            {
                try
                {
                    foreach (var f in Directory.GetFiles(MapsPath, "*.tga"))
                    {
                        string b = Path.GetFileNameWithoutExtension(f);
                        if (b.EndsWith("_m", StringComparison.OrdinalIgnoreCase)) continue;
                        if (b.IndexOf("_face", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        if (!System.Text.RegularExpressions.Regex.IsMatch(b, tokenRx, System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
                        bool isBody = System.Text.RegularExpressions.Regex.IsMatch(b, @"^UT?256_H_(Male|Female)_V2[A-Za-z]?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                        bool isSavage = b.IndexOf("savage", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!isBody && !isSavage) continue;
                        if (seen.Add(b)) bases.Add(b);
                    }
                }
                catch { }
                bases.Sort(StringComparer.OrdinalIgnoreCase);
            }
            foreach (var b in bases) cbCharSkin.Items.Add(new SkinItem { Display = BeautifySkin(b), Base = b });
            cbCharSkin.Enabled = cbCharSkin.Items.Count > 0;
            cbCharSkin.SelectedIndex = 0;
        }

        private static string BeautifySkin(string baseName)
        {
            string n = baseName;
            int i = n.IndexOf("_H_", StringComparison.OrdinalIgnoreCase);
            if (i >= 0) n = n.Substring(i + 3);            // "Male_V2c" / "Savage_Male"
            if (n.StartsWith("Male_", StringComparison.OrdinalIgnoreCase)) n = n.Substring(5);
            else if (n.StartsWith("Female_", StringComparison.OrdinalIgnoreCase)) n = n.Substring(7);
            return n.Replace('_', ' ');
        }

        private void CharSkin_Changed(object sender, EventArgs e)
        {
            if (_suppressChar || cbCharSkin == null) return;
            var item = cbCharSkin.SelectedItem as SkinItem;
            ApplySkin(item != null ? item.Base : null);
        }

        // Retarget every body-skin part to the chosen texture (null = embedded default).
        private void ApplySkin(string skinBase)
        {
            if (string.IsNullOrEmpty(skinBase) && _skinOverride == null) return;
            _skinOverride = string.IsNullOrEmpty(skinBase) ? null : skinBase;
            if (!_glReady || glControl == null || !glControl.IsHandleCreated) return;
            glControl.MakeCurrent();
            int changed = 0;
            foreach (var lm in _loadedMeshes)
            {
                if (string.IsNullOrEmpty(lm.SkinTexName)) continue;
                string name = _skinOverride ?? lm.SkinTexName;
                int tex = TryLoadTexture(lm.SourcePath, name, out string foundTexPath);
                if (tex <= 0) continue;
                if (lm.TextureId > 0 && lm.TextureId != lm.BaseTextureId) GL.DeleteTexture(lm.TextureId);
                lm.TextureId = tex;
                lm.BaseTextureId = tex;
                lm.TextureFilePath = foundTexPath;
                if (lm.BaseTextureBmp != null) { lm.BaseTextureBmp.Dispose(); lm.BaseTextureBmp = null; }
                lm.HueId = 0;
                lm.HasTexture = true;
                changed++;
            }
            Log($"Skin -> {_skinOverride ?? "(défaut)"} : {changed} parties retargétées");
            if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();
        }

        // ==================== ANIMATION TREE ====================

        // Categories shown in the Animations tree: Male / Female / Animals /
        // Monsters / SeaCreatures / Equipment, plus an "All" catch-all.
        private void BuildAnimCategories()
        {
            if (tvAnims == null) return;
            _suppressAnimSelect = true;
            tvAnims.BeginUpdate();
            try
            {
                tvAnims.Nodes.Clear();
                AddAnimCat("All", new AnimCat { Label = "All", IsAll = true });
                AddAnimCat("Male", new AnimCat { Label = "Male", Folder = Path.Combine(BasePath, "Humans"), Prefix = "H_Male_" });
                AddAnimCat("Female", new AnimCat { Label = "Female", Folder = Path.Combine(BasePath, "Humans"), Prefix = "H_Female_" });
                AddAnimCat("Animals", new AnimCat { Label = "Animals", Folder = Path.Combine(BasePath, "Animals") });
                AddAnimCat("Monsters", new AnimCat { Label = "Monsters", Folder = Path.Combine(BasePath, "Monsters") });
                AddAnimCat("SeaCreatures", new AnimCat { Label = "SeaCreatures", Folder = Path.Combine(BasePath, "SeaCreatures") });
                AddAnimCat("Equipment", new AnimCat { Label = "Equipment", Folder = Path.Combine(BasePath, "Others") });
                AddAnimCat("Importés (Mixamo)", new AnimCat { Label = "Importés (Mixamo)", Folder = Path.Combine(BasePath, "Others"), Pattern = "*.anim" });
                AddAnimCat("Poses", new AnimCat { Label = "Poses", Folder = PosesFolder, Pattern = "*.pose" });
            }
            finally
            {
                tvAnims.EndUpdate();
                _suppressAnimSelect = false;
            }
        }

        private void AddAnimCat(string label, AnimCat cat)
        {
            var node = new TreeNode(label) { Tag = cat };
            node.Nodes.Add(new TreeNode("...")); // lazy placeholder
            tvAnims.Nodes.Add(node);
        }

        private class AnimFolderTag
        {
            public string FolderPath = "";
            public string Pattern = "*.anim";
            public bool Populated = false;
        }

        private void TvAnims_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            if (e.Node?.Tag is AnimFolderTag folderTag)
            {
                if (folderTag.Populated || _populatingTree) return;
                _populatingTree = true;
                tvAnims.BeginUpdate();
                try { PopulateAnimFolder(e.Node, folderTag); }
                finally { tvAnims.EndUpdate(); _populatingTree = false; }
                return;
            }

            var cat = e.Node?.Tag as AnimCat;
            if (cat == null || cat.Populated || _populatingTree) return;
            _populatingTree = true;
            tvAnims.BeginUpdate();
            try { PopulateAnimCategory(e.Node, cat); }
            finally { tvAnims.EndUpdate(); _populatingTree = false; }
        }

        private void PopulateAnimCategory(TreeNode node, AnimCat cat)
        {
            node.Nodes.Clear();

            // Support des sous-dossiers personnalisés (Mixamo et Poses)
            if (!cat.IsAll && (cat.Pattern == "*.anim" || cat.Pattern == "*.pose") && !string.IsNullOrEmpty(cat.Folder) && Directory.Exists(cat.Folder))
            {
                foreach (var dir in Directory.GetDirectories(cat.Folder))
                {
                    var dirNode = new TreeNode("📁 " + Path.GetFileName(dir))
                    {
                        Tag = new AnimFolderTag { FolderPath = dir, Pattern = cat.Pattern }
                    };
                    dirNode.Nodes.Add(new TreeNode("...")); // placeholder
                    node.Nodes.Add(dirNode);
                }
            }

            var files = new List<string>();
            if (cat.IsAll)
            {
                foreach (var folder in CategoryFolders)
                    CollectAnims(Path.Combine(BasePath, folder), null, files);
            }
            else
            {
                CollectAnims(cat.Folder, cat.Prefix, files, cat.Pattern);
            }

            files.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
                node.Nodes.Add(new TreeNode(Path.GetFileNameWithoutExtension(f)) { Tag = f });
            cat.Populated = true;
        }

        private void PopulateAnimFolder(TreeNode node, AnimFolderTag tag)
        {
            node.Nodes.Clear();
            if (Directory.Exists(tag.FolderPath))
            {
                foreach (var dir in Directory.GetDirectories(tag.FolderPath))
                {
                    var dirNode = new TreeNode("📁 " + Path.GetFileName(dir))
                    {
                        Tag = new AnimFolderTag { FolderPath = dir, Pattern = tag.Pattern }
                    };
                    dirNode.Nodes.Add(new TreeNode("..."));
                    node.Nodes.Add(dirNode);
                }

                var files = new List<string>();
                CollectAnims(tag.FolderPath, null, files, tag.Pattern);
                files.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (var f in files)
                {
                    node.Nodes.Add(new TreeNode(Path.GetFileNameWithoutExtension(f)) { Tag = f });
                }
            }
            tag.Populated = true;
        }

        public static void CollectAnims(string folder, string prefix, List<string> outFiles, string pattern = "*.grn")
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            try
            {
                foreach (var f in Directory.GetFiles(folder, pattern))
                {
                    string name = Path.GetFileName(f);
                    if (pattern == "*.grn" && name.IndexOf("lod", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (prefix != null && !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    outFiles.Add(f);
                }
            }
            catch { }
        }

        private void TvAnims_NodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Button != MouseButtons.Right || e.Node == null) return;
            _suppressAnimSelect = true;
            try { tvAnims.SelectedNode = e.Node; }
            finally { _suppressAnimSelect = false; }
        }

        private void TvAnimMenu_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            tvAnimMenu.Items.Clear();
            var node = tvAnims.SelectedNode;
            if (node == null)
            {
                var applyMixamoGen = new ToolStripMenuItem("⚡ Convert/create rig to UO (Importer & Adapter un FBX)...", null, (s, a) =>
                {
                    ApplyMixamoForUo(null);
                });
                applyMixamoGen.Font = new Font(tvAnimMenu.Font, FontStyle.Bold);
                tvAnimMenu.Items.Add(applyMixamoGen);
                tvAnimMenu.Items.Add("Importer une animation FBX Mixamo...", null, BtnImportAnim_Click);
                return;
            }

            string animPath = node.Tag as string;
            var cat = node.Tag as AnimCat;
            var folderTag = node.Tag as AnimFolderTag;

            if (folderTag != null)
            {
                tvAnimMenu.Items.Add(node.IsExpanded ? "Réduire" : "Développer", null, (s, a) => node.Toggle());
                tvAnimMenu.Items.Add(new ToolStripSeparator());
                tvAnimMenu.Items.Add("📁 Nouveau sous-dossier...", null, (s, a) => CreateAnimSubfolder(node, folderTag.FolderPath, folderTag.Pattern));
                tvAnimMenu.Items.Add(new ToolStripSeparator());
                tvAnimMenu.Items.Add("Ouvrir le dossier contenant...", null, (s, a) =>
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = folderTag.FolderPath, UseShellExecute = true }); } catch { }
                });
                return;
            }

            if (cat != null)
            {
                tvAnimMenu.Items.Add(node.IsExpanded ? "Réduire" : "Développer", null, (s, a) => node.Toggle());
                if (!cat.IsAll && (cat.Pattern == "*.anim" || cat.Pattern == "*.pose"))
                {
                    tvAnimMenu.Items.Add(new ToolStripSeparator());
                    tvAnimMenu.Items.Add("📁 Nouveau sous-dossier...", null, (s, a) => CreateAnimSubfolder(node, cat.Folder, cat.Pattern));
                }
                tvAnimMenu.Items.Add(new ToolStripSeparator());
                var applyCat = new ToolStripMenuItem("⚡ Convert/create rig to UO (Importer une animation FBX)...", null, (s, a) => BtnImportAnim_Click(s, a));
                applyCat.Font = new Font(tvAnimMenu.Font, FontStyle.Bold);
                tvAnimMenu.Items.Add(applyCat);
                return;
            }

            if (string.IsNullOrEmpty(animPath))
            {
                var applyMixamoGen = new ToolStripMenuItem("⚡ Convert/create rig to UO (Importer & Adapter un FBX)...", null, (s, a) =>
                {
                    ApplyMixamoForUo(null);
                });
                applyMixamoGen.Font = new Font(tvAnimMenu.Font, FontStyle.Bold);
                tvAnimMenu.Items.Add(applyMixamoGen);
                return;
            }

            bool isPose = PoseFile.IsPoseFile(animPath);
            if (isPose)
                tvAnimMenu.Items.Add("Appliquer cette pose", null, (s, a) => ApplyPoseFile(animPath));
            else
                tvAnimMenu.Items.Add("Lire l'animation", null, (s, a) => PlayAnimationFile(animPath));
            tvAnimMenu.Items.Add(new ToolStripSeparator());
            var applyMixamoClip = new ToolStripMenuItem("⚡ Convert/create rig to UO (Importer une animation FBX)...", null, (s, a) => BtnImportAnim_Click(s, a));
            applyMixamoClip.Font = new Font(tvAnimMenu.Font, FontStyle.Bold);
            tvAnimMenu.Items.Add(applyMixamoClip);

            bool isCustomAnim = animPath.EndsWith(".anim", StringComparison.OrdinalIgnoreCase) ||
                                animPath.EndsWith(".pose", StringComparison.OrdinalIgnoreCase) ||
                                (node.Parent?.Tag as AnimCat)?.Pattern == "*.anim" ||
                                (node.Parent?.Tag as AnimCat)?.Pattern == "*.pose" ||
                                (node.Parent?.Tag is AnimFolderTag);

            if (isCustomAnim)
            {
                tvAnimMenu.Items.Add(new ToolStripSeparator());
                tvAnimMenu.Items.Add(isPose ? "📄 Dupliquer cette pose..." : "📄 Dupliquer cette animation...", null, (s, a) =>
                {
                    DuplicateCustomAnimation(node, animPath);
                });
                tvAnimMenu.Items.Add(Localization.Tr("menu_rename", "Renommer..."), null, (s, a) =>
                {
                    RenameCustomAnimation(node, animPath);
                });

                var deleteItem = new ToolStripMenuItem(isPose ? "Supprimer cette pose" : "Supprimer cette animation", null, (s, a) =>
                {
                    DeleteCustomAnimation(node, animPath);
                });
                deleteItem.ForeColor = Color.Crimson;
                tvAnimMenu.Items.Add(deleteItem);
            }

            tvAnimMenu.Items.Add(new ToolStripSeparator());
            tvAnimMenu.Items.Add("Ouvrir le dossier contenant...", null, (s, a) =>
            {
                try
                {
                    string dir = Path.GetDirectoryName(animPath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = dir,
                            UseShellExecute = true
                        });
                    }
                }
                catch { }
            });
        }

        private void RenameCustomAnimation(TreeNode node, string animPath)
        {
            if (string.IsNullOrEmpty(animPath) || !File.Exists(animPath)) return;

            string oldName = !string.IsNullOrEmpty(node.Text) ? node.Text : Path.GetFileNameWithoutExtension(animPath);
            bool isPose = animPath.EndsWith(".pose", StringComparison.OrdinalIgnoreCase);

            using var dlg = new Form
            {
                Text = isPose ? "Renommer la pose" : "Renommer l'animation",
                Size = new Size(380, 160),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.Gainsboro
            };

            var lbl = new Label { Text = "Nouveau nom :", Location = new Point(16, 16), AutoSize = true, ForeColor = Color.Gainsboro };
            var txt = new TextBox
            {
                Text = oldName,
                Location = new Point(16, 40),
                Width = 330,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle
            };
            txt.SelectAll();

            var btnOk = new Button
            {
                Text = "OK",
                DialogResult = DialogResult.OK,
                Location = new Point(170, 80),
                Width = 80,
                Height = 28,
                BackColor = Color.FromArgb(48, 48, 48),
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat
            };
            var btnCancel = new Button
            {
                Text = "Annuler",
                DialogResult = DialogResult.Cancel,
                Location = new Point(266, 80),
                Width = 80,
                Height = 28,
                BackColor = Color.FromArgb(48, 48, 48),
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat
            };
            dlg.Controls.AddRange(new Control[] { lbl, txt, btnOk, btnCancel });
            dlg.AcceptButton = btnOk;
            dlg.CancelButton = btnCancel;

            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            string newName = MakeSafeFilename(txt.Text.Trim());
            if (string.IsNullOrEmpty(newName) || string.Equals(newName, oldName, StringComparison.OrdinalIgnoreCase)) return;

            string folder = Path.GetDirectoryName(animPath) ?? string.Empty;
            string ext = Path.GetExtension(animPath);
            string newPath = Path.Combine(folder, newName + ext);

            if (File.Exists(newPath))
            {
                MessageBox.Show($"Un fichier nommé '{newName}{ext}' existe déjà dans ce dossier.", "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                if (animPath.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                {
                    var anim = GrannyAnimFile.Load(animPath);
                    if (anim != null)
                    {
                        anim.Name = newName;
                        GrannyAnimFile.Save(anim, newPath);
                        try { File.Delete(animPath); } catch { }
                    }
                    else
                    {
                        File.Move(animPath, newPath);
                    }
                }
                else
                {
                    File.Move(animPath, newPath);
                }

                if (string.Equals(_currentAnimPath, animPath, StringComparison.OrdinalIgnoreCase))
                {
                    _currentAnimPath = newPath;
                    if (_currentAnimation != null) _currentAnimation.Name = newName;
                    if (lblAnimHeader != null) lblAnimHeader.Text = newName;
                }

                node.Tag = newPath;
                node.Text = newName;

                Log($"{(isPose ? "Pose renommée" : "Animation renommée")} : '{oldName}' -> '{newName}'");
            }
            catch (Exception ex)
            {
                Log($"Erreur renommage animation: {ex.Message}");
                MessageBox.Show("Erreur lors du renommage : " + ex.Message, "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void DeleteCustomAnimation(TreeNode node, string animPath)
        {
            if (string.IsNullOrEmpty(animPath)) return;
            string animName = !string.IsNullOrEmpty(node.Text) ? node.Text : Path.GetFileNameWithoutExtension(animPath);

            var res = MessageBox.Show(
                $"Voulez-vous vraiment supprimer l'animation '{animName}' ?",
                "Supprimer l'animation",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);

            if (res != DialogResult.Yes) return;

            try
            {
                if (string.Equals(_currentAnimPath, animPath, StringComparison.OrdinalIgnoreCase))
                {
                    _playing = false;
                    _currentAnimation = null;
                    _animParser = null;
                    _currentTime = 0f;
                    _currentAnimPath = null;
                    if (lblAnimHeader != null) lblAnimHeader.Text = string.Empty;
                    if (trackTime != null) trackTime.Value = 0;
                    if (btnPlay != null) btnPlay.Enabled = false;
                    if (btnPause != null) btnPause.Enabled = false;
                    if (btnStop != null) btnStop.Enabled = false;
                    if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();
                }

                if (File.Exists(animPath))
                {
                    File.Delete(animPath);
                }

                node.Remove();
                Log($"Animation supprimée : {animName} ({animPath})");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erreur lors de la suppression de l'animation : {ex.Message}", "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Log($"Erreur suppression animation: {ex.Message}");
            }
        }

        private void CreateAnimSubfolder(TreeNode parentNode, string parentDir, string pattern)
        {
            if (string.IsNullOrEmpty(parentDir) || !Directory.Exists(parentDir)) return;

            using var dlg = new Form
            {
                Text = "Nouveau sous-dossier",
                Size = new Size(380, 160),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.Gainsboro
            };

            var lbl = new Label { Text = "Nom du dossier :", Location = new Point(16, 16), AutoSize = true, ForeColor = Color.Gainsboro };
            var txt = new TextBox
            {
                Text = "Nouveau dossier",
                Location = new Point(16, 40),
                Width = 330,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle
            };
            txt.SelectAll();

            var btnOk = new Button { Text = "Créer", DialogResult = DialogResult.OK, Location = new Point(170, 80), Width = 80, Height = 28, BackColor = Color.FromArgb(48, 48, 48), ForeColor = Color.Gainsboro, FlatStyle = FlatStyle.Flat };
            var btnCancel = new Button { Text = "Annuler", DialogResult = DialogResult.Cancel, Location = new Point(266, 80), Width = 80, Height = 28, BackColor = Color.FromArgb(48, 48, 48), ForeColor = Color.Gainsboro, FlatStyle = FlatStyle.Flat };
            dlg.Controls.AddRange(new Control[] { lbl, txt, btnOk, btnCancel });
            dlg.AcceptButton = btnOk;
            dlg.CancelButton = btnCancel;

            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            string name = MakeSafeFilename(txt.Text.Trim());
            if (string.IsNullOrEmpty(name)) return;

            string newPath = Path.Combine(parentDir, name);
            if (Directory.Exists(newPath))
            {
                MessageBox.Show($"Le dossier '{name}' existe déjà.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                Directory.CreateDirectory(newPath);
                var dirNode = new TreeNode("📁 " + name)
                {
                    Tag = new AnimFolderTag { FolderPath = newPath, Pattern = pattern }
                };
                parentNode.Nodes.Insert(0, dirNode);
                parentNode.Expand();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erreur lors de la création du dossier : " + ex.Message, "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void DuplicateCustomAnimation(TreeNode node, string animPath)
        {
            if (string.IsNullOrEmpty(animPath) || !File.Exists(animPath)) return;

            string baseName = Path.GetFileNameWithoutExtension(animPath);
            string folder = Path.GetDirectoryName(animPath) ?? string.Empty;
            string ext = Path.GetExtension(animPath);
            bool isPose = ext.Equals(".pose", StringComparison.OrdinalIgnoreCase);

            using var dlg = new Form
            {
                Text = isPose ? "Dupliquer la pose" : "Dupliquer l'animation",
                Size = new Size(380, 160),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                BackColor = Color.FromArgb(40, 40, 40),
                ForeColor = Color.Gainsboro
            };

            var lbl = new Label { Text = "Nom de la copie :", Location = new Point(16, 16), AutoSize = true, ForeColor = Color.Gainsboro };
            var txt = new TextBox
            {
                Text = baseName + "_Copie",
                Location = new Point(16, 40),
                Width = 330,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle
            };
            txt.SelectAll();

            var btnOk = new Button { Text = "Dupliquer", DialogResult = DialogResult.OK, Location = new Point(170, 80), Width = 80, Height = 28, BackColor = Color.FromArgb(48, 48, 48), ForeColor = Color.Gainsboro, FlatStyle = FlatStyle.Flat };
            var btnCancel = new Button { Text = "Annuler", DialogResult = DialogResult.Cancel, Location = new Point(266, 80), Width = 80, Height = 28, BackColor = Color.FromArgb(48, 48, 48), ForeColor = Color.Gainsboro, FlatStyle = FlatStyle.Flat };
            dlg.Controls.AddRange(new Control[] { lbl, txt, btnOk, btnCancel });
            dlg.AcceptButton = btnOk;
            dlg.CancelButton = btnCancel;

            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            string newName = MakeSafeFilename(txt.Text.Trim());
            if (string.IsNullOrEmpty(newName)) return;

            string newPath = Path.Combine(folder, newName + ext);
            if (File.Exists(newPath))
            {
                MessageBox.Show($"Un fichier nommé '{newName}{ext}' existe déjà.", "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                File.Copy(animPath, newPath, true);

                if (ext.Equals(".anim", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var anim = GrannyAnimFile.Load(newPath);
                        if (anim != null)
                        {
                            anim.Name = newName;
                            GrannyAnimFile.Save(anim, newPath);
                        }
                    }
                    catch { }
                }

                var parentNode = node.Parent;
                var newNode = new TreeNode(newName) { Tag = newPath };
                if (parentNode != null)
                {
                    parentNode.Nodes.Add(newNode);
                    tvAnims.SelectedNode = newNode;
                }
                else
                {
                    tvAnims.Nodes.Add(newNode);
                }

                Log($"Fichier dupliqué : {Path.GetFileName(newPath)}");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erreur lors de la duplication : " + ex.Message, "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Space)
            {
                var focused = FindFocusedControl(this);
                if (!(focused is TextBox) && !(focused is NumericUpDown))
                {
                    if (_playing)
                    {
                        if (btnPause != null && btnPause.Enabled)
                        {
                            btnPause.PerformClick();
                            return true;
                        }
                    }
                    else
                    {
                        if (btnPlay != null && btnPlay.Enabled)
                        {
                            btnPlay.PerformClick();
                            return true;
                        }
                    }
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private static Control FindFocusedControl(Control control)
        {
            var container = control as IContainerControl;
            while (container != null && container.ActiveControl != null && container.ActiveControl != control)
            {
                control = container.ActiveControl;
                container = control as IContainerControl;
            }
            return control;
        }

        private void TvAnims_AfterSelect(object sender, TreeViewEventArgs e)
        {
            if (_suppressAnimSelect) return;
            if (e.Node?.Tag is string path)
            {
                if (PoseFile.IsPoseFile(path))
                    ApplyPoseFile(path);
                else
                    PlayAnimationFile(path);
            }
        }

        private void BtnTPose_Click(object sender, EventArgs e)
        {
            ResetToTPose();
        }

        public void ResetToTPose()
        {
            _playing = false;
            _currentAnimation = null;
            _currentAnimPath = null;
            _animParser = null;
            _currentTime = 0f;

            if (lblAnimHeader != null)
                lblAnimHeader.Text = "T-Pose (Rest Pose)";

            if (trackTime != null)
            {
                trackTime.Value = 0;
                trackTime.Maximum = 1;
            }
            if (lblTime != null) lblTime.Text = "Time: 0.00s";
            if (lblDuration != null) lblDuration.Text = "/ 0.00s";

            if (btnPlay != null) btnPlay.Enabled = false;
            if (btnPause != null) btnPause.Enabled = false;
            if (btnStop != null) btnStop.Enabled = false;

            if (tvAnims != null)
            {
                _suppressAnimSelect = true;
                try { tvAnims.SelectedNode = null; }
                finally { _suppressAnimSelect = false; }
            }

            foreach (var lm in _loadedMeshes)
            {
                lm.BoneToAnimTrack = null;
                lm.AnimOverride = null;
                lm.AnimOverrideTrack = null;
                ClearClothBake(lm);
            }

            // Drop the pose-studio layer too: it is applied independently of the
            // animation, so leaving it set would make "T-Pose" show the last saved
            // pose instead of the rest pose. The tree selection is cleared above, so
            // no pose node re-applies itself on the next refresh.
            ClearPoseLayer();

            ApplyAnimationPose(0f);
            ResetAllCloth();
            RefreshVertexBuffers(true);
            if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();
            UpdateModelInfo();
            Log("Pose : T-Pose rétablie (Rest Pose).");
        }

        // ==================== POSE STUDIO ====================
        // Additive per-bone pose layer + global character displacement, saved as
        // .pose files in BasePath\Poses (tree category "Poses").

        // The most complete skeleton currently loaded (the posing host).
        private List<GrannyBone> PoseHostBones()
        {
            List<GrannyBone> bones = null;
            foreach (var lm in _loadedMeshes)
            {
                if (lm.Bones != null && lm.Bones.Count > 0 &&
                    (bones == null || lm.Bones.Count > bones.Count))
                    bones = lm.Bones;
            }
            return bones;
        }

        // Drop every pose layer (additive offsets, absolute pose, global displacement)
        // and refresh the pose UI. Used by "T-Pose" and when an animation is loaded, so
        // neither a loaded pose nor a stale L/R offset can leak into another view.
        private void ClearPoseLayer()
        {
            _poseOffsets.Clear();
            _poseAbsolute.Clear();
            _poseHasAbsolute = false;
            _poseGlobalOffset = Vector3.Zero;
            _poseGlobalYaw = 0f;
            UpdatePoseNudForBone(SelectedPoseBone());
            UpdatePoseGlobalNuds();
        }

        private void PopulatePoseBones()
        {
            if (cbPoseBones == null) return;
            string prev = cbPoseBones.SelectedItem as string;
            var bones = PoseHostBones();

            cbPoseBones.BeginUpdate();
            cbPoseBones.Items.Clear();
            cbPoseBones.Items.Add("(choisir un os)");
            if (bones != null)
            {
                foreach (var b in bones)
                    if (!string.IsNullOrEmpty(b.Name)) cbPoseBones.Items.Add(b.Name);
            }
            cbPoseBones.EndUpdate();

            if (!string.IsNullOrEmpty(prev) && cbPoseBones.Items.Contains(prev))
                cbPoseBones.SelectedItem = prev;
            else
                cbPoseBones.SelectedIndex = 0;
        }

        private string SelectedPoseBone()
        {
            if (cbPoseBones == null || cbPoseBones.SelectedIndex <= 0) return null;
            return cbPoseBones.SelectedItem as string;
        }

        private void CbPoseBones_Changed(object sender, EventArgs e)
        {
            if (_suppressPose) return;
            UpdatePoseNudForBone(SelectedPoseBone());
        }

        private void UpdatePoseNudForBone(string name)
        {
            if (nudPoseRotX == null) return;
            _suppressPose = true;
            try
            {
                BonePoseOffset po = null;
                if (!string.IsNullOrEmpty(name)) _poseOffsets.TryGetValue(name, out po);
                nudPoseRotX.Value = ClampNud(nudPoseRotX, po?.RotDeg.X ?? 0f);
                nudPoseRotY.Value = ClampNud(nudPoseRotY, po?.RotDeg.Y ?? 0f);
                nudPoseRotZ.Value = ClampNud(nudPoseRotZ, po?.RotDeg.Z ?? 0f);
                nudPoseOffX.Value = ClampNud(nudPoseOffX, po?.Pos.X ?? 0f);
                nudPoseOffY.Value = ClampNud(nudPoseOffY, po?.Pos.Y ?? 0f);
                nudPoseOffZ.Value = ClampNud(nudPoseOffZ, po?.Pos.Z ?? 0f);
            }
            finally { _suppressPose = false; }
        }

        private static decimal ClampNud(NumericUpDown nud, float v)
        {
            decimal d = (decimal)v;
            if (d < nud.Minimum) return nud.Minimum;
            if (d > nud.Maximum) return nud.Maximum;
            return d;
        }

        private void UpdatePoseGlobalNuds()
        {
            if (nudPoseGlobX == null) return;
            _suppressPose = true;
            try
            {
                nudPoseGlobX.Value = ClampNud(nudPoseGlobX, _poseGlobalOffset.X);
                nudPoseGlobY.Value = ClampNud(nudPoseGlobY, _poseGlobalOffset.Y);
                nudPoseGlobZ.Value = ClampNud(nudPoseGlobZ, _poseGlobalOffset.Z);
                nudPoseGlobYaw.Value = ClampNud(nudPoseGlobYaw, _poseGlobalYaw);
            }
            finally { _suppressPose = false; }
        }

        private BonePoseOffset GetOrCreatePoseOffset(string name)
        {
            if (!_poseOffsets.TryGetValue(name, out var po))
            {
                po = new BonePoseOffset();
                _poseOffsets[name] = po;
            }
            return po;
        }

        private void OnPoseRotChanged(object sender, EventArgs e)
        {
            if (_suppressPose) return;
            string name = SelectedPoseBone();
            if (name == null) return;

            var po = GetOrCreatePoseOffset(name);
            po.RotDeg = new Vector3((float)nudPoseRotX.Value, (float)nudPoseRotY.Value, (float)nudPoseRotZ.Value);

            if (chkPoseMirror != null && chkPoseMirror.Checked)
            {
                string pair = MirrorBoneName(name);
                var bones = PoseHostBones();
                if (pair != null && bones != null && bones.Exists(b => string.Equals(b.Name, pair, StringComparison.OrdinalIgnoreCase)))
                {
                    var m = GetOrCreatePoseOffset(pair);
                    // Sagittal mirror (X = midline): (rx, ry, rz) -> (rx, -ry, -rz).
                    m.RotDeg = new Vector3(po.RotDeg.X, -po.RotDeg.Y, -po.RotDeg.Z);
                    m.Pos = new Vector3(-po.Pos.X, po.Pos.Y, po.Pos.Z);
                }
            }

            CleanupEmptyPoseOffsets();
            Repose();
        }

        private void OnPoseOffChanged(object sender, EventArgs e)
        {
            if (_suppressPose) return;
            string name = SelectedPoseBone();
            if (name == null) return;

            var po = GetOrCreatePoseOffset(name);
            po.Pos = new Vector3((float)nudPoseOffX.Value, (float)nudPoseOffY.Value, (float)nudPoseOffZ.Value);

            if (chkPoseMirror != null && chkPoseMirror.Checked)
            {
                string pair = MirrorBoneName(name);
                var bones = PoseHostBones();
                if (pair != null && bones != null && bones.Exists(b => string.Equals(b.Name, pair, StringComparison.OrdinalIgnoreCase)))
                {
                    var m = GetOrCreatePoseOffset(pair);
                    m.RotDeg = new Vector3(po.RotDeg.X, -po.RotDeg.Y, -po.RotDeg.Z);
                    m.Pos = new Vector3(-po.Pos.X, po.Pos.Y, po.Pos.Z);
                }
            }

            CleanupEmptyPoseOffsets();
            Repose();
        }

        private void OnPoseGlobalChanged(object sender, EventArgs e)
        {
            if (_suppressPose) return;
            if (nudPoseGlobX == null) return;
            _poseGlobalOffset = new Vector3((float)nudPoseGlobX.Value, (float)nudPoseGlobY.Value, (float)nudPoseGlobZ.Value);
            _poseGlobalYaw = (float)nudPoseGlobYaw.Value;
            _poseGlobalBodyOnly = chkPoseBodyOnly.Checked;
            Repose();
        }

        private void CleanupEmptyPoseOffsets()
        {
            var empty = _poseOffsets
                .Where(kv => kv.Value.RotDeg == Vector3.Zero && kv.Value.Pos == Vector3.Zero)
                .Select(kv => kv.Key)
                .ToList();
            foreach (var k in empty) _poseOffsets.Remove(k);
        }

        // "L" <-> " R " token swap ("Bip01 L UpperArm" <-> "Bip01 R UpperArm",
        // "CP_Grasp_L_01" <-> "CP_Grasp_R_01"). Null when the bone has no side.
        private static string MirrorBoneName(string n)
        {
            if (string.IsNullOrEmpty(n)) return null;
            int i = n.IndexOf(" L ", StringComparison.OrdinalIgnoreCase);
            if (i >= 0) return n.Substring(0, i) + (char.IsUpper(n[i + 1]) ? " R " : " r ") + n.Substring(i + 3);
            i = n.IndexOf(" R ", StringComparison.OrdinalIgnoreCase);
            if (i >= 0) return n.Substring(0, i) + (char.IsUpper(n[i + 1]) ? " L " : " l ") + n.Substring(i + 3);
            i = n.IndexOf("_L_", StringComparison.OrdinalIgnoreCase);
            if (i >= 0) return n.Substring(0, i + 1) + (char.IsUpper(n[i + 1]) ? "R" : "r") + n.Substring(i + 2);
            i = n.IndexOf("_R_", StringComparison.OrdinalIgnoreCase);
            if (i >= 0) return n.Substring(0, i + 1) + (char.IsUpper(n[i + 1]) ? "L" : "l") + n.Substring(i + 2);
            return null;
        }

        private void BtnPoseResetBone_Click(object sender, EventArgs e)
        {
            string name = SelectedPoseBone();
            if (name == null) return;
            _poseOffsets.Remove(name);
            string pair = MirrorBoneName(name);
            if (pair != null) _poseOffsets.Remove(pair);
            UpdatePoseNudForBone(name);
            Repose();
        }

        private void BtnPoseResetAll_Click(object sender, EventArgs e)
        {
            _poseOffsets.Clear();
            _poseAbsolute.Clear();
            _poseHasAbsolute = false;
            _poseGlobalOffset = Vector3.Zero;
            _poseGlobalYaw = 0f;
            UpdatePoseNudForBone(SelectedPoseBone());
            UpdatePoseGlobalNuds();
            Repose();
            Log("Pose studio : tous les offsets ont été réinitialisés.");
        }

        // Re-pose the whole scene with the current pose layer (over the current
        // animation time or the bind pose) and refresh the viewport.
        private void Repose()
        {
            if (_loadedMeshes.Count == 0) return;
            if (!_glReady || glControl == null || !glControl.IsHandleCreated) return;
            ApplyAnimationPose(_currentTime);
            glControl.Invalidate();
        }

        private void BtnPoseSave_Click(object sender, EventArgs e)
        {
            var host = PoseHostBones();
            if (host == null || host.Count == 0)
            {
                MessageBox.Show("Aucun squelette à sauvegarder : chargez d'abord un personnage (H_Male / H_Female).",
                    "Pose vide", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            string name = PromptFileName("Nom de la pose", "Pose_001");
            if (string.IsNullOrEmpty(name)) return;

            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');

            try
            {
                if (!Directory.Exists(PosesFolder)) Directory.CreateDirectory(PosesFolder);
                string path = Path.Combine(PosesFolder, name + PoseFile.Extension);

                // Save the FULL current local pose of every posed skeleton, exactly as
                // it is displayed right now (the pose layers are already composited into
                // lm.LocalMatrices by the render loop, so nothing is re-evaluated and the
                // on-screen pose is never altered by saving). Stored as absolute locals
                // (version 2) so reloading reproduces the same screen on the same
                // character, independently of any animation. Keyed by bone name; the
                // first mesh that owns a name wins (matches the apply lookup).
                // Write the live pose layer verbatim: the additive Euler-degree deltas
                // the user actually edited (_poseOffsets). This is exactly the v1 format
                // and the exact data the live editor applies, so reloading reproduces the
                // preview by construction (no matrix round-trip, no per-mesh scrambling).
                var pf = new PoseFile { StoredVersion = 1, RootOffset = _poseGlobalOffset, RootYaw = _poseGlobalYaw };
                foreach (var kv in _poseOffsets)
                {
                    if (string.IsNullOrEmpty(kv.Key)) continue;
                    var po = kv.Value;
                    if (po == null) continue;
                    pf.Bones[kv.Key] = new PoseFile.PoseBone
                    {
                        Legacy = true,
                        LegacyRotDeg = po.RotDeg,
                        LegacyPos = po.Pos
                    };
                }
                if (pf.Bones.Count == 0)
                {
                    MessageBox.Show("Aucune pose à sauvegarder (aucun os).", "Pose vide",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                if (!PoseFile.Save(pf, path))
                {
                    MessageBox.Show("Écriture impossible : " + path, "Erreur Sauvegarde", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                BuildAnimCategories();
                Log($"Pose sauvegardée : {path} bones={pf.Bones.Count} rootOff={_poseGlobalOffset} yaw={_poseGlobalYaw}");
                // Reveal (highlight) the new node WITHOUT selecting it through the
                // AfterSelect handler: selecting would re-apply the just-saved pose
                // and can alter the on-screen pose. Saving must never change the view.
                _suppressAnimSelect = true;
                try { RevealAnimFile(path); }
                finally { _suppressAnimSelect = false; }
                MessageBox.Show($"Pose '{name}' sauvegardée ({pf.Bones.Count} os) dans :\n{PosesFolder}\n\nElle est disponible dans l'arbre Animations → « Poses ».",
                    "Pose Sauvegardée", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erreur lors de la sauvegarde de la pose : " + ex.Message, "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Load a .pose file and apply it as the current pose layer.
        private void ApplyPoseFile(string path)
        {
            try
            {
                if (!PoseFile.Load(path, out var pf, out string err) || pf == null)
                {
                    MessageBox.Show("Chargement de pose impossible : " + (err ?? "raison inconnue"), "Erreur Pose", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                _poseOffsets.Clear();
                _poseAbsolute.Clear();
                _poseHasAbsolute = false;
                bool anyLegacy = false;
                foreach (var kv in pf.Bones)
                {
                    if (kv.Value.Legacy)
                    {
                        anyLegacy = true;
                        _poseOffsets[kv.Key] = new BonePoseOffset { RotDeg = kv.Value.LegacyRotDeg, Pos = kv.Value.LegacyPos };
                    }
                    else
                    {
                        _poseAbsolute[kv.Key] = new BonePoseAbs
                        {
                            Rot = kv.Value.Rot, Pos = kv.Value.Pos,
                            BindRot = kv.Value.BindRot, BindPos = kv.Value.BindPos, HasBind = kv.Value.HasBind
                        };
                    }
                }
                _poseHasAbsolute = _poseAbsolute.Count > 0;
                _poseGlobalOffset = pf.RootOffset;
                _poseGlobalYaw = pf.RootYaw;

                UpdatePoseNudForBone(SelectedPoseBone());
                UpdatePoseGlobalNuds();

                // A pose is an alternative to an animation: stop and detach any clip so
                // the pose is shown on its own (mirror of LoadAnimationCore clearing the
                // pose layer).
                _playing = false;
                _currentAnimation = null;
                _animParser = null;
                _currentAnimPath = null;
                _currentTime = 0f;
                if (trackTime != null) { trackTime.Value = 0; trackTime.Maximum = 1; }
                if (lblTime != null) lblTime.Text = "Time: 0.00s";
                if (lblDuration != null) lblDuration.Text = "/ 0.00s";
                if (btnPlay != null) btnPlay.Enabled = false;
                if (btnPause != null) btnPause.Enabled = false;
                if (btnStop != null) btnStop.Enabled = false;
                int posedBones = _poseHasAbsolute ? _poseAbsolute.Count : _poseOffsets.Count;
                if (lblAnimHeader != null)
                    lblAnimHeader.Text = $"Pose : {Path.GetFileNameWithoutExtension(path)}   ({posedBones} os{(anyLegacy ? ", format v1" : "")})";

                // Diagnostics: how many of the stored bone names actually exist on the
                // loaded skeleton(s)? If this is 0 the pose cannot show up (T-pose).
                try
                {
                    var host = PoseHostBones();
                    int matched = 0;
                    if (host != null)
                    {
                        var hostNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var b in host) if (!string.IsNullOrEmpty(b.Name)) hostNames.Add(b.Name);
                        foreach (var k in (_poseHasAbsolute ? (IEnumerable<string>)_poseAbsolute.Keys : _poseOffsets.Keys))
                            if (hostNames.Contains(k)) matched++;
                    }
                    Log($"Pose match : {matched}/{posedBones} os reconnus sur le squelette chargé (host={(host != null ? host.Count : 0)} os). Cible mode={_poseTargetMode} cibles={_poseTargets.Count}.");
                    if (matched == 0 && posedBones > 0)
                        Log("Pose : AUCUN os reconnu — le squelette chargé ne porte pas les mêmes noms d'os que la pose. Rechargez le même personnage (H_Male/H_Female) que celui posé.");
                }
                catch { }

                ApplyAnimationPose(0f);
                if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();
                Log($"Pose appliquée : {path} bones={posedBones} abs={_poseHasAbsolute} rootOff={_poseGlobalOffset} yaw={_poseGlobalYaw}");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erreur lors du chargement de la pose : " + ex.Message, "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Log($"Pose load error: {ex.Message}\n{ex}");
            }
        }

        // Does the global displacement apply to this mesh? By default only the
        // character body moves (multipart parts + placeholder skeleton, or the
        // fullest skeleton for a custom character), so a free imported equipment
        // mesh stays in place and the character can be aligned against it.
        private bool IsPoseGlobalTarget(LoadedMesh lm)
        {
            if (!PoseAppliesTo(lm)) return false;
            if (!_poseGlobalBodyOnly) return true;
            if (!string.IsNullOrEmpty(lm.CharSlot)) return true;
            if (!string.IsNullOrEmpty(lm.SourcePath) && IsHumanPlaceholder(lm.SourcePath, out _)) return true;

            bool anyChar = _loadedMeshes.Exists(m =>
                !string.IsNullOrEmpty(m.CharSlot) ||
                (!string.IsNullOrEmpty(m.SourcePath) && IsHumanPlaceholder(m.SourcePath, out _)));
            if (anyChar) return false;

            int maxB = 0;
            foreach (var m in _loadedMeshes)
                if (m.Bones != null && m.Bones.Count > maxB) maxB = m.Bones.Count;
            return lm.Bones != null && lm.Bones.Count == maxB;
        }

        // Does the pose layer (bones + global displacement) affect this mesh at all?
        // In "Sélection Meshes affichés" mode only the checked/selected meshes are
        // posed, so the character can be aligned onto a specific imported equipment
        // mesh while other custom meshes stay frozen. In auto mode everything is
        // eligible and the body-only rule narrows the global displacement.
        private bool PoseAppliesTo(LoadedMesh lm)
        {
            if (_poseTargetMode == 0) return true;
            int idx = _loadedMeshes.IndexOf(lm);
            return idx >= 0 && _poseTargets.Contains(idx);
        }

        // Rebuild the pose-target set from the current "Meshes affichés" selection
        // (checked nodes first, else the selected node).
        private void SyncPoseTargetsFromTree()
        {
            _poseTargets.Clear();
            if (_poseTargetMode == 0)
            {
                if (lblPoseTargetInfo != null)
                    lblPoseTargetInfo.Text = "Cible : auto — le personnage (corps + peau) uniquement.";
                return;
            }

            foreach (int i in GetSelectedMeshIndices())
                if (i >= 0 && i < _loadedMeshes.Count)
                    _poseTargets.Add(i);

            if (lblPoseTargetInfo != null)
            {
                if (_poseTargets.Count == 0)
                    lblPoseTargetInfo.Text = "Cible : cochez/choisissez des meshes dans « Meshes affichés » (aucun pour l'instant).";
                else
                {
                    var names = new List<string>();
                    foreach (int i in _poseTargets)
                        names.Add(_loadedMeshes[i].Name);
                    lblPoseTargetInfo.Text = $"Cible ({_poseTargets.Count}) : " + string.Join(", ", names);
                }
            }
        }

        private static string PromptFileName(string title, string defaultName)
        {
            using var f = new Form
            {
                Text = title,
                Width = 360,
                Height = 150,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                StartPosition = FormStartPosition.CenterParent,
                MinimizeBox = false,
                MaximizeBox = false,
                BackColor = Color.FromArgb(45, 45, 48)
            };
            var lbl = new Label { Text = "Nom :", Location = new Point(12, 15), Width = 60, ForeColor = Color.Gainsboro };
            var txt = new TextBox { Location = new Point(75, 12), Width = 255, Text = defaultName };
            var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(175, 75), Width = 75, BackColor = Color.FromArgb(60, 60, 62), ForeColor = Color.Gainsboro, FlatStyle = FlatStyle.Flat };
            var cancel = new Button { Text = "Annuler", DialogResult = DialogResult.Cancel, Location = new Point(255, 75), Width = 75, BackColor = Color.FromArgb(60, 60, 62), ForeColor = Color.Gainsboro, FlatStyle = FlatStyle.Flat };
            f.Controls.AddRange(new Control[] { lbl, txt, ok, cancel });
            f.AcceptButton = ok;
            f.CancelButton = cancel;
            return f.ShowDialog() == DialogResult.OK ? txt.Text.Trim() : null;
        }

        // Import a Mixamo FBX: retarget it onto the UO Biped skeleton, write a
        // standalone ".anim" clip into the "Others" folder, then play it.
        private void BtnImportAnim_Click(object sender, EventArgs e)
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Importer une animation Mixamo (FBX)",
                Filter = "FBX (*.fbx)|*.fbx|Tous les fichiers (*.*)|*.*"
            };
            if (ofd.ShowDialog() != DialogResult.OK) return;
            ImportMixamoAnimDirect(ofd.FileName);
        }

        public void ImportMixamoAnimDirect(string fbxPath)
        {
            try
            {
                if (string.IsNullOrEmpty(fbxPath) || !File.Exists(fbxPath)) return;

                IReadOnlyList<GrannyBone> bones = null;
                if (_modelParser != null && _modelParser.Bones != null && _modelParser.Bones.Count > 0)
                {
                    bones = _modelParser.Bones;
                }
                else
                {
                    string refPath = Path.Combine(BasePath, "Humans", "H_Female_Lod2.grn");
                    if (!File.Exists(refPath)) refPath = Path.Combine(BasePath, "Humans", "H_Male_Lod2.grn");
                    if (!File.Exists(refPath))
                    {
                        MessageBox.Show("Modèle de référence Biped introuvable (H_Female_Lod2.grn ou H_Male_Lod2.grn).", "Erreur Référence", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    var refParser = new GrannyFormatParser();
                    if (!refParser.Load(refPath) || refParser.Bones == null || refParser.Bones.Count == 0)
                    {
                        MessageBox.Show("Impossible de charger le squelette de référence.", "Erreur Référence", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    bones = refParser.Bones;
                }

                string clip = Path.GetFileNameWithoutExtension(fbxPath);
                if (!MixamoRetarget.TryImport(fbxPath, bones, clip, out var anim, out string err) || anim == null)
                {
                    MessageBox.Show("Import Mixamo échoué : " + (err ?? "raison inconnue"), "Erreur Import Mixamo", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                string outFolder = Path.Combine(BasePath, "Others");
                if (!Directory.Exists(outFolder)) Directory.CreateDirectory(outFolder);
                string outPath = Path.Combine(outFolder, clip + GrannyAnimFile.Extension);
                if (!GrannyAnimFile.Save(anim, outPath))
                {
                    MessageBox.Show("Écriture impossible : " + outPath, "Erreur Sauvegarde", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                Log($"Import Mixamo OK: {Path.GetFileName(outPath)} tracks={anim.Tracks.Count} dur={anim.Duration:F2}s mapped={MixamoRetarget.MappedBoneCount(bones)}");

                BuildAnimCategories();
                RevealAnimFile(outPath);

                MessageBox.Show(
                    $"Animation Mixamo '{clip}' importée avec succès !\n\n" +
                    $"• Fichier généré : {Path.GetFileName(outPath)}\n" +
                    $"• Durée : {anim.Duration:F2}s ({anim.Tracks.Count} pistes d'os)\n" +
                    $"• Dossier : {outFolder}\n\n" +
                    "L'animation est disponible dans la catégorie 'Importés (Mixamo)' et a été lancée.",
                    "Import Mixamo Réussi",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                Log($"Import Mixamo error: {ex.Message}\n{ex}");
                MessageBox.Show("Erreur d'import : " + ex.Message, "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // Import an individual 3D mesh (weapon, armor, shield, prop, etc.) into "Meshs custom".
        private void BtnImportMesh_Click(object sender, EventArgs e)
        {
            try
            {
                using var ofd = new OpenFileDialog
                {
                    Title = "Importer un Mesh (Arme, Armure, Équipement...)",
                    Filter = "Fichiers 3D (*.fbx;*.obj;*.dae;*.3ds;*.ply)|*.fbx;*.obj;*.dae;*.3ds;*.ply|FBX (*.fbx)|*.fbx|Wavefront OBJ (*.obj)|*.obj|Tous les fichiers (*.*)|*.*"
                };
                if (ofd.ShowDialog() != DialogResult.OK) return;

                if (string.Equals(Path.GetExtension(ofd.FileName), ".fbx", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var chkCtx = new Assimp.AssimpContext();
                        var chkScene = chkCtx.ImportFile(ofd.FileName, Assimp.PostProcessSteps.None);
                        if (chkScene != null && chkScene.MeshCount == 0 && chkScene.AnimationCount > 0)
                        {
                            var ask = MessageBox.Show(
                                $"Le fichier '{Path.GetFileName(ofd.FileName)}' ne contient aucun mesh 3D, mais contient {chkScene.AnimationCount} animation(s) Mixamo (téléchargé 'Without Skin').\n\n" +
                                "Souhaitez-vous l'importer directement en tant qu'animation Mixamo (.anim) pour vos personnages ?",
                                "Animation Mixamo Détectée",
                                MessageBoxButtons.YesNo,
                                MessageBoxIcon.Question);
                            if (ask == DialogResult.Yes)
                            {
                                ImportMixamoAnimDirect(ofd.FileName);
                                return;
                            }
                        }
                    }
                    catch { }
                }

                string meshName = Path.GetFileNameWithoutExtension(ofd.FileName);
                string outFolder = Path.Combine(BasePath, "Meshs custom");
                if (!Directory.Exists(outFolder)) Directory.CreateDirectory(outFolder);

                bool hasSkeletalBones = false;
                try
                {
                    using var chkCtx = new Assimp.AssimpContext();
                    var chkScene = chkCtx.ImportFile(ofd.FileName, Assimp.PostProcessSteps.None);
                    if (chkScene != null)
                    {
                        for (int m = 0; m < chkScene.MeshCount; m++)
                        {
                            if (chkScene.Meshes[m].HasBones) { hasSkeletalBones = true; break; }
                        }
                    }
                }
                catch { }

                bool asStaticAccessory = false;
                if (!hasSkeletalBones)
                {
                    var askAccessory = MessageBox.Show(
                        Localization.Tr("msg_import_as_accessory",
                            "Ce fichier 3D ne contient aucun squelette d'animation.\n\n" +
                            "Souhaitez-vous l'importer en tant qu'accessoire rigide (Selle, Arme, Équipement) ?\n\n" +
                            "• Oui : Accessoire rigide sans déformation (Idéal pour selles de chevaux, armes, casques, boucliers à attacher sur un os).\n" +
                            "• Non : Tenter de l'associer au squelette d'un personnage."),
                        Localization.Tr("title_import_mesh_type", "Type d'équipement"),
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);
                    asStaticAccessory = (askAccessory == DialogResult.Yes);
                }

                string refPath = asStaticAccessory ? null : Path.Combine(BasePath, "Humans", "H_Male_Lod2.grn");
                if (!asStaticAccessory && !File.Exists(refPath)) refPath = Path.Combine(BasePath, "Humans", "H_Female_Lod2.grn");

                if (!MixamoCharacterImport.TryImport(ofd.FileName, outFolder, refPath, out var model, out string err, asStaticAccessory: asStaticAccessory) || model == null)
                {
                    MessageBox.Show(Localization.Tr("msg_import_mesh_fail", "Import du mesh échoué : ") + (err ?? "raison inconnue"));
                    return;
                }

                int totalParts = model.Parts.Count;
                var missingTextures = new List<GrannyModelPart>();
                foreach (var p in model.Parts)
                {
                    bool hasTex = !string.IsNullOrEmpty(p.TextureName) && File.Exists(Path.Combine(outFolder, p.TextureName));
                    if (!hasTex) missingTextures.Add(p);
                }

                string fbxDir = Path.GetDirectoryName(ofd.FileName) ?? string.Empty;

                if (missingTextures.Count > 0)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"Le fichier 3D contient {totalParts} sous-mesh(es) :");
                    for (int i = 0; i < model.Parts.Count; i++)
                    {
                        var p = model.Parts[i];
                        bool hasTex = !string.IsNullOrEmpty(p.TextureName) && File.Exists(Path.Combine(outFolder, p.TextureName));
                        sb.AppendLine($"  • Mesh #{i + 1} : '{p.Name}' [Mat: {p.MaterialName}] -> {(hasTex ? $"Texture : {p.TextureName}" : "AUCUNE TEXTURE")}");
                    }
                    sb.AppendLine();
                    sb.AppendLine($"{missingTextures.Count} mesh(es) n'ont pas de texture.");
                    sb.AppendLine("Souhaitez-vous sélectionner une texture (BaseColor / Diffuse) pour chaque mesh manquant ?");

                    var promptResult = MessageBox.Show(
                        sb.ToString(),
                        $"Import Mesh - {totalParts} Mesh(es) détecté(s)",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);

                    if (promptResult == DialogResult.Yes)
                    {
                        string initDir = Directory.Exists(Path.Combine(fbxDir, "textures"))
                            ? Path.Combine(fbxDir, "textures")
                            : (Directory.Exists(Path.Combine(fbxDir, "Textures")) ? Path.Combine(fbxDir, "Textures") : fbxDir);

                        for (int i = 0; i < missingTextures.Count; i++)
                        {
                            var part = missingTextures[i];
                            using var texOfd = new OpenFileDialog
                            {
                                Title = $"Texture BaseColor / Diffuse pour '{part.Name}' (Mesh {i + 1}/{missingTextures.Count})",
                                Filter = "Textures (*.png;*.tga;*.jpg;*.jpeg;*.bmp;*.dds)|*.png;*.tga;*.jpg;*.jpeg;*.bmp;*.dds|Tous (*.*)|*.*",
                                InitialDirectory = initDir
                            };
                            if (texOfd.ShowDialog() == DialogResult.OK && File.Exists(texOfd.FileName))
                            {
                                string ext = Path.GetExtension(texOfd.FileName);
                                string cleanMesh = MakeSafeFilename(part.Name);
                                if (!string.IsNullOrEmpty(part.MaterialName) && !part.MaterialName.Equals(part.Name, StringComparison.OrdinalIgnoreCase))
                                {
                                    cleanMesh = $"{cleanMesh}_{MakeSafeFilename(part.MaterialName)}";
                                }
                                else if (i > 0)
                                {
                                    cleanMesh = $"{cleanMesh}_p{i + 1}";
                                }
                                string savedName = $"{meshName}_{cleanMesh}{ext}";
                                File.Copy(texOfd.FileName, Path.Combine(outFolder, savedName), true);
                                part.TextureName = savedName;
                                Log($"Texture manuelle assignée à {part.Name} : {savedName}");
                            }
                        }
                    }
                }
                else
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"Le fichier 3D contient {totalParts} sous-mesh(es) :");
                    for (int i = 0; i < model.Parts.Count; i++)
                    {
                        var p = model.Parts[i];
                        sb.AppendLine($"  • Mesh #{i + 1} : '{p.Name}' -> Texture : {p.TextureName}");
                    }
                    MessageBox.Show(
                        sb.ToString(),
                        $"Import Mesh - {totalParts} Mesh(es) détecté(s)",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }

                string outPath = Path.Combine(outFolder, meshName + GrannyModelFile.Extension);

                if (!GrannyModelFile.Save(model, outPath))
                {
                    MessageBox.Show("Écriture impossible : " + outPath);
                    return;
                }

                Log($"Import Mesh OK: {Path.GetFileName(outPath)} parts={model.Parts.Count} saved in {outFolder}");

                RestoreModelsTree();
                RevealModelFile(outPath);

                if (_loadedMeshes.Count > 0)
                {
                    var choice = MessageBox.Show(
                        $"Mesh '{meshName}' importé avec succès dans 'Meshs custom' !\n\nUne scène est actuellement affichée. Souhaitez-vous l'ajouter à la scène en tant qu'équipement ?",
                        "Ajouter à la scène",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);

                    if (choice == DialogResult.Yes)
                    {
                        AddToScene(outPath);
                    }
                }
                else
                {
                    LoadModel(outPath);
                }
            }
            catch (Exception ex)
            {
                Log($"Import Mesh error: {ex.Message}\n{ex}");
                MessageBox.Show("Erreur d'import du mesh : " + ex.Message);
            }
        }

        // Import a Mixamo FBX character: extract meshes, skeleton, materials and skinning,
        // convert to UO coordinate system (Z-up), save as .char in BasePath\Others,
        // refresh the treeview and load/display the character.
        private void BtnImportModel_Click(object sender, EventArgs e)
        {
            try
            {
                using var ofd = new OpenFileDialog
                {
                    Title = "Importer un Character Mixamo (FBX)",
                    Filter = "FBX (*.fbx)|*.fbx|Tous les fichiers (*.*)|*.*"
                };
                if (ofd.ShowDialog() != DialogResult.OK) return;

                if (string.Equals(Path.GetExtension(ofd.FileName), ".fbx", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        using var chkCtx = new Assimp.AssimpContext();
                        var chkScene = chkCtx.ImportFile(ofd.FileName, Assimp.PostProcessSteps.None);
                        if (chkScene != null && chkScene.MeshCount == 0 && chkScene.AnimationCount > 0)
                        {
                            var ask = MessageBox.Show(
                                $"Le fichier '{Path.GetFileName(ofd.FileName)}' ne contient aucun mesh 3D, mais contient {chkScene.AnimationCount} animation(s) Mixamo (téléchargé 'Without Skin').\n\n" +
                                "Souhaitez-vous l'importer en tant qu'animation Mixamo (.anim) pour vos personnages ?",
                                "Animation Mixamo Détectée",
                                MessageBoxButtons.YesNo,
                                MessageBoxIcon.Question);
                            if (ask == DialogResult.Yes)
                            {
                                ImportMixamoAnimDirect(ofd.FileName);
                                return;
                            }
                        }
                    }
                    catch { }
                }

                string charName = Path.GetFileNameWithoutExtension(ofd.FileName);
                string outFolder = Path.Combine(BasePath, "Others");
                if (!Directory.Exists(outFolder)) Directory.CreateDirectory(outFolder);

                string refPath = Path.Combine(BasePath, "Humans", "H_Male_Lod2.grn");
                if (!File.Exists(refPath)) refPath = Path.Combine(BasePath, "Humans", "H_Female_Lod2.grn");

                if (!MixamoCharacterImport.TryImport(ofd.FileName, outFolder, refPath, out var model, out string err) || model == null)
                {
                    MessageBox.Show("Import Mixamo échoué : " + (err ?? "raison inconnue"));
                    return;
                }

                int totalParts = model.Parts.Count;
                var missingTextures = new List<GrannyModelPart>();
                foreach (var p in model.Parts)
                {
                    bool hasTex = !string.IsNullOrEmpty(p.TextureName) && File.Exists(Path.Combine(outFolder, p.TextureName));
                    if (!hasTex) missingTextures.Add(p);
                }

                string fbxDir = Path.GetDirectoryName(ofd.FileName) ?? string.Empty;

                if (missingTextures.Count > 0)
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"Le modèle FBX contient {totalParts} mesh(es) :");
                    for (int i = 0; i < model.Parts.Count; i++)
                    {
                        var p = model.Parts[i];
                        bool hasTex = !string.IsNullOrEmpty(p.TextureName) && File.Exists(Path.Combine(outFolder, p.TextureName));
                        sb.AppendLine($"  • Mesh #{i + 1} : '{p.Name}' [Mat: {p.MaterialName}] -> {(hasTex ? $"Texture : {p.TextureName}" : "AUCUNE TEXTURE")}");
                    }
                    sb.AppendLine();
                    sb.AppendLine($"{missingTextures.Count} mesh(es) n'ont pas de texture.");
                    sb.AppendLine("Souhaitez-vous sélectionner une texture (BaseColor / Diffuse) pour chaque mesh manquant ?");

                    var promptResult = MessageBox.Show(
                        sb.ToString(),
                        $"Import FBX - {totalParts} Mesh(es) détecté(s)",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);

                    if (promptResult == DialogResult.Yes)
                    {
                        string initDir = Directory.Exists(Path.Combine(fbxDir, "textures"))
                            ? Path.Combine(fbxDir, "textures")
                            : (Directory.Exists(Path.Combine(fbxDir, "Textures")) ? Path.Combine(fbxDir, "Textures") : fbxDir);

                        for (int i = 0; i < missingTextures.Count; i++)
                        {
                            var part = missingTextures[i];
                            using var texOfd = new OpenFileDialog
                            {
                                Title = $"Texture BaseColor / Diffuse pour '{part.Name}' (Mesh {i + 1}/{missingTextures.Count})",
                                Filter = "Textures (*.png;*.tga;*.jpg;*.jpeg;*.bmp;*.dds)|*.png;*.tga;*.jpg;*.jpeg;*.bmp;*.dds|Tous (*.*)|*.*",
                                InitialDirectory = initDir
                            };
                            if (texOfd.ShowDialog() == DialogResult.OK && File.Exists(texOfd.FileName))
                            {
                                string ext = Path.GetExtension(texOfd.FileName);
                                string cleanMesh = MakeSafeFilename(part.Name);
                                string savedName = $"{charName}_{cleanMesh}{ext}";
                                File.Copy(texOfd.FileName, Path.Combine(outFolder, savedName), true);
                                part.TextureName = savedName;
                                Log($"Texture manuelle assignée à {part.Name} : {savedName}");
                            }
                        }
                    }
                }
                else
                {
                    var sb = new StringBuilder();
                    sb.AppendLine($"Le modèle FBX contient {totalParts} mesh(es) :");
                    for (int i = 0; i < model.Parts.Count; i++)
                    {
                        var p = model.Parts[i];
                        sb.AppendLine($"  • Mesh #{i + 1} : '{p.Name}' -> Texture : {p.TextureName}");
                    }
                    MessageBox.Show(
                        sb.ToString(),
                        $"Import FBX - {totalParts} Mesh(es) détecté(s)",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }

                string outPath = Path.Combine(outFolder, charName + GrannyModelFile.Extension);

                if (!GrannyModelFile.Save(model, outPath))
                {
                    MessageBox.Show("Écriture impossible : " + outPath);
                    return;
                }

                Log($"Import Character OK: {Path.GetFileName(outPath)} bones={model.Bones.Count} parts={model.Parts.Count}");

                RestoreModelsTree();
                RevealModelFile(outPath);
            }
            catch (Exception ex)
            {
                Log($"Import Character error: {ex.Message}\n{ex}");
                MessageBox.Show("Erreur d'import : " + ex.Message);
            }
        }

        private void RevealModelFile(string path)
        {
            if (tvModels == null) return;
            string targetName = Path.GetFileNameWithoutExtension(path);

            foreach (TreeNode catNode in tvModels.Nodes)
            {
                var info = catNode.Tag as TreeNodeInfo;
                if (info != null && (info.IsOthersCategory || info.IsCustomMeshCategory))
                {
                    catNode.Expand();
                    foreach (TreeNode child in catNode.Nodes)
                    {
                        var cinfo = child.Tag as TreeNodeInfo;
                        if (cinfo != null && (string.Equals(cinfo.CreatureKey, targetName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(Path.GetFileNameWithoutExtension(cinfo.MeshPath), targetName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(cinfo.MeshPath, path, StringComparison.OrdinalIgnoreCase)))
                        {
                            tvModels.SelectedNode = child;
                            child.EnsureVisible();
                            return;
                        }
                    }
                }
            }
        }

        // Expand the matching category and select the freshly written clip so it plays.
        private void RevealAnimFile(string path)
        {
            if (tvAnims != null)
            {
                string ext = Path.GetExtension(path);
                foreach (TreeNode catNode in tvAnims.Nodes)
                {
                    var cat = catNode.Tag as AnimCat;
                    if (cat == null || cat.IsAll) continue;
                    if (!string.Equals(cat.Pattern, "*" + ext, StringComparison.OrdinalIgnoreCase)) continue;

                    catNode.Expand();
                    foreach (TreeNode child in catNode.Nodes)
                    {
                        if (child.Tag is string s && string.Equals(s, path, StringComparison.OrdinalIgnoreCase))
                        {
                            tvAnims.SelectedNode = child;
                            child.EnsureVisible();
                            return;
                        }
                    }
                }
            }
            if (!PoseFile.IsPoseFile(path))
                PlayAnimationFile(path);
        }

        // Picking an animation loads the creature it belongs to (if needed) and plays it.
        private void PlayAnimationFile(string animPath)
        {
            try
            {
                if (string.IsNullOrEmpty(animPath) || !File.Exists(animPath)) return;

                // Auto-load base mesh ONLY if scene is empty (no model loaded yet).
                // If a model is already loaded (e.g. NPC_Blackthorn, a human, monster, etc.),
                // keep the loaded model so the user can freely test any animation on it.
                if (_loadedMeshes.Count == 0)
                {
                    string baseMesh = NaturalBaseMesh(animPath);
                    if (!string.IsNullOrEmpty(baseMesh) && File.Exists(baseMesh))
                        LoadModel(baseMesh);
                }

                LoadAnimation(animPath);

                if (lblAnimHeader != null)
                {
                    string mName = !string.IsNullOrEmpty(_currentModelPath)
                        ? Path.GetFileNameWithoutExtension(_currentModelPath)
                        : (_loadedMeshes.Count > 0 ? _loadedMeshes[0].Name : string.Empty);
                    lblAnimHeader.Text = string.IsNullOrEmpty(mName)
                        ? Path.GetFileNameWithoutExtension(animPath)
                        : $"{Path.GetFileNameWithoutExtension(animPath)}   ({mName})";
                }
            }
            catch (Exception ex)
            {
                Log($"PlayAnim error: {ex.Message}\n{ex}");
            }
        }

        // The creature mesh an animation naturally belongs to (same folder, "LOD" mesh
        // with the same creature name; H_Male_*/H_Female_* map to the human bodies).
        private static string NaturalBaseMesh(string animPath)
        {
            string folder = Path.GetDirectoryName(animPath);
            string name = Path.GetFileNameWithoutExtension(animPath);
            string folderName = Path.GetFileName(folder);

            // Locally retargeted clips live in "Others" but are authored against the
            // standard UO Biped, so default them onto a human body.
            if (GrannyAnimFile.IsAnimFile(animPath))
            {
                string human = Path.Combine(BasePath, "Humans", "H_Female_Lod2.grn");
                if (!File.Exists(human)) human = Path.Combine(BasePath, "Humans", "H_Male_Lod2.grn");
                return File.Exists(human) ? human : null;
            }

            if (string.Equals(folderName, "Humans", StringComparison.OrdinalIgnoreCase))
            {
                if (name.StartsWith("H_Male", StringComparison.OrdinalIgnoreCase))
                    return Path.Combine(folder, "H_Male_Lod2.grn");
                if (name.StartsWith("H_Female", StringComparison.OrdinalIgnoreCase))
                    return Path.Combine(folder, "H_Female_Lod2.grn");
            }

            string creature = DeriveCreature(name);
            try
            {
                if (Directory.Exists(folder))
                {
                    foreach (var f in Directory.GetFiles(folder, "*.grn"))
                    {
                        string fn = Path.GetFileNameWithoutExtension(f);
                        if (fn.IndexOf("lod", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (string.Equals(DeriveCreature(fn), creature, StringComparison.OrdinalIgnoreCase))
                            return f;
                    }
                }
            }
            catch { }
            return null;
        }

        // When a creature is picked in the Models tree, reveal its matching (already
        // built) category in the Animations tree.
        private void ExpandAnimCategoryFor(TreeNodeInfo info)
        {
            if (tvAnims == null || info == null) return;
            string key = info.CreatureKey ?? string.Empty;
            string folderName = !string.IsNullOrEmpty(info.Folder) ? Path.GetFileName(info.Folder) : null;

            string want = null;
            if (key.StartsWith("H_Male", StringComparison.OrdinalIgnoreCase)) want = "Male";
            else if (key.StartsWith("H_Female", StringComparison.OrdinalIgnoreCase)) want = "Female";
            else if (string.Equals(folderName, "Animals", StringComparison.OrdinalIgnoreCase)) want = "Animals";
            else if (string.Equals(folderName, "Monsters", StringComparison.OrdinalIgnoreCase)) want = "Monsters";
            else if (string.Equals(folderName, "SeaCreatures", StringComparison.OrdinalIgnoreCase)) want = "SeaCreatures";
            else if (string.Equals(folderName, "Others", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(folderName, "Meshs custom", StringComparison.OrdinalIgnoreCase))
                want = (info.IsOthersCategory || info.IsCustomMeshCategory || GrannyModelFile.IsModelFile(info.MeshPath)) ? "Importés (Mixamo)" : "Equipment";

            if (lblAnimHeader != null)
                lblAnimHeader.Text = want == null
                    ? $"{key}   —   choisissez une animation ci-dessous"
                    : $"{key}   —   {want}";

            if (want == null) return;
            foreach (TreeNode n in tvAnims.Nodes)
            {
                if (n.Tag is AnimCat c && string.Equals(c.Label, want, StringComparison.OrdinalIgnoreCase))
                {
                    n.Expand();
                    break;
                }
            }
        }

        private void ApplyAnimFilter()
        {
            if (tvAnims == null) return;
            string filter = txtAnimFilter?.Text?.Trim() ?? string.Empty;
            if (filter.Length == 0) { BuildAnimCategories(); return; }

            _suppressAnimSelect = true;
            tvAnims.BeginUpdate();
            try
            {
                tvAnims.Nodes.Clear();
                var matches = new List<string>();
                foreach (var folder in CategoryFolders)
                    CollectAnimsFiltered(Path.Combine(BasePath, folder), filter, matches);
                matches.Sort(StringComparer.OrdinalIgnoreCase);

                var root = new TreeNode($"Résultats ({matches.Count})");
                foreach (var m in matches)
                    root.Nodes.Add(new TreeNode(Path.GetFileNameWithoutExtension(m)) { Tag = m });
                tvAnims.Nodes.Add(root);
                root.Expand();
            }
            finally
            {
                tvAnims.EndUpdate();
                _suppressAnimSelect = false;
            }
        }

        private static void CollectAnimsFiltered(string folder, string filter, List<string> outFiles)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            try
            {
                foreach (var f in Directory.GetFiles(folder, "*.grn"))
                {
                    string name = Path.GetFileName(f);
                    if (name.IndexOf("lod", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0) outFiles.Add(f);
                }
            }
            catch { }
        }

        // Load the creature's base mesh only if it is not already the current model.
        private void EnsureMeshLoaded(string meshPath)
        {
            if (string.IsNullOrEmpty(meshPath)) return;
            if (_loadedMeshes.Count > 0 && string.Equals(_currentModelPath, meshPath, StringComparison.OrdinalIgnoreCase))
                return;
            LoadModel(meshPath);
        }

        private void LoadModel(string path)
        {
            try
            {
                LoadModelCore(path);
            }
            catch (Exception ex)
            {
                Log($"LoadModel error ({path}): {ex.Message}\n{ex}");
            }
        }

        private void LoadModelCore(string path)
        {
            Log($"LoadModelCore enter path={path} glReady={_glReady} handleCreated={(glControl != null && glControl.IsHandleCreated)}");
            if (!File.Exists(path)) { Log("  -> file not found"); return; }
            if (!_glReady || !glControl.IsHandleCreated) { Log("  -> aborted: GL not ready"); return; }

            glControl.MakeCurrent();

            if (GrannyModelFile.IsModelFile(path) || path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
            {
                LoadCustomCharacter(path);
                return;
            }

            _modelParser = new GrannyFormatParser();
            if (!_modelParser.Load(path))
            {
                Log("  -> parser Load failed");
                return;
            }
            Log($"  parsed meshes={_modelParser.Meshes.Count} bones={_modelParser.Bones.Count}");
            try {
                var nm = new List<string>();
                for (int bi = 0; bi < Math.Min(8, _modelParser.Bones.Count); bi++) nm.Add(_modelParser.Bones[bi].Name);
                Log("  boneNames[0..7]=" + string.Join(",", nm));
            } catch { }

            foreach (var lm in _loadedMeshes)
                DestroyMesh(lm);

            _loadedMeshes.Clear();
            _sceneExtras.Clear();
            if (tvLoaded != null) tvLoaded.Nodes.Clear();
            _currentModelPath = path;

            // Stop and reset any running animation when loading a new model
            _currentAnimation = null;
            _animParser = null;
            _playing = false;
            _currentTime = 0f;

            // H_Male_Lod2 / H_Female_Lod2 only contain the 43-bone skeleton and a
            // 19-vertex placeholder box; the real body is assembled from parts.
            bool humanPlaceholder = IsHumanPlaceholder(path, out bool humanFemale);
            ResetCharMenu();

            foreach (var mesh in _modelParser.Meshes)
            {
                if (mesh.Vertices.Count == 0 || mesh.Indices.Count == 0) continue;

                bool isFullChar = humanPlaceholder || (_modelParser.Bones != null && _modelParser.Bones.Count >= 20);
                var lm = new LoadedMesh
                {
                    Name = $"{Path.GetFileName(path)} ({_loadedMeshes.Count})",
                    SourcePath = path,
                    SourceMesh = mesh,
                    Bones = new List<GrannyBone>(_modelParser.Bones),
                    BaseVertices = mesh.Vertices.ToArray(),
                    Indices = mesh.Indices.ToArray(),
                    Skin = mesh.Skin.Count > 0 ? mesh.Skin.ToArray() : null,
                    CutoutRole = isFullChar ? MeshCutoutRole.Body : MeshCutoutRole.Accessory
                };

                BuildCornerData(lm);
                SetupMeshVBO(lm);
                lm.TextureId = TryLoadTexture(path, _modelParser.TextureName, out lm.TextureFilePath);
                lm.HasTexture = lm.TextureId > 0;
                lm.BaseTextureId = lm.TextureId;
                lm.BaseMeshColor = lm.MeshColor;
                lm.Hidden = humanPlaceholder;   // keep the 43-bone skeleton as host, don't draw the box
                if (!lm.Hidden) TryAutoAttachUnskinned(lm);
                _loadedMeshes.Add(lm);
                Log($"  built mesh vbo={lm.Vbo} vao={lm.Vao} verts={lm.BaseVertices.Length} corners={lm.Indices.Length} tex={lm.TextureId} skin={(lm.Skin != null ? lm.Skin.Length : 0)}");
            }

            if (humanPlaceholder)
            {
                Log($"  human placeholder -> auto-assembling {(humanFemale ? "female" : "male")} body");
                AssembleHumanBody(humanFemale);
                PopulateCharCombos();
            }

            RefreshLoadedList();
            PopulateBoneCombo();
            ApplyAnimationPose(0f);
            ComputeReferenceBounds();
            RefreshVertexBuffers(true);
            _camera.Target = Vector3.Zero;
            _camera.Distance = 5f;
            _camera.Yaw = 0f;
            _camera.Pitch = 0f;
            UpdateModelInfo();
            Log($"  done meshes={_loadedMeshes.Count} refCenter={_refCenter} refScale={_refScale} camDist={_camera.Distance} vp={glControl.Width}x{glControl.Height}");
            if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();
        }

        private void LoadCustomCharacter(string path)
        {
            GrannyModelFile model = null;
            if (path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
            {
                if (!MixamoCharacterImport.TryImport(path, out model, out string err) || model == null)
                {
                    Log($"LoadCustomCharacter FBX import failed ({path}): {err}");
                    return;
                }
            }
            else if (!GrannyModelFile.Load(path, out model) || model == null)
            {
                Log($"LoadCustomCharacter Load failed for {path}");
                return;
            }

            foreach (var lm in _loadedMeshes)
                DestroyMesh(lm);

            _loadedMeshes.Clear();
            _sceneExtras.Clear();
            if (tvLoaded != null) tvLoaded.Nodes.Clear();
            _currentModelPath = path;
            ResetCharMenu();

            for (int pi = 0; pi < model.Parts.Count; pi++)
            {
                var part = model.Parts[pi];
                if (part.Vertices.Length == 0 || part.Indices.Length == 0) continue;

                var gmesh = new GrannyMesh();
                gmesh.Vertices.AddRange(part.Vertices);
                gmesh.Indices.AddRange(part.Indices);
                if (part.UVs != null) gmesh.UVs.AddRange(part.UVs);
                if (part.Skin != null) gmesh.Skin.AddRange(part.Skin);
                foreach (var v in part.Vertices)
                {
                    if (v.X < gmesh.MinX) gmesh.MinX = v.X;
                    if (v.X > gmesh.MaxX) gmesh.MaxX = v.X;
                    if (v.Y < gmesh.MinY) gmesh.MinY = v.Y;
                    if (v.Y > gmesh.MaxY) gmesh.MaxY = v.Y;
                    if (v.Z < gmesh.MinZ) gmesh.MinZ = v.Z;
                    if (v.Z > gmesh.MaxZ) gmesh.MaxZ = v.Z;
                }

                bool isFullChar = model.Bones != null && model.Bones.Count >= 20;
                var lm = new LoadedMesh
                {
                    PartIndex = pi,
                    Name = string.IsNullOrEmpty(part.Name) ? Path.GetFileNameWithoutExtension(path) : part.Name,
                    MaterialName = part.MaterialName ?? string.Empty,
                    SourcePath = path,
                    SourceMesh = gmesh,
                    Bones = new List<GrannyBone>(model.Bones),
                    BaseVertices = part.Vertices,
                    Indices = part.Indices,
                    Skin = (part.Skin != null && part.Skin.Length > 0) ? part.Skin : null,
                    MeshColor = part.Color,
                    TextureName = part.TextureName ?? string.Empty,
                    CutoutRole = !string.IsNullOrEmpty(part.AttachBoneName) ? MeshCutoutRole.Accessory : (isFullChar ? MeshCutoutRole.Body : (part.CutoutRole != 0 ? part.CutoutRole : MeshCutoutRole.Accessory)),
                    AttachBoneName = !string.IsNullOrEmpty(part.AttachBoneName) ? part.AttachBoneName : null,
                    AttachInBindSpace = part.AttachInBindSpace
                };

                BuildCornerData(lm);
                SetupMeshVBO(lm);
                lm.TextureId = !string.IsNullOrEmpty(part.TextureName) ? TryLoadTexture(path, part.TextureName, out lm.TextureFilePath) : -1;
                lm.HasTexture = lm.TextureId > 0;
                lm.BaseTextureId = lm.TextureId;
                lm.BaseMeshColor = lm.MeshColor;
                _loadedMeshes.Add(lm);
                Log($"  built custom mesh vbo={lm.Vbo} vao={lm.Vao} verts={lm.BaseVertices.Length} corners={lm.Indices.Length} color={lm.MeshColor}");
            }

            RefreshLoadedList();
            PopulateBoneCombo();
            ComputeReferenceBounds();
            RefreshVertexBuffers(true);
            _camera.Target = Vector3.Zero;
            _camera.Distance = 5f;
            _camera.Yaw = 0f;
            _camera.Pitch = 0f;
            UpdateModelInfo();
            Log($"  custom character loaded: meshes={_loadedMeshes.Count} refCenter={_refCenter} refScale={_refScale}");
            if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();
        }

        private void BuildCornerData(LoadedMesh lm) { BuildCornerData(lm, null); }

        private void BuildCornerData(LoadedMesh lm, Vector2[] explicitCornerUVs)
        {
            int cornerCount = lm.Indices.Length;
            lm.CornerVertex = new int[cornerCount];
            lm.Interleaved = new float[cornerCount * 8];

            if (explicitCornerUVs != null && explicitCornerUVs.Length == cornerCount)
            {
                lm.CornerUVs = (Vector2[])explicitCornerUVs.Clone();
            }

            var uvs = lm.SourceMesh?.UVs;
            var uvIdx = lm.SourceMesh?.UVIndices;

            if (lm.CornerUVs == null || lm.CornerUVs.Length != cornerCount)
            {
                var cachedUVs = new Vector2[cornerCount];
                for (int c = 0; c < cornerCount; c++)
                {
                    int vi = lm.Indices[c];
                    float u = 0f, v = 0f;
                    if (explicitCornerUVs != null && c < explicitCornerUVs.Length)
                    {
                        u = explicitCornerUVs[c].X; v = explicitCornerUVs[c].Y;
                    }
                    else if (uvIdx != null && uvIdx.Count > 0 && uvs != null && uvs.Count > 0)
                    {
                        int ui = (c < uvIdx.Count) ? uvIdx[c] : -1;
                        if (ui >= 0 && ui < uvs.Count) { u = uvs[ui].X; v = uvs[ui].Y; }
                    }
                    else if (uvs != null && uvs.Count == lm.BaseVertices.Length && vi >= 0 && vi < uvs.Count)
                    {
                        u = uvs[vi].X; v = uvs[vi].Y;
                    }
                    cachedUVs[c] = new Vector2(u, v);
                }
                lm.CornerUVs = cachedUVs;
            }

            for (int c = 0; c < cornerCount; c++)
            {
                int vi = lm.Indices[c];
                lm.CornerVertex[c] = vi;

                Vector2 uv = lm.CornerUVs[c];
                lm.Interleaved[c * 8 + 6] = uv.X;
                lm.Interleaved[c * 8 + 7] = uv.Y;
            }

            // Rest normals (per corner) from triangle faces.
            lm.BaseCornerNormals = new Vector3[cornerCount];
            for (int t = 0; t + 2 < cornerCount; t += 3)
            {
                int i0 = lm.CornerVertex[t], i1 = lm.CornerVertex[t + 1], i2 = lm.CornerVertex[t + 2];
                if (i0 < 0 || i1 < 0 || i2 < 0) continue;
                if (i0 >= lm.BaseVertices.Length || i1 >= lm.BaseVertices.Length || i2 >= lm.BaseVertices.Length) continue;

                Vector3 n = Vector3.Cross(lm.BaseVertices[i1] - lm.BaseVertices[i0], lm.BaseVertices[i2] - lm.BaseVertices[i0]);
                if (n.LengthSquared > 1e-12f) n = Vector3.Normalize(n);
                for (int k = 0; k < 3; k++)
                {
                    int ci = t + k;
                    lm.BaseCornerNormals[ci] = n;
                    lm.Interleaved[ci * 8 + 3] = n.X;
                    lm.Interleaved[ci * 8 + 4] = n.Y;
                    lm.Interleaved[ci * 8 + 5] = n.Z;
                }
            }

            // Bind-space centroid: pivot for the per-part scale tweak (skinned meshes).
            if (lm.BaseVertices != null && lm.BaseVertices.Length > 0)
            {
                Vector3 c = Vector3.Zero;
                Vector3 cL = Vector3.Zero;
                Vector3 cR = Vector3.Zero;
                int countL = 0;
                int countR = 0;

                foreach (var v in lm.BaseVertices)
                {
                    c += v;
                    if (v.X > 0.02f) { cL += v; countL++; }
                    else if (v.X < -0.02f) { cR += v; countR++; }
                }

                lm.BindCentroid = c / lm.BaseVertices.Length;
                lm.HasBindCentroid = true;

                if (countL > 0 && countR > 0)
                {
                    lm.CentroidLeft = cL / countL;
                    lm.CentroidRight = cR / countR;
                    lm.HasBilateralCentroids = true;
                }
                else
                {
                    lm.CentroidLeft = lm.BindCentroid;
                    lm.CentroidRight = lm.BindCentroid;
                    lm.HasBilateralCentroids = false;
                }

                if (lm.HasBilateralCentroids && !lm.MirrorArms)
                {
                    string fn = ((lm.Name ?? "") + " " + (lm.SourcePath ?? "")).ToLowerInvariant();
                    if (fn.Contains("glove") || fn.Contains("gant") || fn.Contains("brassard") ||
                        fn.Contains("bracer") || fn.Contains("sleeve") || fn.Contains("arm") ||
                        fn.Contains("hand") || fn.Contains("main") || fn.Contains("wrist") ||
                        fn.Contains("shoulder") || fn.Contains("epaul") || fn.Contains("cuff"))
                    {
                        lm.MirrorArms = true;
                    }
                }
            }

            // Per-vertex rest normals: area-weighted average of adjacent face normals.
            // Used for the outward "thickness" offset after skinning.
            lm.BaseVertexNormals = new Vector3[lm.BaseVertices.Length];
            for (int t = 0; t + 2 < cornerCount; t += 3)
            {
                int i0 = lm.CornerVertex[t], i1 = lm.CornerVertex[t + 1], i2 = lm.CornerVertex[t + 2];
                if (i0 < 0 || i1 < 0 || i2 < 0) continue;
                if (i0 >= lm.BaseVertices.Length || i1 >= lm.BaseVertices.Length || i2 >= lm.BaseVertices.Length) continue;
                Vector3 fn = Vector3.Cross(lm.BaseVertices[i1] - lm.BaseVertices[i0], lm.BaseVertices[i2] - lm.BaseVertices[i0]);
                lm.BaseVertexNormals[i0] += fn;
                lm.BaseVertexNormals[i1] += fn;
                lm.BaseVertexNormals[i2] += fn;
            }
            for (int v = 0; v < lm.BaseVertexNormals.Length; v++)
            {
                Vector3 nv = lm.BaseVertexNormals[v];
                lm.BaseVertexNormals[v] = nv.LengthSquared > 1e-12f ? Vector3.Normalize(nv) : Vector3.UnitZ;
            }

            // Weld coincident positions (double-sided geometry / UV-seam duplicates).
            BuildWeld(lm);

            // Share one normal across every coincident copy so the "thickness" offset
            // moves them together instead of splitting the piece into two sheets.
            if (lm.Weld != null && lm.WeldCount > 0)
            {
                var acc = new Vector3[lm.WeldCount];
                for (int v = 0; v < lm.BaseVertexNormals.Length; v++) acc[lm.Weld[v]] += lm.BaseVertexNormals[v];
                for (int p = 0; p < lm.WeldCount; p++)
                {
                    Vector3 nv = acc[p];
                    acc[p] = nv.LengthSquared > 1e-12f ? Vector3.Normalize(nv) : Vector3.UnitZ;
                }
                for (int v = 0; v < lm.BaseVertexNormals.Length; v++) lm.BaseVertexNormals[v] = acc[lm.Weld[v]];
            }
        }

        // Group vertices sitting at the same 3D position (within ~0.1 mm) into one
        // particle. Weld[v] = particle id, WeldCount = number of unique positions.
        private static void BuildWeld(LoadedMesh lm)
        {
            if (lm.BaseVertices == null || lm.BaseVertices.Length == 0) { lm.Weld = null; lm.WeldCount = 0; return; }
            int n = lm.BaseVertices.Length;
            var weld = new int[n];
            var groups = new Dictionary<(int, int, int), int>();
            int next = 0;
            for (int v = 0; v < n; v++)
            {
                Vector3 p = lm.BaseVertices[v];
                var key = ((int)Math.Round(p.X * 10000f), (int)Math.Round(p.Y * 10000f), (int)Math.Round(p.Z * 10000f));
                if (!groups.TryGetValue(key, out int id)) { id = next++; groups[key] = id; }
                weld[v] = id;
            }
            lm.Weld = weld;
            lm.WeldCount = next;
        }

        // Mid-point (1->4) subdivision of the selected mesh: gives the cloth solver
        // more particles so it folds/drapes instead of moving a handful of big
        // triangles. Positions, skin weights and per-corner UVs are interpolated;
        // the VBO and (if enabled) the cloth are rebuilt.
        private void SubdivideSelectedMesh(bool smooth)
        {
            var idxs = GetSelectedMeshIndices();
            int si = idxs.Count > 0 ? idxs[0] : -1;
            if (si < 0 || si >= _loadedMeshes.Count) { Log("Subdiviser : aucun mesh sélectionné."); return; }
            var lm = _loadedMeshes[si];
            if (lm.BaseVertices == null || lm.Indices == null || lm.CornerVertex == null || lm.Interleaved == null) return;

            int V = lm.BaseVertices.Length;
            int cc = lm.CornerVertex.Length;
            var oldV = lm.BaseVertices;
            var oldSkin = lm.Skin;
            var oldWrapSkin = lm.WrapSkin;
            var oldOrigSkin = lm.OriginalSkin;

            int oldCorners = lm.Indices.Length;
            var cornerUV = new Vector2[oldCorners];
            for (int c = 0; c < oldCorners; c++)
                cornerUV[c] = (lm.CornerUVs != null && c < lm.CornerUVs.Length)
                    ? lm.CornerUVs[c]
                    : new Vector2(lm.Interleaved[c * 8 + 6], lm.Interleaved[c * 8 + 7]);

            var newV = new List<Vector3>(V * 4);
            for (int v = 0; v < V; v++) newV.Add(oldV[v]);
            var newSkin = new List<VertexInfluence[]>(V * 4);
            for (int v = 0; v < V; v++) newSkin.Add(oldSkin != null && v < oldSkin.Length ? oldSkin[v] : null);

            var newWrapSkin = (oldWrapSkin != null) ? new List<VertexInfluence[]>(V * 4) : null;
            if (newWrapSkin != null)
                for (int v = 0; v < V; v++) newWrapSkin.Add(v < oldWrapSkin.Length ? oldWrapSkin[v] : null);

            var newOrigSkin = (oldOrigSkin != null) ? new List<VertexInfluence[]>(V * 4) : null;
            if (newOrigSkin != null)
                for (int v = 0; v < V; v++) newOrigSkin.Add(v < oldOrigSkin.Length ? oldOrigSkin[v] : null);

            var midMap = new Dictionary<long, int>();
            var outIdx = new List<int>(cc * 4);
            var outUV = new List<Vector2>(cc * 4);

            for (int t = 0; t + 2 < cc; t += 3)
            {
                int v0 = lm.CornerVertex[t], v1 = lm.CornerVertex[t + 1], v2 = lm.CornerVertex[t + 2];
                Vector2 u0 = cornerUV[t], u1 = cornerUV[t + 1], u2 = cornerUV[t + 2];

                int e01 = GetMidVertex(midMap, newV, newSkin, newWrapSkin, newOrigSkin, oldV, oldSkin, oldWrapSkin, oldOrigSkin, v0, v1);
                int e12 = GetMidVertex(midMap, newV, newSkin, newWrapSkin, newOrigSkin, oldV, oldSkin, oldWrapSkin, oldOrigSkin, v1, v2);
                int e20 = GetMidVertex(midMap, newV, newSkin, newWrapSkin, newOrigSkin, oldV, oldSkin, oldWrapSkin, oldOrigSkin, v2, v0);
                Vector2 u01 = (u0 + u1) * 0.5f;
                Vector2 u12 = (u1 + u2) * 0.5f;
                Vector2 u20 = (u2 + u0) * 0.5f;

                outIdx.Add(v0); outUV.Add(u0);
                outIdx.Add(e01); outUV.Add(u01);
                outIdx.Add(e20); outUV.Add(u20);

                outIdx.Add(e01); outUV.Add(u01);
                outIdx.Add(v1); outUV.Add(u1);
                outIdx.Add(e12); outUV.Add(u12);

                outIdx.Add(e20); outUV.Add(u20);
                outIdx.Add(e12); outUV.Add(u12);
                outIdx.Add(v2); outUV.Add(u2);

                outIdx.Add(e01); outUV.Add(u01);
                outIdx.Add(e12); outUV.Add(u12);
                outIdx.Add(e20); outUV.Add(u20);
            }

            // Dependents that borrow this mesh's skin would have stale index maps.
            foreach (var m in _loadedMeshes)
                if (ReferenceEquals(m.WrapSource, lm)) { m.WrapSource = null; m.WrapSkin = null; }

            lm.BaseVertices = newV.ToArray();
            lm.Skin = newSkin.ToArray();
            if (newWrapSkin != null) lm.WrapSkin = newWrapSkin.ToArray();
            if (newOrigSkin != null) lm.OriginalSkin = newOrigSkin.ToArray();
            lm.Indices = outIdx.ToArray();
            lm.CornerUVs = outUV.ToArray();

            if (smooth)
            {
                int nV = lm.BaseVertices.Length;
                if (nV > V)
                {
                    var verts = lm.BaseVertices;
                    int cc2 = outIdx.Count;
                    // Build adjacency
                    var adj = new HashSet<int>[nV];
                    for (int i = 0; i < nV; i++) adj[i] = new HashSet<int>();
                    for (int c = 0; c < cc2; c += 3)
                    {
                        int a = outIdx[c], b = outIdx[c+1], c2v = outIdx[c+2];
                        if (a>=0&&a<nV&&b>=0&&b<nV){ adj[a].Add(b); adj[b].Add(a); }
                        if (b>=0&&b<nV&&c2v>=0&&c2v<nV){ adj[b].Add(c2v); adj[c2v].Add(b); }
                        if (c2v>=0&&c2v<nV&&a>=0&&a<nV){ adj[c2v].Add(a); adj[a].Add(c2v); }
                    }
                    // Compute edge lengths and detect boundary
                    var newPos = new Vector3[nV];
                    for (int i = 0; i < nV; i++)
                    {
                        var list = adj[i];
                        if (list.Count == 0) { newPos[i] = verts[i]; continue; }
                        // boundary if degree unusual? or just smooth less on boundary
                        bool boundary = list.Count < 4; // rough for UO cloth
                        if (i < V)
                        {
                            // original verts - only smooth interior faces, less on edges
                            Vector3 avg = Vector3.Zero;
                            foreach (int nb in list) avg += verts[nb];
                            avg /= list.Count;
                            float t = boundary ? 0.12f : 0.18f;
                            newPos[i] = Vector3.Lerp(verts[i], avg, t);
                        }
                        else
                        {
                            // midpoints - gentle, skip heavy smoothing near boundary
                            Vector3 avg = Vector3.Zero;
                            foreach (int nb in list) avg += verts[nb];
                            avg /= list.Count;
                            float t = boundary ? 0.08f : 0.14f;
                            newPos[i] = Vector3.Lerp(verts[i], avg, t);
                        }
                    }
                    lm.BaseVertices = newPos;
                }
            }

            if (lm.SourceMesh != null)
            {
                lm.SourceMesh.Vertices.Clear();
                lm.SourceMesh.Vertices.AddRange(lm.BaseVertices);
                lm.SourceMesh.Indices.Clear();
                lm.SourceMesh.Indices.AddRange(lm.Indices);
                if (lm.Skin != null)
                {
                    lm.SourceMesh.Skin.Clear();
                    lm.SourceMesh.Skin.AddRange(lm.Skin);
                }
                lm.SourceMesh.UVs.Clear();
                lm.SourceMesh.UVs.AddRange(lm.CornerUVs);
                lm.SourceMesh.UVIndices.Clear();
                for (int i = 0; i < lm.CornerUVs.Length; i++)
                    lm.SourceMesh.UVIndices.Add(i);
            }

            BuildCornerData(lm, lm.CornerUVs);
            SetupMeshVBO(lm);
            lm.DeformedCache = new Vector3[lm.BaseVertices.Length];
            ClearClothBake(lm);              // particle count changed -> must re-bake
            if (lm.IsCloth) BuildClothFromMesh(lm);

            ResetAllCloth();
            ApplyAnimationPose(_currentTime);
            UpdateModelInfo();
            if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();
            Log($"Subdiviser : '{lm.Name}' -> verts={lm.BaseVertices.Length} triangles={lm.Indices.Length / 3}" +
                (lm.IsCloth ? $" (particules={lm.ClothPos.Length})" : ""));
        }

        private static int GetMidVertex(Dictionary<long, int> map, List<Vector3> newV, List<VertexInfluence[]> newSkin,
                                        List<VertexInfluence[]> newWrapSkin, List<VertexInfluence[]> newOrigSkin,
                                        Vector3[] oldV, VertexInfluence[][] oldSkin, VertexInfluence[][] oldWrapSkin, VertexInfluence[][] oldOrigSkin,
                                        int ia, int ib)
        {
            if (ia < 0 || ib < 0 || ia >= oldV.Length || ib >= oldV.Length) return ia >= 0 ? ia : ib;
            long key = ia < ib ? ((long)ia << 32) | (uint)ib : ((long)ib << 32) | (uint)ia;
            if (map.TryGetValue(key, out int id)) return id;
            int nid = newV.Count;
            newV.Add((oldV[ia] + oldV[ib]) * 0.5f);
            newSkin.Add(AverageSkin(oldSkin, ia, ib));
            if (newWrapSkin != null) newWrapSkin.Add(AverageSkin(oldWrapSkin, ia, ib));
            if (newOrigSkin != null) newOrigSkin.Add(AverageSkin(oldOrigSkin, ia, ib));
            map[key] = nid;
            return nid;
        }

        private static VertexInfluence[] AverageSkin(VertexInfluence[][] skin, int ia, int ib)
        {
            VertexInfluence[] a = (skin != null && ia >= 0 && ia < skin.Length) ? skin[ia] : null;
            VertexInfluence[] b = (skin != null && ib >= 0 && ib < skin.Length) ? skin[ib] : null;
            if ((a == null || a.Length == 0) && (b == null || b.Length == 0)) return null;
            if (a == null || a.Length == 0) return b;
            if (b == null || b.Length == 0) return a;

            var acc = new Dictionary<int, float>();
            for (int k = 0; k < a.Length; k++) { acc.TryGetValue(a[k].BoneId, out float w); acc[a[k].BoneId] = w + a[k].Weight * 0.5f; }
            for (int k = 0; k < b.Length; k++) { acc.TryGetValue(b[k].BoneId, out float w); acc[b[k].BoneId] = w + b[k].Weight * 0.5f; }

            var list = new List<VertexInfluence>();
            foreach (var kv in acc) list.Add(new VertexInfluence { BoneId = kv.Key, Weight = kv.Value });
            list.Sort((p, q) => q.Weight.CompareTo(p.Weight));
            if (list.Count > 4) list.RemoveRange(4, list.Count - 4);
            float sum = 0f;
            for (int k = 0; k < list.Count; k++) sum += list[k].Weight;
            if (sum > 1e-6f)
                for (int k = 0; k < list.Count; k++) { var x = list[k]; x.Weight /= sum; list[k] = x; }
            return list.ToArray();
        }

        private void SetupMeshVBO(LoadedMesh lm)
        {
            if (lm.Vao > 0) { GL.DeleteVertexArray(lm.Vao); lm.Vao = 0; }
            if (lm.Vbo > 0) { GL.DeleteBuffer(lm.Vbo); lm.Vbo = 0; }

            lm.Vao = GL.GenVertexArray();
            lm.Vbo = GL.GenBuffer();

            GL.BindVertexArray(lm.Vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, lm.Vbo);

            int stride = 8 * sizeof(float);
            GL.BufferData(BufferTarget.ArrayBuffer, lm.Interleaved.Length * sizeof(float), lm.Interleaved, BufferUsageHint.DynamicDraw);

            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
            GL.EnableVertexAttribArray(2);
            GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));

            GL.BindVertexArray(0);
        }

        private void DestroyMesh(LoadedMesh lm)
        {
            if (lm.Vbo > 0) { GL.DeleteBuffer(lm.Vbo); lm.Vbo = 0; }
            if (lm.Vao > 0) { GL.DeleteVertexArray(lm.Vao); lm.Vao = 0; }
            if (lm.TextureId > 0)
            {
                GL.DeleteTexture(lm.TextureId);
                if (lm.BaseTextureId > 0 && lm.BaseTextureId != lm.TextureId)
                {
                    GL.DeleteTexture(lm.BaseTextureId);
                }
                lm.TextureId = -1;
                lm.BaseTextureId = -1;
            }
            if (lm.BaseTextureBmp != null)
            {
                lm.BaseTextureBmp.Dispose();
                lm.BaseTextureBmp = null;
            }
        }

        private void TvLoaded_DoubleClick(object sender, EventArgs e)
        {
            try
            {
                var node = tvLoaded?.SelectedNode;
                if (node == null) return;
                if (node.Tag is int idx)
                {
                    RemoveMeshAt(idx);
                }
                else if (node.Tag is List<int> list)
                {
                    RemoveMeshesAt(list);
                }
            }
            catch (Exception ex)
            {
                Log($"TvLoaded_DoubleClick error: {ex.Message}\n{ex}");
            }
        }

        // Fill the bone combo from the base (host) skeleton so the user can pick a
        // bone to attach a weapon/armor piece to.
        private void PopulateBoneCombo()
        {
            if (cbBones == null) return;
            string prev = cbBones.Text;
            List<GrannyBone> bones = null;

            // Prioritize primary creature / character skeleton (CutoutRole.Body or Mount)
            foreach (var lm in _loadedMeshes)
            {
                if (lm.Hidden) continue;
                if ((lm.CutoutRole == MeshCutoutRole.Body || lm.CutoutRole == MeshCutoutRole.Mount) &&
                    lm.Bones != null && lm.Bones.Count > 0)
                {
                    if (bones == null || lm.Bones.Count > bones.Count)
                        bones = lm.Bones;
                }
            }

            // Fallback: search all meshes if no explicit body role found
            if (bones == null)
            {
                foreach (var lm in _loadedMeshes)
                {
                    if (lm.Bones != null && lm.Bones.Count > 0 &&
                        (bones == null || lm.Bones.Count > bones.Count))
                        bones = lm.Bones;
                }
            }

            cbBones.BeginUpdate();
            cbBones.Items.Clear();
            cbBones.Items.Add(Localization.Tr("cb_bone_none", "(non attaché)"));
            if (bones != null)
            {
                foreach (var b in bones)
                    if (!string.IsNullOrEmpty(b.Name)) cbBones.Items.Add(b.Name);
            }
            cbBones.EndUpdate();

            if (!string.IsNullOrEmpty(prev) && cbBones.Items.Contains(prev))
            {
                SelectBoneByName(prev);
            }
            else
            {
                cbBones.SelectedIndex = 0;
            }

            // Keep the pose studio's bone list in sync with the loaded skeleton.
            PopulatePoseBones();
        }

        // Select a bone in the (DropDownList) combo by name.
        private void SelectBoneByName(string name)
        {
            if (cbBones == null) return;
            if (string.IsNullOrEmpty(name))
            {
                if (cbBones.Items.Count > 0) cbBones.SelectedIndex = 0;
                return;
            }
            for (int i = 0; i < cbBones.Items.Count; i++)
            {
                if (string.Equals(cbBones.Items[i].ToString(), name, StringComparison.OrdinalIgnoreCase))
                {
                    cbBones.SelectedIndex = i;
                    return;
                }
            }
            if (cbBones.Items.Count > 0) cbBones.SelectedIndex = 0;
        }

        private void RefreshLoadedList()
        {
            if (tvLoaded == null) return;
            tvLoaded.BeginUpdate();
            try
            {
                // Node references are invalidated by the rebuild below.
                _treeMultiSel.Clear();
                _treeSelAnchor = null;

                object prevSelectedTag = tvLoaded.SelectedNode?.Tag;
                var expandedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (TreeNode root in tvLoaded.Nodes)
                {
                    if (root.IsExpanded && root.Name != null)
                        expandedKeys.Add(root.Name);
                    foreach (TreeNode sub in root.Nodes)
                        if (sub.IsExpanded && sub.Name != null)
                            expandedKeys.Add(sub.Name);
                }

                tvLoaded.Nodes.Clear();

                TreeNode nodeToSelect = null;

                // ---- Character body: one root ("H_Female"/"H_Male") whose "+"
                // expands the full part arborescence, grouped by slot. ----
                var slotOrder = new List<string>();
                var slotMeshes = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
                var loose = new List<int>();
                for (int i = 0; i < _loadedMeshes.Count; i++)
                {
                    // Hidden meshes are internal skeleton hosts (the H_Male/H_Female
                    // placeholder box, skin-wrap reference host): never drawn and never
                    // user-facing, so they are not listed in the tree at all.
                    if (_loadedMeshes[i].Hidden) continue;
                    string slot = _loadedMeshes[i].CharSlot;
                    if (string.IsNullOrEmpty(slot)) { loose.Add(i); continue; }
                    if (!slotMeshes.TryGetValue(slot, out var lst))
                    {
                        lst = new List<int>();
                        slotMeshes[slot] = lst;
                        slotOrder.Add(slot);
                    }
                    lst.Add(i);
                }

                if (slotOrder.Count > 0)
                {
                    string charName = _charGender == 2 ? "H_Female" : (_charGender == 1 ? "H_Male" : "Personnage");
                    int charTotal = 0;
                    foreach (var s in slotOrder) charTotal += slotMeshes[s].Count;
                    var charRoot = new TreeNode($"{charName} ({charTotal} parties)") { Name = "char:" + charName, Tag = (object)null };

                    // Order slots in the canonical body layout, extras afterwards.
                    var ordered = new List<string>();
                    foreach (var known in new[] { "pelvis", "torso", "torsoupper", "neck", "head", "face", "ears", "uarms", "farms", "hands", "ulegs", "llegs", "feet" })
                        if (slotMeshes.ContainsKey(known)) ordered.Add(known);
                    foreach (var s in slotOrder)
                        if (!ordered.Contains(s)) ordered.Add(s);

                    foreach (var slot in ordered)
                    {
                        var indices = slotMeshes[slot];
                        string key = "char:" + charName + "/" + slot;
                        var slotNode = new TreeNode($"{SlotDisplayName(slot)} ({indices.Count})") { Name = key, Tag = indices.Count == 1 ? (object)indices[0] : (object)indices };
                        foreach (int i in indices)
                        {
                            var lm = _loadedMeshes[i];
                            string childText = string.IsNullOrEmpty(lm.AttachBoneName)
                                ? lm.Name
                                : $"{lm.Name}  →  {lm.AttachBoneName}";
                            var childNode = new TreeNode(childText) { Tag = i, Name = key + "#" + i };
                            slotNode.Nodes.Add(childNode);
                            if (prevSelectedTag is int prevI && prevI == i) nodeToSelect = childNode;
                        }
                        slotNode.Expand();
                        charRoot.Nodes.Add(slotNode);
                    }
                    charRoot.Expand();
                    tvLoaded.Nodes.Add(charRoot);
                }

                // ---- Standalone meshes (equipment / custom), grouped by source file. ----
                var groups = new List<(string key, string title, List<int> indices)>();
                var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (int i in loose)
                {
                    var lm = _loadedMeshes[i];
                    string key = !string.IsNullOrEmpty(lm.SourcePath) ? lm.SourcePath : lm.Name;
                    if (!map.TryGetValue(key, out int gIndex))
                    {
                        gIndex = groups.Count;
                        map[key] = gIndex;
                        string title = !string.IsNullOrEmpty(lm.SourcePath)
                            ? Path.GetFileNameWithoutExtension(lm.SourcePath)
                            : key;
                        groups.Add((key, title, new List<int>()));
                    }
                    groups[gIndex].indices.Add(i);
                }

                foreach (var grp in groups)
                {
                    string commonBone = null;
                    bool sameBone = true;
                    for (int k = 0; k < grp.indices.Count; k++)
                    {
                        var lm = _loadedMeshes[grp.indices[k]];
                        if (k == 0) commonBone = lm.AttachBoneName;
                        else if (!string.Equals(commonBone, lm.AttachBoneName, StringComparison.OrdinalIgnoreCase))
                        {
                            sameBone = false;
                            break;
                        }
                    }

                    string parentSuffix = (sameBone && !string.IsNullOrEmpty(commonBone))
                        ? $"  →  {commonBone}"
                        : "";

                    string parentText = $"{grp.title} ({grp.indices.Count} meshes){parentSuffix}";
                    var parentNode = new TreeNode(parentText)
                    {
                        Name = grp.key,
                        Tag = grp.indices
                    };

                    foreach (int i in grp.indices)
                    {
                        var lm = _loadedMeshes[i];
                        string meshTitle = lm.Name;
                        if (!string.IsNullOrEmpty(lm.MaterialName) && !lm.MaterialName.Equals(lm.Name, StringComparison.OrdinalIgnoreCase))
                            meshTitle = $"{lm.Name} [{lm.MaterialName}]";

                        string childText = string.IsNullOrEmpty(lm.AttachBoneName)
                            ? meshTitle
                            : $"{meshTitle}  →  {lm.AttachBoneName}";

                        var childNode = new TreeNode(childText)
                        {
                            Tag = i
                        };
                        parentNode.Nodes.Add(childNode);

                        if (prevSelectedTag is int prevI && prevI == i)
                            nodeToSelect = childNode;
                    }

                    if (expandedKeys.Count == 0 || expandedKeys.Contains(grp.key))
                        parentNode.Expand();

                    tvLoaded.Nodes.Add(parentNode);

                    if (nodeToSelect == null && prevSelectedTag is List<int> prevList &&
                        prevList.Count == grp.indices.Count && prevList.TrueForAll(grp.indices.Contains))
                        nodeToSelect = parentNode;
                }

                if (nodeToSelect != null)
                    tvLoaded.SelectedNode = nodeToSelect;
                else if (tvLoaded.Nodes.Count > 0 && tvLoaded.SelectedNode == null)
                    tvLoaded.SelectedNode = tvLoaded.Nodes[0];
            }
            finally
            {
                tvLoaded.EndUpdate();
            }
        }

        private static string SlotDisplayName(string slot)
        {
            switch ((slot ?? "").ToLowerInvariant())
            {
                case "pelvis": return "Bassin";
                case "torso": return "Torse";
                case "torsoupper": return "Torse haut";
                case "neck": return "Cou";
                case "head": return "Tête";
                case "face": return "Visage";
                case "ears": return "Oreilles";
                case "hair": return "Cheveux";
                case "beard": return "Barbe";
                case "uarms": return "Bras sup.";
                case "farms": return "Avant-bras";
                case "hands": return "Mains";
                case "ulegs": return "Cuisses";
                case "llegs": return "Jambes";
                case "feet": return "Pieds";
                default: return slot;
            }
        }

        private void TvLoaded_AfterSelect(object sender, TreeViewEventArgs e)
        {
            if (e.Node == null) return;
            var idxs = GetSelectedMeshIndices();
            if (idxs.Count > 0 && cbBones != null)
            {
                var first = _loadedMeshes[idxs[0]];
                SelectBoneByName(first.AttachBoneName);
            }
            else
            {
                SelectBoneByName(null);
            }
            LoadEquipControlsFromMesh();
            SyncHueFromSelectedMesh();
            if (_poseTargetMode == 1) { SyncPoseTargetsFromTree(); Repose(); }
        }

        // Recover the flat list of mesh indices a node stands for (int = one mesh,
        // List<int> = a group/part, other/null = character root -> all part children).
        private static void CollectNodeIndices(TreeNode n, List<int> outList, int meshCount)
        {
            if (n == null) return;
            switch (n.Tag)
            {
                case int idx:
                    if (idx >= 0 && idx < meshCount && !outList.Contains(idx)) outList.Add(idx);
                    break;
                case List<int> lst:
                    foreach (int i in lst)
                        if (i >= 0 && i < meshCount && !outList.Contains(i)) outList.Add(i);
                    break;
                default:
                    foreach (TreeNode c in n.Nodes) CollectNodeIndices(c, outList, meshCount);
                    break;
            }
        }

        private void TvLoaded_NodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Node == null || e.Button != MouseButtons.Left) return;
            // Only rebuild the multi-selection when clicking outside the checkbox glyph
            // (17px on the left) so toggling a checkbox keeps working normally.
            bool onCheck = e.X < e.Node.Bounds.Left + 16;
            var mods = Control.ModifierKeys;

            if (onCheck || (mods & (Keys.Control | Keys.Shift)) == 0)
            {
                _treeMultiSel.Clear();
                _treeMultiSel.Add(e.Node);
                _treeSelAnchor = e.Node;
                if (onCheck) { tvLoaded.Invalidate(); return; }
                return;
            }

            if ((mods & Keys.Shift) != 0 && _treeSelAnchor != null)
            {
                var flat = new List<TreeNode>();
                FlattenVisible(tvLoaded.Nodes, flat);
                int a = flat.IndexOf(_treeSelAnchor), b = flat.IndexOf(e.Node);
                if (a >= 0 && b >= 0)
                {
                    _treeMultiSel.Clear();
                    int lo = Math.Min(a, b), hi = Math.Max(a, b);
                    for (int i = lo; i <= hi; i++) _treeMultiSel.Add(flat[i]);
                }
            }
            else // Ctrl
            {
                if (!_treeMultiSel.Remove(e.Node)) _treeMultiSel.Add(e.Node);
                _treeSelAnchor = e.Node;
            }
            tvLoaded.Invalidate();
            if (_poseTargetMode == 1) { SyncPoseTargetsFromTree(); Repose(); }
        }

        private void TvLoaded_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape && _treeMultiSel.Count > 0)
            {
                _treeMultiSel.Clear();
                tvLoaded.Invalidate();
                e.Handled = true;
            }
        }

        private static void FlattenVisible(TreeNodeCollection nodes, List<TreeNode> outList)
        {
            foreach (TreeNode n in nodes)
            {
                outList.Add(n);
                if (n.IsExpanded && n.Nodes.Count > 0) FlattenVisible(n.Nodes, outList);
            }
        }

        private void TvLoaded_DrawNode(object sender, DrawTreeNodeEventArgs e)
        {
            bool selected = _treeMultiSel.Contains(e.Node);
            if (!selected)
            {
                e.DrawDefault = true;
                return;
            }
            var bounds = new Rectangle(e.Bounds.X - 2, e.Bounds.Y - 1, e.Bounds.Width + 4, e.Bounds.Height + 2);
            using (var b = new SolidBrush(Color.FromArgb(70, 110, 170)))
                e.Graphics.FillRectangle(b, bounds);
            TextRenderer.DrawText(e.Graphics, e.Node.Text, tvLoaded.Font,
                new Point(e.Bounds.X, e.Bounds.Y), Color.White);
        }

        private bool _suppressTreeCheck;
        private void TvLoaded_AfterCheck(object sender, TreeViewEventArgs e)
        {
            if (_suppressTreeCheck || e.Node == null) return;
            _suppressTreeCheck = true;
            try
            {
                SetCheckedRecursive(e.Node, e.Node.Checked);
            }
            finally { _suppressTreeCheck = false; }
            LoadEquipControlsFromMesh();
            if (_poseTargetMode == 1) { SyncPoseTargetsFromTree(); Repose(); }
        }

        private void SetCheckedRecursive(TreeNode node, bool value)
        {
            foreach (TreeNode child in node.Nodes)
            {
                child.Checked = value;
                SetCheckedRecursive(child, value);
            }
        }

        // Indices of the meshes currently selected or checked in the "Meshes affichés"
        // TreeView (supports Ctrl/Shift multi-selection, checking parent or individual
        // nodes for batch operations).
        private List<int> GetSelectedMeshIndices()
        {
            var list = new List<int>();
            if (tvLoaded == null) return list;

            // 1. Ctrl/Shift multi-selection (highest priority)
            if (_treeMultiSel.Count > 0)
            {
                foreach (var n in _treeMultiSel)
                    CollectNodeIndices(n, list, _loadedMeshes.Count);
                if (list.Count > 0) return list;
            }

            // 2. Otherwise, any nodes with checkboxes checked
            Action<TreeNodeCollection> collectChecked = null;
            collectChecked = (nodes) =>
            {
                foreach (TreeNode n in nodes)
                {
                    if (n.Checked)
                        CollectNodeIndices(n, list, _loadedMeshes.Count);
                    if (n.Nodes.Count > 0)
                        collectChecked(n.Nodes);
                }
            };
            collectChecked(tvLoaded.Nodes);

            if (list.Count > 0) return list;

            // 3. Otherwise use the currently selected node
            CollectNodeIndices(tvLoaded.SelectedNode, list, _loadedMeshes.Count);
            return list;
        }

        // Push the equipment-controller values onto every selected mesh and re-pose.
        private void ApplyEquipControls()
        {
            if (_suppressEquip) return;
            var idxs = GetSelectedMeshIndices();
            if (idxs.Count == 0) return;

            foreach (var i in idxs) ApplyEquipToMesh(_loadedMeshes[i]);

            // Re-pose for any mesh (skinned parts are driven by their own skeleton;
            // rigid weapons by their attach bone). The offset/scale tweak applies to
            // both paths. ApplyAnimationPose also handles the no-animation case.
            ApplyAnimationPose(_currentTime);
            RefreshVertexBuffers(true);
            if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();
        }

        // Copy the placement widgets onto a single mesh's stored tweak.
        private void ApplyEquipToMesh(LoadedMesh lm)
        {
            lm.MirrorX = cbMirrorX.Checked;
            lm.MirrorY = cbMirrorY.Checked;
            lm.MirrorZ = cbMirrorZ.Checked;
            lm.MirrorArms = cbMirrorArms != null && cbMirrorArms.Checked;
            lm.AttachRotation = new Vector3((float)nudRotX.Value, (float)nudRotY.Value, (float)nudRotZ.Value);
            lm.AttachOffset = new Vector3((float)nudOffX.Value, (float)nudOffY.Value, (float)nudOffZ.Value);
            lm.PartScale = nudScale != null ? (float)nudScale.Value : 1f;
            lm.PartScaleVec = new Vector3(
                nudScaleX != null ? (float)nudScaleX.Value : 1f,
                nudScaleY != null ? (float)nudScaleY.Value : 1f,
                nudScaleZ != null ? (float)nudScaleZ.Value : 1f);
            lm.Thickness = nudThickness != null ? (float)nudThickness.Value : 0f;
            if (lm.IsCloth)
            {
                if (nudClothGrav != null)
                {
                    float g = (float)nudClothGrav.Value;
                    if (lm.ClothBake != null && Math.Abs(g - lm.ClothBakeGravity) > 1e-4f) ClearClothBake(lm);
                    lm.ClothGravity = g;
                }
                ClearClothBake(lm);
                BuildClothFromMesh(lm);
                ResetAllCloth();
            }
        }

        private void ResetEquipControls()
        {
            _suppressEquip = true;
            try
            {
                cbMirrorX.Checked = cbMirrorY.Checked = cbMirrorZ.Checked = false;
                if (cbMirrorArms != null) cbMirrorArms.Checked = false;
                nudRotX.Value = nudRotY.Value = nudRotZ.Value = 0;
                nudOffX.Value = nudOffY.Value = nudOffZ.Value = 0;
                if (nudScale != null) nudScale.Value = 1m;
                if (nudScaleX != null) nudScaleX.Value = 1m;
                if (nudScaleY != null) nudScaleY.Value = 1m;
                if (nudScaleZ != null) nudScaleZ.Value = 1m;
                if (nudThickness != null) nudThickness.Value = 0m;
            }
            finally { _suppressEquip = false; }
            ApplyEquipControls();
        }

        private void DuplicateMirrorSelectedMesh()
        {
            var idxs = GetSelectedMeshIndices();
            if (idxs.Count == 0)
            {
                MessageBox.Show("Sélectionnez d'abord un mesh à dupliquer en miroir.", "Dupliquer en miroir", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int addedCount = 0;
            foreach (int idx in idxs)
            {
                if (idx < 0 || idx >= _loadedMeshes.Count) continue;
                var src = _loadedMeshes[idx];
                if (src.BaseVertices == null || src.BaseVertices.Length == 0) continue;

                var clone = new LoadedMesh
                {
                    Name = $"{src.Name} (Miroir)",
                    SourcePath = src.SourcePath,
                    SourceMesh = src.SourceMesh,
                    Bones = src.Bones != null ? new List<GrannyBone>(src.Bones) : new List<GrannyBone>(),
                    BaseVertices = new Vector3[src.BaseVertices.Length],
                    Indices = src.Indices != null ? (int[])src.Indices.Clone() : Array.Empty<int>(),
                    Skin = src.Skin != null ? (VertexInfluence[][])src.Skin.Clone() : null,
                    CutoutRole = src.CutoutRole,
                    TextureId = src.TextureId,
                    HasTexture = src.HasTexture,
                    TextureFilePath = src.TextureFilePath,
                    BaseTextureId = src.BaseTextureId,
                    BaseMeshColor = src.BaseMeshColor,
                    HueId = src.HueId,
                    PartialHue = src.PartialHue,
                    Desaturate = src.Desaturate,
                    PartScale = src.PartScale,
                    PartScaleVec = src.PartScaleVec,
                    Thickness = src.Thickness,
                    MirrorX = false,
                    MirrorY = src.MirrorY,
                    MirrorZ = src.MirrorZ,
                    AttachRotation = new Vector3(src.AttachRotation.X, -src.AttachRotation.Y, -src.AttachRotation.Z),
                    AttachOffset = new Vector3(-src.AttachOffset.X, src.AttachOffset.Y, src.AttachOffset.Z),
                    AttachInBindSpace = src.AttachInBindSpace
                };

                // Mirror base vertices across sagittal plane X=0
                for (int v = 0; v < src.BaseVertices.Length; v++)
                {
                    var p = src.BaseVertices[v];
                    clone.BaseVertices[v] = new Vector3(-p.X, p.Y, p.Z);
                }

                // Invert triangle winding so face normals point outward
                if (clone.Indices != null)
                {
                    for (int t = 0; t + 2 < clone.Indices.Length; t += 3)
                    {
                        int tmp = clone.Indices[t + 1];
                        clone.Indices[t + 1] = clone.Indices[t + 2];
                        clone.Indices[t + 2] = tmp;
                    }
                }

                // Switch bone attach name if right/left
                if (!string.IsNullOrEmpty(src.AttachBoneName))
                {
                    string b = src.AttachBoneName;
                    if (b.IndexOf(" R ", StringComparison.OrdinalIgnoreCase) >= 0)
                        b = System.Text.RegularExpressions.Regex.Replace(b, @"\b[Rr]\b", "L");
                    else if (b.IndexOf("_R", StringComparison.OrdinalIgnoreCase) >= 0)
                        b = System.Text.RegularExpressions.Regex.Replace(b, @"_[Rr]\b", "_L");
                    else if (b.IndexOf("Rhand", StringComparison.OrdinalIgnoreCase) >= 0)
                        b = System.Text.RegularExpressions.Regex.Replace(b, @"[Rr]hand", "Lhand");
                    else if (b.IndexOf("Right", StringComparison.OrdinalIgnoreCase) >= 0)
                        b = System.Text.RegularExpressions.Regex.Replace(b, @"Right", "Left");
                    else if (b.IndexOf(" L ", StringComparison.OrdinalIgnoreCase) >= 0)
                        b = System.Text.RegularExpressions.Regex.Replace(b, @"\b[Ll]\b", "R");
                    else if (b.IndexOf("_L", StringComparison.OrdinalIgnoreCase) >= 0)
                        b = System.Text.RegularExpressions.Regex.Replace(b, @"_[Ll]\b", "_R");
                    else if (b.IndexOf("Lhand", StringComparison.OrdinalIgnoreCase) >= 0)
                        b = System.Text.RegularExpressions.Regex.Replace(b, @"[Ll]hand", "Rhand");
                    else if (b.IndexOf("Left", StringComparison.OrdinalIgnoreCase) >= 0)
                        b = System.Text.RegularExpressions.Regex.Replace(b, @"Left", "Right");

                    clone.AttachBoneName = b;
                }

                BuildCornerData(clone);
                SetupMeshVBO(clone);
                _loadedMeshes.Add(clone);
                addedCount++;
                Log($"Mesh dupliqué en miroir : '{src.Name}' -> '{clone.Name}'");
            }

            if (addedCount > 0)
            {
                RefreshLoadedList();
                RefreshVertexBuffers(true);
                if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();
            }
        }

        // Reflect the selected mesh's stored placement tweak in the controller widgets.
        private void LoadEquipControlsFromMesh()
        {
            if (nudRotX == null) return;
            _suppressEquip = true;
            try
            {
                var idxs = GetSelectedMeshIndices();
                int nSel = idxs.Count;
                bool ok = nSel > 0;
                var lm = ok ? _loadedMeshes[idxs[0]] : null;

                // Multiple meshes selected: show batch target label.
                if (nSel > 1)
                {
                    string groupName = (tvLoaded?.SelectedNode?.Tag is List<int>)
                        ? tvLoaded.SelectedNode.Text
                        : $"{nSel} meshes sélectionnés";
                    if (lblEquipTarget != null)
                        lblEquipTarget.Text = $"Cible : {groupName} (édition par lot)";
                }
                else
                {
                    if (lblEquipTarget != null)
                        lblEquipTarget.Text = ok
                            ? $"Cible : {lm.Name}" + (string.IsNullOrEmpty(lm.AttachBoneName) ? "  (non attaché)" : $"  → {lm.AttachBoneName}")
                            : "Cible : (aucun)";
                }

                cbMirrorX.Checked = ok && lm.MirrorX;
                cbMirrorY.Checked = ok && lm.MirrorY;
                cbMirrorZ.Checked = ok && lm.MirrorZ;
                if (cbMirrorArms != null) cbMirrorArms.Checked = ok && lm.MirrorArms;
                SetNud(nudRotX, ok ? lm.AttachRotation.X : 0);
                SetNud(nudRotY, ok ? lm.AttachRotation.Y : 0);
                SetNud(nudRotZ, ok ? lm.AttachRotation.Z : 0);
                SetNud(nudOffX, ok ? lm.AttachOffset.X : 0);
                SetNud(nudOffY, ok ? lm.AttachOffset.Y : 0);
                SetNud(nudOffZ, ok ? lm.AttachOffset.Z : 0);
                if (nudScale != null) SetNud(nudScale, ok ? lm.PartScale : 1f);
                if (nudScaleX != null) SetNud(nudScaleX, ok ? lm.PartScaleVec.X : 1f);
                if (nudScaleY != null) SetNud(nudScaleY, ok ? lm.PartScaleVec.Y : 1f);
                if (nudScaleZ != null) SetNud(nudScaleZ, ok ? lm.PartScaleVec.Z : 1f);
                if (nudThickness != null) SetNud(nudThickness, ok ? lm.Thickness : 0f);
                if (chkCloth != null) chkCloth.Checked = ok && lm.IsCloth;
                if (nudClothGrav != null) SetNud(nudClothGrav, ok ? lm.ClothGravity : 9.8f);
                if (cmbCutoutRole != null)
                {
                    int roleIdx = ok ? (int)lm.CutoutRole : 0;
                    if (roleIdx >= 0 && roleIdx < cmbCutoutRole.Items.Count)
                        cmbCutoutRole.SelectedIndex = roleIdx;
                    else if (cmbCutoutRole.Items.Count > 0)
                        cmbCutoutRole.SelectedIndex = 0;
                }
            }
            finally { _suppressEquip = false; }
        }

        private void CmbCutoutRole_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_suppressEquip || cmbCutoutRole == null) return;
            int sel = cmbCutoutRole.SelectedIndex;
            if (sel < 0) return;
            var role = (MeshCutoutRole)sel;
            foreach (var i in GetSelectedMeshIndices())
                _loadedMeshes[i].CutoutRole = role;
        }

        private static void SetNud(NumericUpDown n, float v)
        {
            decimal d = (decimal)v;
            n.Value = Math.Max(n.Minimum, Math.Min(n.Maximum, d));
        }

        // Skin-wrap: bind the selected accessory to the SURFACE of the entire body (all body parts:
        // ============================================================================
        // Torso, UArms, FArms, Hands, Pelvis, ULegs, LLegs, Feet, Head, Neck...) by copying, for each
        // accessory vertex, the skin weights of the nearest body vertices across the full body model,
        // remapping all influences into the master host skeleton (e.g. 43-bone human).
        private void SkinWrapSelected()
        {
            var idxs = GetSelectedMeshIndices();
            if (idxs.Count == 0)
            {
                Log("Skin wrap : aucun mesh sélectionné.");
                MessageBox.Show("Veuillez sélectionner ou cocher au moins un mesh à lier (skin wrap).", "Skin wrap", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // Check if all selected are already wrapped -> toggle detach
            bool allWrapped = true;
            foreach (int ti in idxs)
            {
                if (_loadedMeshes[ti].WrapSource == null)
                {
                    allWrapped = false;
                    break;
                }
            }

            if (allWrapped)
            {
                foreach (int ti in idxs)
                {
                    var target = _loadedMeshes[ti];
                    Log($"Skin wrap : '{target.Name}' détaché.");
                    target.WrapSource = null;
                    target.WrapSkin = null;

                    // Restore original skeleton, skin weights and attachment state
                    if (target.HasOriginalWrapBackup)
                    {
                        if (target.OriginalBones != null)
                            target.Bones = new List<GrannyBone>(target.OriginalBones);
                        if (target.OriginalSkin != null)
                            target.Skin = target.OriginalSkin;
                        target.AttachBoneName = target.OriginalAttachBoneName;
                        target.HasAttachMatrix = target.OriginalHasAttachMatrix;
                        target.AttachMatrix = target.OriginalAttachMatrix;
                        target.CutoutRole = target.OriginalCutoutRole;

                        target.OriginalBones = null;
                        target.OriginalSkin = null;
                        target.OriginalAttachBoneName = null;
                        target.OriginalHasAttachMatrix = false;
                        target.OriginalAttachMatrix = Matrix4.Identity;
                        target.HasOriginalWrapBackup = false;
                    }

                    target.BoneToAnimTrack = null;
                    target.LocalMatrices = null;
                    target.WorldMatrices = null;
                    target.SkinMatrices = null;

                    if (target.Skin == null || target.Skin.Length == 0)
                        TryAutoAttachUnskinned(target);
                }
                if (_currentAnimation != null)
                    EnsureAnimationBindings(_currentAnimation);
                ApplyAnimationPose(_currentTime);
                RefreshVertexBuffers(true);
                if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();
                if (lblModelInfo != null) lblModelInfo.Text = $"{idxs.Count} mesh(es) détaché(s) du skin wrap";
                return;
            }

            // 1. Collect ALL body source meshes (Torso, Arms, Legs, Hands, Feet, Pelvis, Head...)
            var targetSet = new HashSet<int>(idxs);
            var sources = new List<LoadedMesh>();

            // First pass: collect explicit body parts (CutoutRole.Body or CharSlot)
            foreach (var m in _loadedMeshes)
            {
                int mIdx = _loadedMeshes.IndexOf(m);
                if (targetSet.Contains(mIdx)) continue;
                if (m.Hidden) continue;
                if (m.WrapSource != null) continue;
                if (m.Skin == null || m.Skin.Length == 0) continue;
                if (m.Bones == null || m.Bones.Count == 0) continue;
                if (m.BaseVertices == null || m.BaseVertices.Length == 0) continue;

                if (m.CutoutRole == MeshCutoutRole.Body || !string.IsNullOrEmpty(m.CharSlot))
                    sources.Add(m);
            }

            // Fallback: if no explicit body meshes found (e.g. custom character), accept any visible skinned meshes
            if (sources.Count == 0)
            {
                foreach (var m in _loadedMeshes)
                {
                    int mIdx = _loadedMeshes.IndexOf(m);
                    if (targetSet.Contains(mIdx)) continue;
                    if (m.Hidden) continue;
                    if (m.WrapSource != null) continue;
                    if (m.Skin == null || m.Skin.Length == 0) continue;
                    if (m.Bones == null || m.Bones.Count == 0) continue;
                    if (m.BaseVertices == null || m.BaseVertices.Length == 0) continue;
                    sources.Add(m);
                }
            }

            if (sources.Count == 0)
            {
                Log("Skin wrap : aucun mesh de corps skinné disponible comme source.");
                MessageBox.Show("Aucune pièce de corps skinnée trouvée pour transférer les poids.", "Skin wrap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 2. Identify or create the master host skeleton covering all body bones
            LoadedMesh masterHost = null;
            int maxBones = -1;
            foreach (var m in _loadedMeshes)
            {
                if (m.Bones != null && m.Bones.Count > maxBones)
                {
                    maxBones = m.Bones.Count;
                    masterHost = m;
                }
            }

            // If the best skeleton in the scene has fewer than 40 bones and we are assembling humanoid parts,
            // load the full 43-bone reference skeleton (H_Male_Lod2 / H_Female_Lod2) or synthesize a unified host.
            bool isHumanoidPartAssembly = _charGender != 0 || sources.Any(s => !string.IsNullOrEmpty(s.CharSlot));
            if (masterHost == null || (maxBones < 40 && isHumanoidPartAssembly))
            {
                bool preferFemale = _charGender == 2;
                foreach (var s in sources)
                {
                    if (s.Name != null && s.Name.IndexOf("female", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        preferFemale = true;
                        break;
                    }
                }

                string refFile = preferFemale ? "H_Female_Lod2.grn" : "H_Male_Lod2.grn";
                string refPath = Path.Combine(BasePath, "Humans", refFile);
                if (!File.Exists(refPath)) refPath = Path.Combine(BasePath, "Humans", "H_Male_Lod2.grn");

                if (File.Exists(refPath))
                {
                    var parser = new GrannyFormatParser();
                    if (parser.Load(refPath) && parser.Bones.Count >= 40)
                    {
                        var host = new LoadedMesh
                        {
                            Name = $"[Host Skeleton] {Path.GetFileNameWithoutExtension(refPath)}",
                            SourcePath = refPath,
                            Bones = new List<GrannyBone>(parser.Bones),
                            BaseVertices = parser.Meshes.Count > 0 ? parser.Meshes[0].Vertices.ToArray() : new Vector3[0],
                            Indices = parser.Meshes.Count > 0 ? parser.Meshes[0].Indices.ToArray() : new int[0],
                            CutoutRole = MeshCutoutRole.Body,
                            Hidden = true
                        };
                        _loadedMeshes.Add(host);
                        masterHost = host;
                        maxBones = host.Bones.Count;
                        Log($"Skin wrap : squelette de référence chargé automatiquement ({refFile}, {maxBones} os).");
                    }
                }

                // If still under 2 bones, synthesize a complete skeleton from all source parts
                if (masterHost == null || maxBones < 2)
                {
                    var synthBones = new List<GrannyBone>();
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var s in sources)
                    {
                        if (s.Bones == null) continue;
                        foreach (var b in s.Bones)
                        {
                            if (seen.Add(b.Name))
                            {
                                synthBones.Add(new GrannyBone
                                {
                                    Name = b.Name,
                                    ParentId = -1,
                                    LocalPosition = b.LocalPosition,
                                    LocalRotation = b.LocalRotation,
                                    InverseBindMatrix = b.InverseBindMatrix
                                });
                            }
                        }
                    }

                    // Re-link ParentId
                    for (int bi = 0; bi < synthBones.Count; bi++)
                    {
                        var mb = synthBones[bi];
                        foreach (var s in sources)
                        {
                            if (s.Bones == null) continue;
                            int li = s.Bones.FindIndex(b => string.Equals(b.Name, mb.Name, StringComparison.OrdinalIgnoreCase));
                            if (li >= 0)
                            {
                                int p = s.Bones[li].ParentId;
                                if (p >= 0 && p < s.Bones.Count)
                                {
                                    string pName = s.Bones[p].Name;
                                    int mp = synthBones.FindIndex(b => string.Equals(b.Name, pName, StringComparison.OrdinalIgnoreCase));
                                    if (mp >= 0 && mp != bi)
                                    {
                                        mb.ParentId = mp;
                                        break;
                                    }
                                }
                            }
                        }
                    }

                    if (synthBones.Count > 0)
                    {
                        var host = new LoadedMesh
                        {
                            Name = "[Host Skeleton Synthétisé]",
                            Bones = synthBones,
                            BaseVertices = new Vector3[0],
                            Indices = new int[0],
                            CutoutRole = MeshCutoutRole.Body,
                            Hidden = true
                        };
                        _loadedMeshes.Add(host);
                        masterHost = host;
                        maxBones = host.Bones.Count;
                        Log($"Skin wrap : squelette synthétisé avec succès ({maxBones} os uniques combinés).");
                    }
                }
            }

            if (masterHost == null || maxBones < 2)
            {
                Log("Skin wrap : aucun squelette hôte valide trouvé sur la scène.");
                MessageBox.Show("Impossible d'effectuer le skin wrap : aucun personnage/squelette hôte n'est présent sur la scène.", "Skin wrap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (_currentAnimation != null)
                EnsureAnimationBindings(_currentAnimation);

            // Ensure master host has matrices computed
            if (masterHost.SkinMatrices == null || masterHost.SkinMatrices.Length != masterHost.Bones.Count)
            {
                ApplyAnimationPose(_currentTime);
            }

            // 3. Build bone mapping tables from each source's local bones to masterHost bones
            var sourceBoneMaps = new int[sources.Count][];
            for (int s = 0; s < sources.Count; s++)
            {
                var src = sources[s];
                var map = new int[src.Bones.Count];
                for (int b = 0; b < src.Bones.Count; b++)
                {
                    string bName = src.Bones[b].Name;
                    int masterIdx = masterHost.Bones.FindIndex(mb => string.Equals(mb.Name, bName, StringComparison.OrdinalIgnoreCase));
                    map[b] = (masterIdx >= 0) ? masterIdx : 0;
                }
                sourceBoneMaps[s] = map;
            }

            // 4. Pool ALL body vertices across ALL sources into a single sample list
            var allSamples = new List<(Vector3 pos, int srcIdx, int localVertIdx)>();
            for (int s = 0; s < sources.Count; s++)
            {
                var sv = sources[s].BaseVertices;
                var sk = sources[s].Skin;
                for (int k = 0; k < sv.Length; k++)
                {
                    if (k < sk.Length && sk[k] != null && sk[k].Length > 0)
                    {
                        allSamples.Add((sv[k], s, k));
                    }
                }
            }

            if (allSamples.Count == 0)
            {
                Log("Skin wrap : aucun sommet skinné valide dans les sources.");
                return;
            }

            const int K = 4; // number of nearest body neighbours to blend
            var tmpSample = new int[K];
            var tmpDist = new float[K];
            var acc = new Dictionary<int, float>();

            // Pre-locate key leg and limb bones in master skeleton
            int rCalfIdx = masterHost.Bones.FindIndex(b => b.Name.IndexOf("R Calf", StringComparison.OrdinalIgnoreCase) >= 0);
            int lCalfIdx = masterHost.Bones.FindIndex(b => b.Name.IndexOf("L Calf", StringComparison.OrdinalIgnoreCase) >= 0);
            int rThighIdx = masterHost.Bones.FindIndex(b => b.Name.IndexOf("R Thigh", StringComparison.OrdinalIgnoreCase) >= 0);
            int lThighIdx = masterHost.Bones.FindIndex(b => b.Name.IndexOf("L Thigh", StringComparison.OrdinalIgnoreCase) >= 0);
            int rHandIdx = masterHost.Bones.FindIndex(b => b.Name.IndexOf("R Hand", StringComparison.OrdinalIgnoreCase) >= 0);
            int lHandIdx = masterHost.Bones.FindIndex(b => b.Name.IndexOf("L Hand", StringComparison.OrdinalIgnoreCase) >= 0);

            var srcIsULegs = new bool[sources.Count];
            var srcIsLLegs = new bool[sources.Count];
            for (int s = 0; s < sources.Count; s++)
            {
                string sName = sources[s].Name ?? "";
                srcIsULegs[s] = sName.IndexOf("ULegs", StringComparison.OrdinalIgnoreCase) >= 0;
                srcIsLLegs[s] = sName.IndexOf("LLegs", StringComparison.OrdinalIgnoreCase) >= 0;
            }

            int totalWrappedMeshes = 0;

            foreach (int ti in idxs)
            {
                var target = _loadedMeshes[ti];
                if (target.BaseVertices == null || target.BaseVertices.Length == 0) continue;

                // 1. If target was attached to a bone, detach it cleanly
                if (!string.IsNullOrEmpty(target.AttachBoneName))
                {
                    target.AttachBoneName = null;
                    target.HasAttachMatrix = false;
                    target.AttachMatrix = Matrix4.Identity;
                    if (IsClothingOrSkinned(target))
                    {
                        target.AttachOffset = Vector3.Zero;
                        target.AttachRotation = Vector3.Zero;
                        target.PartScale = 1f;
                        target.PartScaleVec = Vector3.One;
                    }
                }

                // 2. If target was tweaked (placed, scaled, rotated), bake the placement into BaseVertices
                // so that wrap weights are calculated from the fitted rest position, and reset AttachOffset to 0
                // to prevent any double-offset / jump!
                if (IsMeshTweaked(target))
                {
                    BakeMeshGeometry(target);
                }

                int n = target.BaseVertices.Length;
                var wrap = new VertexInfluence[n][];
                int wrappedVerts = 0;

                for (int v = 0; v < n; v++)
                {
                    Vector3 p = target.BaseVertices[v];

                    for (int k = 0; k < K; k++)
                    {
                        tmpSample[k] = -1;
                        tmpDist[k] = float.MaxValue;
                    }

                    // Pass 1: Anatomically constrained search (bilateral isolation + vertical leg separation)
                    for (int si = 0; si < allSamples.Count; si++)
                    {
                        var samp = allSamples[si];

                        // 1. Bilateral leg isolation below pelvis: prevent opposite leg cross-talk
                        if (p.Z < 0.88f)
                        {
                            if (p.X < -0.01f && samp.pos.X > 0.01f) continue;
                            if (p.X > 0.01f && samp.pos.X < -0.01f) continue;
                        }

                        // 2. Anatomical vertical separation between shin (Calf) and thigh
                        if (p.Z < 0.52f)
                        {
                            // In shin region (below knee): do not sample thigh vertices
                            if (srcIsULegs[samp.srcIdx]) continue;
                            if (samp.pos.Z > 0.55f) continue;
                        }
                        else if (p.Z >= 0.56f && p.Z < 0.88f)
                        {
                            // In thigh region (above knee): do not sample shin vertices
                            if (srcIsLLegs[samp.srcIdx]) continue;
                            if (samp.pos.Z < 0.48f) continue;
                        }

                        float dx = p.X - samp.pos.X;
                        float dy = p.Y - samp.pos.Y;
                        float dz = p.Z - samp.pos.Z;
                        float d = dx * dx + dy * dy + dz * dz * 2.5f;

                        if (d < tmpDist[K - 1])
                        {
                            int pos = K - 1;
                            while (pos > 0 && d < tmpDist[pos - 1])
                            {
                                tmpDist[pos] = tmpDist[pos - 1];
                                tmpSample[pos] = tmpSample[pos - 1];
                                pos--;
                            }
                            tmpDist[pos] = d;
                            tmpSample[pos] = si;
                        }
                    }

                    // Fallback pass: if strict filters didn't find enough samples, relax height restrictions
                    if (tmpSample[0] < 0)
                    {
                        for (int si = 0; si < allSamples.Count; si++)
                        {
                            var samp = allSamples[si];
                            if (p.Z < 0.88f)
                            {
                                if (p.X < -0.01f && samp.pos.X > 0.01f) continue;
                                if (p.X > 0.01f && samp.pos.X < -0.01f) continue;
                            }
                            float d = Vector3.DistanceSquared(p, samp.pos);
                            if (d < tmpDist[K - 1])
                            {
                                int pos = K - 1;
                                while (pos > 0 && d < tmpDist[pos - 1])
                                {
                                    tmpDist[pos] = tmpDist[pos - 1];
                                    tmpSample[pos] = tmpSample[pos - 1];
                                    pos--;
                                }
                                tmpDist[pos] = d;
                                tmpSample[pos] = si;
                            }
                        }
                    }

                    // Blend the K neighbours' influences, weighted by inverse distance
                    acc.Clear();
                    for (int k = 0; k < K; k++)
                    {
                        int sIdx = tmpSample[k];
                        if (sIdx < 0) continue;
                        var sample = allSamples[sIdx];
                        var src = sources[sample.srcIdx];
                        var infs = src.Skin[sample.localVertIdx];
                        if (infs == null || infs.Length == 0) continue;

                        float dist = (float)Math.Sqrt(tmpDist[k]);
                        float wdist = 1f / (dist + 0.001f);

                        var map = sourceBoneMaps[sample.srcIdx];
                        for (int m = 0; m < infs.Length; m++)
                        {
                            int bId = infs[m].BoneId;
                            if (bId >= 0 && bId < map.Length)
                            {
                                int masterBoneId = map[bId];
                                string origName = (masterBoneId >= 0 && masterBoneId < masterHost.Bones.Count)
                                    ? masterHost.Bones[masterBoneId].Name
                                    : "";

                                // Remap Foot/Toe to Calf for clothing (skirts / robes)
                                if (origName.IndexOf("Foot", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                    origName.IndexOf("Toe", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    bool isLeft = origName.IndexOf(" L ", StringComparison.OrdinalIgnoreCase) >= 0 || p.X > 0f;
                                    int calfIdx = isLeft ? lCalfIdx : rCalfIdx;
                                    if (calfIdx >= 0) masterBoneId = calfIdx;
                                }
                                // Remap Finger to Hand for clothing sleeves
                                else if (origName.IndexOf("Finger", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    bool isLeft = origName.IndexOf(" L ", StringComparison.OrdinalIgnoreCase) >= 0 || p.X > 0f;
                                    int handIdx = isLeft ? lHandIdx : rHandIdx;
                                    if (handIdx >= 0) masterBoneId = handIdx;
                                }
                                // In shin region (Z < 0.52), eliminate Thigh holding back the tibia:
                                // The skirt must swing fully with the Calf
                                else if (p.Z < 0.52f && origName.IndexOf("Thigh", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    bool isLeft = origName.IndexOf(" L ", StringComparison.OrdinalIgnoreCase) >= 0 || p.X > 0f;
                                    int calfIdx = isLeft ? lCalfIdx : rCalfIdx;
                                    if (calfIdx >= 0) masterBoneId = calfIdx;
                                }
                                // In thigh region (Z > 0.58), eliminate Calf
                                else if (p.Z > 0.58f && p.Z < 0.88f && origName.IndexOf("Calf", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    bool isLeft = origName.IndexOf(" L ", StringComparison.OrdinalIgnoreCase) >= 0 || p.X > 0f;
                                    int thighIdx = isLeft ? lThighIdx : rThighIdx;
                                    if (thighIdx >= 0) masterBoneId = thighIdx;
                                }

                                float w = infs[m].Weight * wdist;
                                acc[masterBoneId] = acc.TryGetValue(masterBoneId, out float cur) ? cur + w : w;
                            }
                        }
                    }

                    if (acc.Count == 0) continue;

                    var list = new List<KeyValuePair<int, float>>(acc);
                    list.Sort((a, b) => b.Value.CompareTo(a.Value));
                    int keep = Math.Min(4, list.Count);
                    float kept = 0f;
                    for (int m = 0; m < keep; m++) kept += list[m].Value;
                    if (kept <= 1e-6f) continue;

                    var outInf = new VertexInfluence[keep];
                    for (int m = 0; m < keep; m++)
                        outInf[m] = new VertexInfluence { BoneId = list[m].Key, Weight = list[m].Value / kept };

                    wrap[v] = outInf;
                    wrappedVerts++;
                }

                if (!target.HasOriginalWrapBackup)
                {
                    target.OriginalBones = target.Bones != null ? new List<GrannyBone>(target.Bones) : null;
                    target.OriginalSkin = target.Skin;
                    target.OriginalAttachBoneName = target.AttachBoneName;
                    target.OriginalHasAttachMatrix = target.HasAttachMatrix;
                    target.OriginalAttachMatrix = target.AttachMatrix;
                    target.OriginalCutoutRole = target.CutoutRole;
                    target.HasOriginalWrapBackup = true;
                }

                target.WrapSource = masterHost;
                target.WrapSkin = wrap;
                target.Bones = new List<GrannyBone>(masterHost.Bones);
                target.AttachMatrix = Matrix4.Identity;
                target.HasAttachMatrix = false;
                target.AttachBoneName = null;
                target.CutoutRole = MeshCutoutRole.Accessory;

                Log($"Skin wrap : '{target.Name}' lié à {sources.Count} parties du corps ({wrappedVerts}/{n} sommets liés via squelette '{masterHost.Name}' [{masterHost.Bones.Count} os]).");
                totalWrappedMeshes++;
            }

            LoadEquipControlsFromMesh();
            RefreshLoadedList();
            ApplyAnimationPose(_currentTime);
            RefreshVertexBuffers(true);
            if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();

            if (lblModelInfo != null)
                lblModelInfo.Text = $"Skin wrap réussi : {totalWrappedMeshes} mesh(es) lié(s) sur {sources.Count} pièces du corps ({masterHost.Bones.Count} os)";
        }

        // ============================================================================
        // ADVANCED SKIN WRAP — parametrized engine + non-destructive live preview.
        //
        // Three binding algorithms share one source/host setup:
        //   0 "Voisins (sommets)"   : copy the weights of the K nearest body VERTICES
        //                             (blended by distance / uniform / gaussian).
        //   1 "Os (capsules)"       : distribute weights by distance to the body BONE
        //                             SEGMENTS (capsules) - robust where surface is sparse.
        //   2 "Projection surface"  : project each vertex onto the nearest body TRIANGLE
        //                             and interpolate the 3 corner weights (barycentric).
        // Preview writes the result into WrapSkin/WrapSource (live) keeping the
        // committed values in PreviewSaved*; "Appliquer" drops the backup, "Annuler"
        // restores it. Nothing else in the scene is touched.
        // ============================================================================
        private struct WrapSettings
        {
            public int Source;      // 0 auto body, 1 nearby parts, 2 checked sources
            public int Algo;        // 0 nearest, 1 capsule, 2 surface
            public int K;           // neighbours
            public float Radius;    // 0 = unlimited
            public int Falloff;     // 0 inv-dist, 1 uniform, 2 gaussian, 3 inv-dist²
            public int MaxBones;    // bones kept per vertex
            public bool Anatomical; // leg/arm D-G separation filters
        }

        private WrapSettings ReadWrapSettings()
        {
            return new WrapSettings
            {
                Source = cmbWrapSource != null ? Math.Max(0, cmbWrapSource.SelectedIndex) : 0,
                Algo = cmbWrapAlgo != null ? Math.Max(0, cmbWrapAlgo.SelectedIndex) : 0,
                K = nudWrapK != null ? (int)nudWrapK.Value : 4,
                Radius = nudWrapRadius != null ? (float)nudWrapRadius.Value : 0f,
                Falloff = cmbWrapFalloff != null ? Math.Max(0, cmbWrapFalloff.SelectedIndex) : 0,
                MaxBones = nudWrapMaxBones != null ? (int)nudWrapMaxBones.Value : 4,
                Anatomical = chkWrapAnatomical == null || chkWrapAnatomical.Checked
            };
        }

        private float WrapWeight(float dist, int falloff)
        {
            switch (falloff)
            {
                case 1: return 1f;                                  // uniform
                case 2: return (float)Math.Exp(-dist * dist * 25f); // gaussian
                case 3: return 1f / (dist * dist + 0.0001f);        // inverse distance²
                default: return 1f / (dist + 0.001f);               // inverse distance
            }
        }

        // Collect body source meshes + the master host skeleton (same logic as the
        // classic SkinWrapSelected, so results are consistent).
        private void GatherWrapContext(List<int> targets, WrapSettings set,
            out List<LoadedMesh> sources, out LoadedMesh masterHost)
        {
            var targetSet = new HashSet<int>(targets);
            sources = new List<LoadedMesh>();

            bool wantChecked = set.Source == 2;
            foreach (var m in _loadedMeshes)
            {
                int mIdx = _loadedMeshes.IndexOf(m);
                if (targetSet.Contains(mIdx)) continue;
                if (m.Hidden) continue;
                if (m.WrapSource != null) continue;
                if (m.Skin == null || m.Skin.Length == 0) continue;
                if (m.Bones == null || m.Bones.Count == 0) continue;
                if (m.BaseVertices == null || m.BaseVertices.Length == 0) continue;
                if (wantChecked && !_poseTargets.Contains(mIdx)) continue;
                if (m.CutoutRole == MeshCutoutRole.Body || !string.IsNullOrEmpty(m.CharSlot))
                    sources.Add(m);
            }
            if (sources.Count == 0)
            {
                foreach (var m in _loadedMeshes)
                {
                    int mIdx = _loadedMeshes.IndexOf(m);
                    if (targetSet.Contains(mIdx)) continue;
                    if (m.Hidden) continue;
                    if (m.WrapSource != null) continue;
                    if (m.Skin == null || m.Skin.Length == 0) continue;
                    if (m.Bones == null || m.Bones.Count == 0) continue;
                    if (m.BaseVertices == null || m.BaseVertices.Length == 0) continue;
                    if (wantChecked && !_poseTargets.Contains(mIdx)) continue;
                    sources.Add(m);
                }
            }

            masterHost = null;
            int maxBones = -1;
            foreach (var m in _loadedMeshes)
                if (m.Bones != null && m.Bones.Count > maxBones) { maxBones = m.Bones.Count; masterHost = m; }

            bool isHumanoidPartAssembly = _charGender != 0 || sources.Any(s => !string.IsNullOrEmpty(s.CharSlot));
            if (masterHost == null || (maxBones < 40 && isHumanoidPartAssembly))
            {
                bool preferFemale = _charGender == 2;
                foreach (var s in sources)
                    if (s.Name != null && s.Name.IndexOf("female", StringComparison.OrdinalIgnoreCase) >= 0) { preferFemale = true; break; }
                string refFile = preferFemale ? "H_Female_Lod2.grn" : "H_Male_Lod2.grn";
                string refPath = Path.Combine(BasePath, "Humans", refFile);
                if (!File.Exists(refPath)) refPath = Path.Combine(BasePath, "Humans", "H_Male_Lod2.grn");
                if (File.Exists(refPath))
                {
                    var parser = new GrannyFormatParser();
                    if (parser.Load(refPath) && parser.Bones.Count >= 40)
                    {
                        var host = new LoadedMesh
                        {
                            Name = $"[Host Skeleton] {Path.GetFileNameWithoutExtension(refPath)}",
                            SourcePath = refPath,
                            Bones = new List<GrannyBone>(parser.Bones),
                            BaseVertices = parser.Meshes.Count > 0 ? parser.Meshes[0].Vertices.ToArray() : new Vector3[0],
                            Indices = parser.Meshes.Count > 0 ? parser.Meshes[0].Indices.ToArray() : new int[0],
                            CutoutRole = MeshCutoutRole.Body,
                            Hidden = true
                        };
                        _loadedMeshes.Add(host);
                        masterHost = host;
                    }
                }
            }

            if (_currentAnimation != null)
                EnsureAnimationBindings(_currentAnimation);
            if (masterHost != null && (masterHost.SkinMatrices == null || masterHost.SkinMatrices.Length != masterHost.Bones.Count))
                ApplyAnimationPose(_currentTime);

            // Compute the master host's REST world matrices (needed by capsule mode).
        }

        // Shared per-run context: source bone->host bone maps, pooled body samples,
        // and pooled body triangles (for the surface algorithm).
        private class WrapCtx
        {
            public List<LoadedMesh> Sources;
            public LoadedMesh Host;
            public int[][] SourceBoneMaps;
            public List<(Vector3 pos, int srcIdx, int localVertIdx)> Samples = new();
            public List<(int a, int b, int c, int srcIdx)> Tris = new();
            public bool[] SrcIsULegs;
            public bool[] SrcIsLLegs;
            public int RCalf, LCalf, RThigh, LThigh, RHand, LHand;
        }

        private WrapCtx BuildWrapCtx(List<LoadedMesh> sources, LoadedMesh host)
        {
            var ctx = new WrapCtx { Sources = sources, Host = host };
            ctx.SourceBoneMaps = new int[sources.Count][];
            for (int s = 0; s < sources.Count; s++)
            {
                var src = sources[s];
                var map = new int[src.Bones.Count];
                for (int b = 0; b < src.Bones.Count; b++)
                {
                    string bName = src.Bones[b].Name;
                    int mi = host.Bones.FindIndex(mb => string.Equals(mb.Name, bName, StringComparison.OrdinalIgnoreCase));
                    map[b] = mi >= 0 ? mi : 0;
                }
                ctx.SourceBoneMaps[s] = map;

                var sv = src.BaseVertices;
                var sk = src.Skin;
                for (int k = 0; k < sv.Length; k++)
                    if (k < sk.Length && sk[k] != null && sk[k].Length > 0)
                        ctx.Samples.Add((sv[k], s, k));

                var idx = src.Indices;
                if (idx != null && idx.Length >= 3)
                    for (int t = 0; t + 2 < idx.Length; t += 3)
                        ctx.Tris.Add((idx[t], idx[t + 1], idx[t + 2], s));
            }

            ctx.RCalf = host.Bones.FindIndex(b => b.Name.IndexOf("R Calf", StringComparison.OrdinalIgnoreCase) >= 0);
            ctx.LCalf = host.Bones.FindIndex(b => b.Name.IndexOf("L Calf", StringComparison.OrdinalIgnoreCase) >= 0);
            ctx.RThigh = host.Bones.FindIndex(b => b.Name.IndexOf("R Thigh", StringComparison.OrdinalIgnoreCase) >= 0);
            ctx.LThigh = host.Bones.FindIndex(b => b.Name.IndexOf("L Thigh", StringComparison.OrdinalIgnoreCase) >= 0);
            ctx.RHand = host.Bones.FindIndex(b => b.Name.IndexOf("R Hand", StringComparison.OrdinalIgnoreCase) >= 0);
            ctx.LHand = host.Bones.FindIndex(b => b.Name.IndexOf("L Hand", StringComparison.OrdinalIgnoreCase) >= 0);

            ctx.SrcIsULegs = new bool[sources.Count];
            ctx.SrcIsLLegs = new bool[sources.Count];
            for (int s = 0; s < sources.Count; s++)
            {
                string sName = sources[s].Name ?? "";
                ctx.SrcIsULegs[s] = sName.IndexOf("ULegs", StringComparison.OrdinalIgnoreCase) >= 0;
                ctx.SrcIsLLegs[s] = sName.IndexOf("LLegs", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            return ctx;
        }

        // Anatomical re-filter + remap of one influence set, then keep top-MaxBones.
        private VertexInfluence[] FinalizeInfluences(Dictionary<int, float> acc, Vector3 p, WrapCtx ctx, WrapSettings set)
        {
            if (acc.Count == 0) return null;
            var list = new List<KeyValuePair<int, float>>(acc);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));

            if (set.Anatomical)
            {
                for (int m = 0; m < list.Count; m++)
                {
                    int bid = list[m].Key;
                    if (bid < 0 || bid >= ctx.Host.Bones.Count) continue;
                    string nm = ctx.Host.Bones[bid].Name;
                    bool left = nm.IndexOf(" L ", StringComparison.OrdinalIgnoreCase) >= 0 || p.X > 0f;

                    if (nm.IndexOf("Foot", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        nm.IndexOf("Toe", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        int calf = left ? ctx.LCalf : ctx.RCalf;
                        if (calf >= 0) list[m] = new KeyValuePair<int, float>(calf, list[m].Value);
                    }
                    else if (nm.IndexOf("Finger", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        int hand = left ? ctx.LHand : ctx.RHand;
                        if (hand >= 0) list[m] = new KeyValuePair<int, float>(hand, list[m].Value);
                    }
                    else if (p.Z < 0.52f && nm.IndexOf("Thigh", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        int calf = left ? ctx.LCalf : ctx.RCalf;
                        if (calf >= 0) list[m] = new KeyValuePair<int, float>(calf, list[m].Value);
                    }
                    else if (p.Z > 0.58f && p.Z < 0.88f && nm.IndexOf("Calf", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        int thigh = left ? ctx.LThigh : ctx.RThigh;
                        if (thigh >= 0) list[m] = new KeyValuePair<int, float>(thigh, list[m].Value);
                    }
                }

                // merge duplicates created by remaps
                var merge = new Dictionary<int, float>();
                foreach (var kv in list) merge[kv.Key] = merge.TryGetValue(kv.Key, out float c) ? c + kv.Value : kv.Value;
                list = new List<KeyValuePair<int, float>>(merge);
                list.Sort((a, b) => b.Value.CompareTo(a.Value));
            }

            int keep = Math.Min(Math.Max(1, set.MaxBones), list.Count);
            float kept = 0f;
            for (int m = 0; m < keep; m++) kept += list[m].Value;
            if (kept <= 1e-6f) return null;

            var outInf = new VertexInfluence[keep];
            for (int m = 0; m < keep; m++)
                outInf[m] = new VertexInfluence { BoneId = list[m].Key, Weight = list[m].Value / kept };
            return outInf;
        }

        // ---- Algorithm 0: K nearest body vertices ----
        private VertexInfluence[][] ComputeNearest(LoadedMesh target, WrapCtx ctx, WrapSettings set)
        {
            int K = Math.Max(1, Math.Min(8, set.K));
            int n = target.BaseVertices.Length;
            var wrap = new VertexInfluence[n][];
            var tmpSample = new int[K];
            var tmpDist = new float[K];
            var acc = new Dictionary<int, float>();
            float r2 = set.Radius > 0f ? set.Radius * set.Radius : float.MaxValue;

            for (int v = 0; v < n; v++)
            {
                Vector3 p = target.BaseVertices[v];
                for (int k = 0; k < K; k++) { tmpSample[k] = -1; tmpDist[k] = float.MaxValue; }

                for (int si = 0; si < ctx.Samples.Count; si++)
                {
                    var samp = ctx.Samples[si];
                    if (set.Anatomical)
                    {
                        if (p.Z < 0.88f)
                        {
                            if (p.X < -0.01f && samp.pos.X > 0.01f) continue;
                            if (p.X > 0.01f && samp.pos.X < -0.01f) continue;
                        }
                        if (p.Z < 0.52f)
                        {
                            if (ctx.SrcIsULegs[samp.srcIdx]) continue;
                            if (samp.pos.Z > 0.55f) continue;
                        }
                        else if (p.Z >= 0.56f && p.Z < 0.88f)
                        {
                            if (ctx.SrcIsLLegs[samp.srcIdx]) continue;
                            if (samp.pos.Z < 0.48f) continue;
                        }
                    }
                    float dx = p.X - samp.pos.X, dy = p.Y - samp.pos.Y, dz = p.Z - samp.pos.Z;
                    float d = dx * dx + dy * dy + dz * dz * 2.5f;
                    if (d > r2) continue;
                    if (d < tmpDist[K - 1])
                    {
                        int pos = K - 1;
                        while (pos > 0 && d < tmpDist[pos - 1])
                        {
                            tmpDist[pos] = tmpDist[pos - 1]; tmpSample[pos] = tmpSample[pos - 1]; pos--;
                        }
                        tmpDist[pos] = d; tmpSample[pos] = si;
                    }
                }

                if (tmpSample[0] < 0)   // relax filters fallback
                {
                    for (int si = 0; si < ctx.Samples.Count; si++)
                    {
                        var samp = ctx.Samples[si];
                        if (set.Anatomical && p.Z < 0.88f)
                        {
                            if (p.X < -0.01f && samp.pos.X > 0.01f) continue;
                            if (p.X > 0.01f && samp.pos.X < -0.01f) continue;
                        }
                        float d = Vector3.DistanceSquared(p, samp.pos);
                        if (d > r2) continue;
                        if (d < tmpDist[K - 1])
                        {
                            int pos = K - 1;
                            while (pos > 0 && d < tmpDist[pos - 1])
                            {
                                tmpDist[pos] = tmpDist[pos - 1]; tmpSample[pos] = tmpSample[pos - 1]; pos--;
                            }
                            tmpDist[pos] = d; tmpSample[pos] = si;
                        }
                    }
                }

                acc.Clear();
                for (int k = 0; k < K; k++)
                {
                    int sIdx = tmpSample[k];
                    if (sIdx < 0) continue;
                    var sample = ctx.Samples[sIdx];
                    var infs = ctx.Sources[sample.srcIdx].Skin[sample.localVertIdx];
                    if (infs == null || infs.Length == 0) continue;
                    float wdist = WrapWeight((float)Math.Sqrt(tmpDist[k]), set.Falloff);
                    var map = ctx.SourceBoneMaps[sample.srcIdx];
                    for (int m = 0; m < infs.Length; m++)
                    {
                        int bId = infs[m].BoneId;
                        if (bId < 0 || bId >= map.Length) continue;
                        int mb = map[bId];
                        float w = infs[m].Weight * wdist;
                        acc[mb] = acc.TryGetValue(mb, out float cur) ? cur + w : w;
                    }
                }

                wrap[v] = FinalizeInfluences(acc, p, ctx, set);
            }
            return wrap;
        }

        // ---- Algorithm 1: distance to body bone capsules ----
        private VertexInfluence[][] ComputeCapsule(LoadedMesh target, WrapCtx ctx, WrapSettings set)
        {
            int n = target.BaseVertices.Length;
            var wrap = new VertexInfluence[n][];
            var acc = new Dictionary<int, float>();

            // Bone capsules from the host's REST world matrices (bind skeleton).
            var rest = ComputeBindWorld(ctx.Host.Bones);
            var a = new Vector3[ctx.Host.Bones.Count];
            var b = new Vector3[ctx.Host.Bones.Count];
            var active = new bool[ctx.Host.Bones.Count];
            for (int i = 0; i < ctx.Host.Bones.Count; i++)
            {
                var child = ctx.Host.Bones.FindIndex(c => c.ParentId == i);
                a[i] = new Vector3(rest[i].M41, rest[i].M42, rest[i].M43);
                b[i] = child >= 0
                    ? new Vector3(rest[child].M41, rest[child].M42, rest[child].M43)
                    : a[i];
                active[i] = true;
            }

            float r2 = set.Radius > 0f ? set.Radius * set.Radius : float.MaxValue;

            for (int v = 0; v < n; v++)
            {
                Vector3 p = target.BaseVertices[v];
                acc.Clear();
                int K = Math.Max(1, Math.Min(8, set.K));
                var bestI = new int[K];
                var bestD = new float[K];
                for (int k = 0; k < K; k++) { bestI[k] = -1; bestD[k] = float.MaxValue; }

                for (int i = 0; i < ctx.Host.Bones.Count; i++)
                {
                    if (!active[i]) continue;
                    if (set.Anatomical && p.Z < 0.52f && ctx.Host.Bones[i].Name.IndexOf("Thigh", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    float d = PointSegmentDistanceSquared(p, a[i], b[i]);
                    if (d > r2) continue;
                    if (d < bestD[K - 1])
                    {
                        int pos = K - 1;
                        while (pos > 0 && d < bestD[pos - 1])
                        {
                            bestD[pos] = bestD[pos - 1]; bestI[pos] = bestI[pos - 1]; pos--;
                        }
                        bestD[pos] = d; bestI[pos] = i;
                    }
                }

                for (int k = 0; k < K; k++)
                {
                    if (bestI[k] < 0) continue;
                    float w = WrapWeight((float)Math.Sqrt(bestD[k]), set.Falloff);
                    acc[bestI[k]] = acc.TryGetValue(bestI[k], out float c) ? c + w : w;
                }
                wrap[v] = FinalizeInfluences(acc, p, ctx, set);
            }
            return wrap;
        }

        private static float PointSegmentDistanceSquared(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float len2 = ab.LengthSquared;
            if (len2 < 1e-9f) return (p - a).LengthSquared;
            float t = Vector3.Dot(p - a, ab) / len2;
            t = Math.Max(0f, Math.Min(1f, t));
            return (p - (a + ab * t)).LengthSquared;
        }

        // ---- Algorithm 2: project onto nearest body triangle (barycentric) ----
        private VertexInfluence[][] ComputeSurface(LoadedMesh target, WrapCtx ctx, WrapSettings set)
        {
            int n = target.BaseVertices.Length;
            var wrap = new VertexInfluence[n][];
            var acc = new Dictionary<int, float>();
            float r2 = set.Radius > 0f ? set.Radius * set.Radius : float.MaxValue;

            if (ctx.Tris.Count == 0) return wrap;

            for (int v = 0; v < n; v++)
            {
                Vector3 p = target.BaseVertices[v];
                float bestD = float.MaxValue;
                int bestT = -1;
                Vector3 bestProj = p;
                for (int t = 0; t < ctx.Tris.Count; t++)
                {
                    var tri = ctx.Tris[t];
                    var src = ctx.Sources[tri.srcIdx];
                    if (tri.a < 0 || tri.b < 0 || tri.c < 0 ||
                        tri.a >= src.BaseVertices.Length || tri.b >= src.BaseVertices.Length || tri.c >= src.BaseVertices.Length)
                        continue;
                    Vector3 A = src.BaseVertices[tri.a], B = src.BaseVertices[tri.b], C = src.BaseVertices[tri.c];
                    Vector3 proj = ClosestPointOnTriangle(p, A, B, C);
                    float d = (proj - p).LengthSquared;
                    if (d < bestD) { bestD = d; bestT = t; bestProj = proj; }
                }
                if (bestT < 0 || bestD > r2) continue;

                var bt = ctx.Tris[bestT];
                var s2 = ctx.Sources[bt.srcIdx];
                Vector3 A2 = s2.BaseVertices[bt.a], B2 = s2.BaseVertices[bt.b], C2 = s2.BaseVertices[bt.c];
                Vector3 bary = Barycentric(bestProj, A2, B2, C2);
                var map = ctx.SourceBoneMaps[bt.srcIdx];

                acc.Clear();
                AddWeightedTriInf(s2, bt.a, bary.X, map, ctx, p, acc);
                AddWeightedTriInf(s2, bt.b, bary.Y, map, ctx, p, acc);
                AddWeightedTriInf(s2, bt.c, bary.Z, map, ctx, p, acc);
                wrap[v] = FinalizeInfluences(acc, p, ctx, set);
            }
            return wrap;
        }

        private static void AddWeightedTriInf(LoadedMesh src, int vi, float w, int[] map, WrapCtx ctx, Vector3 p, Dictionary<int, float> acc)
        {
            if (w <= 1e-5f) return;
            if (src.Skin == null || vi >= src.Skin.Length) return;
            var infs = src.Skin[vi];
            if (infs == null) return;
            for (int m = 0; m < infs.Length; m++)
            {
                int bId = infs[m].BoneId;
                if (bId < 0 || bId >= map.Length) continue;
                int mb = map[bId];
                float ww = infs[m].Weight * w;
                acc[mb] = acc.TryGetValue(mb, out float c) ? c + ww : ww;
            }
        }

        private static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 ab = b - a, ac = c - a, ap = p - a;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return a;
            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return b;
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f) { float t = d1 / (d1 - d3); return a + ab * t; }
            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) return c;
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f) { float t = d2 / (d2 - d6); return a + ac * t; }
            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f)
            {
                float t = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                return b + (c - b) * t;
            }
            float denom = 1f / (va + vb + vc);
            float vv = vb * denom, ww2 = vc * denom;
            return a + ab * vv + ac * ww2;
        }

        private static Vector3 Barycentric(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
        {
            Vector3 v0 = b - a, v1 = c - a, v2 = p - a;
            float d00 = Vector3.Dot(v0, v0), d01 = Vector3.Dot(v0, v1), d11 = Vector3.Dot(v1, v1);
            float d20 = Vector3.Dot(v2, v0), d21 = Vector3.Dot(v2, v1);
            float denom = d00 * d11 - d01 * d01;
            if (Math.Abs(denom) < 1e-9f) return new Vector3(1f, 0f, 0f);
            float v = (d11 * d20 - d01 * d21) / denom;
            float w = (d00 * d21 - d01 * d20) / denom;
            float u = 1f - v - w;
            return new Vector3(u, v, w);
        }

        // ---- Preview / Apply / Cancel ----
        private void RebuildWrapPreview()
        {
            RebuildWrapPreview(chkWrapLive != null && chkWrapLive.Checked);
        }

        private void RebuildWrapPreview(bool force)
        {
            if (_wrapPreviewBusy) return;
            if (!force) return;     // live handlers only act when "Aperçu temps réel" is on

            var idxs = GetSelectedMeshIndices();
            if (idxs.Count == 0)
            {
                if (lblWrapInfo != null) lblWrapInfo.Text = "Aperçu : sélectionnez l'accessoire à lier.";
                return;
            }

            _wrapPreviewBusy = true;
            try
            {
                var set = ReadWrapSettings();
                GatherWrapContext(idxs, set, out var sources, out var masterHost);
                if (sources.Count == 0 || masterHost == null)
                {
                    if (lblWrapInfo != null) lblWrapInfo.Text = "Aperçu : aucune source de corps valide.";
                    return;
                }
                var ctx = BuildWrapCtx(sources, masterHost);

                for (int k = 0; k < _loadedMeshes.Count; k++)
                    if (_loadedMeshes[k].PreviewActive) RestoreWrapPreview(_loadedMeshes[k]);

                int boundTotal = 0, vertTotal = 0, meshes = 0;
                foreach (int ti in idxs)
                {
                    var target = _loadedMeshes[ti];
                    if (target.BaseVertices == null || target.BaseVertices.Length == 0) continue;

                    VertexInfluence[][] wrap;
                    switch (set.Algo)
                    {
                        case 1: wrap = ComputeCapsule(target, ctx, set); break;
                        case 2: wrap = ComputeSurface(target, ctx, set); break;
                        default: wrap = ComputeNearest(target, ctx, set); break;
                    }

                    int bound = 0;
                    foreach (var w in wrap) if (w != null && w.Length > 0) bound++;

                    target.PreviewSavedWrapSource = target.WrapSource;
                    target.PreviewSavedWrapSkin = target.WrapSkin;
                    target.PreviewActive = true;
                    target.WrapSource = masterHost;
                    target.WrapSkin = wrap;

                    boundTotal += bound; vertTotal += wrap.Length; meshes++;
                }

                ApplyAnimationPose(_currentTime);
                RefreshVertexBuffers(true);
                if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();

                if (lblWrapInfo != null)
                    lblWrapInfo.Text = $"Aperçu ({SetName(set)}) : {meshes} mesh(es), {boundTotal}/{vertTotal} sommets liés sur {sources.Count} sources.";
                Log($"Wrap preview : algo={set.Algo} source={set.Source} K={set.K} rayon={set.Radius} falloff={set.Falloff} maxBones={set.MaxBones} anato={set.Anatomical} -> {boundTotal}/{vertTotal} sommets ({meshes} mesh).");
            }
            finally
            {
                _wrapPreviewBusy = false;
            }
        }

        private static string SetName(WrapSettings set)
        {
            string a = set.Algo == 1 ? "os-capsules" : set.Algo == 2 ? "projection" : "voisins";
            return a;
        }

        private void RestoreWrapPreview(LoadedMesh lm)
        {
            if (!lm.PreviewActive) return;
            lm.WrapSource = lm.PreviewSavedWrapSource;
            lm.WrapSkin = lm.PreviewSavedWrapSkin;
            lm.PreviewSavedWrapSource = null;
            lm.PreviewSavedWrapSkin = null;
            lm.PreviewActive = false;
        }

        private void ApplyWrapPreview()
        {
            bool any = false;
            foreach (var lm in _loadedMeshes)
            {
                if (!lm.PreviewActive) continue;
                any = true;
                // Preview already holds the new wrap in WrapSkin/WrapSource; commit it.
                if (!lm.HasOriginalWrapBackup)
                {
                    lm.OriginalBones = lm.Bones != null ? new List<GrannyBone>(lm.Bones) : null;
                    lm.OriginalSkin = lm.Skin;
                    lm.OriginalAttachBoneName = lm.AttachBoneName;
                    lm.OriginalHasAttachMatrix = lm.HasAttachMatrix;
                    lm.OriginalAttachMatrix = lm.AttachMatrix;
                    lm.OriginalCutoutRole = lm.CutoutRole;
                    lm.HasOriginalWrapBackup = true;
                }
                lm.Bones = new List<GrannyBone>(lm.WrapSource.Bones);
                lm.AttachMatrix = Matrix4.Identity;
                lm.HasAttachMatrix = false;
                lm.AttachBoneName = null;
                lm.CutoutRole = MeshCutoutRole.Accessory;
                lm.PreviewSavedWrapSource = null;
                lm.PreviewSavedWrapSkin = null;
                lm.PreviewActive = false;
            }

            if (!any) { Log("Wrap : aucun aperçu à appliquer."); return; }

            if (_currentAnimation != null) EnsureAnimationBindings(_currentAnimation);
            LoadEquipControlsFromMesh();
            RefreshLoadedList();
            ApplyAnimationPose(_currentTime);
            RefreshVertexBuffers(true);
            if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();
            if (lblWrapInfo != null) lblWrapInfo.Text = "Aperçu appliqué (skin wrap enregistré).";
            Log("Wrap : aperçu appliqué.");
        }

        private void CancelWrapPreview()
        {
            foreach (var lm in _loadedMeshes) RestoreWrapPreview(lm);
            ApplyAnimationPose(_currentTime);
            RefreshVertexBuffers(true);
            if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();
            if (lblWrapInfo != null) lblWrapInfo.Text = "Aperçu annulé (wrap d'origine restauré).";
            Log("Wrap : aperçu annulé.");
        }

        // DEBUG/test: toggle raw-base display on the checked meshes (bypasses skin/wrap)
        // so the user can tell whether a visible pose lives in the geometry or in skinning.
        private void ToggleForceBaseDisplay()
        {
            var idxs = GetSelectedMeshIndices();
            if (idxs.Count == 0)
            {
                MessageBox.Show("Cochez ou sélectionnez au moins un mesh.", "Géométrie brute");
                return;
            }
            bool anyOff = false;
            foreach (int ti in idxs) if (!_loadedMeshes[ti].ForceBaseDisplay) { anyOff = true; break; }
            foreach (int ti in idxs) _loadedMeshes[ti].ForceBaseDisplay = anyOff;

            RefreshVertexBuffers(true);

            foreach (int ti in idxs)
            {
                var lm = _loadedMeshes[ti];
                float bx0 = float.MaxValue, bx1 = float.MinValue, by0 = float.MaxValue, by1 = float.MinValue, bz0 = float.MaxValue, bz1 = float.MinValue;
                if (lm.BaseVertices != null)
                    foreach (var p in lm.BaseVertices) { if (p.X < bx0) bx0 = p.X; if (p.X > bx1) bx1 = p.X; if (p.Y < by0) by0 = p.Y; if (p.Y > by1) by1 = p.Y; if (p.Z < bz0) bz0 = p.Z; if (p.Z > bz1) bz1 = p.Z; }
                float dx0 = float.MaxValue, dx1 = float.MinValue, dy0 = float.MaxValue, dy1 = float.MinValue, dz0 = float.MaxValue, dz1 = float.MinValue;
                if (lm.DeformedCache != null)
                    foreach (var p in lm.DeformedCache) { if (p.X < dx0) dx0 = p.X; if (p.X > dx1) dx1 = p.X; if (p.Y < dy0) dy0 = p.Y; if (p.Y > dy1) dy1 = p.Y; if (p.Z < dz0) dz0 = p.Z; if (p.Z > dz1) dz1 = p.Z; }
                int idMats = 0, totMats = 0;
                if (lm.SkinMatrices != null) { totMats = lm.SkinMatrices.Length; foreach (var m in lm.SkinMatrices) if (m.Equals(Matrix4.Identity)) idMats++; }
                int skinned = 0;
                if (lm.Skin != null) for (int k = 0; k < lm.Skin.Length; k++) if (lm.Skin[k] != null && lm.Skin[k].Length > 0) skinned++;
                Log($"[BASEDIAG] '{lm.Name}' forceBase={lm.ForceBaseDisplay} bones={lm.Bones?.Count ?? 0} skinVerts={skinned} idMats={idMats}/{totMats} wrap={(lm.WrapSource != null)} attach={(lm.HasAttachMatrix ? 1 : 0)} rot=({lm.AttachRotation.X:F1};{lm.AttachRotation.Y:F1};{lm.AttachRotation.Z:F1}) off=({lm.AttachOffset.X:F3};{lm.AttachOffset.Y:F3};{lm.AttachOffset.Z:F3}) scale={lm.PartScale:F3} mirArms={lm.MirrorArms} | base X=[{bx0:F3};{bx1:F3}] Y=[{by0:F3};{by1:F3}] Z=[{bz0:F3};{bz1:F3}] | disp X=[{dx0:F3};{dx1:F3}] Y=[{dy0:F3};{dy1:F3}] Z=[{dz0:F3};{dz1:F3}]");
            }
            if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();
            if (lblWrapInfo != null) lblWrapInfo.Text = anyOff ? "Affichage BRUT (base, sans peau) activé." : "Affichage normal (peau) rétabli.";
            Log($"Géométrie brute : {(anyOff ? "activée" : "désactivée")} sur {idxs.Count} mesh(es).");

            // Full-scene extents (body parts included) to compare T vs A across the scene.
            foreach (var sm in _loadedMeshes)
            {
                if (sm.DeformedCache == null || sm.DeformedCache.Length == 0) continue;
                float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue, z0 = float.MaxValue, z1 = float.MinValue;
                foreach (var p in sm.DeformedCache) { if (p.X < x0) x0 = p.X; if (p.X > x1) x1 = p.X; if (p.Y < y0) y0 = p.Y; if (p.Y > y1) y1 = p.Y; if (p.Z < z0) z0 = p.Z; if (p.Z > z1) z1 = p.Z; }
                Log($"[SCENEDIAG] '{sm.Name}' slot='{sm.CharSlot}' base={sm.ForceBaseDisplay} disp X=[{x0:F3};{x1:F3}] Y=[{y0:F3};{y1:F3}] Z=[{z0:F3};{z1:F3}]");
                LogProfile(sm.Name, sm.DeformedCache);
            }
        }

        // ==================== MESH-MESH CLOTHING COLLISION ====================
        // "Repousser hors du corps" and "Rétrécir / Coudre" work on the CURRENT
        // deformed (skinned/animated) geometry, every frame. The selected clothing
        // piece is tested against the DEFORMED BODY MESH (triangles), not just bone
        // capsules, so torso/abdomen/back poke-through is removed in T-pose and in
        // every animation frame.

        private static bool LooksLikeEquipment(LoadedMesh lm)
        {
            if (lm == null) return false;
            string s = ((lm.Name ?? "") + " " + Path.GetFileNameWithoutExtension(lm.SourcePath ?? "")).ToLowerInvariant();
            return s.StartsWith("e_") || s.Contains(" robe") || s.StartsWith("e ") ||
                   s.Contains("robe") || s.Contains("armor") || s.Contains("armour") ||
                   s.Contains("cloak") || s.Contains("cape") || s.Contains("dress") ||
                   s.Contains("skirt") || s.Contains("equip") || s.Contains("tunic") ||
                   s.Contains("pants") || s.Contains("legging") || s.Contains("glove") ||
                   s.Contains("boot") || s.Contains("bracer") || s.Contains("helmet") ||
                   s.Contains("hat") || s.Contains("hood");
        }

        // Selected clothing targets + the body collision set captured at button time.
        private List<LoadedMesh> ResolveClothingTargets(out List<LoadedMesh> body, out string error)
        {
            error = null;
            body = new List<LoadedMesh>();
            var idxs = GetSelectedMeshIndices();
            var selected = new List<LoadedMesh>();
            foreach (int i in idxs)
                if (i >= 0 && i < _loadedMeshes.Count && !selected.Contains(_loadedMeshes[i]))
                    selected.Add(_loadedMeshes[i]);

            if (selected.Count == 0)
            {
                error = "Sélectionnez d'abord le vêtement/équipement (et éventuellement le skin/corps) dans « Meshes affichés ».";
                return selected;
            }

            var targets = new List<LoadedMesh>();
            foreach (var m in selected)
                if (m.CutoutRole != MeshCutoutRole.Body && m.Indices != null && m.BaseVertices != null && m.BaseVertices.Length > 0)
                    targets.Add(m);
            if (targets.Count == 0)
                foreach (var m in selected)
                    if (LooksLikeEquipment(m) && m.Indices != null && m.BaseVertices != null && m.BaseVertices.Length > 0)
                        targets.Add(m);
            if (targets.Count == 0 && selected.Count == 1)
            {
                var m = selected[0];
                if (m.Indices != null && m.BaseVertices != null && m.BaseVertices.Length > 0) targets.Add(m);
            }
            if (targets.Count == 0)
            {
                error = "Aucun vêtement reconnu dans la sélection. Sélectionnez le mesh de robe/équipement (rôle Accessory) avec le skin.";
                return targets;
            }

            // Body = explicitly selected non-target meshes first (exact control),
            // else every visible Body-role skinned mesh, else any other visible mesh.
            foreach (var m in selected)
                if (!targets.Contains(m) && !m.Hidden && m.Indices != null && m.BaseVertices != null && m.BaseVertices.Length > 0)
                    body.Add(m);
            if (body.Count == 0)
                foreach (var m in _loadedMeshes)
                    if (!targets.Contains(m) && !m.Hidden && m.Indices != null && m.BaseVertices != null && m.BaseVertices.Length > 0
                        && m.CutoutRole == MeshCutoutRole.Body)
                        body.Add(m);
            if (body.Count == 0)
                foreach (var m in _loadedMeshes)
                    if (!targets.Contains(m) && !m.Hidden && m.Indices != null && m.BaseVertices != null && m.BaseVertices.Length > 0
                        && !m.IsCloth)
                        body.Add(m);

            if (body.Count == 0)
                error = "Aucun corps/skin de collision visible. Chargez le H_Male/H_Female (ou sélectionnez-le avec le vêtement).";
            return targets;
        }

        private void TogglePushOutOfBodySelected()
        {
            var body = new List<LoadedMesh>();
            var targets = ResolveClothingTargets(out body, out string error);
            if (error != null) { MessageBox.Show(error, "Repousser hors du corps", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

            bool enable = !targets.Exists(m => m.PushOutOfBody);
            foreach (var m in targets)
            {
                m.PushOutOfBody = enable;
                if (enable) 
                { 
                    m.ShrinkSew = false;
                    m.ClothPushBase = null;
                    // Capture body in current pose
                    m.ClothPushCapturedGrid = null;
                    m.ClothPushCapturedBodyTris = null;
                }
                m.CollisionBody = enable ? new List<LoadedMesh>(body) : null;
            }

            ApplyAnimationPose(_currentTime);
            RefreshVertexBuffers(true);
            UpdateClothingEffectButtons();
            if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();
            Log($"Mesh-mesh : repousser hors du corps {(enable ? "activé" : "désactivé")} sur {string.Join(", ", targets.ConvertAll(t => t.Name))} (corps: {body.Count} mesh(es), marge={_pushOutMargin:F4}).");
        }

        private void ToggleShrinkSewSelected()
        {
            var body = new List<LoadedMesh>();
            var targets = ResolveClothingTargets(out body, out string error);
            if (error != null) { MessageBox.Show(error, "Rétrécir / Coudre", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }

            bool enable = !targets.Exists(m => m.ShrinkSew);
            foreach (var m in targets)
            {
                m.ShrinkSew = enable;
                if (enable) m.PushOutOfBody = false;
                m.CollisionBody = enable ? new List<LoadedMesh>(body) : null;
            }

            ApplyAnimationPose(_currentTime);
            RefreshVertexBuffers(true);
            UpdateClothingEffectButtons();
            if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();
            Log($"Mesh-mesh : rétrécir/coudre {(enable ? "activé" : "désactivé")} sur {string.Join(", ", targets.ConvertAll(t => t.Name))} (corps: {body.Count} mesh(es), force={_sewShrinkForce:F2}).");
        }

        private void UpdateClothingEffectButtons()
        {
            bool push = false, sew = false;
            foreach (var m in _loadedMeshes) { if (m.PushOutOfBody) push = true; if (m.ShrinkSew) sew = true; }
            if (_btnPushOutBody != null)
            {
                _btnPushOutBody.Text = (push ? "● " : "") + "Repousser hors du corps (mesh-mesh)";
                _btnPushOutBody.BackColor = push ? Color.FromArgb(120, 55, 55) : Color.FromArgb(90, 60, 60);
            }
            if (_btnShrinkSew != null)
            {
                _btnShrinkSew.Text = (sew ? "● " : "") + "Rétrécir / Coudre (Shrink/Sew)";
                _btnShrinkSew.BackColor = sew ? Color.FromArgb(55, 95, 120) : Color.FromArgb(70, 80, 90);
            }
        }

        private void OnClothingEffectParameterChanged()
        {
            if (glControl == null || !glControl.IsHandleCreated) return;
            bool active = _loadedMeshes != null && _loadedMeshes.Exists(m => m.PushOutOfBody || m.ShrinkSew);
            if (active)
            {
                // clear cached pushed positions to apply new margin immediately
                foreach (var m in _loadedMeshes)
                    if (m.PushOutOfBody)
                        m.ClothPushBase = null;
                ApplyAnimationPose(_currentTime);
                RefreshVertexBuffers(true);
            }
            glControl.Invalidate();
        }

        // One body triangle used by the mesh-mesh collision.
        private struct CollisionTri
        {
            public Vector3 A, B, C;
            public Vector3 N;          // Geometric face normal
            public Vector3 NA, NB, NC; // Smooth vertex normals
        }

        private sealed class CollisionGrid
        {
            public float Cell = 0.08f;
            public readonly List<CollisionTri> Tris = new List<CollisionTri>();
            public readonly Dictionary<long, List<int>> Cells = new Dictionary<long, List<int>>();
            public int[] Stamp = Array.Empty<int>();
            public int QueryId;
            public Vector3 BoundsMin = new Vector3(float.MaxValue);
            public Vector3 BoundsMax = new Vector3(float.MinValue);

            public static long Key(int x, int y, int z)
            {
                return (((long)(x + 0x20000) & 0x7FFFF) << 38)
                     | (((long)(y + 0x20000) & 0x7FFFF) << 19)
                     | (((long)(z + 0x20000) & 0x7FFFF));
            }

            public void Add(CollisionTri t)
            {
                int idx = Tris.Count;
                Tris.Add(t);

                float minX = Math.Min(t.A.X, Math.Min(t.B.X, t.C.X));
                float minY = Math.Min(t.A.Y, Math.Min(t.B.Y, t.C.Y));
                float minZ = Math.Min(t.A.Z, Math.Min(t.B.Z, t.C.Z));
                float maxX = Math.Max(t.A.X, Math.Max(t.B.X, t.C.X));
                float maxY = Math.Max(t.A.Y, Math.Max(t.B.Y, t.C.Y));
                float maxZ = Math.Max(t.A.Z, Math.Max(t.B.Z, t.C.Z));

                if (minX < BoundsMin.X) BoundsMin.X = minX;
                if (minY < BoundsMin.Y) BoundsMin.Y = minY;
                if (minZ < BoundsMin.Z) BoundsMin.Z = minZ;
                if (maxX > BoundsMax.X) BoundsMax.X = maxX;
                if (maxY > BoundsMax.Y) BoundsMax.Y = maxY;
                if (maxZ > BoundsMax.Z) BoundsMax.Z = maxZ;

                int x0 = (int)Math.Floor(minX / Cell), x1 = (int)Math.Floor(maxX / Cell);
                int y0 = (int)Math.Floor(minY / Cell), y1 = (int)Math.Floor(maxY / Cell);
                int z0 = (int)Math.Floor(minZ / Cell), z1 = (int)Math.Floor(maxZ / Cell);

                if (x1 - x0 > 4) x1 = x0 + 4;
                if (y1 - y0 > 4) y1 = y0 + 4;
                if (z1 - z0 > 4) z1 = z0 + 4;

                for (int x = x0; x <= x1; x++)
                    for (int y = y0; y <= y1; y++)
                        for (int z = z0; z <= z1; z++)
                        {
                            long k = Key(x, y, z);
                            if (!Cells.TryGetValue(k, out var list))
                            {
                                list = new List<int>(4);
                                Cells[k] = list;
                            }
                            list.Add(idx);
                        }
            }
        }

        [ThreadStatic] private static int[] t_queryStamp;
        [ThreadStatic] private static int t_queryId;

        private static bool FindClosestBodyPoint(CollisionGrid grid, Vector3 p, float maxR,
            out CollisionTri bestTri, out Vector3 bestPoint, out float bestDist, Vector3 clothNormal = default)
        {
            bestTri = default;
            bestPoint = p;
            bestDist = float.MaxValue;
            if (grid.Tris.Count == 0) return false;

            // Bounding box early rejection
            if (p.X < grid.BoundsMin.X - maxR || p.X > grid.BoundsMax.X + maxR ||
                p.Y < grid.BoundsMin.Y - maxR || p.Y > grid.BoundsMax.Y + maxR ||
                p.Z < grid.BoundsMin.Z - maxR || p.Z > grid.BoundsMax.Z + maxR)
            {
                return false;
            }

            float bestDistSq = maxR * maxR;

            if (t_queryStamp == null || t_queryStamp.Length < grid.Tris.Count)
            {
                t_queryStamp = new int[Math.Max(grid.Tris.Count, 4096)];
                t_queryId = 0;
            }
            t_queryId++;
            if (t_queryId == int.MaxValue)
            {
                Array.Clear(t_queryStamp, 0, t_queryStamp.Length);
                t_queryId = 1;
            }
            int stamp = t_queryId;

            int cx = (int)Math.Floor(p.X / grid.Cell);
            int cy = (int)Math.Floor(p.Y / grid.Cell);
            int cz = (int)Math.Floor(p.Z / grid.Cell);

            int span = (int)Math.Ceiling(maxR / grid.Cell);
            if (span > 3) span = 3;

            bool checkNormal = clothNormal != Vector3.Zero;

            for (int dx = -span; dx <= span; dx++)
            {
                int x = cx + dx;
                for (int dy = -span; dy <= span; dy++)
                {
                    int y = cy + dy;
                    for (int dz = -span; dz <= span; dz++)
                    {
                        int z = cz + dz;
                        if (!grid.Cells.TryGetValue(CollisionGrid.Key(x, y, z), out var list)) continue;
                        int cnt = list.Count;
                        for (int k = 0; k < cnt; k++)
                        {
                            int ti = list[k];
                            if (ti < 0 || ti >= grid.Tris.Count || t_queryStamp[ti] == stamp) continue;
                            t_queryStamp[ti] = stamp;

                            var t = grid.Tris[ti];

                            // Armpit / pinching filter: reject body triangles whose normal faces directly opposite the cloth
                            // (e.g. inner arm swinging past the ribs, or inner thighs).
                            if (checkNormal && Vector3.Dot(clothNormal, t.N) < -0.15f)
                            {
                                continue;
                            }

                            Vector3 q = ClosestPointOnTriangle(p, t.A, t.B, t.C);
                            float dsq = (p - q).LengthSquared;
                            if (dsq < bestDistSq)
                            {
                                bestDistSq = dsq;
                                bestTri = t;
                                bestPoint = q;
                                float d = (float)Math.Sqrt(dsq);
                                int newSpan = (int)Math.Ceiling(d / grid.Cell);
                                if (newSpan < span) span = newSpan;
                            }
                        }
                    }
                }
            }

            if (bestDistSq <= maxR * maxR)
            {
                bestDist = (float)Math.Sqrt(bestDistSq);
                return true;
            }
            return false;
        }

        private CollisionGrid BuildBodyCollisionGrid(List<LoadedMesh> body)
        {
            var grid = new CollisionGrid();
            var seen = new HashSet<LoadedMesh>();

            foreach (var bm in body)
            {
                if (bm == null || bm.DeformedCache == null || bm.Indices == null || bm.Indices.Length < 3) continue;
                if (!seen.Add(bm)) continue;
                var dc = bm.DeformedCache;
                var idx = bm.Indices;
                int vc = dc.Length;

                // 1. Compute smooth vertex normals on the deformed body mesh
                Vector3[] vNormals = new Vector3[vc];
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    int ia = idx[t], ib = idx[t + 1], ic = idx[t + 2];
                    if (ia < 0 || ib < 0 || ic < 0 || ia >= vc || ib >= vc || ic >= vc) continue;
                    Vector3 a = dc[ia], b = dc[ib], c = dc[ic];
                    Vector3 fn = Vector3.Cross(b - a, c - a);
                    if (fn.LengthSquared < 1e-12f) continue;
                    vNormals[ia] += fn;
                    vNormals[ib] += fn;
                    vNormals[ic] += fn;
                }
                for (int i = 0; i < vc; i++)
                {
                    if (vNormals[i].LengthSquared > 1e-12f)
                        vNormals[i] = Vector3.Normalize(vNormals[i]);
                    else
                        vNormals[i] = Vector3.UnitZ;
                }

                // 2. Add each triangle with its geometric face normal & smooth vertex normals
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    int ia = idx[t], ib = idx[t + 1], ic = idx[t + 2];
                    if (ia < 0 || ib < 0 || ic < 0 || ia >= vc || ib >= vc || ic >= vc) continue;
                    Vector3 a = dc[ia], b = dc[ib], c = dc[ic];
                    Vector3 fn = Vector3.Cross(b - a, c - a);
                    if (fn.LengthSquared < 1e-12f) continue;
                    fn = Vector3.Normalize(fn);

                    var tri = new CollisionTri
                    {
                        A = a, B = b, C = c,
                        N = fn,
                        NA = vNormals[ia], NB = vNormals[ib], NC = vNormals[ic]
                    };
                    grid.Add(tri);
                }
            }

            return grid;
        }

        // Applies the enabled mesh-mesh effects to the deformed clothing geometry.
        // Called at the end of RefreshVertexBuffers, after every mesh's DeformedCache
        // has been built for the current pose/animation frame.
        private void ApplyClothingBodyCollision(bool includeNormals)
        {
            if (_loadedMeshes == null || _loadedMeshes.Count == 0) return;
            bool any = false;
            foreach (var m in _loadedMeshes) if (m != null && (m.PushOutOfBody || m.ShrinkSew)) { any = true; break; }
            if (!any) return;

            var grids = new Dictionary<LoadedMesh, CollisionGrid>();
            foreach (var lm in _loadedMeshes)
            {
                if (lm == null || (!lm.PushOutOfBody && !lm.ShrinkSew)) continue;
                if (lm.DeformedCache == null || lm.Indices == null || lm.Indices.Length < 3 || lm.Hidden) continue;
                if (lm.IsCloth || lm.ForceBaseDisplay) continue;

                List<LoadedMesh> body = lm.CollisionBody;
                if (body == null || body.Count == 0)
                {
                    body = new List<LoadedMesh>();
                    foreach (var m in _loadedMeshes)
                        if (m != null && !ReferenceEquals(m, lm) && !m.Hidden && m.Indices != null
                            && m.DeformedCache != null && !m.IsCloth && !m.PushOutOfBody && !m.ShrinkSew)
                            body.Add(m);
                    if (body.Count == 0)
                        foreach (var m in _loadedMeshes)
                            if (m != null && !ReferenceEquals(m, lm) && !m.Hidden && m.Indices != null
                                && m.DeformedCache != null && !m.PushOutOfBody && !m.ShrinkSew)
                                body.Add(m);
                }

                if (body.Count == 0) continue;

                CollisionGrid grid;
                if (!grids.TryGetValue(lm, out grid))
                {
                    grid = BuildBodyCollisionGrid(body);
                    grids[lm] = grid;
                }
                if (grid.Tris.Count == 0) continue;

                float margin = _pushOutMargin;
                if (margin < 0.001f) margin = 0.001f;
                if (margin > 0.15f) margin = 0.15f;

                // Force adjusts the push clearance & stiffness:
                // Default force = 0.3 -> multiplier = 1.0 (exact margin).
                // Higher force (up to 2.0) powerfully repels the cloth away from ribs/torso.
                float force = Math.Clamp(_sewShrinkForce, 0.05f, 2.0f);
                float effectiveMargin = margin * (0.4f + force * 2.0f);

                var dc = lm.DeformedCache;
                int moved = 0;

                // Precompute cloth deformed normals to filter opposing surfaces (armpits, inner thighs)
                int lvc = dc.Length;
                Vector3[] clothNormals = new Vector3[lvc];
                int[] lidx = lm.Indices;
                for (int t = 0; t + 2 < lidx.Length; t += 3)
                {
                    int i0 = lidx[t], i1 = lidx[t + 1], i2 = lidx[t + 2];
                    if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= lvc || i1 >= lvc || i2 >= lvc) continue;
                    Vector3 a = dc[i0], b = dc[i1], c = dc[i2];
                    Vector3 cfn = Vector3.Cross(b - a, c - a);
                    if (cfn.LengthSquared < 1e-12f) continue;
                    clothNormals[i0] += cfn;
                    clothNormals[i1] += cfn;
                    clothNormals[i2] += cfn;
                }
                for (int i = 0; i < lvc; i++)
                {
                    if (clothNormals[i].LengthSquared > 1e-12f)
                        clothNormals[i] = Vector3.Normalize(clothNormals[i]);
                    else
                        clothNormals[i] = Vector3.UnitZ;
                }

                // Extended penetration threshold (15 cm = 150 mm) prevents any poke-through during walk/twist poses
                const float maxPenetration = 0.15f;
                float maxR = lm.ShrinkSew ? 0.22f : Math.Max(effectiveMargin * 3.0f, 0.18f);

                for (int v = 0; v < dc.Length; v++)
                {
                    Vector3 p = dc[v];
                    Vector3 cNorm = clothNormals[v];
                    if (!FindClosestBodyPoint(grid, p, maxR, out var tri, out var q, out float dist, cNorm))
                    {
                        continue;
                    }

                    // Smooth outward surface normal at contact point q
                    Vector3 bary = Barycentric(q, tri.A, tri.B, tri.C);
                    Vector3 n = bary.X * tri.NA + bary.Y * tri.NB + bary.Z * tri.NC;
                    if (n.LengthSquared > 1e-12f) n = Vector3.Normalize(n);
                    else n = tri.N;

                    if (Vector3.Dot(cNorm, n) < -0.15f)
                    {
                        continue;
                    }

                    Vector3 diff = p - q;
                    float signed = Vector3.Dot(diff, n);

                    Vector3 target = p;
                    bool modify = false;

                    if (lm.PushOutOfBody)
                    {
                        // 1. Penetrating vertex (inside body, e.g. ribs during walk):
                        // Project directly to body surface + effectiveMargin along the outward normal.
                        // Guaranteed to remove poke-through completely!
                        if (signed < 0f && signed >= -maxPenetration)
                        {
                            target = q + n * effectiveMargin;
                            modify = true;
                        }
                        // 2. Near-surface vertex: gently nudge outward to maintain margin
                        else if (signed >= 0f && signed < effectiveMargin)
                        {
                            float pushDist = effectiveMargin - signed;
                            target = p + n * pushDist;
                            modify = true;
                        }
                        // 3. Outside with clearance (signed >= effectiveMargin, e.g. loose sleeves & hood):
                        // NEVER touched! Preserves original shape 100%.
                    }
                    else if (lm.ShrinkSew)
                    {
                        if (signed < 0f && signed >= -maxPenetration)
                        {
                            // Never allow clothing to penetrate body during shrink
                            target = q + n * effectiveMargin;
                            modify = true;
                        }
                        else if (signed >= 0f && signed < effectiveMargin)
                        {
                            float pushDist = effectiveMargin - signed;
                            target = p + n * pushDist;
                            modify = true;
                        }
                        else if (signed > effectiveMargin && dist > 1e-6f && force > 0.01f)
                        {
                            // Pull excess fabric towards the surface
                            float excess = signed - effectiveMargin;
                            float pull = Math.Min(excess, 0.15f) * Math.Clamp(force, 0.05f, 1.0f);
                            target = p - (diff / dist) * pull;
                            modify = true;
                        }
                    }

                    if (modify && (target - p).LengthSquared > 1e-10f)
                    {
                        dc[v] = target;
                        moved++;
                    }
                }

                if (moved == 0) continue;

                // Push the adjusted positions back into the de-indexed VBO.
                int cn = lm.CornerVertex?.Length ?? 0;
                for (int c = 0; c < cn; c++)
                {
                    int vi = lm.CornerVertex[c];
                    Vector3 p = (vi >= 0 && vi < dc.Length) ? dc[vi] : Vector3.Zero;
                    lm.Interleaved[c * 8 + 0] = (p.X - _refCenter.X) * _refScale;
                    lm.Interleaved[c * 8 + 1] = (p.Y - _refCenter.Y) * _refScale;
                    lm.Interleaved[c * 8 + 2] = (p.Z - _refCenter.Z) * _refScale;
                }
                GL.BindBuffer(BufferTarget.ArrayBuffer, lm.Vbo);
                GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, lm.Interleaved.Length * sizeof(float), lm.Interleaved);
            }
        }


        // Silhouette profile: X extent per Z slice. T-pose -> widest band at shoulder height;
        // A-pose -> widest band low (hips) and narrow towards the top (arms hanging down).
        private static void LogProfile(string name, Vector3[] pts)
        {
            if (pts == null || pts.Length == 0) return;
            float z0 = float.MaxValue, z1 = float.MinValue;
            foreach (var p in pts) { if (p.Z < z0) z0 = p.Z; if (p.Z > z1) z1 = p.Z; }
            if (z1 - z0 < 1e-4f) return;
            const int SLICES = 16;
            var minX = new float[SLICES];
            var maxX = new float[SLICES];
            var cnt = new int[SLICES];
            for (int i = 0; i < SLICES; i++) { minX[i] = float.MaxValue; maxX[i] = float.MinValue; }
            foreach (var p in pts)
            {
                int s = (int)((p.Z - z0) / (z1 - z0) * SLICES);
                if (s < 0) s = 0; if (s >= SLICES) s = SLICES - 1;
                if (p.X < minX[s]) minX[s] = p.X;
                if (p.X > maxX[s]) maxX[s] = p.X;
                cnt[s]++;
            }
            var sb = new System.Text.StringBuilder();
            sb.Append($"[PROFILE] '{name}' ");
            for (int s = 0; s < SLICES; s++)
            {
                if (cnt[s] == 0) continue;
                float zc = z0 + (z1 - z0) * (s + 0.5f) / SLICES;
                sb.Append($"z{zc:F2}:{minX[s]:F2}..{maxX[s]:F2} ");
            }
            Log(sb.ToString());
        }


        private static bool IsClothingOrSkinned(LoadedMesh lm)
        {
            if (lm == null) return false;
            if (lm.Skin != null && lm.Skin.Length > 0) return true;
            if (lm.WrapSkin != null && lm.WrapSkin.Length > 0) return true;
            if (lm.Bones != null && lm.Bones.Count >= 5) return true;
            if (lm.CutoutRole == MeshCutoutRole.Body) return true;

            string name = (lm.Name ?? "").ToLowerInvariant();
            string src = (lm.SourcePath ?? "").ToLowerInvariant();
            string[] keywords = new[]
            {
                "robe", "tunic", "dress", "armor", "plate", "chain", "ringmail",
                "leather", "cloak", "cape", "pants", "skirt", "boot", "shoe", "sandals",
                "helm", "hat", "hood", "cowl", "cap", "hair", "facial", "beard",
                "torso", "neck", "leg", "arm", "hand", "foot", "feet", "head", "body", "chest"
            };
            foreach (var kw in keywords)
            {
                if (name.Contains(kw) || src.Contains(kw)) return true;
            }

            if (lm.BaseVertices != null && lm.BaseVertices.Length > 0)
            {
                float minZ = float.MaxValue, maxZ = float.MinValue;
                for (int i = 0; i < lm.BaseVertices.Length; i++)
                {
                    float z = lm.BaseVertices[i].Z;
                    if (z < minZ) minZ = z;
                    if (z > maxZ) maxZ = z;
                }
                if (maxZ - minZ > 0.6f || (minZ < 0.4f && maxZ > 1.0f)) return true;
            }

            return false;
        }

        private static bool IsClothingOrSkinnedGrn(string path, GrannyFormatParser parser, GrannyMesh mesh)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string fn = Path.GetFileName(path).ToLowerInvariant();

            string[] clothingKeywords = new[]
            {
                "robe", "tunic", "dress", "armor", "plate", "chain", "ringmail",
                "leather", "cloak", "cape", "pants", "skirt", "boot", "shoe", "sandals",
                "helm", "hat", "hood", "cowl", "cap", "hair", "facial", "beard",
                "torso", "neck", "leg", "arm", "hand", "foot", "feet", "head", "body", "chest"
            };
            foreach (var kw in clothingKeywords)
            {
                if (fn.Contains(kw)) return true;
            }

            if (mesh != null && mesh.Skin != null && mesh.Skin.Count > 0) return true;
            if (parser != null && parser.Bones != null && parser.Bones.Count >= 5) return true;

            if (mesh != null && mesh.Vertices != null && mesh.Vertices.Count > 0)
            {
                float minZ = float.MaxValue, maxZ = float.MinValue;
                foreach (var v in mesh.Vertices)
                {
                    if (v.Z < minZ) minZ = v.Z;
                    if (v.Z > maxZ) maxZ = v.Z;
                }
                if (maxZ - minZ > 0.6f || (minZ < 0.4f && maxZ > 1.0f)) return true;
            }

            return false;
        }

        private static bool IsMeshTweaked(LoadedMesh m)
        {
            if (m == null) return false;
            if (m.PushOutOfBody || m.ShrinkSew) return true;
            if (m.Thickness != 0f) return true;
            if (m.PartScale != 1f) return true;
            if (m.PartScaleVec.X != 1f || m.PartScaleVec.Y != 1f || m.PartScaleVec.Z != 1f) return true;
            if (m.AttachRotation != Vector3.Zero) return true;
            if (m.AttachOffset != Vector3.Zero) return true;
            if (m.MirrorX || m.MirrorY || m.MirrorZ || m.MirrorArms) return true;
            if (m.WrapSkin != null) return true;
            if (!string.IsNullOrEmpty(m.AttachBoneName)) return true;
            return false;
        }

        private void BakeMeshGeometry(LoadedMesh lm)
        {
            if (lm == null || lm.BaseVertices == null || lm.BaseVertices.Length == 0) return;

            if (lm.BaseVertexNormals == null || lm.BaseVertexNormals.Length != lm.BaseVertices.Length)
                BuildCornerData(lm);

            Vector3 pscale = new Vector3(
                lm.PartScale * lm.PartScaleVec.X,
                lm.PartScale * lm.PartScaleVec.Y,
                lm.PartScale * lm.PartScaleVec.Z);
            bool scaleTweaked = pscale.X != 1f || pscale.Y != 1f || pscale.Z != 1f;

            bool hasRot = lm.AttachRotation != Vector3.Zero;
            bool hasMirror = lm.MirrorX || lm.MirrorY || lm.MirrorZ;
            bool hasOffset = lm.AttachOffset != Vector3.Zero;
            bool hasThickness = lm.Thickness != 0f && lm.BaseVertexNormals != null;

            // If PushOutOfBody or ShrinkSew was active in T-pose, bake the pushed/shrunk shape directly into BaseVertices
            if ((lm.PushOutOfBody || lm.ShrinkSew) && lm.DeformedCache != null && lm.DeformedCache.Length == lm.BaseVertices.Length)
            {
                for (int v = 0; v < lm.BaseVertices.Length; v++)
                {
                    lm.BaseVertices[v] = lm.DeformedCache[v];
                }
                lm.PushOutOfBody = false;
                lm.ShrinkSew = false;
                UpdateClothingEffectButtons();
            }
            else if (scaleTweaked || hasRot || hasMirror || hasOffset || hasThickness || lm.MirrorArms)
            {
                bool isRigidAttached = (!string.IsNullOrEmpty(lm.AttachBoneName) || lm.HasAttachMatrix) && !lm.MirrorArms;

                if (isRigidAttached)
                {
                    // The accessory is attached to a host bone. During animation/display, it was rendered
                    // with BuildAttachTweak(lm) around Vector3.Zero (the attach socket origin / grip).
                    // We must apply BuildAttachTweak(lm) so that all parts of a multi-mesh accessory (e.g. lute, weapon)
                    // stay perfectly assembled and do NOT explode!
                    Matrix4 tweak = BuildAttachTweak(lm);
                    int vcount = lm.BaseVertices.Length;
                    for (int v = 0; v < vcount; v++)
                    {
                        Vector3 pos = Vector3.TransformPosition(lm.BaseVertices[v], tweak);
                        if (hasThickness && v < lm.BaseVertexNormals.Length)
                        {
                            Vector3 n = lm.BaseVertexNormals[v];
                            if (n.LengthSquared > 1e-12f)
                                pos += Vector3.Normalize(n) * lm.Thickness;
                        }
                        lm.BaseVertices[v] = pos;
                    }
                    if (lm.BaseVertexNormals != null && lm.BaseVertexNormals.Length == lm.BaseVertices.Length)
                    {
                        for (int v = 0; v < lm.BaseVertexNormals.Length; v++)
                        {
                            lm.BaseVertexNormals[v] = Vector3.TransformNormal(lm.BaseVertexNormals[v], tweak).Normalized();
                        }
                    }
                }
                else
                {
                    int vcount = lm.BaseVertices.Length;
                    for (int v = 0; v < vcount; v++)
                    {
                        lm.BaseVertices[v] = ApplyMeshTweak(lm, lm.BaseVertices[v], hasThickness, (hasThickness && v < lm.BaseVertexNormals.Length) ? lm.BaseVertexNormals[v] : default);
                    }

                    // Preserve skinned skeleton: adapt InverseBindMatrix so bones remain perfectly aligned with transformed vertices
                    if (lm.Skin != null && lm.Skin.Length > 0 && lm.Bones != null && lm.Bones.Count > 0)
                    {
                        Matrix4 tweakMat = Matrix4.CreateScale(pscale) *
                                           Matrix4.CreateFromQuaternion(Quaternion.FromEulerAngles(
                                               MathHelper.DegreesToRadians(lm.AttachRotation.X),
                                               MathHelper.DegreesToRadians(lm.AttachRotation.Y),
                                               MathHelper.DegreesToRadians(lm.AttachRotation.Z))) *
                                           Matrix4.CreateTranslation(lm.AttachOffset);

                        if (Math.Abs(tweakMat.Determinant) > 1e-6f)
                        {
                            Matrix4 invTweak = Matrix4.Invert(tweakMat);
                            foreach (var b in lm.Bones)
                            {
                                if (Math.Abs(b.InverseBindMatrix.Determinant) > 1e-6f)
                                {
                                    b.InverseBindMatrix = invTweak * b.InverseBindMatrix;
                                }
                            }
                        }
                    }
                }

                // 4. Invert triangle winding if odd number of mirror axes
                if (lm.MirrorX ^ lm.MirrorY ^ lm.MirrorZ)
                {
                    if (lm.Indices != null)
                    {
                        for (int t = 0; t + 2 < lm.Indices.Length; t += 3)
                        {
                            int tmp = lm.Indices[t + 1];
                            lm.Indices[t + 1] = lm.Indices[t + 2];
                            lm.Indices[t + 2] = tmp;
                        }
                    }
                    if (lm.SourceMesh?.Indices != null)
                    {
                        for (int t = 0; t + 2 < lm.SourceMesh.Indices.Count; t += 3)
                        {
                            int tmp = lm.SourceMesh.Indices[t + 1];
                            lm.SourceMesh.Indices[t + 1] = lm.SourceMesh.Indices[t + 2];
                            lm.SourceMesh.Indices[t + 2] = tmp;
                        }
                    }
                }
            }

            // Update SourceMesh bounds & vertices
            if (lm.SourceMesh != null)
            {
                lm.SourceMesh.Vertices.Clear();
                lm.SourceMesh.Vertices.AddRange(lm.BaseVertices);
                lm.SourceMesh.Indices.Clear();
                if (lm.Indices != null) lm.SourceMesh.Indices.AddRange(lm.Indices);
                if (lm.CornerUVs != null)
                {
                    lm.SourceMesh.UVs.Clear();
                    lm.SourceMesh.UVs.AddRange(lm.CornerUVs);
                    lm.SourceMesh.UVIndices.Clear();
                    for (int i = 0; i < lm.CornerUVs.Length; i++)
                        lm.SourceMesh.UVIndices.Add(i);
                }
                lm.SourceMesh.MinX = lm.SourceMesh.MinY = lm.SourceMesh.MinZ = float.MaxValue;
                lm.SourceMesh.MaxX = lm.SourceMesh.MaxY = lm.SourceMesh.MaxZ = float.MinValue;
                foreach (var v in lm.BaseVertices)
                {
                    if (v.X < lm.SourceMesh.MinX) lm.SourceMesh.MinX = v.X;
                    if (v.X > lm.SourceMesh.MaxX) lm.SourceMesh.MaxX = v.X;
                    if (v.Y < lm.SourceMesh.MinY) lm.SourceMesh.MinY = v.Y;
                    if (v.Y > lm.SourceMesh.MaxY) lm.SourceMesh.MaxY = v.Y;
                    if (v.Z < lm.SourceMesh.MinZ) lm.SourceMesh.MinZ = v.Z;
                    if (v.Z > lm.SourceMesh.MaxZ) lm.SourceMesh.MaxZ = v.Z;
                }
            }

            // Reset tweak values on lm
            lm.PartScale = 1f;
            lm.PartScaleVec = Vector3.One;
            lm.Thickness = 0f;
            lm.AttachRotation = Vector3.Zero;
            lm.AttachOffset = Vector3.Zero;
            lm.MirrorX = lm.MirrorY = lm.MirrorZ = lm.MirrorArms = false;
            lm.PushOutOfBody = lm.ShrinkSew = false;

            // If it was a cloth mesh, clear any old bakes
            if (lm.IsCloth)
            {
                ClearClothBake(lm);
                BuildClothFromMesh(lm);
            }

            // If the mesh had a temporary WrapSkin, bake it permanently into Skin & Bones!
            if (lm.WrapSkin != null && lm.WrapSource != null)
            {
                lm.Skin = lm.WrapSkin;
                lm.Bones = new List<GrannyBone>(lm.WrapSource.Bones);
                if (lm.SourceMesh != null)
                {
                    lm.SourceMesh.Skin.Clear();
                    lm.SourceMesh.Skin.AddRange(lm.WrapSkin);
                }
                lm.WrapSkin = null;
                lm.WrapSource = null;
                lm.HasOriginalWrapBackup = false;
                lm.OriginalBones = null;
                lm.OriginalSkin = null;
                lm.OriginalAttachBoneName = null;
                lm.OriginalHasAttachMatrix = false;
                lm.OriginalAttachMatrix = Matrix4.Identity;
                Log($"Bake: skin wrap fixé définitivement sur '{lm.Name}' ({lm.Skin.Length} sommets, {lm.Bones.Count} os).");
            }

            // Recompute corners, normals, and bind centroid
            BuildCornerData(lm);
            SetupMeshVBO(lm);
        }

        private void BakeAndSaveSelectedMesh()
        {
            var selIndices = GetSelectedMeshIndices();

            if (selIndices.Count == 0)
            {
                // Check if any mesh in _loadedMeshes has modifications
                for (int i = 0; i < _loadedMeshes.Count; i++)
                {
                    if (IsMeshTweaked(_loadedMeshes[i]))
                        selIndices.Add(i);
                }
            }

            if (selIndices.Count == 0)
            {
                MessageBox.Show(
                    "Aucun mesh sélectionné ou aucune modification (échelle, épaisseur, miroir, rotation, décalage, skin wrap) détectée à figer.",
                    "Figer et Enregistrer",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            var meshesToBake = new List<LoadedMesh>();
            foreach (int i in selIndices)
            {
                if (i >= 0 && i < _loadedMeshes.Count && !meshesToBake.Contains(_loadedMeshes[i]))
                    meshesToBake.Add(_loadedMeshes[i]);
            }

            if (meshesToBake.Count == 0) return;

            string names = string.Join(", ", meshesToBake.ConvertAll(m => m.Name));
            var dr = MessageBox.Show(
                $"Voulez-vous figer définitivement les modifications (échelle, épaisseur, miroir, placement, attache d'os) et les enregistrer pour :\n\n{names}\n\nLes modifications seront écrites sur le disque afin d'être conservées lors du rechargement du modèle.",
                "Confirmer la sauvegarde définitive",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (dr != DialogResult.Yes) return;

            // Group meshes by their target .char file path
            var pathGroups = new Dictionary<string, List<LoadedMesh>>(StringComparer.OrdinalIgnoreCase);
            string customDir = Path.Combine(BasePath, "Meshs custom");
            if (!Directory.Exists(customDir)) Directory.CreateDirectory(customDir);

            foreach (var lm in meshesToBake)
            {
                string targetPath = lm.SourcePath;
                if (string.IsNullOrEmpty(targetPath) || !targetPath.EndsWith(".char", StringComparison.OrdinalIgnoreCase))
                {
                    // If current model is a .char and this mesh belongs to that character model
                    if (!string.IsNullOrEmpty(_currentModelPath) && 
                        _currentModelPath.EndsWith(".char", StringComparison.OrdinalIgnoreCase) &&
                        (string.IsNullOrEmpty(lm.SourcePath) || string.Equals(lm.SourcePath, _currentModelPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        targetPath = _currentModelPath;
                    }
                    else
                    {
                        string baseName = !string.IsNullOrEmpty(lm.SourcePath)
                            ? Path.GetFileNameWithoutExtension(lm.SourcePath)
                            : lm.Name;
                        string safeName = MakeSafeFilename(baseName);
                        targetPath = Path.Combine(customDir, safeName + GrannyModelFile.Extension);
                    }
                }

                if (!pathGroups.TryGetValue(targetPath, out var list))
                {
                    list = new List<LoadedMesh>();
                    pathGroups[targetPath] = list;
                }
                list.Add(lm);
            }

            int savedFilesCount = 0;
            int bakedMeshesCount = 0;

            foreach (var kvp in pathGroups)
            {
                string charPath = kvp.Key;
                var groupMeshes = kvp.Value;

                // 1. Bake each mesh geometry
                foreach (var lm in groupMeshes)
                {
                    BakeMeshGeometry(lm);
                    bakedMeshesCount++;
                }

                // 2. Load or create the GrannyModelFile
                GrannyModelFile model = null;
                if (File.Exists(charPath))
                {
                    model = GrannyModelFile.Load(charPath);
                }

                if (model == null)
                {
                    model = new GrannyModelFile
                    {
                        Name = Path.GetFileNameWithoutExtension(charPath),
                        Bones = (groupMeshes[0].Bones != null) ? new List<GrannyBone>(groupMeshes[0].Bones) : new List<GrannyBone>()
                    };
                }
                else
                {
                    // Always preserve host bones if model bones were empty
                    if ((model.Bones == null || model.Bones.Count == 0) && groupMeshes[0].Bones != null && groupMeshes[0].Bones.Count > 0)
                    {
                        model.Bones = new List<GrannyBone>(groupMeshes[0].Bones);
                    }
                }

                var usedPartIndices = new HashSet<int>();

                // 3. Update or add parts
                foreach (var lm in groupMeshes)
                {
                    GrannyModelPart part = null;
                    int matchedIdx = -1;

                    // Match 1: exact PartIndex if valid and not yet claimed
                    if (lm.PartIndex >= 0 && lm.PartIndex < model.Parts.Count && !usedPartIndices.Contains(lm.PartIndex))
                    {
                        matchedIdx = lm.PartIndex;
                        part = model.Parts[matchedIdx];
                    }

                    // Match 2: matching name among unclaimed parts
                    if (part == null)
                    {
                        for (int pi = 0; pi < model.Parts.Count; pi++)
                        {
                            if (!usedPartIndices.Contains(pi) && string.Equals(model.Parts[pi].Name, lm.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                matchedIdx = pi;
                                part = model.Parts[pi];
                                break;
                            }
                        }
                    }

                    if (matchedIdx >= 0)
                    {
                        usedPartIndices.Add(matchedIdx);
                    }

                    if (part == null)
                    {
                        part = new GrannyModelPart
                        {
                            Name = lm.Name,
                            Color = lm.MeshColor,
                            TextureName = lm.TextureName ?? string.Empty,
                            AttachBoneName = lm.AttachBoneName ?? string.Empty,
                            AttachInBindSpace = lm.AttachInBindSpace,
                            CutoutRole = lm.CutoutRole
                        };
                        if (lm.SourceMesh?.UVs != null && lm.SourceMesh.UVs.Count > 0)
                            part.UVs = lm.SourceMesh.UVs.ToArray();
                        model.Parts.Add(part);
                        usedPartIndices.Add(model.Parts.Count - 1);
                    }

                    // Copy vertices, normals, indices
                    part.Vertices = (Vector3[])lm.BaseVertices.Clone();
                    part.Indices = (int[])lm.Indices.Clone();
                    part.Normals = (lm.BaseVertexNormals != null && lm.BaseVertexNormals.Length == lm.BaseVertices.Length)
                        ? (Vector3[])lm.BaseVertexNormals.Clone()
                        : Array.Empty<Vector3>();

                    if (lm.CornerUVs != null && lm.CornerUVs.Length > 0)
                        part.UVs = (Vector2[])lm.CornerUVs.Clone();
                    else if (lm.SourceMesh?.UVs != null && lm.SourceMesh.UVs.Count > 0)
                        part.UVs = lm.SourceMesh.UVs.ToArray();

                    if (lm.WrapSkin != null && lm.WrapSkin.Length == lm.BaseVertices.Length)
                    {
                        part.Skin = lm.WrapSkin;
                        lm.Skin = lm.WrapSkin;
                        lm.WrapSkin = null;
                        lm.WrapSource = null;
                    }
                    else if (lm.Skin != null)
                    {
                        part.Skin = lm.Skin;
                    }

                    // Preserve and intelligently update texture name (never overwrite with empty!)
                    string texToSet = !string.IsNullOrEmpty(lm.TextureName) ? lm.TextureName :
                                      (!string.IsNullOrEmpty(lm.TextureFilePath) ? Path.GetFileName(lm.TextureFilePath) : string.Empty);
                    if (!string.IsNullOrEmpty(texToSet))
                    {
                        part.TextureName = texToSet;
                    }

                    part.AttachBoneName = lm.AttachBoneName ?? string.Empty;
                    part.AttachInBindSpace = lm.AttachInBindSpace;
                    part.CutoutRole = lm.CutoutRole;

                    lm.SourcePath = charPath;
                }

                // Automatic safety backup before overwriting .char file
                if (File.Exists(charPath))
                {
                    try { File.Copy(charPath, charPath + ".bak", true); } catch { }
                }

                // 4. Save to disk
                if (GrannyModelFile.Save(model, charPath))
                {
                    savedFilesCount++;
                    Log($"Mesh(es) figés et sauvegardés dans {charPath} (Backup .bak créé)");
                }
                else
                {
                    Log($"Erreur lors de la sauvegarde de {charPath}");
                }
            }

            // Refresh UI and GL
            _suppressEquip = true;
            try
            {
                LoadEquipControlsFromMesh();
            }
            finally { _suppressEquip = false; }

            ApplyAnimationPose(_currentTime);
            RefreshVertexBuffers(true);
            if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();

            RefreshLoadedList();
            RestoreModelsTree();

            MessageBox.Show(
                $"Opération terminée avec succès !\n\n• {bakedMeshesCount} mesh(es) figé(s)\n• Sauvegardé(s) dans {savedFilesCount} fichier(s) .char\n\nLes modifications sont désormais intégrées au modèle et seront rechargées telles quelles depuis l'arbre des modèles.",
                "Modifications enregistrées",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        // Attach all meshes currently selected or checked in the treeview to the chosen bone
        // (or detach if already attached to that bone). An attached mesh rides the bone and follows
        // the current animation.
        private void AttachSelectedMeshToBone()
        {
            try
            {
                var idxs = GetSelectedMeshIndices();
                if (idxs.Count == 0)
                {
                    Log("Attach: aucun mesh sélectionné.");
                    MessageBox.Show("Veuillez sélectionner ou cocher au moins un mesh à attacher.", "Attacher à l'os", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                string bone = cbBones?.Text?.Trim();
                bool isDetached = string.IsNullOrEmpty(bone) ||
                    cbBones?.SelectedIndex == 0 ||
                    string.Equals(bone, "(non attaché)", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(bone, "(not attached)", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(bone, Localization.Tr("cb_bone_none", "(non attaché)"), StringComparison.OrdinalIgnoreCase);
                if (isDetached)
                {
                    foreach (var i in idxs)
                    {
                        var lm = _loadedMeshes[i];
                        lm.AttachBoneName = null;
                        lm.HasAttachMatrix = false;
                        lm.AttachMatrix = Matrix4.Identity;
                        Log($"Détaché : {lm.Name}");
                    }
                    RefreshLoadedList();
                    LoadEquipControlsFromMesh();
                    ApplyAnimationPose(_currentTime);
                    RefreshVertexBuffers(true);
                    if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();
                    return;
                }

                // Check if all selected meshes are already attached to this bone (then toggle detach)
                bool allAttachedToThis = true;
                foreach (var i in idxs)
                {
                    if (!string.Equals(_loadedMeshes[i].AttachBoneName, bone, StringComparison.OrdinalIgnoreCase))
                    {
                        allAttachedToThis = false;
                        break;
                    }
                }

                foreach (var i in idxs)
                {
                    var lm = _loadedMeshes[i];
                    if (allAttachedToThis)
                    {
                        lm.AttachBoneName = null;
                        lm.HasAttachMatrix = false;
                        lm.AttachMatrix = Matrix4.Identity;
                        Log($"Détaché : {lm.Name}");
                    }
                    else
                    {
                        lm.AttachBoneName = bone;
                        lm.AttachInBindSpace = false;
                        lm.CutoutRole = MeshCutoutRole.Accessory;
                        Log($"Attaché : {lm.Name} -> {bone}");
                    }
                }

                RefreshLoadedList();
                LoadEquipControlsFromMesh();
                // ApplyAnimationPose also handles the no-animation (bind) case, so the
                // attach is visible immediately.
                ApplyAnimationPose(_currentTime);
                RefreshVertexBuffers(true);
                if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();

                if (lblModelInfo != null)
                {
                    lblModelInfo.Text = allAttachedToThis
                        ? $"{idxs.Count} mesh(es) détaché(s)"
                        : $"{idxs.Count} mesh(es) attaché(s) à {bone}";
                }
            }
            catch (Exception ex)
            {
                Log($"Attach error: {ex.Message}\n{ex}");
            }
        }

        // Retire un ou plusieurs meshes de l'affichage.
        private void RemoveMeshAt(int index)
        {
            RemoveMeshesAt(new[] { index });
        }

        private void RemoveMeshesAt(IEnumerable<int> indices)
        {
            var set = new HashSet<int>(indices);
            var sorted = new List<int>();
            foreach (int i in set)
            {
                if (i >= 0 && i < _loadedMeshes.Count)
                    sorted.Add(i);
            }
            sorted.Sort();
            sorted.Reverse();
            if (sorted.Count == 0) return;
            if (glControl == null || !glControl.IsHandleCreated) return;
            glControl.MakeCurrent();

            foreach (int index in sorted)
            {
                var lm = _loadedMeshes[index];
                DestroyMesh(lm);
                for (int k = 0; k < _loadedMeshes.Count; k++)
                {
                    if (k == index) continue;
                    if (_loadedMeshes[k].WrapSource == lm)
                    {
                        _loadedMeshes[k].WrapSource = null;
                        _loadedMeshes[k].WrapSkin = null;
                    }
                }
                if (!string.IsNullOrEmpty(lm.SourcePath)) _sceneExtras.Remove(lm.SourcePath);
                _loadedMeshes.RemoveAt(index);
            }

            RefreshLoadedList();
            UpdateModelInfo();
            glControl.Invalidate();
            Log($"RemoveMeshesAt count={sorted.Count} -> remaining={_loadedMeshes.Count}");
        }

        private void ChangeBasePath()
        {
            using var fbd = new FolderBrowserDialog();
            fbd.Description = "Sélectionnez le dossier où se trouvent les modèles UO 3D (Models) :";
            if (Directory.Exists(BasePath)) fbd.SelectedPath = BasePath;
            if (fbd.ShowDialog() == DialogResult.OK && !string.IsNullOrWhiteSpace(fbd.SelectedPath))
            {
                if (!Directory.Exists(fbd.SelectedPath)) return;
                BasePath = fbd.SelectedPath;
                SaveSettings();
                if (lblBasePath != null)
                {
                    lblBasePath.Text = "UO 3D : " + BasePath;
                    _toolTip?.SetToolTip(lblBasePath, BasePath);
                }
                LoadCategoryTree();
                cmbMount.Items.Clear();
                cmbMount.Items.Add("(aucune)");
                try { foreach (var m in GatherMountModels()) cmbMount.Items.Add(Path.GetFileName(m)); } catch { }
                cmbMount.SelectedIndex = 0;
                Log($"Dossier UO 3D mis à jour : {BasePath}");
            }
        }

        private void PicHueBar_Paint(object sender, PaintEventArgs e)
        {
            int hueId = nudHue != null ? (int)nudHue.Value : 0;
            if (hueId <= 0 || hueId >= 3000)
            {
                e.Graphics.Clear(Color.FromArgb(40, 40, 40));
                using var font = new Font("Segoe UI", 7.5f);
                using var brush = new SolidBrush(Color.Gray);
                e.Graphics.DrawString("Original / Non teinté", font, brush, 4, 3);
                return;
            }
            var hue = Ultima.Hues.GetHue(hueId);
            if (hue == null || hue.Colors == null || hue.Colors.Length == 0) return;

            float stepW = (float)picHueBar.ClientSize.Width / 32f;
            int h = picHueBar.ClientSize.Height;
            for (int i = 0; i < 32; i++)
            {
                Color c = hue.GetColor(i);
                using var brush = new SolidBrush(c);
                e.Graphics.FillRectangle(brush, i * stepW, 0, stepW + 1, h);
            }
            e.Graphics.DrawRectangle(Pens.DarkGray, 0, 0, picHueBar.ClientSize.Width - 1, h - 1);
        }

        private void UpdateHueDisplay()
        {
            if (lblHueName == null || nudHue == null) return;
            int hueId = (int)nudHue.Value;
            if (hueId <= 0)
            {
                lblHueName.Text = "#0 (Original / Non teinté)";
            }
            else
            {
                var hue = Ultima.Hues.GetHue(hueId);
                string name = hue != null && !string.IsNullOrWhiteSpace(hue.Name) && hue.Name != "Null" ? hue.Name : "(Sans nom)";
                lblHueName.Text = $"#{hueId} (0x{hueId:X3}) - {name}";
            }
            picHueBar?.Invalidate();
        }

        private void SyncHueFromSelectedMesh()
        {
            if (_suppressHueEvents || nudHue == null || chkPartialHue == null) return;
            var sel = GetSelectedMeshIndices();
            if (sel.Count > 0 && sel[0] >= 0 && sel[0] < _loadedMeshes.Count)
            {
                var lm = _loadedMeshes[sel[0]];
                _suppressHueEvents = true;
                try
                {
                    nudHue.Value = Math.Max(nudHue.Minimum, Math.Min(nudHue.Maximum, lm.HueId));
                    chkPartialHue.Checked = lm.PartialHue;
                    if (chkDesaturate != null) chkDesaturate.Checked = lm.Desaturate;
                }
                finally
                {
                    _suppressHueEvents = false;
                }
                UpdateHueDisplay();
            }
        }

        private int UploadMeshTextureBitmap(Bitmap bmp)
        {
            BitmapData bd = bmp.LockBits(
                new Rectangle(0, 0, bmp.Width, bmp.Height),
                ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            int tex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, tex);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
                bmp.Width, bmp.Height, 0, OpenTK.Graphics.OpenGL.PixelFormat.Bgra,
                PixelType.UnsignedByte, bd.Scan0);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
            bmp.UnlockBits(bd);
            return tex;
        }

        private void ApplyHueToMesh(LoadedMesh lm, int hueId, bool partialHue, bool desaturate)
        {
            if (lm == null) return;

            if (hueId <= 0 && !desaturate)
            {
                // Reset to original
                if (lm.TextureId > 0 && lm.TextureId != lm.BaseTextureId)
                {
                    GL.DeleteTexture(lm.TextureId);
                }
                lm.TextureId = lm.BaseTextureId;
                lm.HasTexture = lm.TextureId > 0;
                lm.MeshColor = lm.BaseMeshColor;
                lm.HueId = 0;
                lm.PartialHue = partialHue;
                lm.Desaturate = false;
                return;
            }

            var hue = hueId > 0 ? Ultima.Hues.GetHue(hueId) : null;

            lm.HueId = hueId;
            lm.PartialHue = partialHue;
            lm.Desaturate = desaturate;

            if (lm.BaseTextureId > 0 || !string.IsNullOrEmpty(lm.TextureFilePath))
            {
                if (lm.BaseTextureBmp == null && !string.IsNullOrEmpty(lm.TextureFilePath) && File.Exists(lm.TextureFilePath))
                {
                    lm.BaseTextureBmp = LoadImageBitmap(lm.TextureFilePath);
                }

                if (lm.BaseTextureBmp != null)
                {
                    using var clone = new Bitmap(lm.BaseTextureBmp.Width, lm.BaseTextureBmp.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (var g = Graphics.FromImage(clone))
                    {
                        g.DrawImage(lm.BaseTextureBmp, 0, 0, lm.BaseTextureBmp.Width, lm.BaseTextureBmp.Height);
                    }

                    if (desaturate)
                    {
                        var bd = clone.LockBits(new Rectangle(0, 0, clone.Width, clone.Height), ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                        try
                        {
                            unsafe
                            {
                                byte* scan = (byte*)bd.Scan0;
                                for (int y = 0; y < bd.Height; y++)
                                {
                                    byte* row = scan + (long)y * bd.Stride;
                                    for (int x = 0; x < bd.Width; x++)
                                    {
                                        int b = row[x * 4 + 0];
                                        int g = row[x * 4 + 1];
                                        int r = row[x * 4 + 2];
                                        byte gray = (byte)((r * 299 + g * 587 + b * 114) / 1000);
                                        row[x * 4 + 0] = gray;
                                        row[x * 4 + 1] = gray;
                                        row[x * 4 + 2] = gray;
                                    }
                                }
                            }
                        }
                        finally { clone.UnlockBits(bd); }
                    }

                    if (hue != null)
                    {
                        hue.ApplyTo(clone, partialHue);
                    }

                    if (glControl != null && glControl.IsHandleCreated) glControl.MakeCurrent();
                    int newTex = UploadMeshTextureBitmap(clone);
                    if (newTex > 0)
                    {
                        if (lm.TextureId > 0 && lm.TextureId != lm.BaseTextureId)
                        {
                            GL.DeleteTexture(lm.TextureId);
                        }
                        lm.TextureId = newTex;
                        lm.HasTexture = true;
                    }
                }
                else
                {
                    ApplyColorToMesh(lm, hue, desaturate);
                }
            }
            else
            {
                ApplyColorToMesh(lm, hue, desaturate);
            }
        }

        private void ApplyColorToMesh(LoadedMesh lm, Ultima.Hue hue, bool desaturate)
        {
            if (hue != null)
            {
                var c = hue.GetColor(16);
                float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f;
                if (desaturate)
                {
                    float gray = r * 0.299f + g * 0.587f + b * 0.114f;
                    lm.MeshColor = new Vector3(gray, gray, gray);
                }
                else
                {
                    lm.MeshColor = new Vector3(r, g, b);
                }
            }
            else if (desaturate)
            {
                float gray = lm.BaseMeshColor.X * 0.299f + lm.BaseMeshColor.Y * 0.587f + lm.BaseMeshColor.Z * 0.114f;
                lm.MeshColor = new Vector3(gray, gray, gray);
            }
        }

        private void ApplyCurrentHue()
        {
            if (_loadedMeshes == null || _loadedMeshes.Count == 0 || nudHue == null) return;
            int hueId = (int)nudHue.Value;
            bool partial = chkPartialHue != null && chkPartialHue.Checked;
            bool desaturate = chkDesaturate != null && chkDesaturate.Checked;
            int targetMode = cmbHueTarget != null ? cmbHueTarget.SelectedIndex : 1;

            if (glControl != null && glControl.IsHandleCreated) glControl.MakeCurrent();

            if (targetMode == 0)
            {
                // Mesh sélectionné
                var selIndices = GetSelectedMeshIndices();
                if (selIndices.Count > 0)
                {
                    foreach (int idx in selIndices)
                    {
                        if (idx >= 0 && idx < _loadedMeshes.Count)
                        {
                            ApplyHueToMesh(_loadedMeshes[idx], hueId, partial, desaturate);
                        }
                    }
                }
                else
                {
                    foreach (var lm in _loadedMeshes)
                    {
                        if (lm.CutoutRole == MeshCutoutRole.Accessory)
                            ApplyHueToMesh(lm, hueId, partial, desaturate);
                    }
                }
            }
            else if (targetMode == 1)
            {
                // Tous les équipements (Accessoires ou attachés)
                foreach (var lm in _loadedMeshes)
                {
                    if (lm.CutoutRole == MeshCutoutRole.Accessory || !string.IsNullOrEmpty(lm.AttachBoneName))
                    {
                        ApplyHueToMesh(lm, hueId, partial, desaturate);
                    }
                }
            }
            else
            {
                // Tous les meshes
                foreach (var lm in _loadedMeshes)
                {
                    ApplyHueToMesh(lm, hueId, partial, desaturate);
                }
            }

            if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();
        }

        private string ResolveTextureFile(string modelPath, string embeddedName = null)
        {
            if (string.IsNullOrEmpty(modelPath)) return null;
            string dir = Path.GetDirectoryName(modelPath);
            string baseName = Path.GetFileNameWithoutExtension(modelPath);
            string[] exts = { ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".dds" };

            // 0) Direct texture name embedded in the model/mesh (authoritative)
            if (!string.IsNullOrEmpty(embeddedName))
            {
                var embCandidates = new List<string> { embeddedName };
                if (embeddedName.Contains(" "))
                    embCandidates.Add(embeddedName.Replace(" ", "_"));
                if (embeddedName.Contains("_"))
                    embCandidates.Add(embeddedName.Replace("_", ""));

                // Known aliases for legacy Granny models
                if (embeddedName.Equals("CrystalEle_256x256", StringComparison.OrdinalIgnoreCase))
                    embCandidates.Add("Crystal_Elemental_256x256");
                else if (embeddedName.Equals("Light-n-lad", StringComparison.OrdinalIgnoreCase))
                    embCandidates.Add("LightingElemental01_512");
                else if (embeddedName.Equals("Wailing_Banshee_256x256", StringComparison.OrdinalIgnoreCase))
                    embCandidates.Add("WailBanshee_256x256");
                else if (embeddedName.Equals("UT256_Samurai_Haidate_Studded_V2", StringComparison.OrdinalIgnoreCase))
                    embCandidates.Add("UT256_Samurai_Studded_V2");
                else if (embeddedName.Equals("Shirt", StringComparison.OrdinalIgnoreCase))
                    embCandidates.Add("UT256_Armor_Japanese_1_V2");
                else if (embeddedName.Equals("Weapons01", StringComparison.OrdinalIgnoreCase))
                    embCandidates.Add("Weapons01_256x256");

                foreach (var emb in embCandidates)
                {
                    if (!string.IsNullOrEmpty(dir))
                    {
                        string direct = Path.Combine(dir, emb);
                        if (File.Exists(direct)) return direct;
                        foreach (var ext in exts)
                        {
                            string p = Path.Combine(dir, emb + ext);
                            if (File.Exists(p)) return p;
                        }
                    }
                    if (Directory.Exists(MapsPath))
                    {
                        string mapDirect = Path.Combine(MapsPath, emb);
                        if (File.Exists(mapDirect)) return mapDirect;
                        foreach (var ext in exts)
                        {
                            string p = Path.Combine(MapsPath, emb + ext);
                            if (File.Exists(p)) return p;
                        }

                        try
                        {
                            foreach (var f in Directory.GetFiles(MapsPath, "*", SearchOption.TopDirectoryOnly))
                            {
                                if (!f.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)) continue;
                                string fn = Path.GetFileNameWithoutExtension(f);
                                if (fn.EndsWith("_m", StringComparison.OrdinalIgnoreCase)) continue;
                                if (fn.Equals(emb, StringComparison.OrdinalIgnoreCase) || fn.StartsWith(emb + "_", StringComparison.OrdinalIgnoreCase))
                                {
                                    return f;
                                }
                            }
                        }
                        catch { }
                    }
                }
            }

            // 1) same folder, same base name (or _Lod0 variant)
            if (!string.IsNullOrEmpty(dir))
            {
                foreach (var ext in exts)
                {
                    foreach (var candidate in new[] { baseName + ext, baseName.Replace("_Lod2", "_Lod0") + ext })
                    {
                        string p = Path.Combine(dir, candidate);
                        if (File.Exists(p)) return p;
                    }
                }
            }

            // 2) Models/Maps: <creature>.tga (+ UT256_* human skins, armor variants, Npc_ strip)
            string creature = DeriveCreature(baseName);
            var creatureCandidates = new List<string> { creature };
            if (creature.StartsWith("Npc_", StringComparison.OrdinalIgnoreCase))
                creatureCandidates.Add(creature.Substring(4));
            if (creature.StartsWith("Npc_Cpt_", StringComparison.OrdinalIgnoreCase))
                creatureCandidates.Add(creature.Substring(8));

            if (Directory.Exists(MapsPath))
            {
                foreach (var cname in creatureCandidates)
                {
                    // Try exact creature name
                    foreach (var ext in exts)
                    {
                        string p = Path.Combine(MapsPath, cname + ext);
                        if (File.Exists(p)) return p;
                    }

                    // Try human base skins
                    foreach (var cand in new[]
                    {
                        "UT256_H_" + cname.Replace("H_Male", "Male").Replace("H_Female", "Female") + "_V2",
                        "UT256_H_" + cname.Replace("H_Male", "Male").Replace("H_Female", "Female") + "A_V2",
                        "Ut256_H_" + cname.Replace("H_Male", "Male").Replace("H_Female", "Female"),
                        "UT256_H_Male_V2", "UT256_H_Female_V2"
                    })
                    {
                        string p = Path.Combine(MapsPath, cand + ".tga");
                        if (File.Exists(p)) return p;
                    }

                    // First Map file starting with the creature name (skip _M normal maps)
                    try
                    {
                        foreach (var f in Directory.GetFiles(MapsPath, cname + "*", SearchOption.TopDirectoryOnly))
                        {
                            string fn = Path.GetFileName(f);
                            if (fn.EndsWith("_M.tga", StringComparison.OrdinalIgnoreCase)) continue;
                            if (!fn.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)) continue;
                            return f;
                        }
                    }
                    catch { }
                }
            }

            return null;
        }

        private int TryLoadTexture(string modelPath, string embeddedName = null)
        {
            return TryLoadTexture(modelPath, embeddedName, out _);
        }

        private int TryLoadTexture(string modelPath, string embeddedName, out string foundTexturePath)
        {
            foundTexturePath = ResolveTextureFile(modelPath, embeddedName);
            if (!string.IsNullOrEmpty(foundTexturePath))
            {
                return LoadTexture(foundTexturePath);
            }
            return -1;
        }

        private int LoadTexture(string file)
        {
            try
            {
                Bitmap bmp;
                if (Path.GetExtension(file).Equals(".tga", StringComparison.OrdinalIgnoreCase))
                    bmp = LoadTga(file);
                else if (Path.GetExtension(file).Equals(".dds", StringComparison.OrdinalIgnoreCase))
                    bmp = LoadDds(file);
                else
                    bmp = new Bitmap(file);

                if (bmp == null) return -1;

                using (bmp)
                {
                    var bmpData = bmp.LockBits(
                        new Rectangle(0, 0, bmp.Width, bmp.Height),
                        ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                    int tex = GL.GenTexture();
                    GL.BindTexture(TextureTarget.Texture2D, tex);
                    GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
                        bmp.Width, bmp.Height, 0, OpenTK.Graphics.OpenGL.PixelFormat.Bgra, PixelType.UnsignedByte, bmpData.Scan0);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);

                    bmp.UnlockBits(bmpData);
                    return tex;
                }
            }
            catch
            {
                return -1;
            }
        }

        public static string MakeSafeFilename(string name)
        {
            if (string.IsNullOrEmpty(name)) return "mesh";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                if (Array.IndexOf(invalid, c) >= 0 || c == ' ' || c == ':' || c == '/' || c == '\\')
                    sb.Append('_');
                else
                    sb.Append(c);
            }
            return sb.ToString();
        }

        public static Bitmap? LoadImageBitmap(string file)
        {
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;
            try
            {
                if (Path.GetExtension(file).Equals(".tga", StringComparison.OrdinalIgnoreCase))
                    return LoadTga(file);
                if (Path.GetExtension(file).Equals(".dds", StringComparison.OrdinalIgnoreCase))
                    return LoadDds(file);
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var temp = Image.FromStream(stream);
                return new Bitmap(temp);
            }
            catch
            {
                return null;
            }
        }

        private void OpenTextureManagerForModel(string charPath, string? initialFbxDir = null)
        {
            if (string.IsNullOrEmpty(charPath) || !File.Exists(charPath)) return;
            try
            {
                var model = GrannyModelFile.Load(charPath);
                if (model == null || model.Parts.Count == 0)
                {
                    MessageBox.Show("Impossible de charger les données du modèle .char.", "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                using var dlg = new ModelTexturesDialog(
                    charPath,
                    model,
                    _loadedMeshes,
                    initialFbxDir,
                    TryLoadTexture,
                    () => { if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate(); },
                    () => { OpenExtractPartsDialog(charPath); });


                if (dlg.ShowDialog(this) == DialogResult.OK)
                {
                    Log($"Textures mises à jour pour {Path.GetFileName(charPath)}");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erreur lors de l'ouverture du gestionnaire de textures : {ex.Message}", "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void LstLoadedMenu_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            lstLoadedMenu.Items.Clear();
            var idxs = GetSelectedMeshIndices();
            if (idxs.Count == 0)
            {
                var applyMixamoFallback = new ToolStripMenuItem(Localization.Tr("menu_auto_rig", "⚡ Convert/create rig to UO (Rig Studio)..."), null, (s, a) =>
                {
                    string target = !string.IsNullOrEmpty(_currentModelPath) ? _currentModelPath : null;
                    ApplyMixamoForUo(target);
                });
                applyMixamoFallback.Font = new Font(lstLoadedMenu.Font, FontStyle.Bold);
                lstLoadedMenu.Items.Add(applyMixamoFallback);

                lstLoadedMenu.Items.Add(Localization.Tr("menu_import_mixamo", "Importer un Character Mixamo (FBX)..."), null, BtnImportModel_Click);
                lstLoadedMenu.Items.Add(Localization.Tr("menu_import_mesh", "Importer un mesh (FBX / OBJ)..."), null, BtnImportMesh_Click);
                return;
            }

            int firstIdx = idxs[0];
            var lm = _loadedMeshes[firstIdx];
            string title = idxs.Count > 1
                ? (tvLoaded?.SelectedNode?.Tag is List<int> ? tvLoaded.SelectedNode.Text : Localization.TrFmt("lbl_selected_meshes_fmt", "{0} meshes sélectionnés", idxs.Count))
                : $"Mesh : {lm.Name}";

            var headerItem = new ToolStripMenuItem(title) { Enabled = false };
            lstLoadedMenu.Items.Add(headerItem);
            lstLoadedMenu.Items.Add(new ToolStripSeparator());

            var bakeMenuItem = new ToolStripMenuItem(Localization.Tr("btn_freeze_save", "💾 Figer & Enregistrer les modifications du mesh..."), null, (s, a) =>
            {
                BakeAndSaveSelectedMesh();
            });
            bakeMenuItem.Font = new Font(lstLoadedMenu.Font, FontStyle.Bold);
            lstLoadedMenu.Items.Add(bakeMenuItem);
            lstLoadedMenu.Items.Add(Localization.Tr("btn_duplicate_mirror", "Dupliquer en miroir (D ↔ G)"), null, (s, a) => DuplicateMirrorSelectedMesh());

            // Convert to rigid accessory / remove skeleton option
            bool anyHasBonesOrSkin = false;
            foreach (int i in idxs)
            {
                var m = _loadedMeshes[i];
                if ((m.Bones != null && m.Bones.Count > 0) || m.Skin != null || m.WrapSkin != null)
                {
                    anyHasBonesOrSkin = true;
                    break;
                }
            }

            if (anyHasBonesOrSkin)
            {
                var stripRigItem = new ToolStripMenuItem(Localization.Tr("menu_strip_rig", "✂️ Supprimer le squelette / Convertir en accessoire rigide (Selle, Arme, Prop)..."), null, (s, a) =>
                {
                    var ask = MessageBox.Show(
                        Localization.Tr("msg_strip_rig_confirm", "Voulez-vous supprimer le squelette et le skinning de ce(s) mesh(es) ?\n\nIl(s) deviendront des accessoires 100% rigides (idéal pour les selles, armes, casques, boucliers) qui s'attachent à n'importe quel os (ex: dos du cheval, main, tête) sans aucune déformation."),
                        Localization.Tr("title_strip_rig", "Convertir en accessoire rigide"),
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);
                    if (ask != DialogResult.Yes) return;

                    foreach (int i in idxs)
                    {
                        var m = _loadedMeshes[i];
                        m.Bones = new List<GrannyBone>();
                        m.Skin = null;
                        m.WrapSkin = null;
                        m.WrapSource = null;
                        m.SkinMatrices = null;
                        m.CutoutRole = MeshCutoutRole.Accessory;
                        m.AttachInBindSpace = false;
                        m.CharSlot = null;
                    }
                    RefreshLoadedList();
                    PopulateBoneCombo();
                    ApplyAnimationPose(_currentTime);
                    RefreshVertexBuffers(true);
                    if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();
                    MessageBox.Show(
                        Localization.Tr("msg_strip_rig_done", "Squelette et skinning supprimés avec succès !\nVous pouvez maintenant sélectionner l'os cible dans « Attacher à l'os » (ex: Bip01 Spine pour le dos du cheval) et cliquer sur « Attacher / Détacher »."),
                        Localization.Tr("title_strip_rig", "Convertir en accessoire rigide"),
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                });
                lstLoadedMenu.Items.Add(stripRigItem);
            }

            lstLoadedMenu.Items.Add(new ToolStripSeparator());

            lstLoadedMenu.Items.Add(Localization.Tr("menu_assign_tex", "Assigner une texture..."), null, (s, a) =>
            {
                foreach (int i in idxs) AssignTextureToLoadedMesh(_loadedMeshes[i]);
            });

            if (lm.HasTexture && lm.TextureId > 0)
            {
                lstLoadedMenu.Items.Add(Localization.Tr("menu_remove_tex", "Retirer la texture"), null, (s, a) =>
                {
                    foreach (int i in idxs) RemoveTextureFromLoadedMesh(_loadedMeshes[i]);
                });
            }

            if (!string.IsNullOrEmpty(lm.SourcePath) && lm.SourcePath.EndsWith(".char", StringComparison.OrdinalIgnoreCase))
            {
                lstLoadedMenu.Items.Add(new ToolStripSeparator());
                lstLoadedMenu.Items.Add(Localization.Tr("menu_manage_all_tex", "Gérer toutes les textures du modèle..."), null, (s, a) =>
                {
                    OpenTextureManagerForModel(lm.SourcePath);
                });
            }

            lstLoadedMenu.Items.Add(new ToolStripSeparator());
            var applyMixamoLoaded = new ToolStripMenuItem(Localization.Tr("menu_auto_rig", "⚡ Convert/create rig to UO (Rig Studio)..."), null, (s, a) =>
            {
                string target = !string.IsNullOrEmpty(lm.SourcePath) ? lm.SourcePath : _currentModelPath;
                ApplyMixamoForUo(target);
            });
            applyMixamoLoaded.Font = new Font(lstLoadedMenu.Font, FontStyle.Bold);
            lstLoadedMenu.Items.Add(applyMixamoLoaded);

            string removeText = idxs.Count > 1
                ? Localization.TrFmt("menu_remove_meshes_fmt", "Retirer ces {0} meshes de la scène", idxs.Count)
                : Localization.Tr("menu_remove_mesh", "Retirer ce mesh de la scène");
            lstLoadedMenu.Items.Add(removeText, null, (s, a) =>
            {
                RemoveMeshesAt(idxs);
            });
        }

        private void AssignTextureToLoadedMesh(LoadedMesh lm)
        {
            if (lm == null) return;
            string initDir = Path.Combine(BasePath, "Others");
            using var ofd = new OpenFileDialog
            {
                Title = $"Choisir une texture BaseColor / Diffuse pour '{lm.Name}'",
                Filter = "Textures (*.png;*.tga;*.jpg;*.jpeg;*.bmp;*.dds)|*.png;*.tga;*.jpg;*.jpeg;*.bmp;*.dds|Tous (*.*)|*.*",
                InitialDirectory = initDir
            };
            if (ofd.ShowDialog() != DialogResult.OK || !File.Exists(ofd.FileName)) return;

            string outFolder = Path.Combine(BasePath, "Others");
            string ext = Path.GetExtension(ofd.FileName);
            string safeName = MakeSafeFilename(lm.Name);
            if (!string.IsNullOrEmpty(lm.MaterialName) && !lm.MaterialName.Equals(lm.Name, StringComparison.OrdinalIgnoreCase))
                safeName = $"{safeName}_{MakeSafeFilename(lm.MaterialName)}";
            else if (lm.PartIndex > 0)
                safeName = $"{safeName}_p{lm.PartIndex + 1}";

            string charName = !string.IsNullOrEmpty(lm.SourcePath) ? Path.GetFileNameWithoutExtension(lm.SourcePath) : "custom";
            string destName = $"{charName}_{safeName}{ext}";
            string destPath = Path.Combine(outFolder, destName);
            try
            {
                if (!string.Equals(ofd.FileName, destPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(ofd.FileName, destPath, true);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erreur copie texture : {ex.Message}");
                return;
            }

            int newTex = LoadTexture(destPath);
            if (newTex > 0)
            {
                if (lm.TextureId > 0 && lm.TextureId != newTex && lm.TextureId != lm.BaseTextureId) GL.DeleteTexture(lm.TextureId);
                lm.TextureId = newTex;
                lm.BaseTextureId = newTex;
                lm.TextureFilePath = destPath;
                if (lm.BaseTextureBmp != null) { lm.BaseTextureBmp.Dispose(); lm.BaseTextureBmp = null; }
                lm.HueId = 0;
                lm.HasTexture = true;
            }

            if (!string.IsNullOrEmpty(lm.SourcePath) && File.Exists(lm.SourcePath) && lm.SourcePath.EndsWith(".char", StringComparison.OrdinalIgnoreCase))
            {
                var model = GrannyModelFile.Load(lm.SourcePath);
                if (model != null)
                {
                    GrannyModelPart part = null;
                    if (lm.PartIndex >= 0 && lm.PartIndex < model.Parts.Count && string.Equals(model.Parts[lm.PartIndex].Name, lm.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        part = model.Parts[lm.PartIndex];
                    }
                    else
                    {
                        part = model.Parts.Find(p => string.Equals(p.Name, lm.Name, StringComparison.OrdinalIgnoreCase));
                    }

                    if (part != null)
                    {
                        part.TextureName = destName;
                        GrannyModelFile.Save(model, lm.SourcePath);
                    }
                }
            }

            if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();
            Log($"Texture assignée à {lm.Name} : {destName}");
        }

        private void RemoveTextureFromLoadedMesh(LoadedMesh lm)
        {
            if (lm == null) return;
            if (lm.TextureId > 0)
            {
                GL.DeleteTexture(lm.TextureId);
                if (lm.BaseTextureId > 0 && lm.BaseTextureId != lm.TextureId) GL.DeleteTexture(lm.BaseTextureId);
                lm.TextureId = -1;
                lm.BaseTextureId = -1;
                lm.TextureFilePath = string.Empty;
                if (lm.BaseTextureBmp != null) { lm.BaseTextureBmp.Dispose(); lm.BaseTextureBmp = null; }
                lm.HueId = 0;
                lm.HasTexture = false;
            }

            if (!string.IsNullOrEmpty(lm.SourcePath) && File.Exists(lm.SourcePath) && lm.SourcePath.EndsWith(".char", StringComparison.OrdinalIgnoreCase))
            {
                var model = GrannyModelFile.Load(lm.SourcePath);
                if (model != null)
                {
                    GrannyModelPart part = null;
                    if (lm.PartIndex >= 0 && lm.PartIndex < model.Parts.Count && string.Equals(model.Parts[lm.PartIndex].Name, lm.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        part = model.Parts[lm.PartIndex];
                    }
                    else
                    {
                        part = model.Parts.Find(p => string.Equals(p.Name, lm.Name, StringComparison.OrdinalIgnoreCase));
                    }

                    if (part != null)
                    {
                        part.TextureName = string.Empty;
                        GrannyModelFile.Save(model, lm.SourcePath);
                    }
                }
            }

            if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();
            Log($"Texture retirée de {lm.Name}");
        }

        // Minimal TGA reader: 24/32-bit uncompressed (type 2) or RLE (type 10).
        public static Bitmap LoadTga(string file)
        {
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read))
            using (var br = new BinaryReader(fs))
            {
                byte idLength = br.ReadByte();
                byte colorMapType = br.ReadByte();
                byte imageType = br.ReadByte();
                br.ReadInt16();   // color map origin
                br.ReadInt16();   // color map length
                br.ReadByte();    // color map entry size
                br.ReadInt16();   // x origin
                br.ReadInt16();   // y origin
                short w = br.ReadInt16();
                short h = br.ReadInt16();
                byte bpp = br.ReadByte();
                byte desc = br.ReadByte();

                if (w <= 0 || h <= 0) return null;
                fs.Seek(idLength, SeekOrigin.Current);

                int width = w, height = h;
                int channels = bpp / 8;
                if (channels != 3 && channels != 4) return null; // 8-bit indexed not supported

                int pixelCount = width * height;
                byte[] img = new byte[pixelCount * 4]; // BGRA

                if (colorMapType == 1)
                    return null;

                if (imageType == 2)
                {
                    byte[] raw = br.ReadBytes(pixelCount * channels);
                    for (int i = 0; i < pixelCount; i++)
                    {
                        int src = i * channels;
                        int dst = i * 4;
                        img[dst] = raw[src];                         // B
                        img[dst + 1] = channels >= 3 ? raw[src + 1] : (byte)0; // G
                        img[dst + 2] = channels >= 3 ? raw[src + 2] : (byte)0; // R
                        img[dst + 3] = channels == 4 ? raw[src + 3] : (byte)255; // A
                    }
                }
                else if (imageType == 10)
                {
                    int count = 0;
                    while (count < pixelCount)
                    {
                        byte header = br.ReadByte();
                        int runLen = (header & 0x7F) + 1;
                        if ((header & 0x80) != 0)
                        {
                            byte[] px = br.ReadBytes(channels);
                            for (int r = 0; r < runLen && count < pixelCount; r++, count++)
                            {
                                int dst = count * 4;
                                img[dst] = px[0];
                                img[dst + 1] = channels >= 3 ? px[1] : (byte)0;
                                img[dst + 2] = channels >= 3 ? px[2] : (byte)0;
                                img[dst + 3] = channels == 4 ? px[3] : (byte)255;
                            }
                        }
                        else
                        {
                            for (int r = 0; r < runLen && count < pixelCount; r++, count++)
                            {
                                byte[] px = br.ReadBytes(channels);
                                int dst = count * 4;
                                img[dst] = px[0];
                                img[dst + 1] = channels >= 3 ? px[1] : (byte)0;
                                img[dst + 2] = channels >= 3 ? px[2] : (byte)0;
                                img[dst + 3] = channels == 4 ? px[3] : (byte)255;
                            }
                        }
                    }
                }
                else
                {
                    return null;
                }

                // TGA is stored bottom-up unless the "top-left" origin bit is set.
                Bitmap bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var rect = new Rectangle(0, 0, width, height);
                var bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                int stride = bd.Stride;
                byte[] row = new byte[stride];
                bool topDown = (desc & 0x20) != 0;

                for (int y = 0; y < height; y++)
                {
                    int srcY = topDown ? y : (height - 1 - y);
                    int srcRow = srcY * width * 4;
                    System.Buffer.BlockCopy(img, srcRow, row, 0, width * 4);
                    Marshal.Copy(row, 0, new IntPtr(bd.Scan0.ToInt64() + y * stride), stride);
                }

                bmp.UnlockBits(bd);
                return bmp;
            }
        }

        public static Bitmap? LoadDds(string file)
        {
            if (string.IsNullOrEmpty(file) || !File.Exists(file)) return null;
            try
            {
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var br = new BinaryReader(fs);

                uint magic = br.ReadUInt32();
                if (magic != 0x20534444) return null; // "DDS "

                byte[] header = br.ReadBytes(124);
                if (header.Length < 124) return null;

                int height = BitConverter.ToInt32(header, 8);
                int width = BitConverter.ToInt32(header, 12);
                if (width <= 0 || height <= 0) return null;

                uint pfFlags = BitConverter.ToUInt32(header, 76);
                uint fourCC = BitConverter.ToUInt32(header, 80);
                uint rgbBitCount = BitConverter.ToUInt32(header, 84);
                uint rMask = BitConverter.ToUInt32(header, 88);
                uint gMask = BitConverter.ToUInt32(header, 92);
                uint bMask = BitConverter.ToUInt32(header, 96);
                uint aMask = BitConverter.ToUInt32(header, 100);

                int[] pixels = new int[width * height];

                if ((pfFlags & 0x4) != 0) // DDPF_FOURCC
                {
                    if (fourCC == 0x31545844) // DXT1
                    {
                        DecodeDxt1(br, width, height, pixels);
                    }
                    else if (fourCC == 0x33545844) // DXT3
                    {
                        DecodeDxt3(br, width, height, pixels);
                    }
                    else if (fourCC == 0x35545844) // DXT5
                    {
                        DecodeDxt5(br, width, height, pixels);
                    }
                    else
                    {
                        return null;
                    }
                }
                else if (rgbBitCount == 32)
                {
                    for (int i = 0; i < width * height; i++)
                    {
                        uint raw = br.ReadUInt32();
                        int a = aMask != 0 ? (int)((raw & aMask) >> GetDdsShift(aMask)) : 255;
                        int r = (int)((raw & rMask) >> GetDdsShift(rMask));
                        int g = (int)((raw & gMask) >> GetDdsShift(gMask));
                        int b = (int)((raw & bMask) >> GetDdsShift(bMask));
                        pixels[i] = (a << 24) | (r << 16) | (g << 8) | b;
                    }
                }
                else
                {
                    return null;
                }

                Bitmap bmp = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var rect = new Rectangle(0, 0, width, height);
                var bd = bmp.LockBits(rect, ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                Marshal.Copy(pixels, 0, bd.Scan0, pixels.Length);
                bmp.UnlockBits(bd);
                return bmp;
            }
            catch
            {
                return null;
            }
        }

        private static int GetDdsShift(uint mask)
        {
            if (mask == 0) return 0;
            int shift = 0;
            while ((mask & 1) == 0)
            {
                mask >>= 1;
                shift++;
            }
            return shift;
        }

        private static void DecodeRgb565(ushort c, out int r, out int g, out int b)
        {
            r = ((c >> 11) & 0x1F) * 255 / 31;
            g = ((c >> 5) & 0x3F) * 255 / 63;
            b = (c & 0x1F) * 255 / 31;
        }

        private static void DecodeDxt1(BinaryReader br, int width, int height, int[] pixels)
        {
            int bw = (width + 3) / 4;
            int bh = (height + 3) / 4;
            for (int by = 0; by < bh; by++)
            {
                for (int bx = 0; bx < bw; bx++)
                {
                    ushort c0 = br.ReadUInt16();
                    ushort c1 = br.ReadUInt16();
                    uint code = br.ReadUInt32();

                    DecodeRgb565(c0, out int r0, out int g0, out int b0);
                    DecodeRgb565(c1, out int r1, out int g1, out int b1);

                    int[] colors = new int[4];
                    colors[0] = (255 << 24) | (r0 << 16) | (g0 << 8) | b0;
                    colors[1] = (255 << 24) | (r1 << 16) | (g1 << 8) | b1;
                    if (c0 > c1)
                    {
                        int r2 = (2 * r0 + r1) / 3;
                        int g2 = (2 * g0 + g1) / 3;
                        int b2 = (2 * b0 + b1) / 3;
                        colors[2] = (255 << 24) | (r2 << 16) | (g2 << 8) | b2;

                        int r3 = (r0 + 2 * r1) / 3;
                        int g3 = (g0 + 2 * g1) / 3;
                        int b3 = (b0 + 2 * b1) / 3;
                        colors[3] = (255 << 24) | (r3 << 16) | (g3 << 8) | b3;
                    }
                    else
                    {
                        int r2 = (r0 + r1) / 2;
                        int g2 = (g0 + g1) / 2;
                        int b2 = (b0 + b1) / 2;
                        colors[2] = (255 << 24) | (r2 << 16) | (g2 << 8) | b2;
                        colors[3] = 0;
                    }

                    for (int py = 0; py < 4; py++)
                    {
                        int y = by * 4 + py;
                        if (y >= height) continue;
                        for (int px = 0; px < 4; px++)
                        {
                            int x = bx * 4 + px;
                            if (x >= width) continue;
                            int idx = (int)((code >> (2 * (py * 4 + px))) & 0x3);
                            pixels[y * width + x] = colors[idx];
                        }
                    }
                }
            }
        }

        private static void DecodeDxt3(BinaryReader br, int width, int height, int[] pixels)
        {
            int bw = (width + 3) / 4;
            int bh = (height + 3) / 4;
            for (int by = 0; by < bh; by++)
            {
                for (int bx = 0; bx < bw; bx++)
                {
                    ulong alphaBits = br.ReadUInt64();
                    ushort c0 = br.ReadUInt16();
                    ushort c1 = br.ReadUInt16();
                    uint code = br.ReadUInt32();

                    DecodeRgb565(c0, out int r0, out int g0, out int b0);
                    DecodeRgb565(c1, out int r1, out int g1, out int b1);

                    int[] colors = new int[4];
                    colors[0] = (r0 << 16) | (g0 << 8) | b0;
                    colors[1] = (r1 << 16) | (g1 << 8) | b1;
                    colors[2] = (((2 * r0 + r1) / 3) << 16) | (((2 * g0 + g1) / 3) << 8) | ((2 * b0 + b1) / 3);
                    colors[3] = (((r0 + 2 * r1) / 3) << 16) | (((g0 + 2 * g1) / 3) << 8) | ((b0 + 2 * b1) / 3);

                    for (int py = 0; py < 4; py++)
                    {
                        int y = by * 4 + py;
                        if (y >= height) continue;
                        for (int px = 0; px < 4; px++)
                        {
                            int x = bx * 4 + px;
                            if (x >= width) continue;
                            int bitPos = py * 4 + px;
                            int a = (int)((alphaBits >> (4 * bitPos)) & 0xF) * 255 / 15;
                            int cIdx = (int)((code >> (2 * bitPos)) & 0x3);
                            pixels[y * width + x] = (a << 24) | colors[cIdx];
                        }
                    }
                }
            }
        }

        private static void DecodeDxt5(BinaryReader br, int width, int height, int[] pixels)
        {
            int bw = (width + 3) / 4;
            int bh = (height + 3) / 4;
            for (int by = 0; by < bh; by++)
            {
                for (int bx = 0; bx < bw; bx++)
                {
                    byte a0 = br.ReadByte();
                    byte a1 = br.ReadByte();
                    ulong aBits = 0;
                    for (int i = 0; i < 6; i++)
                    {
                        aBits |= ((ulong)br.ReadByte()) << (8 * i);
                    }

                    int[] alphas = new int[8];
                    alphas[0] = a0;
                    alphas[1] = a1;
                    if (a0 > a1)
                    {
                        alphas[2] = (6 * a0 + 1 * a1) / 7;
                        alphas[3] = (5 * a0 + 2 * a1) / 7;
                        alphas[4] = (4 * a0 + 3 * a1) / 7;
                        alphas[5] = (3 * a0 + 4 * a1) / 7;
                        alphas[6] = (2 * a0 + 5 * a1) / 7;
                        alphas[7] = (1 * a0 + 6 * a1) / 7;
                    }
                    else
                    {
                        alphas[2] = (4 * a0 + 1 * a1) / 5;
                        alphas[3] = (3 * a0 + 2 * a1) / 5;
                        alphas[4] = (2 * a0 + 3 * a1) / 5;
                        alphas[5] = (1 * a0 + 4 * a1) / 5;
                        alphas[6] = 0;
                        alphas[7] = 255;
                    }

                    ushort c0 = br.ReadUInt16();
                    ushort c1 = br.ReadUInt16();
                    uint code = br.ReadUInt32();

                    DecodeRgb565(c0, out int r0, out int g0, out int b0);
                    DecodeRgb565(c1, out int r1, out int g1, out int b1);

                    int[] colors = new int[4];
                    colors[0] = (r0 << 16) | (g0 << 8) | b0;
                    colors[1] = (r1 << 16) | (g1 << 8) | b1;
                    colors[2] = (((2 * r0 + r1) / 3) << 16) | (((2 * g0 + g1) / 3) << 8) | ((2 * b0 + b1) / 3);
                    colors[3] = (((r0 + 2 * r1) / 3) << 16) | (((g0 + 2 * g1) / 3) << 8) | ((b0 + 2 * b1) / 3);

                    for (int py = 0; py < 4; py++)
                    {
                        int y = by * 4 + py;
                        if (y >= height) continue;
                        for (int px = 0; px < 4; px++)
                        {
                            int x = bx * 4 + px;
                            if (x >= width) continue;
                            int bitPos = py * 4 + px;
                            int aIdx = (int)((aBits >> (3 * bitPos)) & 0x7);
                            int a = alphas[aIdx];
                            int cIdx = (int)((code >> (2 * bitPos)) & 0x3);
                            pixels[y * width + x] = (a << 24) | colors[cIdx];
                        }
                    }
                }
            }
        }

        private void LoadAnimation(string path)
        {
            try
            {
                LoadAnimationCore(path);
            }
            catch (Exception ex)
            {
                Log($"LoadAnimation error ({path}): {ex.Message}\n{ex}");
            }
        }

        // Load a clip regardless of container: native .grn (Granny) or the custom
        // .anim emitted by the Mixamo importer.
        private static GrannyAnimation LoadAnyAnimation(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            if (GrannyAnimFile.IsAnimFile(path)) return GrannyAnimFile.Load(path);
            var parser = new GrannyFormatParser();
            return (parser.Load(path) ? parser.Animation : null);
        }

        private void LoadAnimationCore(string path)
        {
            if (!File.Exists(path)) return;

            // Loading an animation replaces any pose: a pose is an alternative to an
            // animation, never an additive layer riding on top of every clip. Clearing
            // the pose layer here (single choke point for native + custom clips) is what
            // stops a loaded pose from "contaminating" all animations.
            ClearPoseLayer();

            if (GrannyAnimFile.IsAnimFile(path))
            {
                _animParser = null;
                _currentAnimation = GrannyAnimFile.Load(path);
            }
            else
            {
                _animParser = new GrannyFormatParser();
                if (!_animParser.Load(path)) return;
                _currentAnimation = _animParser.Animation;
            }
            _currentAnimPath = path;
            RefreshMountAnimation();
            foreach (var lm in _loadedMeshes) ClearClothBake(lm);   // stale loop
            try {
                if (_currentAnimation != null) {
                    var tn = new List<string>();
                    for (int ti = 0; ti < Math.Min(8, _currentAnimation.Tracks.Count); ti++) tn.Add(_currentAnimation.Tracks[ti].Name);
                    Log($"  anim loaded file={Path.GetFileName(path)} tracks={_currentAnimation.Tracks.Count} dur={_currentAnimation.Duration:F3} skelByName={_currentAnimation.SkeletonByName.Count} trackNames[0..7]={string.Join(",", tn)}");
                } else Log($"  anim loaded file={Path.GetFileName(path)} -> Animation==null");
            } catch { }
            _playing = false;
            bool hasAnim = _currentAnimation != null;
            btnPlay.Enabled = hasAnim;
            btnPause.Enabled = false;
            btnStop.Enabled = false;

            if (hasAnim)
            {
                trackTime.Maximum = Math.Max(1, (int)(_currentAnimation.Duration * 1000));
                trackTime.Value = 0;
                _currentTime = 0f;
                lblTime.Text = $"Time: {_currentTime:F2}s";
                lblDuration.Text = $"/ {_currentAnimation.Duration:F2}s";

                foreach (var lm in _loadedMeshes)
                    lm.BoneToAnimTrack = null;

                ResetAllCloth();
                ApplyAnimationPose(0f);

                // Auto-play on selection so the user never stares at the frozen
                // first frame of the clip (which is the animation's authored rest
                // pose, not the model's bind pose).
                _playing = true;
                _lastFrameTime = DateTime.Now;
                btnPlay.Enabled = false;
                btnPause.Enabled = true;
                btnStop.Enabled = true;
                Log($"  auto-play started (loop={_looping} speed={_playbackSpeed:F1}x)");
            }

            UpdateModelInfo();
            if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();
        }

        private void ComputeReferenceBounds()
        {
            float minX = float.MaxValue, minY = float.MaxValue, minZ = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue, maxZ = float.MinValue;
            bool any = false;

            foreach (var lm in _loadedMeshes)
            {
                foreach (var v in lm.BaseVertices)
                {
                    any = true;
                    if (v.X < minX) minX = v.X;
                    if (v.Y < minY) minY = v.Y;
                    if (v.Z < minZ) minZ = v.Z;
                    if (v.X > maxX) maxX = v.X;
                    if (v.Y > maxY) maxY = v.Y;
                    if (v.Z > maxZ) maxZ = v.Z;
                }
            }

            if (!any) return;

            _refCenter = new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, (minZ + maxZ) * 0.5f);
            float maxDim = Math.Max(maxX - minX, Math.Max(maxY - minY, maxZ - minZ));
            _refScale = maxDim > 0 ? 2f / maxDim : 1f;

            _normMin = new Vector3(
                (minX - _refCenter.X) * _refScale,
                (minY - _refCenter.Y) * _refScale,
                (minZ - _refCenter.Z) * _refScale);
            _normMax = new Vector3(
                (maxX - _refCenter.X) * _refScale,
                (maxY - _refCenter.Y) * _refScale,
                (maxZ - _refCenter.Z) * _refScale);
        }

        private void UpdateModelInfo()
        {
            int totalBones = 0;
            int trackCount = _currentAnimation?.Tracks.Count ?? 0;
            foreach (var lm in _loadedMeshes)
                totalBones = Math.Max(totalBones, lm.Bones.Count);

            if (lblModelInfo != null)
                lblModelInfo.Text = $"Bones: {totalBones} | Tracks: {trackCount} | Meshes: {_loadedMeshes.Count}";
            if (lblBasePath != null)
            {
                lblBasePath.Text = "UO 3D : " + BasePath;
                _toolTip?.SetToolTip(lblBasePath, BasePath);
            }
        }

        // ==================== ANIMATION ====================

        private static string NormalizeBoneName(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var parts = s.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", parts).Trim();
        }

        private static string StripBipPrefix(string s)
        {
            s = NormalizeBoneName(s);
            if (s.StartsWith("Bip01 ", StringComparison.OrdinalIgnoreCase)) return s.Substring(6).Trim();
            if (s.StartsWith("Bip01", StringComparison.OrdinalIgnoreCase)) return s.Substring(5).Trim();
            return s;
        }

        // Name-only binding: map each track to the model bone with the same name.
        // Returns the number of bones matched.
        private int BindMeshToAnimation(LoadedMesh lm, GrannyAnimation anim)
        {
            int boneCount = lm.Bones.Count;
            lm.BoneToAnimTrack = new int[boneCount];
            for (int i = 0; i < boneCount; i++) lm.BoneToAnimTrack[i] = -1;

            var modelBoneByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < boneCount; i++)
            {
                string raw = lm.Bones[i].Name;
                if (string.IsNullOrEmpty(raw)) continue;
                string norm = NormalizeBoneName(raw);
                string stripped = StripBipPrefix(raw);
                if (!modelBoneByName.ContainsKey(raw)) modelBoneByName[raw] = i;
                if (!modelBoneByName.ContainsKey(norm)) modelBoneByName[norm] = i;
                if (!string.IsNullOrEmpty(stripped) && !modelBoneByName.ContainsKey(stripped)) modelBoneByName[stripped] = i;
            }

            int matched = 0;
            for (int trackIdx = 0; trackIdx < anim.Tracks.Count; trackIdx++)
            {
                var track = anim.Tracks[trackIdx];
                if (string.IsNullOrEmpty(track.Name)) continue;

                string tName = track.Name;
                string tNorm = NormalizeBoneName(tName);
                string tStrip = StripBipPrefix(tName);

                if (modelBoneByName.TryGetValue(tName, out int modelBoneIdx) ||
                    modelBoneByName.TryGetValue(tNorm, out modelBoneIdx) ||
                    modelBoneByName.TryGetValue(tStrip, out modelBoneIdx) ||
                    modelBoneByName.TryGetValue("Bip01 " + tStrip, out modelBoneIdx) ||
                    (MixamoRetarget.TryGetBoneAlias(tName, out string alias) && modelBoneByName.TryGetValue(alias, out modelBoneIdx)))
                {
                    lm.BoneToAnimTrack[modelBoneIdx] = trackIdx;
                    matched++;
                }
            }
            return matched;
        }

        // Bind every mesh to the animation. Most meshes match by bone name. Many
        // armor/body pieces, however, export ALL of their bone names as the source
        // ".max" path (identical strings) so almost none match -> the piece would
        // stay frozen in its bind pose while the body animates, letting the body
        // poke through it. Those pieces still have correct bind POSITIONS, so we
        // bind each unmatched bone to the closest bone of a correctly name-bound
        // reference mesh (the one whose skeleton matched the most tracks).
        private void EnsureAnimationBindings(GrannyAnimation anim)
        {
            if (anim == null) return;

            bool need = false;
            foreach (var lm in _loadedMeshes)
            {
                if (lm.CutoutRole == MeshCutoutRole.Mount) continue;
                if (lm.Bones != null && lm.Bones.Count > 0 && lm.BoneToAnimTrack == null) { need = true; break; }
            }
            if (!need) return;

            LoadedMesh refMesh = null;
            int refScore = -1;
            foreach (var lm in _loadedMeshes)
            {
                if (lm.CutoutRole == MeshCutoutRole.Mount) continue;
                if (lm.Bones == null || lm.Bones.Count == 0) continue;
                int m = BindMeshToAnimation(lm, anim);
                if (m > refScore) { refScore = m; refMesh = lm; }
            }
            if (refMesh == null || refScore <= 0) return;

            Matrix4[] refWorld;
            try { refWorld = ComputeBindWorld(refMesh.Bones); }
            catch { return; }
            int[] refTrack = refMesh.BoneToAnimTrack;

            foreach (var lm in _loadedMeshes)
            {
                if (lm.CutoutRole == MeshCutoutRole.Mount) continue;
                if (lm.Bones == null || lm.Bones.Count < 2 || ReferenceEquals(lm, refMesh)) continue;

                int matched = 0;
                for (int i = 0; i < lm.BoneToAnimTrack.Length; i++) if (lm.BoneToAnimTrack[i] >= 0) matched++;
                if (matched >= lm.Bones.Count / 2) continue;   // already bound well by name

                Matrix4[] mw;
                try { mw = ComputeBindWorld(lm.Bones); }
                catch { continue; }

                int added = 0;
                for (int i = 0; i < lm.Bones.Count; i++)
                {
                    if (lm.BoneToAnimTrack[i] >= 0) continue;
                    var mp = new Vector3(mw[i].M41, mw[i].M42, mw[i].M43);
                    int best = -1; float bestD = float.MaxValue;
                    for (int j = 0; j < refMesh.Bones.Count; j++)
                    {
                        if (j >= refTrack.Length || refTrack[j] < 0) continue;
                        var rp = new Vector3(refWorld[j].M41, refWorld[j].M42, refWorld[j].M43);
                        float d = (rp - mp).LengthSquared;
                        if (d < bestD) { bestD = d; best = j; }
                    }
                    if (best >= 0) { lm.BoneToAnimTrack[i] = refTrack[best]; added++; }
                }
                if (added > 0)
                    Log($"Positional fallback: {lm.Name} nameMatched={matched}/{lm.Bones.Count} positional+={added} ref={refMesh.Name}");
            }
        }

        private void ApplyAnimationPose(float t)
        {
            if (_loadedMeshes.Count == 0) return;
            if (!_glReady || !glControl.IsHandleCreated) return;
            glControl.MakeCurrent();

            PoseSkeletons(t);

            // Cloth: replay the baked loop when it matches the current animation
            // (deterministic + seamless), otherwise run the real-time solver.
            for (int ci = 0; ci < _loadedMeshes.Count; ci++)
            {
                var clm = _loadedMeshes[ci];
                if (!clm.IsCloth || clm.ClothPos == null) continue;
                if (clm.ClothBake != null && ReferenceEquals(clm.ClothBakeAnim, _currentAnimation))
                    ApplyBakeCloth(clm, t);
                else
                    StepClothForMesh(clm, _clothDt);
            }

            RefreshVertexBuffers(!_playing);
        }

        private static bool TrackPositionVaries(GrannyTrack tr, float eps = 0.002f)
        {
            if (tr?.Pos == null || tr.Pos.Length < 2) return false;
            float eps2 = eps * eps;
            Vector3 first = tr.Pos[0];
            for (int k = 1; k < tr.Pos.Length; k++)
            {
                if ((tr.Pos[k] - first).LengthSquared > eps2)
                    return true;
            }
            return false;
        }

        // Compute every mesh's local/world/skin matrices for animation time t, plus the
        // rigid attach matrices. Pure CPU: it only fills the matrix arrays (no vertex
        // deformation, no GL upload), so the cloth bake can reuse it frame by frame.
        private void PoseSkeletons(float t, Vector3 riderOffset = default)
        {
            // Direct application: each track's local transforms are played straight on the
            // model skeleton (tracks are authored on the same joint frames as the mesh bones),
            // composed along the model hierarchy, then skinned with the inverse bind matrices.
            // No bind compensation: UO granny assets' animation bind differs from the mesh
            // bind, and any compensation (local or world) distorts the pose and tears the mesh.

            // Bind every mesh to the animation (name + positional fallback) before posing.
            if (_currentAnimation != null)
                EnsureAnimationBindings(_currentAnimation);

            foreach (var lm in _loadedMeshes)
            {
                if (lm.Bones.Count == 0) continue;
                // Hidden skeleton-only meshes (H_Male/H_Female placeholder) still need
                // their animated bone matrices computed so they can host attachments.

                int boneCount = lm.Bones.Count;
                if (lm.LocalMatrices == null || lm.LocalMatrices.Length != boneCount) lm.LocalMatrices = new Matrix4[boneCount];
                if (lm.WorldMatrices == null || lm.WorldMatrices.Length != boneCount) lm.WorldMatrices = new Matrix4[boneCount];
                if (lm.SkinMatrices == null || lm.SkinMatrices.Length != boneCount) lm.SkinMatrices = new Matrix4[boneCount];

                // A mesh with an animation override is posed on its OWN clip, sampled at
                // a time remapped from the rider's clock (so a longer/shorter mount clip
                // still lines up frame-for-frame with the rider's cycle).
                float meshT = t;
                if (lm.AnimOverride != null)
                    meshT = t * lm.AnimOverrideTimeScale + lm.AnimOverrideTimeBase;

                GrannyAnimation anim = (lm.CutoutRole == MeshCutoutRole.Mount) ? lm.AnimOverride : (lm.AnimOverride ?? _currentAnimation);
                int[] trackMap = (lm.CutoutRole == MeshCutoutRole.Mount) ? lm.AnimOverrideTrack : (lm.AnimOverride != null ? lm.AnimOverrideTrack : lm.BoneToAnimTrack);

                bool isCustomModel = GrannyModelFile.IsModelFile(lm.SourcePath);

                // Coordinate orientation compensation for 3ds Max Root Dummy tracks (e.g. Master_Blackthorn):
                GrannyTrack trMaster = anim?.Tracks?.FirstOrDefault(t => t.Name != null && t.Name.StartsWith("master_", StringComparison.OrdinalIgnoreCase));
                Quaternion rMaster = trMaster != null ? trMaster.EvaluateRot(meshT) : Quaternion.Identity;

                for (int i = 0; i < boneCount; i++)
                {
                    int trackIdx = (trackMap != null && i < trackMap.Length) ? trackMap[i] : -1;
                    if (anim != null && trackIdx >= 0 && trackIdx < anim.Tracks.Count)
                    {
                        var tr = anim.Tracks[trackIdx];
                        var b = lm.Bones[i];
                        int p = b.ParentId;
                        bool isDummyRoot = b.Name.Equals("__Root", StringComparison.OrdinalIgnoreCase) ||
                                           b.Name.StartsWith("master_", StringComparison.OrdinalIgnoreCase);

                        bool isRoot = p < 0 || p >= boneCount || p == i ||
                                      b.Name.Equals("Bip01", StringComparison.OrdinalIgnoreCase) ||
                                      (p >= 0 && p < boneCount && (lm.Bones[p].Name.Equals("__Root", StringComparison.OrdinalIgnoreCase) ||
                                                                   lm.Bones[p].Name.StartsWith("master_", StringComparison.OrdinalIgnoreCase)));

                        Vector3 pos;
                        Quaternion rot;
                        if (isDummyRoot)
                        {
                            // __Root and Master_* are 3ds Max scene helper/dummy objects without skinning weights.
                            // In UO/Iris mesh assets, they are always identity at origin. Keeping their rest bind
                            // prevents 3ds Max exporter coordinate flips from throwing the character off-axis.
                            pos = b.LocalPosition;
                            rot = b.LocalRotation;
                        }
                        else if (b.Name.Equals("Bip01", StringComparison.OrdinalIgnoreCase))
                        {
                            // If clip had a Master_* track with coordinate orientation, compose it:
                            rot = rMaster * tr.EvaluateRot(meshT);

                            Vector3 delta = Vector3.Zero;
                            if (tr.Pos != null && tr.Pos.Length > 0)
                            {
                                Vector3 trPos = tr.EvaluatePos(meshT);
                                Vector3 restPos = tr.Pos[0];
                                Vector3 localDelta = trPos - restPos;
                                delta = Vector3.TransformVector(localDelta, Matrix4.CreateFromQuaternion(rMaster));
                            }
                            pos = b.LocalPosition + delta;
                        }
                        else if (isRoot)
                        {
                            rot = tr.EvaluateRot(meshT);
                            Vector3 delta = Vector3.Zero;
                            if (tr.Pos != null && tr.Pos.Length > 0)
                            {
                                Vector3 trPos = tr.EvaluatePos(meshT);
                                Vector3 restPos = tr.Pos[0];
                                Vector3 localDelta = trPos - restPos;
                                delta = Vector3.TransformVector(localDelta, Matrix4.CreateFromQuaternion(rMaster));
                            }
                            pos = b.LocalPosition + delta;
                        }
                        else
                        {
                            Vector3 delta = Vector3.Zero;
                            if (b.Name.StartsWith("CP_", StringComparison.OrdinalIgnoreCase))
                            {
                                pos = tr.EvaluatePos(meshT);
                            }
                            else
                            {
                                if (tr.Pos != null && tr.Pos.Length > 1 && TrackPositionVaries(tr, 0.005f))
                                {
                                    Vector3 trPos = tr.EvaluatePos(meshT);
                                    Vector3 restPos = tr.Pos[0];
                                    delta = trPos - restPos;
                                }
                                pos = b.LocalPosition + delta;
                            }
                            rot = tr.EvaluateRot(meshT);
                        }

                        lm.LocalMatrices[i] = Matrix4.CreateFromQuaternion(rot) * Matrix4.CreateTranslation(pos);
                    }
                    else
                    {
                        var b = lm.Bones[i];
                        lm.LocalMatrices[i] = Matrix4.CreateFromQuaternion(b.LocalRotation) * Matrix4.CreateTranslation(b.LocalPosition);
                    }

                    // Pose studio layer. Two modes, both only affect the meshes chosen
                    // as pose targets (so a free imported equipment mesh you align
                    // against stays frozen):
                    //  * absolute (_poseAbsolute, from a loaded .pose): the stored local
                    //    transform REPLACES the animated/bind one, so reloading the pose
                    //    reproduces exactly what was on screen;
                    //  * additive (_poseOffsets, live editing): extra local rotation +
                    //    translation layered over whatever base is active.
                    if (PoseAppliesTo(lm))
                    {
                        if (_poseHasAbsolute && _poseAbsolute.Count > 0)
                        {
                            string abn = lm.Bones[i].Name;
                            if (!string.IsNullOrEmpty(abn) && _poseAbsolute.TryGetValue(abn, out var pa))
                            {
                                if (pa.HasBind)
                                {
                                    // v3: the pose is stored as (posed, bind). Row-vector
                                    // convention: display = delta * base, where the live
                                    // editor layers the offset on the LEFT of the base
                                    // (pq * local_base). So delta = posed * bind^-1 and the
                                    // pose is re-applied as delta * currentBase, making it
                                    // independent of the target mesh's rest orientation.
                                    var mPosed = Matrix4.CreateFromQuaternion(pa.Rot) * Matrix4.CreateTranslation(pa.Pos);
                                    var mBind = Matrix4.CreateFromQuaternion(pa.BindRot) * Matrix4.CreateTranslation(pa.BindPos);
                                    var mDelta = mPosed * mBind.Inverted();   // posed * bind^-1
                                    var bb = lm.Bones[i];
                                    var mBase = Matrix4.CreateFromQuaternion(bb.LocalRotation) * Matrix4.CreateTranslation(bb.LocalPosition);
                                    lm.LocalMatrices[i] = mDelta * mBase;
                                }
                                else
                                {
                                    // v2: absolute replacement (legacy behavior).
                                    lm.LocalMatrices[i] = Matrix4.CreateFromQuaternion(pa.Rot) * Matrix4.CreateTranslation(pa.Pos);
                                }
                            }
                        }
                        if (_poseOffsets.Count > 0)
                        {
                            string bn = lm.Bones[i].Name;
                            if (!string.IsNullOrEmpty(bn) && _poseOffsets.TryGetValue(bn, out var po))
                            {
                                if (po.RotDeg != Vector3.Zero)
                                {
                                    var pq = Quaternion.FromEulerAngles(
                                        MathHelper.DegreesToRadians(po.RotDeg.X),
                                        MathHelper.DegreesToRadians(po.RotDeg.Y),
                                        MathHelper.DegreesToRadians(po.RotDeg.Z));
                                    lm.LocalMatrices[i] = Matrix4.CreateFromQuaternion(pq) * lm.LocalMatrices[i];
                                }
                                if (po.Pos != Vector3.Zero)
                                    lm.LocalMatrices[i] = Matrix4.CreateTranslation(po.Pos) * lm.LocalMatrices[i];
                            }
                        }
                    }
                }

                ComputeWorldMatrices(lm);

                // Mount placement: scale about the root then translate, so the whole
                // mount is resized/repositioned under the rider in world space.
                if (lm.CutoutRole == MeshCutoutRole.Mount && (lm.MountScale != 1f || lm.MountOffset != Vector3.Zero))
                {
                    for (int i = 0; i < boneCount; i++)
                    {
                        int p = lm.Bones[i].ParentId;
                        bool isRoot = p < 0 || p >= boneCount || p == i;
                        if (!isRoot) continue;
                        lm.LocalMatrices[i] = Matrix4.CreateScale(lm.MountScale) * Matrix4.CreateTranslation(lm.MountOffset) * lm.LocalMatrices[i];
                    }
                    ComputeWorldMatrices(lm);
                }

                // Rider / Character action offset (e.g. Z = +0.38 for mounted actions)
                if (lm.CutoutRole != MeshCutoutRole.Mount && riderOffset != Vector3.Zero)
                {
                    for (int i = 0; i < boneCount; i++)
                    {
                        int p = lm.Bones[i].ParentId;
                        bool isRoot = p < 0 || p >= boneCount || p == i;
                        if (!isRoot) continue;
                        lm.LocalMatrices[i] = lm.LocalMatrices[i] * Matrix4.CreateTranslation(riderOffset);
                    }
                    ComputeWorldMatrices(lm);
                }

                // Pose studio: global displacement of the character body (root bones)
                // so it can be slid / rotated against a fixed imported equipment mesh.
                if ((_poseGlobalOffset != Vector3.Zero || Math.Abs(_poseGlobalYaw) > 1e-4f) && IsPoseGlobalTarget(lm))
                {
                    Matrix4 g = Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(_poseGlobalYaw))
                              * Matrix4.CreateTranslation(_poseGlobalOffset);
                    for (int i = 0; i < boneCount; i++)
                    {
                        int p = lm.Bones[i].ParentId;
                        bool isRoot = p < 0 || p >= boneCount || p == i;
                        if (!isRoot) continue;
                        lm.LocalMatrices[i] = lm.LocalMatrices[i] * g;
                    }
                    ComputeWorldMatrices(lm);
                }

                for (int i = 0; i < boneCount; i++)
                    lm.SkinMatrices[i] = lm.Bones[i].InverseBindMatrix * lm.WorldMatrices[i];
            }

            // Rigid bone attach: a weapon/shield/armor piece attached to a host bone
            // is driven by that bone's skin matrix (from the body's animated skeleton)
            // so it moves with the animation instead of sitting in its bind pose.
            foreach (var lm in _loadedMeshes)
            {
                lm.HasAttachMatrix = false;
                if (string.IsNullOrEmpty(lm.AttachBoneName)) continue;

                // Pick the host whose skeleton is the most complete (most bones). The
                // full H_Male/H_Female placeholder skeleton carries correctly positioned
                // sockets (CP_Grasp_*), whereas part meshes may expose a degenerate copy
                // of the same bone (e.g. the Torso's CP_Grasp pinned at the origin).
                LoadedMesh bestHost = null;
                int bestBi = -1;
                int bestBones = -1;
                foreach (var host in _loadedMeshes)
                {
                    if (ReferenceEquals(host, lm)) continue;
                    if (host.Bones == null || host.SkinMatrices == null) continue;
                    int bi = -1;
                    for (int k = 0; k < host.Bones.Count; k++)
                    {
                        if (string.Equals(host.Bones[k].Name, lm.AttachBoneName, StringComparison.OrdinalIgnoreCase))
                        { bi = k; break; }
                    }
                    if (bi < 0) continue;
                    if (host.Bones.Count > bestBones)
                    {
                        bestBones = host.Bones.Count;
                        bestHost = host;
                        bestBi = bi;
                    }
                }
                if (bestHost == null) continue;

                // Armor/hair are full-skeleton skinned meshes authored in the
                // body's bind space -> drive with the bone's skin matrix.
                // Weapons/bows/shields are rigid local-space meshes (root at
                // origin, no skin) -> place with the bone's world matrix.
                // Unskinned accessories in bind space (e.g. Dawn_Girl's sword)
                // also use the skin matrix.
                bool inBodySpace = lm.AttachInBindSpace;
                if (inBodySpace && bestBi < bestHost.SkinMatrices.Length)
                    lm.AttachMatrix = bestHost.SkinMatrices[bestBi];
                else if (bestHost.WorldMatrices != null && bestBi < bestHost.WorldMatrices.Length)
                    lm.AttachMatrix = BuildAttachTweak(lm) * bestHost.WorldMatrices[bestBi];
                else
                    continue;

                lm.HasAttachMatrix = true;
            }
        }

        // Bind-pose world matrices from the model's local bone transforms.
        private static Matrix4[] ComputeBindWorld(List<GrannyBone> bones)
        {
            int n = bones.Count;
            var loc = new Matrix4[n];
            for (int i = 0; i < n; i++)
                loc[i] = Matrix4.CreateFromQuaternion(bones[i].LocalRotation) * Matrix4.CreateTranslation(bones[i].LocalPosition);
            var w = new Matrix4[n];
            for (int i = 0; i < n; i++)
            {
                int p = bones[i].ParentId;
                w[i] = (p >= 0 && p < n && p != i) ? loc[i] * w[p] : loc[i];
            }
            return w;
        }

        // A character model may contain small unskinned meshes authored in bind space
        // (e.g. Dawn_Girl's sword). They don't deform, so without an attach they stay
        // frozen while the body animates. Auto-attach such accessories to the nearest
        // bone of the model's own skeleton, driven by that bone's skin matrix.
        private void TryAutoAttachUnskinned(LoadedMesh lm)
        {
            if (lm.Skin != null && lm.Skin.Length > 0) return;          // skinned -> normal path
            if (lm.Bones == null || lm.Bones.Count <= 4) return;        // standalone prop / tiny skeleton
            if (lm.BaseVertices == null || lm.BaseVertices.Length == 0) return;
            if (lm.BaseVertices.Length > 512) return;                   // only small accessories

            Vector3 c = Vector3.Zero;
            foreach (var v in lm.BaseVertices) c += v;
            c /= lm.BaseVertices.Length;

            var bw = ComputeBindWorld(lm.Bones);
            int best = -1; float bestD = float.MaxValue;
            for (int i = 0; i < lm.Bones.Count; i++)
            {
                if (string.IsNullOrEmpty(lm.Bones[i].Name)) continue;
                var p = new Vector3(bw[i].M41, bw[i].M42, bw[i].M43);
                float d = (p - c).LengthSquared;
                if (d < bestD) { bestD = d; best = i; }
            }
            if (best < 0) return;

            // Prefer an author-provided socket/holder bone near the accessory (e.g.
            // Dawn_Girl's "Sword01", human "CP_Grasp_*") over the bare nearest bone
            // (which can be a finger tip and pivots the item wrongly).
            string[] socketKeys = { "grasp", "cp_", "sword", "weapon", "shield", "bow", "staff", "wand", "axe", "mace" };
            int socket = -1; float socketD = float.MaxValue;
            for (int i = 0; i < lm.Bones.Count; i++)
            {
                string nm = lm.Bones[i].Name;
                if (string.IsNullOrEmpty(nm)) continue;
                string lnm = nm.ToLowerInvariant();
                bool isSocket = false;
                foreach (var k in socketKeys) if (lnm.Contains(k)) { isSocket = true; break; }
                if (!isSocket) continue;
                var p = new Vector3(bw[i].M41, bw[i].M42, bw[i].M43);
                float d = (p - c).LengthSquared;
                if (d < socketD) { socketD = d; socket = i; }
            }

            int chosen = best;
            if (socket >= 0 && socketD <= bestD + 0.10f) chosen = socket;

            lm.AttachBoneName = lm.Bones[chosen].Name;
            lm.AttachInBindSpace = true;
            Log($"AutoAttach: {lm.Name} verts={lm.BaseVertices.Length} -> {lm.AttachBoneName} (bindSpace)");
        }

        private void ComputeWorldMatrices(LoadedMesh lm)
        {
            int boneCount = lm.Bones.Count;
            var children = new List<int>[boneCount];
            for (int i = 0; i < boneCount; i++) children[i] = new List<int>();
            var roots = new List<int>();

            for (int i = 0; i < boneCount; i++)
            {
                int parent = lm.Bones[i].ParentId;
                if (parent >= 0 && parent < boneCount && parent != i)
                    children[parent].Add(i);
                else
                    roots.Add(i);
            }

            void ComputeWorld(int idx)
            {
                int parent = lm.Bones[idx].ParentId;
                if (parent >= 0 && parent < boneCount && parent != idx)
                    lm.WorldMatrices[idx] = lm.LocalMatrices[idx] * lm.WorldMatrices[parent];
                else
                    lm.WorldMatrices[idx] = lm.LocalMatrices[idx];

                foreach (int child in children[idx])
                    ComputeWorld(child);
            }

            foreach (int root in roots)
                ComputeWorld(root);
        }

        // Mirror + rotation + offset applied to an attached item in its own space.
        private static Matrix4 BuildAttachTweak(LoadedMesh lm)
        {
            float ps = lm.PartScale == 0f ? 1f : lm.PartScale;
            var s = new Vector3(
                (lm.MirrorX ? -1f : 1f) * ps * (lm.PartScaleVec.X == 0f ? 1f : lm.PartScaleVec.X),
                (lm.MirrorY ? -1f : 1f) * ps * (lm.PartScaleVec.Y == 0f ? 1f : lm.PartScaleVec.Y),
                (lm.MirrorZ ? -1f : 1f) * ps * (lm.PartScaleVec.Z == 0f ? 1f : lm.PartScaleVec.Z));
            Matrix4 scale = Matrix4.CreateScale(s);
            Vector3 rotAngles = lm.MirrorX
                ? new Vector3(lm.AttachRotation.X, -lm.AttachRotation.Y, -lm.AttachRotation.Z)
                : lm.AttachRotation;

            Matrix4 rot = Matrix4.CreateFromQuaternion(Quaternion.FromEulerAngles(
                MathHelper.DegreesToRadians(rotAngles.X),
                MathHelper.DegreesToRadians(rotAngles.Y),
                MathHelper.DegreesToRadians(rotAngles.Z)));

            Vector3 off = lm.MirrorX
                ? new Vector3(-lm.AttachOffset.X, lm.AttachOffset.Y, lm.AttachOffset.Z)
                : lm.AttachOffset;

            Matrix4 tr = Matrix4.CreateTranslation(off);
            // row-vector order: scale, then rotate, then translate
            return scale * rot * tr;
        }

        // ---- Real-time cloth simulation (verlet) ----

        private struct ClothCapsule
        {
            public Vector3 A, B;
            public float RA, RB;
            public string Name;
        }

        private Vector3 ApplyMeshTweak(LoadedMesh lm, Vector3 pos, bool hasThickness = false, Vector3 normal = default)
        {
            if (lm == null) return pos;

            Vector3 pscale = new Vector3(
                lm.PartScale * lm.PartScaleVec.X,
                lm.PartScale * lm.PartScaleVec.Y,
                lm.PartScale * lm.PartScaleVec.Z);
            bool scaleTweaked = pscale.X != 1f || pscale.Y != 1f || pscale.Z != 1f;

            bool hasRot = lm.AttachRotation != Vector3.Zero;
            bool hasMirror = lm.MirrorX || lm.MirrorY || lm.MirrorZ;
            bool hasOffset = lm.AttachOffset != Vector3.Zero;
            bool thick = hasThickness && lm.Thickness != 0f && normal != default;

            if (!scaleTweaked && !hasRot && !hasMirror && !hasOffset && !thick && !lm.MirrorArms)
                return pos;

            bool isMultiPart = !string.IsNullOrEmpty(lm.SourcePath) &&
                _loadedMeshes.Exists(m => !ReferenceEquals(m, lm) && string.Equals(m.SourcePath, lm.SourcePath, StringComparison.OrdinalIgnoreCase));
            bool isClothingOrBody = lm.CutoutRole != MeshCutoutRole.Accessory || lm.Skin != null || lm.WrapSource != null || IsClothingOrSkinned(lm);
            Vector3 scalePivot = (isMultiPart || !lm.HasBindCentroid) ? Vector3.Zero : (isClothingOrBody ? new Vector3(0, 0, lm.BindCentroid.Z) : lm.BindCentroid);
            Vector3 rotPivot = (isMultiPart || lm.CutoutRole != MeshCutoutRole.Accessory) ? Vector3.Zero : (isClothingOrBody ? new Vector3(0, 0, lm.BindCentroid.Z) : scalePivot);

            // =========================================================================
            // 1. BILATERAL MODE (gloves, bracers, shoulders, sleeves)
            // Symmetrical sagittal reflection across the character's midline X = 0!
            // =========================================================================
            if (lm.MirrorArms)
            {
                bool isLeft = pos.X >= 0f;

                // Strict sagittal symmetric pivots across X = 0
                float armX = lm.HasBilateralCentroids
                    ? (Math.Abs(lm.CentroidLeft.X) + Math.Abs(lm.CentroidRight.X)) * 0.5f
                    : Math.Abs(rotPivot.X);
                if (armX < 0.05f) armX = Math.Abs(pos.X);
                float armY = lm.HasBilateralCentroids
                    ? (lm.CentroidLeft.Y + lm.CentroidRight.Y) * 0.5f
                    : rotPivot.Y;
                float armZ = lm.HasBilateralCentroids
                    ? (lm.CentroidLeft.Z + lm.CentroidRight.Z) * 0.5f
                    : rotPivot.Z;

                Vector3 armPivot = isLeft
                    ? new Vector3(armX, armY, armZ)
                    : new Vector3(-armX, armY, armZ);

                Vector3 r = pos - armPivot;

                // 1. Scale around local arm pivot
                if (scaleTweaked)
                {
                    r = new Vector3(r.X * pscale.X, r.Y * pscale.Y, r.Z * pscale.Z);
                }

                // 2. Secondary axis mirrors (Y/Z) if requested
                if (lm.MirrorY || lm.MirrorZ)
                {
                    r = new Vector3(r.X, r.Y * (lm.MirrorY ? -1f : 1f), r.Z * (lm.MirrorZ ? -1f : 1f));
                }

                // 3. Rotation:
                // Right side (X < 0) rotates directly with rotMat.
                // Left side (X >= 0) rotates with EXACT sagittal mirror of rotMat: S * R * S
                if (hasRot)
                {
                    Matrix4 rotMat = Matrix4.CreateFromQuaternion(Quaternion.FromEulerAngles(
                        MathHelper.DegreesToRadians(lm.AttachRotation.X),
                        MathHelper.DegreesToRadians(lm.AttachRotation.Y),
                        MathHelper.DegreesToRadians(lm.AttachRotation.Z)));

                    if (!isLeft)
                    {
                        r = Vector3.TransformNormal(r, rotMat);
                    }
                    else
                    {
                        Vector3 rS = new Vector3(-r.X, r.Y, r.Z);
                        Vector3 rRot = Vector3.TransformNormal(rS, rotMat);
                        r = new Vector3(-rRot.X, rRot.Y, rRot.Z);
                    }
                }

                // 4. Offset (Déplacement):
                // Symmetrical across sagittal plane X = 0:
                // Positive AttachOffset.X pushes both arms OUTWARD away from sagittal midline.
                // Negative AttachOffset.X pulls both arms INWARD towards sagittal midline.
                Vector3 armOff = isLeft
                    ? new Vector3(lm.AttachOffset.X, lm.AttachOffset.Y, lm.AttachOffset.Z)
                    : new Vector3(-lm.AttachOffset.X, lm.AttachOffset.Y, lm.AttachOffset.Z);

                pos = armPivot + r + armOff;

                // 5. Thickness (along normal)
                if (thick)
                {
                    Vector3 dnorm;
                    if (hasRot)
                    {
                        Matrix4 rotMat = Matrix4.CreateFromQuaternion(Quaternion.FromEulerAngles(
                            MathHelper.DegreesToRadians(lm.AttachRotation.X),
                            MathHelper.DegreesToRadians(lm.AttachRotation.Y),
                            MathHelper.DegreesToRadians(lm.AttachRotation.Z)));

                        if (!isLeft)
                        {
                            dnorm = Vector3.TransformNormal(normal, rotMat);
                        }
                        else
                        {
                            Vector3 nS = new Vector3(-normal.X, normal.Y, normal.Z);
                            Vector3 nRot = Vector3.TransformNormal(nS, rotMat);
                            dnorm = new Vector3(-nRot.X, nRot.Y, nRot.Z);
                        }
                    }
                    else
                    {
                        dnorm = isLeft ? new Vector3(-normal.X, normal.Y, normal.Z) : normal;
                    }

                    if (dnorm.LengthSquared > 1e-12f)
                        pos += Vector3.Normalize(dnorm) * lm.Thickness;
                }

                return pos;
            }

            // =========================================================================
            // 2. UNILATERAL / STANDARD MODE (Single piece, accessory, weapon, armor)
            // When MirrorX is checked, mirror across the SAGITTAL MIDLINE X = 0!
            // =========================================================================
            if (scaleTweaked)
            {
                Vector3 r = pos - scalePivot;
                r = new Vector3(r.X * pscale.X, r.Y * pscale.Y, r.Z * pscale.Z);
                pos = scalePivot + r;
            }

            if (hasMirror || hasRot || hasOffset)
            {
                // When MirrorX is active, the sagittal mirror plane is X = 0!
                Vector3 effPivot = lm.MirrorX
                    ? new Vector3(0, rotPivot.Y, rotPivot.Z)
                    : rotPivot;

                Vector3 rRot = pos - effPivot;

                if (hasMirror)
                {
                    rRot = new Vector3(
                        rRot.X * (lm.MirrorX ? -1f : 1f),
                        rRot.Y * (lm.MirrorY ? -1f : 1f),
                        rRot.Z * (lm.MirrorZ ? -1f : 1f));
                }

                if (hasRot)
                {
                    Matrix4 rotMat = Matrix4.CreateFromQuaternion(Quaternion.FromEulerAngles(
                        MathHelper.DegreesToRadians(lm.AttachRotation.X),
                        MathHelper.DegreesToRadians(lm.AttachRotation.Y),
                        MathHelper.DegreesToRadians(lm.AttachRotation.Z)));

                    if (lm.MirrorX)
                    {
                        // Sagittal reflection of rotation: S * R * S
                        Vector3 rS = new Vector3(-rRot.X, rRot.Y, rRot.Z);
                        Vector3 rRotated = Vector3.TransformNormal(rS, rotMat);
                        rRot = new Vector3(-rRotated.X, rRotated.Y, rRotated.Z);
                    }
                    else
                    {
                        rRot = Vector3.TransformNormal(rRot, rotMat);
                    }
                }

                Vector3 effOffset = lm.MirrorX
                    ? new Vector3(-lm.AttachOffset.X, lm.AttachOffset.Y, lm.AttachOffset.Z)
                    : lm.AttachOffset;

                pos = effPivot + rRot + effOffset;
            }

            if (thick)
            {
                Vector3 dnorm = normal;
                if (hasMirror)
                {
                    dnorm = new Vector3(
                        dnorm.X * (lm.MirrorX ? -1f : 1f),
                        dnorm.Y * (lm.MirrorY ? -1f : 1f),
                        dnorm.Z * (lm.MirrorZ ? -1f : 1f));
                }
                if (hasRot)
                {
                    Matrix4 rotMat = Matrix4.CreateFromQuaternion(Quaternion.FromEulerAngles(
                        MathHelper.DegreesToRadians(lm.AttachRotation.X),
                        MathHelper.DegreesToRadians(lm.AttachRotation.Y),
                        MathHelper.DegreesToRadians(lm.AttachRotation.Z)));

                    if (lm.MirrorX)
                    {
                        Vector3 nS = new Vector3(-dnorm.X, dnorm.Y, dnorm.Z);
                        Vector3 nRot = Vector3.TransformNormal(nS, rotMat);
                        dnorm = new Vector3(-nRot.X, nRot.Y, nRot.Z);
                    }
                    else
                    {
                        dnorm = Vector3.TransformNormal(dnorm, rotMat);
                    }
                }
                if (dnorm.LengthSquared > 1e-12f)
                    pos += Vector3.Normalize(dnorm) * lm.Thickness;
            }

            return pos;
        }

        // Reset every cloth mesh to its rest (bind) shape and snap pinned vertices to current pose.
        private void ResetAllCloth()
        {
            foreach (var lm in _loadedMeshes)
            {
                if (lm.IsCloth && lm.ClothPos != null && lm.ClothPos0 != null)
                {
                    int len = Math.Min(lm.ClothPos.Length, lm.ClothPos0.Length);
                    for (int p = 0; p < len; p++)
                    {
                        Vector3 target = ClothPinTarget(lm, p);
                        Vector3 initialPos = (target != Vector3.Zero) ? target : lm.ClothPos0[p];
                        lm.ClothPos[p] = initialPos;
                        if (lm.ClothPrev != null && p < lm.ClothPrev.Length)
                            lm.ClothPrev[p] = initialPos;
                    }
                }
            }
        }

        // Lazily build the cloth representation from the mesh's bind geometry:
        // particles = WELDED vertices (coincident copies share one), constraints =
        // triangle edges. Pinning attaches the upper body (torso, arms, pelvis) to the
        // animated skeleton, leaving the loose skirt or cape to flow freely (XPBD / Velvet style).
        private void BuildClothFromMesh(LoadedMesh lm)
        {
            if (lm.BaseVertices == null || lm.BaseVertices.Length == 0) return;
            if (lm.BaseVertexNormals == null || lm.BaseVertexNormals.Length != lm.BaseVertices.Length) BuildCornerData(lm);
            if (lm.Weld == null || lm.Weld.Length != lm.BaseVertices.Length) BuildWeld(lm);
            int P = Math.Max(1, lm.WeldCount);

            // Representative original vertex + rest position for each particle.
            var rep = new int[P];
            var rest = new Vector3[P];
            for (int p = 0; p < P; p++) rep[p] = -1;
            for (int v = 0; v < lm.Weld.Length; v++)
            {
                int p = lm.Weld[v];
                if (p >= 0 && p < P && rep[p] < 0)
                {
                    rep[p] = v;
                    Vector3 orig = lm.BaseVertices[v];
                    Vector3 norm = (lm.BaseVertexNormals != null && v < lm.BaseVertexNormals.Length) ? lm.BaseVertexNormals[v] : Vector3.Zero;
                    rest[p] = ApplyMeshTweak(lm, orig, true, norm);
                }
            }
            for (int p = 0; p < P; p++)
            {
                if (rep[p] < 0)
                {
                    rep[p] = 0;
                    rest[p] = lm.BaseVertices.Length > 0 ? lm.BaseVertices[0] : Vector3.Zero;
                }
            }
            lm.ClothRep = rep;
            lm.ClothPos0 = rest;
            lm.ClothPos = (Vector3[])rest.Clone();
            lm.ClothPrev = (Vector3[])rest.Clone();
            lm.ClothPin = new bool[P];
            lm.ClothPinWeight = new float[P];

            float minZ = float.MaxValue, maxZ = float.MinValue;
            for (int p = 0; p < P; p++) { float z = rest[p].Z; if (z < minZ) minZ = z; if (z > maxZ) maxZ = z; }
            float height = Math.Max(1e-4f, maxZ - minZ);

            bool hasSkin = (lm.WrapSkin != null && lm.WrapSkin.Length > 0) ||
                           (lm.Skin != null && lm.Skin.Length > 0);

            for (int p = 0; p < P; p++)
            {
                int v = rep[p];
                float w = 0f;

                // Anchors: neck / collar / top seam of the shoulders is 100% pinned (like a hanger).
                bool isCollar = height > 0.8f
                    ? (rest[p].Z >= (maxZ - 0.05f * height))
                    : (rest[p].Z >= (maxZ - 0.10f * height));

                if (hasSkin && v >= 0)
                {
                    var infs = (lm.WrapSkin != null && v < lm.WrapSkin.Length)
                        ? lm.WrapSkin[v]
                        : (lm.Skin != null && v < lm.Skin.Length ? lm.Skin[v] : null);
                    var boneList = (lm.WrapSkin != null && lm.WrapSource != null) ? lm.WrapSource.Bones : lm.Bones;
                    if (boneList == null)
                    {
                        var host = _loadedMeshes.Find(m => !ReferenceEquals(m, lm) && m.Bones != null && m.Bones.Count >= 2);
                        if (host != null) boneList = host.Bones;
                    }

                    if (infs != null && infs.Length > 0 && boneList != null)
                    {
                        float upperBodyWeight = 0f;
                        float shoulderWeight = 0f;
                        float upperArmWeight = 0f;
                        float forearmWeight = 0f;
                        float handWeight = 0f;
                        float legWeight = 0f;
                        float neckWeight = 0f;

                        for (int k = 0; k < infs.Length; k++)
                        {
                            int bid = infs[k].BoneId;
                            if (bid >= 0 && bid < boneList.Count)
                            {
                                string bName = boneList[bid].Name;
                                if (IsNeckBone(bName))
                                    neckWeight += infs[k].Weight;
                                else if (IsShoulderBone(bName))
                                    shoulderWeight += infs[k].Weight;
                                else if (IsUpperArmBone(bName))
                                    upperArmWeight += infs[k].Weight;
                                else if (IsForearmBone(bName))
                                    forearmWeight += infs[k].Weight;
                                else if (IsHandBone(bName))
                                    handWeight += infs[k].Weight;
                                else if (IsTorsoBone(bName) || IsPelvisBone(bName))
                                    upperBodyWeight += infs[k].Weight;
                                else if (IsLegBone(bName))
                                    legWeight += infs[k].Weight;
                            }
                        }

                        float relZ = (rest[p].Z - minZ) / height;
                        if (neckWeight > 0.4f || isCollar)
                        {
                            w = 1.0f; // Rigid anchor at neck/collar line / hood top
                        }
                        else if (shoulderWeight > 0.35f)
                        {
                            // Shoulder seam: attached to shoulder joint
                            w = 0.85f;
                        }
                        else if (upperArmWeight > 0.20f)
                        {
                            // Upper arm: follows bicep/tricep movement
                            w = 0.70f;
                        }
                        else if (forearmWeight > 0.15f)
                        {
                            // Forearm: follows forearm and elbow movement firmly
                            w = 0.60f;
                        }
                        else if (handWeight > 0.15f)
                        {
                            // Wrist / cuff / bell sleeve: follows wrist movement, with 55% cloth freedom to drape & sway
                            w = 0.45f;
                        }
                        else if (upperBodyWeight > 0.30f || relZ >= 0.50f)
                        {
                            // Torso / chest / ribs / waist
                            if (relZ >= 0.70f)
                                w = 0.80f; // Upper chest & back
                            else if (relZ >= 0.50f)
                                w = 0.70f; // Abdomen & waistline
                            else
                                w = 0.65f; // Upper pelvis
                        }
                        else if (legWeight > 0.15f || relZ < 0.50f)
                        {
                            // Thighs, knees, lower skirt, pants: firmly follow hip and leg cadence
                            if (relZ >= 0.38f)
                                w = 0.60f; // Upper thighs / hips
                            else if (relZ >= 0.22f)
                                w = 0.50f; // Mid thigh & knees (firmly follows knee stride!)
                            else
                                w = 0.40f; // Knees, shins, hem (follows stride with 60% cloth freedom to billow and drape!)
                        }
                        else
                        {
                            w = 0.45f;
                        }
                    }
                    else
                    {
                        // Fallback vertical height gradient
                        float relZ = (rest[p].Z - minZ) / height;
                        if (isCollar || relZ >= 0.88f)
                            w = 1.0f;
                        else if (relZ >= 0.60f)
                            w = 0.70f;
                        else if (relZ >= 0.35f)
                            w = 0.55f;
                        else
                            w = 0.40f;
                    }
                }
                else
                {
                    // Non-skinned piece (e.g. cape or attached accessory)
                    float relZ = (rest[p].Z - minZ) / height;
                    if (isCollar || relZ >= 0.88f)
                        w = 1.0f;
                    else if (relZ >= 0.60f)
                        w = 0.70f;
                    else if (relZ >= 0.35f)
                        w = 0.55f;
                    else
                        w = 0.40f;
                }

                lm.ClothPinWeight[p] = w;
                lm.ClothPin[p] = (w >= 0.999f);
            }

            // Edges from the triangle corners, remapped to particles (deduplicated).
            var edges = new HashSet<long>();
            var a = new List<int>();
            var b = new List<int>();
            int cc = lm.CornerVertex != null ? lm.CornerVertex.Length : 0;
            for (int t = 0; t + 2 < cc; t += 3)
            {
                int w0 = WeldOf(lm, lm.CornerVertex[t]);
                int w1 = WeldOf(lm, lm.CornerVertex[t + 1]);
                int w2 = WeldOf(lm, lm.CornerVertex[t + 2]);
                if (w0 < 0 || w1 < 0 || w2 < 0 || w0 >= P || w1 >= P || w2 >= P) continue;
                AddClothEdge(edges, a, b, w0, w1);
                AddClothEdge(edges, a, b, w1, w2);
                AddClothEdge(edges, a, b, w2, w0);
            }
            var restLengths = new List<float>();
            var validA = new List<int>();
            var validB = new List<int>();
            for (int e = 0; e < a.Count; e++)
            {
                float d = Vector3.Distance(rest[a[e]], rest[b[e]]);
                if (d > 1e-5f && !float.IsNaN(d) && !float.IsInfinity(d))
                {
                    validA.Add(a[e]);
                    validB.Add(b[e]);
                    restLengths.Add(d);
                }
            }
            lm.ClothEdgeA = validA.ToArray();
            lm.ClothEdgeB = validB.ToArray();
            lm.ClothRest = restLengths.ToArray();

            // Detect isolated particles (particles with no cloth edges) and pin them 100%
            // to their skin target so they never drift or wander into space!
            var edgeCountPerParticle = new int[P];
            for (int e = 0; e < lm.ClothEdgeA.Length; e++)
            {
                edgeCountPerParticle[lm.ClothEdgeA[e]]++;
                edgeCountPerParticle[lm.ClothEdgeB[e]]++;
            }
            for (int p = 0; p < P; p++)
            {
                if (edgeCountPerParticle[p] == 0)
                {
                    lm.ClothPin[p] = true;
                    lm.ClothPinWeight[p] = 1.0f;
                }
            }
        }

        private static bool IsPelvisBone(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string l = s.ToLowerInvariant();
            return l.Contains("pelvis") || l.Contains("hip") || (l.Contains("root") && !l.Contains("motion"));
        }

        private static bool IsTorsoBone(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string l = s.ToLowerInvariant();
            return l.Contains("spine") || l.Contains("chest") || l.Contains("rib") || l.Contains("belly") || l.Contains("abdomen") || l.Contains("torso");
        }

        private static bool IsNeckBone(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string l = s.ToLowerInvariant();
            return l.Contains("neck") || l.Contains("head");
        }

        private static bool IsShoulderBone(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string l = s.ToLowerInvariant();
            return l.Contains("clavicle") || l.Contains("shoulder") || l.Contains("collar");
        }

        private static bool IsUpperArmBone(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string l = s.ToLowerInvariant();
            if (l.Contains("forearm") || l.Contains("lowerarm") || l.Contains("hand") || l.Contains("finger") || l.Contains("wrist")) return false;
            return l.Contains("upperarm") || l.Contains("upper_arm") || l.Contains("uparm") || l.Contains("up_arm") || l.Contains("arm");
        }

        private static bool IsForearmBone(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string l = s.ToLowerInvariant();
            if (l.Contains("hand") || l.Contains("finger") || l.Contains("wrist")) return false;
            return l.Contains("forearm") || l.Contains("fore_arm") || l.Contains("lowerarm") || l.Contains("lower_arm") || l.Contains("elbow");
        }

        private static bool IsHandBone(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string l = s.ToLowerInvariant();
            return l.Contains("hand") || l.Contains("wrist") || l.Contains("palm") || l.Contains("finger") || l.Contains("thumb");
        }

        private static bool IsThighBone(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string l = s.ToLowerInvariant();
            if (l.Contains("calf") || l.Contains("shin") || l.Contains("knee") || l.Contains("foot") || l.Contains("toe") || l.Contains("ankle")) return false;
            return l.Contains("thigh") || l.Contains("upperleg") || l.Contains("upper_leg") || l.Contains("upleg") || l.Contains("up_leg");
        }

        private static bool IsCalfBone(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string l = s.ToLowerInvariant();
            if (l.Contains("thigh") || l.Contains("upperleg") || l.Contains("upleg") || l.Contains("foot") || l.Contains("toe") || l.Contains("ankle")) return false;
            return l.Contains("calf") || l.Contains("lowerleg") || l.Contains("lower_leg") || l.Contains("shin") || l.Contains("knee") || l.Contains("leg");
        }

        private static bool IsFootBone(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            string l = s.ToLowerInvariant();
            return l.Contains("foot") || l.Contains("ankle") || l.Contains("toe") || l.Contains("heel");
        }

        private static bool IsLegBone(string s)
        {
            return IsThighBone(s) || IsCalfBone(s) || IsFootBone(s);
        }

        private static int WeldOf(LoadedMesh lm, int vi)
        {
            if (lm.Weld == null || vi < 0 || vi >= lm.Weld.Length) return -1;
            return lm.Weld[vi];
        }

        private static void AddClothEdge(HashSet<long> set, List<int> a, List<int> b, int i, int j)
        {
            if (i < 0 || j < 0 || i == j) return;
            long key = i < j ? ((long)i << 32) | (uint)j : ((long)j << 32) | (uint)i;
            if (set.Add(key)) { a.Add(i); b.Add(j); }
        }

        // Skinned position of a pinned particle: dynamically calculates animated
        // position using WrapSkin + master skeleton matrices, native Skin matrices,
        // or rigid attach matrix, and applies scale/rotation/offset/thickness tweaks.
        private Vector3 ClothPinTarget(LoadedMesh lm, int p)
        {
            int v = (lm.ClothRep != null && p >= 0 && p < lm.ClothRep.Length) ? lm.ClothRep[p] : -1;
            if (v < 0 || lm.BaseVertices == null || v >= lm.BaseVertices.Length) return Vector3.Zero;
            Vector3 orig = lm.BaseVertices[v];

            bool thick = lm.Thickness != 0f && lm.BaseVertexNormals != null && v < lm.BaseVertexNormals.Length;
            Vector3 baseN = thick ? lm.BaseVertexNormals[v] : Vector3.Zero;
            Vector3 skinnedPos = orig;
            Vector3 dnorm = baseN;

            // 1. WrapSkin (mesh skin-wrapped to host skeleton)
            if (lm.WrapSkin != null && v < lm.WrapSkin.Length && lm.WrapSkin[v] != null && lm.WrapSkin[v].Length > 0)
            {
                var inf = lm.WrapSkin[v];
                Matrix4[] mats = (lm.WrapSource != null && lm.WrapSource.SkinMatrices != null) ? lm.WrapSource.SkinMatrices : null;
                int bc = (lm.WrapSource != null && lm.WrapSource.Bones != null) ? lm.WrapSource.Bones.Count : (mats != null ? mats.Length : 0);

                if (mats == null)
                {
                    var host = _loadedMeshes.Find(m => !ReferenceEquals(m, lm) && m.SkinMatrices != null && m.SkinMatrices.Length >= 2);
                    if (host != null)
                    {
                        mats = host.SkinMatrices;
                        bc = host.Bones != null ? host.Bones.Count : mats.Length;
                    }
                }

                if (mats != null)
                {
                    Vector3 def = Vector3.Zero;
                    Vector3 dn = Vector3.Zero;
                    float tw = 0f;
                    for (int k = 0; k < inf.Length; k++)
                    {
                        int bid = inf[k].BoneId;
                        if (bid >= 0 && bid < bc && bid < mats.Length)
                        {
                            def += Vector3.TransformPosition(orig, mats[bid]) * inf[k].Weight;
                            if (thick) dn += Vector3.TransformNormal(baseN, mats[bid]) * inf[k].Weight;
                            tw += inf[k].Weight;
                        }
                    }
                    if (tw > 1e-4f)
                    {
                        skinnedPos = def / tw;
                        dnorm = dn;
                    }
                }
            }
            // 2. Native Skin
            else if (lm.Skin != null && v < lm.Skin.Length && lm.Skin[v] != null && lm.Skin[v].Length > 0)
            {
                var inf = lm.Skin[v];
                Matrix4[] mats = lm.SkinMatrices;
                int bc = lm.Bones != null ? lm.Bones.Count : (mats != null ? mats.Length : 0);

                if (mats == null)
                {
                    var host = _loadedMeshes.Find(m => !ReferenceEquals(m, lm) && m.SkinMatrices != null && m.SkinMatrices.Length >= 2);
                    if (host != null && lm.Bones != null && host.Bones != null)
                    {
                        // Map each bone of lm by NAME to host's bone matrices!
                        // This prevents wearable dummy nodes (__Description, .max, .tga) from mapping to wrong character bones!
                        mats = new Matrix4[lm.Bones.Count];
                        for (int bi = 0; bi < lm.Bones.Count; bi++)
                        {
                            string bn = lm.Bones[bi].Name;
                            int hi = host.Bones.FindIndex(hb => hb.Name.Equals(bn, StringComparison.OrdinalIgnoreCase));
                            if (hi >= 0 && hi < host.SkinMatrices.Length)
                                mats[bi] = host.SkinMatrices[hi];
                            else
                                mats[bi] = Matrix4.Identity;
                        }
                        bc = lm.Bones.Count;
                    }
                }

                if (mats != null)
                {
                    Vector3 def = Vector3.Zero;
                    Vector3 dn = Vector3.Zero;
                    float tw = 0f;
                    for (int k = 0; k < inf.Length; k++)
                    {
                        int bid = inf[k].BoneId;
                        if (bid >= 0 && bid < bc && bid < mats.Length)
                        {
                            Matrix4 bm = mats[bid];
                            if (bm.M44 == 0f && bm.M11 == 0f) continue;
                            def += Vector3.TransformPosition(orig, bm) * inf[k].Weight;
                            if (thick) dn += Vector3.TransformNormal(baseN, bm) * inf[k].Weight;
                            tw += inf[k].Weight;
                        }
                    }
                    if (tw > 1e-4f)
                    {
                        skinnedPos = def / tw;
                        dnorm = dn;
                    }
                    else if (orig.LengthSquared > 0.01f)
                    {
                        var host = _loadedMeshes.Find(m => !ReferenceEquals(m, lm) && m.SkinMatrices != null && m.SkinMatrices.Length >= 2);
                        if (host != null && host.SkinMatrices != null)
                        {
                            int spineIdx = host.Bones != null ? host.Bones.FindIndex(b => IsTorsoBone(b.Name)) : -1;
                            if (spineIdx >= 0 && spineIdx < host.SkinMatrices.Length)
                                skinnedPos = Vector3.TransformPosition(orig, host.SkinMatrices[spineIdx]);
                        }
                    }
                }
            }
            // 3. Rigid attach
            else if (lm.HasAttachMatrix)
            {
                return Vector3.TransformPosition(orig, lm.AttachMatrix);
            }

            if (skinnedPos.LengthSquared < 1e-4f && orig.LengthSquared > 0.01f)
            {
                skinnedPos = orig;
            }

            return ApplyMeshTweak(lm, skinnedPos, thick, dnorm);
        }

        private void SetClothSelected(bool on)
        {
            var idxs = GetSelectedMeshIndices();
            int i = idxs.Count > 0 ? idxs[0] : -1;
            if (i < 0 || i >= _loadedMeshes.Count)
            {
                if (on) Log("Tissu : aucun mesh sélectionné.");
                return;
            }
            var lm = _loadedMeshes[i];
            lm.IsCloth = on;
            if (on)
            {
                ClearClothBake(lm); // rebuild -> (re)bake needed

                // Absorb Repousser / Rétrécir clearance directly into BaseVertices so the relaxed
                // non-penetrating shape is 100% preserved and dynamically follows all bones during animations!
                if ((lm.PushOutOfBody || lm.ShrinkSew) && lm.DeformedCache != null && lm.DeformedCache.Length == lm.BaseVertices.Length)
                {
                    for (int v = 0; v < lm.BaseVertices.Length; v++)
                    {
                        Vector3 norm = (lm.BaseVertexNormals != null && v < lm.BaseVertexNormals.Length) ? lm.BaseVertexNormals[v] : Vector3.Zero;
                        Vector3 normalTweak = ApplyMeshTweak(lm, lm.BaseVertices[v], true, norm);
                        Vector3 pushDelta = lm.DeformedCache[v] - normalTweak;
                        // Clamp displacement to valid range (< 20 cm)
                        if (pushDelta.LengthSquared > 1e-8f && pushDelta.LengthSquared < 0.04f)
                        {
                            lm.BaseVertices[v] += pushDelta;
                        }
                    }
                    BuildCornerData(lm);
                    SetupMeshVBO(lm);
                    lm.PushOutOfBody = false;
                    lm.ShrinkSew = false;
                    UpdateClothingEffectButtons();
                    Log($"Tissu : '{lm.Name}' a absorbé le repousser dans son patron de repos.");
                }

                // If mesh has placement tweaks (rotation, offset, scale), bake them into BaseVertices
                // so the placed T-pose shape becomes the permanent bind geometry and won't get distorted
                // during skeletal animations!
                if (IsMeshTweaked(lm))
                {
                    BakeMeshGeometry(lm);
                    Log($"Tissu : '{lm.Name}' - placement T-pose figé dans la géométrie.");
                }

                // If mesh is NOT skinned or has dummy/placeholder wearable bones (__Description, .max, .tga),
                // automatically link it (SkinWrap) to the character body so the sleeves, torso, and legs follow cleanly.
                // Properly skinned official gear (like Robe with 22 bones) preserves its native artist rig!
                bool isDummyBone(string nm) =>
                    string.IsNullOrEmpty(nm) ||
                    nm.StartsWith("__Description", StringComparison.OrdinalIgnoreCase) ||
                    nm.EndsWith(".max", StringComparison.OrdinalIgnoreCase) ||
                    nm.EndsWith(".tga", StringComparison.OrdinalIgnoreCase);

                int realBones = lm.Bones != null ? lm.Bones.Count(b => !isDummyBone(b.Name)) : 0;
                bool isProperlySkinned = lm.Skin != null && lm.Skin.Length > 0 && realBones >= 10;
                bool needsWrap = !isProperlySkinned && lm.WrapSkin == null && !lm.HasAttachMatrix;
                if (needsWrap && lm.WrapSource == null)
                {
                    bool hasHostBody = _loadedMeshes.Exists(m => !ReferenceEquals(m, lm) && m.Bones != null && m.Bones.Count >= 2);
                    if (hasHostBody)
                    {
                        Log($"Tissu : '{lm.Name}' - harmonisation de la peau (skin wrap) au squelette du corps...");
                        SkinWrapSelected();
                    }
                }

                BuildClothFromMesh(lm);
                ResetAllCloth();
                Log($"Tissu : '{lm.Name}' activé (verts={lm.BaseVertices.Length} particules={lm.ClothPos?.Length ?? 0}, liens={lm.ClothEdgeA?.Length ?? 0}).");
            }
            else
            {
                ClearClothBake(lm);
                Log($"Tissu : '{lm.Name}' désactivé.");
            }
            ApplyAnimationPose(_currentTime);
            if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();
        }

        // Collect bone capsules of every other skinned mesh (or master body host),
        // giving analytical cylinder colliders for the full skeleton: thighs, knees/shins,
        // feet, pelvis/hips, spine, chest, neck, head, shoulders, and arms.
        private List<ClothCapsule> BuildBodyCapsules(LoadedMesh clothMesh)
        {
            var caps = new List<ClothCapsule>();

            var targetMeshes = new List<LoadedMesh>();
            foreach (var m in _loadedMeshes)
            {
                if (ReferenceEquals(m, clothMesh)) continue;
                if (m.IsCloth) continue;
                if (m.WorldMatrices != null && m.Bones != null && m.Bones.Count >= 2)
                    targetMeshes.Add(m);
            }

            if (targetMeshes.Count == 0 && clothMesh != null && clothMesh.Bones != null && clothMesh.Bones.Count >= 2 && clothMesh.WorldMatrices != null)
            {
                targetMeshes.Add(clothMesh);
            }

            var seenSegments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            float clearance = Math.Max(0.005f, clothMesh != null ? clothMesh.Thickness : 0.005f);

            foreach (var m in targetMeshes)
            {
                if (m.WorldMatrices == null || m.Bones == null || m.Bones.Count < 2) continue;
                int bn = m.Bones.Count;
                bool hasHeadSegment = false;

                for (int i = 0; i < bn; i++)
                {
                    int p = m.Bones[i].ParentId;
                    if (p < 0 || p >= bn || p == i) continue;
                    if (i >= m.WorldMatrices.Length || p >= m.WorldMatrices.Length) continue;

                    string bName = m.Bones[i].Name;
                    string pName = m.Bones[p].Name;

                    // Filter out non-bone dummies (Root, master, Skin meshes, attachment points CP_, Nubs)
                    if (pName.StartsWith("__") || pName.StartsWith("master", StringComparison.OrdinalIgnoreCase)) continue;
                    if (bName.Contains("Skin") || pName.Contains("Skin")) continue;
                    if (bName.StartsWith("CP_") || pName.StartsWith("CP_")) continue;
                    if (pName.Contains("Nub")) continue;
                    if (bName.Contains("Nub") && !bName.ToLowerInvariant().Contains("head")) continue;

                    // Keep full harmonized skeleton segments (legs, hips, spine/chest, neck/head, arms)
                    if (!IsBodyCapsuleSegment(pName, bName)) continue;

                    string segKey = $"{pName}->{bName}";
                    if (!seenSegments.Add(segKey)) continue;

                    var wa = m.WorldMatrices[p];
                    var wb = m.WorldMatrices[i];
                    Vector3 A = new Vector3(wa.M41, wa.M42, wa.M43);
                    Vector3 B = new Vector3(wb.M41, wb.M42, wb.M43);
                    float len = Vector3.Distance(A, B);
                    if (len < 0.03f) continue;
                    float rA = CapsuleRadiusFor(pName, clearance);
                    float rB = CapsuleRadiusFor(bName, clearance);

                    // Anatomical leg tapering based on actual UO male leg mesh dimensions:
                    // Thigh -> Calf: hip (0.095m) tapering to knee (0.090m)
                    if (IsThighBone(pName) && IsCalfBone(bName))
                    {
                        rA = 0.095f + clearance + _clothClearance;
                        rB = 0.090f + clearance + _clothClearance;
                    }
                    // Calf -> Foot: knee (0.090m) tapering to ankle (0.065m)
                    else if (IsCalfBone(pName) && IsFootBone(bName))
                    {
                        rA = 0.090f + clearance + _clothClearance;
                        rB = 0.065f + clearance + _clothClearance;
                    }
                    // Foot -> Toe: ankle (0.065m) tapering to toe (0.060m)
                    else if (IsFootBone(pName))
                    {
                        rA = 0.065f + clearance + _clothClearance;
                        rB = 0.060f + clearance + _clothClearance;
                    }

                    // For the neck segment (Neck -> Head), keep the neck slender along its full length!
                    // This creates a clear, distinct neck indentation between shoulders and head.
                    if (pName.ToLowerInvariant().Contains("neck") && bName.ToLowerInvariant().Contains("head"))
                    {
                        rA = CapsuleRadiusFor("neck", clearance); // 0.045f + clearance
                        rB = 0.048f + clearance + _clothClearance; // 0.048f + clearance at jaw level
                    }

                    if ((pName.ToLowerInvariant().Contains("head") || bName.ToLowerInvariant().Contains("head")) && IsNeckBone(bName))
                    {
                        hasHeadSegment = true;
                    }

                    caps.Add(new ClothCapsule
                    {
                        A = A, B = B,
                        RA = rA, RB = rB,
                        Name = bName
                    });
                }

                // If this mesh has a Head bone but NO child HeadNub / Head_End bone, synthesize the head capsule
                // extending upwards from the jaw/head joint into the cranium!
                if (!hasHeadSegment)
                {
                    int headIdx = m.Bones.FindIndex(b => b.Name.IndexOf("head", StringComparison.OrdinalIgnoreCase) >= 0 && b.Name.IndexOf("nub", StringComparison.OrdinalIgnoreCase) < 0);
                    if (headIdx >= 0 && headIdx < m.WorldMatrices.Length)
                    {
                        int neckIdx = m.Bones[headIdx].ParentId;
                        Vector3 hPos = new Vector3(m.WorldMatrices[headIdx].M41, m.WorldMatrices[headIdx].M42, m.WorldMatrices[headIdx].M43);
                        Vector3 hDir = Vector3.UnitY;
                        if (neckIdx >= 0 && neckIdx < m.WorldMatrices.Length)
                        {
                            Vector3 nPos = new Vector3(m.WorldMatrices[neckIdx].M41, m.WorldMatrices[neckIdx].M42, m.WorldMatrices[neckIdx].M43);
                            Vector3 diff = hPos - nPos;
                            if (diff.LengthSquared > 1e-4f) hDir = Vector3.Normalize(diff);
                        }
                        string headKey = $"{m.Bones[headIdx].Name}->HeadTop";
                        if (seenSegments.Add(headKey))
                        {
                            float rHead = CapsuleRadiusFor("head", clearance);
                            caps.Add(new ClothCapsule
                            {
                                A = hPos,
                                B = hPos + hDir * 0.14f,
                                RA = rHead,
                                RB = rHead,
                                Name = m.Bones[headIdx].Name + "_Cranium"
                            });
                        }
                    }
                }
            }
            return caps;
        }

        private static bool IsBodyCapsuleSegment(string pName, string bName)
        {
            if (string.IsNullOrEmpty(pName) || string.IsNullOrEmpty(bName)) return false;

            // Legs: Pelvis -> Thigh, Thigh -> Calf, Calf -> Foot, Foot -> Toe
            if (IsPelvisBone(pName) && IsThighBone(bName)) return true;
            if (IsThighBone(pName) && IsCalfBone(bName)) return true;
            if (IsCalfBone(pName) && IsFootBone(bName)) return true;
            if (IsFootBone(pName) && (IsFootBone(bName) || bName.ToLowerInvariant().Contains("toe"))) return true;

            // Torso & Spine (Spine chain protects abdomen, chest, ribs)
            if (IsPelvisBone(pName) && IsTorsoBone(bName)) return true;
            if (IsTorsoBone(pName) && (IsTorsoBone(bName) || IsNeckBone(bName) || IsShoulderBone(bName))) return true;
            if (IsNeckBone(pName) && (IsNeckBone(bName) || bName.ToLowerInvariant().Contains("head"))) return true;

            // Arms: Shoulder -> UpperArm, UpperArm -> Forearm, Forearm -> Hand, Hand -> Fingers
            if ((IsShoulderBone(pName) || IsTorsoBone(pName)) && IsUpperArmBone(bName)) return true;
            if (IsUpperArmBone(pName) && IsForearmBone(bName)) return true;
            if (IsForearmBone(pName) && IsHandBone(bName)) return true;
            if (IsHandBone(pName) && (IsHandBone(bName) || bName.ToLowerInvariant().Contains("finger") || bName.ToLowerInvariant().Contains("thumb"))) return true;

            return false;
        }

        // Harmonized anatomical radii for character collision capsules.
        private float CapsuleRadiusFor(string boneName, float clearance = 0f)
        {
            float totalClearance = clearance + _clothClearance;
            if (string.IsNullOrEmpty(boneName)) return 0.070f + totalClearance;

            // Knee / Calf / Shin / LowerLeg: 16 cm diameter
            if (IsCalfBone(boneName)) return 0.080f + totalClearance;

            // Thigh (hip / groin / femur / upperleg): 18.4 cm diameter
            if (IsThighBone(boneName)) return 0.092f + totalClearance;

            // Ankle (Foot) / Toe: 12 cm diameter
            if (IsFootBone(boneName)) return 0.060f + totalClearance;

            // Pelvis / Hips / Buttocks: covers pelvis core without protruding into thighs
            if (IsPelvisBone(boneName)) return 0.115f + totalClearance;

            // Spine / Chest / Ribs: covers torso & chest comfortably (user: "pour le torse et les bras ça semble bien")
            if (IsTorsoBone(boneName)) return 0.150f + totalClearance;

            // Neck & Head
            if (IsNeckBone(boneName))
            {
                // Head / cranium capsule: 15-16 cm diameter located in the skull
                if (boneName.ToLowerInvariant().Contains("head")) return 0.075f + totalClearance;
                // Slender neck: 9 cm diameter so the neck is distinct and does not swallow the throat/shoulders!
                return 0.045f + totalClearance;
            }

            // Arms & Shoulders (user: "pour le torse et les bras ça semble bien")
            if (IsShoulderBone(boneName)) return 0.080f + totalClearance;
            if (IsUpperArmBone(boneName)) return 0.065f + totalClearance;
            if (IsForearmBone(boneName)) return 0.055f + totalClearance;
            if (IsHandBone(boneName)) return 0.040f + totalClearance;

            return 0.070f + totalClearance;
        }

        // Analytical tapered capsule collision: projects particle onto cylinder axis AB,
        // and pushes radially outward if distance is less than interpolated radius r(t).
        // Uses pure idempotent position projection on ClothPos without modifying ClothPrev inside the iteration loop.
        private void PushOutOfCapsules(LoadedMesh lm, List<ClothCapsule> caps)
        {
            if (lm == null || lm.ClothPos == null || caps == null || caps.Count == 0) return;
            int count = lm.ClothPos.Length;

            // 1. Particle vs Capsule (Vertex collision: true radial outward projection)
            Parallel.For(0, count, v =>
            {
                float pinW = lm.ClothPinWeight != null && v < lm.ClothPinWeight.Length ? lm.ClothPinWeight[v] : (lm.ClothPin != null && lm.ClothPin[v] ? 1f : 0f);
                if (pinW >= 0.999f) return; // Only skip 100% rigid neck anchors
                Vector3 p = lm.ClothPos[v];
                if (float.IsNaN(p.X) || float.IsInfinity(p.X))
                {
                    p = (lm.ClothPos0 != null && v < lm.ClothPos0.Length) ? lm.ClothPos0[v] : Vector3.Zero;
                }

                for (int c = 0; c < caps.Count; c++)
                {
                    Vector3 ab = caps[c].B - caps[c].A;
                    float abLen2 = ab.LengthSquared;
                    if (abLen2 < 1e-8f) continue;
                    float t = Vector3.Dot(p - caps[c].A, ab) / abLen2;
                    if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
                    Vector3 closest = caps[c].A + ab * t;
                    Vector3 d = p - closest;
                    float dist = d.Length;
                    float r = caps[c].RA + (caps[c].RB - caps[c].RA) * t;

                    if (dist < r)
                    {
                        Vector3 pushDir;
                        if (dist > 1e-4f)
                        {
                            pushDir = d / dist;
                        }
                        else
                        {
                            Vector3 temp = Math.Abs(ab.Z) < 0.9f * (float)Math.Sqrt(abLen2) ? Vector3.UnitZ : Vector3.UnitY;
                            pushDir = Vector3.Normalize(Vector3.Cross(ab, temp));
                        }

                        p = closest + pushDir * r;
                    }
                }

                if (float.IsNaN(p.X) || float.IsInfinity(p.X))
                {
                    p = (lm.ClothPos0 != null && v < lm.ClothPos0.Length) ? lm.ClothPos0[v] : Vector3.Zero;
                }

                lm.ClothPos[v] = p;
            });

            // 2. Edge vs Capsule: Midpoint collision prevents knees/legs from passing between triangle vertices
            if (lm.ClothEdgeA != null && lm.ClothEdgeB != null)
            {
                int edgeCount = lm.ClothEdgeA.Length;
                for (int e = 0; e < edgeCount; e++)
                {
                    int ia = lm.ClothEdgeA[e], ib = lm.ClothEdgeB[e];
                    if (ia < 0 || ib < 0 || ia >= count || ib >= count) continue;
                    Vector3 pa = lm.ClothPos[ia], pb = lm.ClothPos[ib];
                    Vector3 mid = (pa + pb) * 0.5f;

                    for (int c = 0; c < caps.Count; c++)
                    {
                        Vector3 ab = caps[c].B - caps[c].A;
                        float abLen2 = ab.LengthSquared;
                        if (abLen2 < 1e-8f) continue;
                        float t = Vector3.Dot(mid - caps[c].A, ab) / abLen2;
                        if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
                        Vector3 closest = caps[c].A + ab * t;
                        Vector3 d = mid - closest;
                        float dist = d.Length;
                        float r = caps[c].RA + (caps[c].RB - caps[c].RA) * t;

                        if (dist < r)
                        {
                            Vector3 pushDir = dist > 1e-4f ? (d / dist) : Vector3.UnitY;
                            float pen = r - dist;
                            Vector3 corr = pushDir * (pen * 0.5f);
                            float wa = lm.ClothPinWeight != null && ia < lm.ClothPinWeight.Length ? lm.ClothPinWeight[ia] : 0f;
                            float wb = lm.ClothPinWeight != null && ib < lm.ClothPinWeight.Length ? lm.ClothPinWeight[ib] : 0f;
                            if (wa < 0.999f) lm.ClothPos[ia] += corr;
                            if (wb < 0.999f) lm.ClothPos[ib] += corr;
                        }
                    }
                }
            }
        }

        // Bake the cloth solver ONCE over the whole animation: pose + collision at
        // every frame, two full cycles (the driven system settles into a periodic
        // state), keep the second cycle. Playback then just interpolates the cache,
        // which is deterministic and loops seamlessly.
        private void BakeCloth()
        {
            if (_currentAnimation == null || _currentAnimation.Duration <= 0.001f)
            { Log("Tissu : chargez d'abord une animation avant de calculer."); return; }
            if (!_glReady || !glControl.IsHandleCreated) return;

            var cloth = new List<LoadedMesh>();
            foreach (var m in _loadedMeshes) if (m.IsCloth && m.ClothPos != null) cloth.Add(m);
            if (cloth.Count == 0) { Log("Tissu : aucun mesh tissu actif (cochez « Tissu »)."); return; }

            int fps = nudBakeFps != null ? (int)nudBakeFps.Value : 30;
            if (fps < 5) fps = 5;
            float dur = _currentAnimation.Duration;
            int N = Math.Max(2, (int)Math.Round(dur * fps));
            float dt = dur / N;

            glControl.MakeCurrent();
            Log($"Tissu : calcul sur {cloth.Count} mesh(es) — {N} frames ({fps}/s), 2 cycles, dt={dt:F4}s...");
            var sw = System.Diagnostics.Stopwatch.StartNew();

            ResetAllCloth();
            foreach (var lm in cloth)
            {
                lm.ClothBakeRaw = new Vector3[N][];
                lm.ClothBakeFrames = N;
                lm.ClothBakeDt = dt;
            }

            for (int cycle = 0; cycle < 2; cycle++)
            {
                for (int f = 0; f < N; f++)
                {
                    float t = f * dt;
                    PoseSkeletons(t);
                    RefreshVertexBuffers(false);
                    foreach (var lm in cloth) StepClothForMesh(lm, dt);
                    if (cycle == 1)
                        foreach (var lm in cloth) lm.ClothBakeRaw[f] = (Vector3[])lm.ClothPos.Clone();
                }
            }

            int blendPct = nudLoopBlend != null ? (int)nudLoopBlend.Value : 10;
            foreach (var lm in cloth)
            {
                ApplyLoopBlend(lm, blendPct);
                lm.ClothBakeAnim = _currentAnimation;
                lm.ClothBakeGravity = lm.ClothGravity;
            }

            sw.Stop();
            Log($"Tissu : calcul terminé en {sw.ElapsedMilliseconds} ms ({N} frames x2, fondu={blendPct}%).");
            ApplyAnimationPose(_currentTime);
            if (glControl.IsHandleCreated) glControl.Invalidate();
        }

        // Build the replayable loop from the raw bake, applying a loop "blend": over
        // the last W frames the cache is ramped from its natural end value toward the
        // first frame, so the wrap N-1 -> 0 has no sudden jump. pct = window size as a
        // percentage of the cycle. Cheap (no re-simulation), so the slider is live.
        private void ApplyLoopBlend(LoadedMesh lm, int pct)
        {
            var raw = lm.ClothBakeRaw;
            if (raw == null || raw.Length < 2) { lm.ClothBake = raw; return; }
            int N = raw.Length;
            var outp = new Vector3[N][];
            for (int f = 0; f < N; f++) outp[f] = (Vector3[])raw[f].Clone();

            int W = (int)Math.Round(N * (Math.Max(0, pct) / 100.0));
            if (W > N / 2) W = N / 2;
            if (W >= 2)
            {
                int pc = outp[0].Length;
                var delta = new Vector3[pc];
                for (int p = 0; p < pc; p++) delta[p] = outp[0][p] - outp[N - 1][p];
                for (int f = N - W; f < N; f++)
                {
                    float a = (f - (N - W) + 1) / (float)W;   // (0,1]
                    if (a > 1f) a = 1f;
                    float s = a * a * (3f - 2f * a);          // smoothstep
                    var arr = outp[f];
                    int np = Math.Min(arr.Length, delta.Length);
                    for (int p = 0; p < np; p++) arr[p] += delta[p] * s;
                }
            }
            lm.ClothBake = outp;
            lm.ClothBakeFrames = N;
        }

        // Re-apply the loop blend (slider) to every cloth mesh, without re-simulating.
        private void ReblendClothBakes()
        {
            int pct = nudLoopBlend != null ? (int)nudLoopBlend.Value : 10;
            bool any = false;
            foreach (var lm in _loadedMeshes)
                if (lm.ClothBakeRaw != null) { ApplyLoopBlend(lm, pct); any = true; }
            if (any && _currentAnimation != null)
            {
                ApplyAnimationPose(_currentTime);
                if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();
            }
        }

        // Invalidate a baked loop (called when the cloth geometry / params change).
        private void ClearClothBake(LoadedMesh lm)
        {
            if (lm == null) return;
            lm.ClothBake = null;
            lm.ClothBakeRaw = null;
            lm.ClothBakeAnim = null;
            lm.ClothBakeFrames = 0;
        }

        // Replay the baked loop at time t: interpolate between the two surrounding
        // frames (wrapping N-1 -> 0), then re-snap the pinned band to the skeleton.
        private void ApplyBakeCloth(LoadedMesh lm, float t)
        {
            int N = lm.ClothBakeFrames;
            if (lm.ClothBake == null || N < 2 || lm.ClothPos == null) return;
            float fsf = lm.ClothBakeDt > 1e-6f ? t / lm.ClothBakeDt : 0f;
            int i0 = (int)Math.Floor(fsf);
            float frac = fsf - i0;
            if (i0 < 0) { i0 = 0; frac = 0f; }
            if (i0 >= N) { i0 = N - 1; frac = 0f; }
            int i1 = (i0 + 1) % N;
            var fa = lm.ClothBake[i0];
            var fb = lm.ClothBake[i1];
            int pc = lm.ClothPos.Length;
            for (int p = 0; p < pc; p++)
                if (p < fa.Length && p < fb.Length)
                    lm.ClothPos[p] = fa[p] + (fb[p] - fa[p]) * frac;

            if (lm.ClothPin != null)
                for (int p = 0; p < pc && p < lm.ClothPin.Length; p++)
                    if (lm.ClothPin[p]) lm.ClothPos[p] = ClothPinTarget(lm, p);

            var caps = BuildBodyCapsules(lm);
            if (caps.Count > 0) PushOutOfCapsules(lm, caps);
        }

        // XPBD / Velvet style sub-stepped cloth simulation: 3 sub-steps per frame,
        // damped Verlet velocity integration, edge distance constraints, and analytical
        // capsule SDF collision.
        private void StepClothForMesh(LoadedMesh lm, float dt)
        {
            if (lm.ClothPos == null || lm.ClothEdgeA == null) return;
            if (dt <= 0f) return;
            if (dt > 0.05f) dt = 0.05f; // Clamp to 50ms (avoids huge time jump on lag / timer hitch)
            int n = lm.ClothPos.Length;

            for (int v = 0; v < n; v++)
            {
                if (lm.ClothPin != null && v < lm.ClothPin.Length && lm.ClothPin[v])
                {
                    Vector3 target = ClothPinTarget(lm, v);
                    lm.ClothPos[v] = target;
                    lm.ClothPrev[v] = target;
                }
            }

            var caps = BuildBodyCapsules(lm);

            // Sub-stepping: 3 sub-steps per frame offers rock-solid XPBD stability and smoothness
            const int subSteps = 3;
            float subDt = dt / subSteps;
            float subDt2 = subDt * subDt;
            float damp = 0.92f; // realistic fabric aerodynamic damping
            Vector3 acc = new Vector3(0f, 0f, -lm.ClothGravity);
            float maxStepDisp = 3.5f * subDt; // Velocity clamp: max 3.5 m/s

            int subIter = Math.Max(2, lm.ClothSimIterations / subSteps);

            for (int step = 0; step < subSteps; step++)
            {
                // 1. Verlet integrate with soft skin drive in parallel across all CPU cores
                Parallel.For(0, n, v =>
                {
                    float w = lm.ClothPinWeight != null && v < lm.ClothPinWeight.Length ? lm.ClothPinWeight[v] : (lm.ClothPin != null && lm.ClothPin[v] ? 1f : 0f);
                    Vector3 target = ClothPinTarget(lm, v);

                    if (w >= 0.999f)
                    {
                        // 100% rigid neck anchor
                        lm.ClothPos[v] = target;
                        lm.ClothPrev[v] = target;
                    }
                    else
                    {
                        Vector3 p = lm.ClothPos[v];
                        if (float.IsNaN(p.X) || float.IsInfinity(p.X))
                        {
                            p = (target != Vector3.Zero) ? target : ((lm.ClothPos0 != null && v < lm.ClothPos0.Length) ? lm.ClothPos0[v] : Vector3.Zero);
                        }
                        Vector3 pPrev = (lm.ClothPrev != null && v < lm.ClothPrev.Length) ? lm.ClothPrev[v] : p;
                        if (float.IsNaN(pPrev.X) || float.IsInfinity(pPrev.X)) pPrev = p;

                        Vector3 vel = (p - pPrev) * damp;

                        // Velocity clamp prevents explosive kicks
                        if (vel.LengthSquared > maxStepDisp * maxStepDisp)
                        {
                            vel = Vector3.Normalize(vel) * maxStepDisp;
                        }

                        lm.ClothPrev[v] = p;

                        // Gravity & inertia
                        Vector3 nextP = p + vel + acc * subDt2;

                        // Soft animation drive: keeps sleeves on arms, torso on chest, and skirt following stride
                        if (w > 0.01f && target != Vector3.Zero)
                        {
                            float drive = w * 0.45f;
                            nextP = Vector3.Lerp(nextP, target, drive);
                        }

                        if (float.IsNaN(nextP.X) || float.IsInfinity(nextP.X))
                        {
                            nextP = target != Vector3.Zero ? target : lm.ClothPos0[v];
                        }

                        lm.ClothPos[v] = nextP;
                    }
                });

                // 2. Distance constraints + coupled capsule collision
                for (int it = 0; it < subIter; it++)
                {
                    for (int e = 0; e < lm.ClothEdgeA.Length; e++)
                    {
                        int ia = lm.ClothEdgeA[e], ib = lm.ClothEdgeB[e];
                        float wa = lm.ClothPinWeight != null && ia < lm.ClothPinWeight.Length ? lm.ClothPinWeight[ia] : (lm.ClothPin != null && lm.ClothPin[ia] ? 1f : 0f);
                        float wb = lm.ClothPinWeight != null && ib < lm.ClothPinWeight.Length ? lm.ClothPinWeight[ib] : (lm.ClothPin != null && lm.ClothPin[ib] ? 1f : 0f);
                        if (wa >= 0.999f && wb >= 0.999f) continue; // both rigid neck anchors

                        Vector3 pa = lm.ClothPos[ia], pb = lm.ClothPos[ib];
                        if (float.IsNaN(pa.X) || float.IsNaN(pb.X)) continue;
                        Vector3 d = pb - pa;
                        float len = d.Length;
                        if (len < 1e-6f || float.IsNaN(len)) continue;
                        float rawDiff = (len - lm.ClothRest[e]) / len;
                        float diff = rawDiff > 0f ? (rawDiff * 0.50f) : (rawDiff * 0.18f);

                        // Pinned or anchor vertices have infinite mass (invM = 0)
                        float invMa = (wa >= 0.999f) ? 0f : (1.0f - wa * 0.5f);
                        float invMb = (wb >= 0.999f) ? 0f : (1.0f - wb * 0.5f);
                        float invSum = invMa + invMb;
                        if (invSum > 1e-6f)
                        {
                            Vector3 corr = d * (diff / invSum);
                            pa += corr * invMa;
                            pb -= corr * invMb; // Pull pb towards pa!
                            lm.ClothPos[ia] = pa;
                            lm.ClothPos[ib] = pb;
                        }
                    }

                    // Coupled collision enforcement: immediately push vertices out so edges cannot drag them inside bones!
                    if (caps.Count > 0) PushOutOfCapsules(lm, caps);
                }

                // Enforce rigid neck anchors
                for (int v = 0; v < n; v++)
                {
                    float w = lm.ClothPinWeight != null && v < lm.ClothPinWeight.Length ? lm.ClothPinWeight[v] : (lm.ClothPin != null && lm.ClothPin[v] ? 1f : 0f);
                    if (w >= 0.999f)
                    {
                        lm.ClothPos[v] = ClothPinTarget(lm, v);
                    }
                }
            }

            // Final safety pass at the end of the frame to guarantee 0 penetration
            if (caps.Count > 0) PushOutOfCapsules(lm, caps);

            // Contact velocity damping: vertices against a capsule have inward velocity zeroed and friction applied cleanly once per frame
            if (caps.Count > 0 && lm.ClothPrev != null)
            {
                for (int v = 0; v < n; v++)
                {
                    Vector3 p = lm.ClothPos[v];
                    Vector3 prev = lm.ClothPrev[v];
                    for (int c = 0; c < caps.Count; c++)
                    {
                        Vector3 ab = caps[c].B - caps[c].A;
                        float abLen2 = ab.LengthSquared;
                        if (abLen2 < 1e-8f) continue;
                        float t = Vector3.Dot(p - caps[c].A, ab) / abLen2;
                        if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
                        Vector3 closest = caps[c].A + ab * t;
                        Vector3 d = p - closest;
                        float dist = d.Length;
                        float r = caps[c].RA + (caps[c].RB - caps[c].RA) * t;
                        if (dist <= r + 0.005f && dist > 1e-4f)
                        {
                            Vector3 normal = d / dist;
                            Vector3 vel = p - prev;
                            float vNorm = Vector3.Dot(vel, normal);
                            if (vNorm < 0f)
                            {
                                vel -= normal * vNorm;
                                vel *= 0.7f;
                                lm.ClothPrev[v] = p - vel;
                            }
                            break;
                        }
                    }
                }
            }
        }


        private void RefreshVertexBuffers(bool includeNormals, Vector3 riderOffset = default)
        {
            foreach (var lm in _loadedMeshes)
            {
                if (lm.Interleaved == null || lm.Hidden) continue;

                int vcount = lm.BaseVertices.Length;
                if (lm.DeformedCache == null || lm.DeformedCache.Length != vcount)
                    lm.DeformedCache = new Vector3[vcount];

                bool hasWrap = lm.WrapSource != null && lm.WrapSkin != null && lm.WrapSource.SkinMatrices != null;
                bool skinned = (lm.Skin != null && lm.Skin.Length > 0 && lm.SkinMatrices != null) || hasWrap;
                bool rigid = lm.HasAttachMatrix;
                bool cloth = lm.IsCloth && lm.ClothPos != null && lm.Weld != null;
                if (lm.ForceBaseDisplay) { skinned = false; rigid = false; cloth = false; }

                // Effective per-axis scale = global * per-axis.
                Vector3 pscale = new Vector3(
                    lm.PartScale * lm.PartScaleVec.X,
                    lm.PartScale * lm.PartScaleVec.Y,
                    lm.PartScale * lm.PartScaleVec.Z);
                bool scaleTweaked = pscale.X != 1f || pscale.Y != 1f || pscale.Z != 1f;

                bool hasRot = lm.AttachRotation != Vector3.Zero;
                bool hasMirror = lm.MirrorX || lm.MirrorY || lm.MirrorZ;
                bool hasOffset = lm.AttachOffset != Vector3.Zero;
                bool hasTweak = scaleTweaked || hasRot || hasMirror || hasOffset || lm.MirrorArms;

                Matrix4 rotMat = hasRot
                    ? Matrix4.CreateFromQuaternion(Quaternion.FromEulerAngles(
                        MathHelper.DegreesToRadians(lm.AttachRotation.X),
                        MathHelper.DegreesToRadians(lm.AttachRotation.Y),
                        MathHelper.DegreesToRadians(lm.AttachRotation.Z)))
                    : Matrix4.Identity;

                bool isMultiPart = !string.IsNullOrEmpty(lm.SourcePath) &&
                    _loadedMeshes.Exists(m => !ReferenceEquals(m, lm) && string.Equals(m.SourcePath, lm.SourcePath, StringComparison.OrdinalIgnoreCase));
                bool isClothingOrBody = lm.CutoutRole != MeshCutoutRole.Accessory || lm.Skin != null || lm.WrapSource != null || IsClothingOrSkinned(lm);
                Vector3 scalePivot = (isMultiPart || !lm.HasBindCentroid) ? Vector3.Zero : (isClothingOrBody ? new Vector3(0, 0, lm.BindCentroid.Z) : lm.BindCentroid);
                Vector3 rotPivot = (isMultiPart || lm.CutoutRole != MeshCutoutRole.Accessory) ? Vector3.Zero : (isClothingOrBody ? new Vector3(0, 0, lm.BindCentroid.Z) : scalePivot);

                if (cloth)
                {
                    // Positions come from the cloth solver (per particle); every
                    // coincident vertex reads the same particle -> the piece can't tear.
                    for (int v = 0; v < vcount; v++)
                    {
                        int p = v < lm.Weld.Length ? lm.Weld[v] : 0;
                        if (p < 0 || p >= lm.ClothPos.Length) p = 0;
                        Vector3 cp = lm.ClothPos[p];
                        if (lm.CutoutRole != MeshCutoutRole.Mount && riderOffset != Vector3.Zero)
                            cp += riderOffset;
                        lm.DeformedCache[v] = cp;
                    }
                }
                else
                for (int v = 0; v < vcount; v++)
                {
                    Vector3 orig = lm.BaseVertices[v];
                    if (rigid)
                    {
                        lm.DeformedCache[v] = Vector3.TransformPosition(orig, lm.AttachMatrix);
                        continue;
                    }
                    if (!skinned)
                    {
                        Vector3 pos = hasTweak ? ApplyMeshTweak(lm, orig, false, default) : orig;
                        if (lm.CutoutRole != MeshCutoutRole.Mount && riderOffset != Vector3.Zero)
                            pos += riderOffset;
                        lm.DeformedCache[v] = pos;
                        continue;
                    }

                    // Wrap uses the body mesh's weights/matrices; otherwise the mesh's own.
                    var influences = hasWrap
                        ? (v < lm.WrapSkin.Length ? lm.WrapSkin[v] : null)
                        : (v < lm.Skin.Length ? lm.Skin[v] : null);
                    if (influences == null || influences.Length == 0)
                    {
                        Vector3 pos = hasTweak ? ApplyMeshTweak(lm, orig, false, default) : orig;
                        if (lm.CutoutRole != MeshCutoutRole.Mount && riderOffset != Vector3.Zero)
                            pos += riderOffset;
                        lm.DeformedCache[v] = pos;
                        continue;
                    }

                    Matrix4[] mats = hasWrap ? lm.WrapSource.SkinMatrices : lm.SkinMatrices;
                    int boneCount = hasWrap ? lm.WrapSource.Bones.Count : lm.Bones.Count;

                    bool thick = lm.Thickness != 0f && lm.BaseVertexNormals != null && v < lm.BaseVertexNormals.Length;
                    Vector3 baseN = thick ? lm.BaseVertexNormals[v] : Vector3.Zero;

                    Vector3 def = Vector3.Zero;
                    Vector3 dnorm = Vector3.Zero;
                    float totalW = 0f;
                    for (int k = 0; k < influences.Length; k++)
                    {
                        int bId = influences[k].BoneId;
                        if (bId >= 0 && bId < boneCount)
                        {
                            def += Vector3.TransformPosition(orig, mats[bId]) * influences[k].Weight;
                            if (thick) dnorm += Vector3.TransformNormal(baseN, mats[bId]) * influences[k].Weight;
                            totalW += influences[k].Weight;
                        }
                    }
                    Vector3 skinnedPos = totalW > 0.0001f ? (Math.Abs(totalW - 1f) > 0.001f ? def / totalW : def) : orig;

                    // Per-part tweak: apply full ApplyMeshTweak (bilateral sagittal mirror, scale, offset, rotation, thickness)
                    if (hasTweak)
                    {
                        skinnedPos = ApplyMeshTweak(lm, skinnedPos, thick, dnorm);
                    }
                    else if (thick && dnorm.LengthSquared > 1e-12f)
                    {
                        skinnedPos += Vector3.Normalize(dnorm) * lm.Thickness;
                    }
                    lm.DeformedCache[v] = skinnedPos;
                }

                int cornerCount = lm.Indices.Length;

                // Cloth smooth normals: accumulated across adjacent faces and welded particles
                // to completely eliminate black facets / black seams on hoods, folds, and fabric!
                if (cloth)
                {
                    int cn = lm.CornerVertex.Length;
                    int P = lm.ClothPos != null ? lm.ClothPos.Length : vcount;
                    var accumNorm = new Vector3[P];

                    // Pass 1: Accumulate area-weighted face normals onto coincident welded particles
                    for (int t = 0; t + 2 < cn; t += 3)
                    {
                        int i0 = lm.CornerVertex[t], i1 = lm.CornerVertex[t + 1], i2 = lm.CornerVertex[t + 2];
                        if (i0 >= 0 && i1 >= 0 && i2 >= 0 && i0 < vcount && i1 < vcount && i2 < vcount)
                        {
                            Vector3 va = lm.DeformedCache[i0], vb = lm.DeformedCache[i1], vc = lm.DeformedCache[i2];
                            Vector3 fn = Vector3.Cross(vb - va, vc - va);
                            if (fn.LengthSquared > 1e-12f)
                            {
                                int p0 = WeldOf(lm, i0), p1 = WeldOf(lm, i1), p2 = WeldOf(lm, i2);
                                if (p0 >= 0 && p0 < P) accumNorm[p0] += fn;
                                if (p1 >= 0 && p1 < P) accumNorm[p1] += fn;
                                if (p2 >= 0 && p2 < P) accumNorm[p2] += fn;
                            }
                        }
                    }

                    // Pass 2: Normalize particle normals
                    for (int p = 0; p < P; p++)
                    {
                        if (accumNorm[p].LengthSquared > 1e-12f)
                            accumNorm[p] = Vector3.Normalize(accumNorm[p]);
                        else
                            accumNorm[p] = Vector3.UnitZ;
                    }

                    // Pass 3: Write smooth continuous Gouraud/Phong normals to interleaved buffer
                    Parallel.For(0, cn, c =>
                    {
                        int vi = lm.CornerVertex[c];
                        int p = WeldOf(lm, vi);
                        Vector3 n = (p >= 0 && p < P) ? accumNorm[p] : Vector3.UnitZ;
                        lm.Interleaved[c * 8 + 3] = n.X;
                        lm.Interleaved[c * 8 + 4] = n.Y;
                        lm.Interleaved[c * 8 + 5] = n.Z;
                    });
                }

                for (int c = 0; c < cornerCount; c++)
                {
                    int vi = lm.CornerVertex[c];
                    Vector3 p = (vi >= 0 && vi < vcount) ? lm.DeformedCache[vi] : Vector3.Zero;
                    lm.Interleaved[c * 8 + 0] = (p.X - _refCenter.X) * _refScale;
                    lm.Interleaved[c * 8 + 1] = (p.Y - _refCenter.Y) * _refScale;
                    lm.Interleaved[c * 8 + 2] = (p.Z - _refCenter.Z) * _refScale;

                    if ((skinned || rigid) && !cloth && includeNormals)
                    {
                        Vector3 n = (lm.BaseCornerNormals != null && c < lm.BaseCornerNormals.Length)
                            ? lm.BaseCornerNormals[c]
                            : new Vector3(lm.Interleaved[c * 8 + 3], lm.Interleaved[c * 8 + 4], lm.Interleaved[c * 8 + 5]);
                        // Approximate normal transform using the skin matrix's rotation part
                        // (for a rigid attach, the host bone's matrix).
                        if (rigid)
                        {
                            Vector3 tn0 = Vector3.TransformNormal(n, lm.AttachMatrix);
                            if (tn0.LengthSquared > 1e-12f) tn0 = Vector3.Normalize(tn0);
                            lm.Interleaved[c * 8 + 3] = tn0.X;
                            lm.Interleaved[c * 8 + 4] = tn0.Y;
                            lm.Interleaved[c * 8 + 5] = tn0.Z;
                        }
                        else
                        {
                            var ninf = hasWrap
                                ? (vi < lm.WrapSkin.Length ? lm.WrapSkin[vi] : null)
                                : (lm.Skin != null && vi < lm.Skin.Length ? lm.Skin[vi] : null);
                            int bi = ninf != null && ninf.Length > 0 ? ninf[0].BoneId : -1;
                            Matrix4[] nmats = hasWrap ? lm.WrapSource.SkinMatrices : lm.SkinMatrices;
                            int nbones = hasWrap ? lm.WrapSource.Bones.Count : lm.Bones.Count;
                            if (bi >= 0 && bi < nbones)
                            {
                                Vector3 tn = Vector3.TransformNormal(n, nmats[bi]);
                                if (hasMirror)
                                    tn = new Vector3(tn.X * (lm.MirrorX ? -1f : 1f), tn.Y * (lm.MirrorY ? -1f : 1f), tn.Z * (lm.MirrorZ ? -1f : 1f));
                                if (hasRot)
                                    tn = Vector3.TransformNormal(tn, rotMat);
                                if (tn.LengthSquared > 1e-12f) tn = Vector3.Normalize(tn);
                                lm.Interleaved[c * 8 + 3] = tn.X;
                                lm.Interleaved[c * 8 + 4] = tn.Y;
                                lm.Interleaved[c * 8 + 5] = tn.Z;
                            }
                            else if (hasMirror || hasRot)
                            {
                                Vector3 tn = n;
                                if (hasMirror)
                                    tn = new Vector3(tn.X * (lm.MirrorX ? -1f : 1f), tn.Y * (lm.MirrorY ? -1f : 1f), tn.Z * (lm.MirrorZ ? -1f : 1f));
                                if (hasRot)
                                    tn = Vector3.TransformNormal(tn, rotMat);
                                if (tn.LengthSquared > 1e-12f) tn = Vector3.Normalize(tn);
                                lm.Interleaved[c * 8 + 3] = tn.X;
                                lm.Interleaved[c * 8 + 4] = tn.Y;
                                lm.Interleaved[c * 8 + 5] = tn.Z;
                            }
                        }
                    }
                    else if (!skinned && !rigid && !cloth && includeNormals && (hasMirror || hasRot))
                    {
                        Vector3 n = (lm.BaseCornerNormals != null && c < lm.BaseCornerNormals.Length)
                            ? lm.BaseCornerNormals[c]
                            : new Vector3(lm.Interleaved[c * 8 + 3], lm.Interleaved[c * 8 + 4], lm.Interleaved[c * 8 + 5]);
                        Vector3 tn = n;
                        if (hasMirror)
                            tn = new Vector3(tn.X * (lm.MirrorX ? -1f : 1f), tn.Y * (lm.MirrorY ? -1f : 1f), tn.Z * (lm.MirrorZ ? -1f : 1f));
                        if (hasRot)
                            tn = Vector3.TransformNormal(tn, rotMat);
                        if (tn.LengthSquared > 1e-12f) tn = Vector3.Normalize(tn);
                        lm.Interleaved[c * 8 + 3] = tn.X;
                        lm.Interleaved[c * 8 + 4] = tn.Y;
                        lm.Interleaved[c * 8 + 5] = tn.Z;
                    }
                }

                GL.BindBuffer(BufferTarget.ArrayBuffer, lm.Vbo);
                GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, lm.Interleaved.Length * sizeof(float), lm.Interleaved);
            }

            // Mesh-mesh clothing collision runs after every DeformedCache is built, so
            // it can test the garment against the CURRENT (animated) body surface.
            ApplyClothingBodyCollision(includeNormals);
        }

        // ==================== RENDERING ====================

        private void GlControl_Paint(object sender, PaintEventArgs e)
        {
            if (!glControl.IsHandleCreated) return;
            try
            {
                glControl.MakeCurrent();

                if (!_glReady)
                {
                    _glReady = true;
                    _uoRefKey = int.MinValue;   // (re)load reference textures for this GL context
                    CompileShaders();
                    _renderTimer.Start();
                    StartInitialLoad();
                }

                RenderScene();
            }
            catch (Exception ex)
            {
                Log($"Paint error: {ex.Message}\n{ex}");
            }
        }

        private void GlControl_Resize(object sender, EventArgs e)
        {
            if (glControl.IsHandleCreated && glControl.Width > 0 && glControl.Height > 0)
            {
                glControl.MakeCurrent();
                GL.Viewport(0, 0, glControl.Width, glControl.Height);
            }
        }

        private void RenderTimer_Tick(object sender, EventArgs e)
        {
            if (!_playing || _currentAnimation == null) return;
            if (!_glReady || !glControl.IsHandleCreated) return;

            glControl.MakeCurrent();
            float dt = (float)(DateTime.Now - _lastFrameTime).TotalSeconds * _playbackSpeed;
            _lastFrameTime = DateTime.Now;
            _clothDt = dt;

            _currentTime += dt;
            if (_currentTime >= _currentAnimation.Duration)
            {
                if (_looping)
                    _currentTime = 0f;
                else
                {
                    _currentTime = _currentAnimation.Duration;
                    _playing = false;
                    btnPlay.Enabled = true;
                    btnPause.Enabled = false;
                    btnStop.Enabled = false;
                }
            }

            trackTime.Value = Math.Min(trackTime.Maximum, (int)(_currentTime * 1000));
            lblTime.Text = $"Time: {_currentTime:F2}s";

            ApplyAnimationPose(_currentTime);
            if (glControl.IsHandleCreated) glControl.Invalidate();
        }

        private int UoSplitX()
        {
            int w = Math.Max(1, glControl.Width);
            if (!_uoView) return w;
            return (int)MathHelper.Clamp(w * _uoSplit, 40f, w - 40f);
        }

        private void RenderScene()
        {
            if (!glControl.IsHandleCreated) return;
            glControl.MakeCurrent();

            int W = Math.Max(1, glControl.Width);
            int H = Math.Max(1, glControl.Height);

            if (_dbgFrame < 2)
            {
                _dbgFrame++;
                Log($"RenderScene frame#{_dbgFrame} size={W}x{H} meshes={_loadedMeshes.Count} prog={_shaderProg}");
            }

            int splitX = UoSplitX();

            GL.Enable(EnableCap.ScissorTest);
            GL.Disable(EnableCap.CullFace);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Lequal);
            // Alpha: discard fully-transparent texels (cutout) and blend soft edges.
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            // ---- Editor half (left): perspective orbit ----
            GL.Viewport(0, 0, splitX, H);
            GL.Scissor(0, 0, splitX, H);
            GL.ClearColor(0.08f, 0.09f, 0.12f, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            if (_shaderProg != 0 && splitX > 1 && H > 1)
                DrawEditorView(splitX, H);

            // ---- UO half (right): orthographic 2:1 isometric, flat ----
            if (_uoView && splitX < W - 1)
            {
                int ux = splitX, uw = W - splitX;
                GL.Viewport(ux, 0, uw, H);
                GL.Scissor(ux, 0, uw, H);
                GL.ClearColor(_uoBgR, _uoBgG, _uoBgB, 1f);
                GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

                if (_uoDbg < 3)
                {
                    _uoDbg++;
                    Log($"UOdbg W={W} H={H} splitX={splitX} ctl={glControl.Width}x{glControl.Height} this={Width}x{Height} parent={glControl.Parent?.Width}x{glControl.Parent?.Height} refCenter={_refCenter} refScale={_refScale} maxDim={(2f / Math.Max(1e-6f, _refScale)):F3}");
                }

                if (_shaderProg != 0 && uw > 1 && H > 1)
                    DrawUoView(ux, uw, H);

                // Divider line between the two halves.
                GL.Scissor(splitX - 1, 0, 2, H);
                GL.ClearColor(0.55f, 0.55f, 0.58f, 1f);
                GL.Clear(ClearBufferMask.ColorBufferBit);
            }

            GL.Disable(EnableCap.ScissorTest);
            glControl.SwapBuffers();
        }

        private void DrawEditorView(int w, int h)
        {
            GL.Viewport(0, 0, w, h);
            GL.Scissor(0, 0, w, h);

            Matrix4 proj = Matrix4.CreatePerspectiveFieldOfView(
                MathHelper.DegreesToRadians(45f),
                (float)w / Math.Max(1, h),
                0.01f, 5000f);
            Matrix4 view = _camera.GetViewMatrix();
            Matrix4 mvp = view * proj;
            DrawMeshSet(mvp, unlit: false, alphaCut: 0.15f);

            // 3D Visualizers: Colliders, Skeleton, and Cloth Zones (Heatmap)
            if (_showColliders) DrawClothColliders(mvp);
            if (_showBones) DrawBones(mvp);
            if (_showClothHeatmap) DrawClothHeatmap(mvp);

            // 3ds Max-style view cube, top-left corner of the editor view.
            DrawViewCube();
        }

        // Editor view rectangle in GL viewport pixel space (bottom-left origin),
        // matching the glControl client area. Used by the view cube so it stays
        // anchored in the corner of the visible editor even when the GLControl is
        // positioned with an offset inside its parent panel.
        private void GetEditorViewportRect(out int x, out int y, out int w, out int h)
        {
            w = Math.Max(1, glControl.Width);
            h = Math.Max(1, glControl.Height);
            x = 0;
            y = 0;
            int splitX = UoSplitX();
            if (_uoView && splitX > 1 && splitX < w - 1) w = splitX;
        }

                private static void AddLineVertex(List<float> data, Vector3 pos)
        {
            data.Add(pos.X); data.Add(pos.Y); data.Add(pos.Z);
            data.Add(0f); data.Add(0f); data.Add(1f);
            data.Add(0f); data.Add(0f);
        }

        private static void AddLineSegment(List<float> data, Vector3 p1, Vector3 p2)
        {
            AddLineVertex(data, p1);
            AddLineVertex(data, p2);
        }

        private void DrawDebugLines(Matrix4 mvp, List<float> linesData, Vector4 color, float lineWidth = 2.0f)
        {
            if (linesData == null || linesData.Count == 0 || _shaderProg == 0) return;

            if (_debugLinesVao == 0)
            {
                _debugLinesVao = GL.GenVertexArray();
                _debugLinesVbo = GL.GenBuffer();
                GL.BindVertexArray(_debugLinesVao);
                GL.BindBuffer(BufferTarget.ArrayBuffer, _debugLinesVbo);
                int stride = 8 * sizeof(float);
                GL.EnableVertexAttribArray(0);
                GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
                GL.EnableVertexAttribArray(1);
                GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
                GL.EnableVertexAttribArray(2);
                GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
                GL.BindVertexArray(0);
            }

            GL.BindBuffer(BufferTarget.ArrayBuffer, _debugLinesVbo);
            GL.BufferData(BufferTarget.ArrayBuffer, linesData.Count * sizeof(float), linesData.ToArray(), BufferUsageHint.DynamicDraw);

            GL.UseProgram(_shaderProg);
            GL.UniformMatrix4(_uMvp, false, ref mvp);
            GL.Uniform1(_uHasTex, 0);
            GL.Uniform1(_uUnlit, 1);
            GL.Uniform1(_uAlphaCut, 0f);
            GL.Uniform1(_uOpacity, color.W);
            GL.Uniform1(_uBrightness, 1.2f);
            GL.Uniform3(_uColor, color.X, color.Y, color.Z);

            GL.LineWidth(lineWidth);
            GL.BindVertexArray(_debugLinesVao);
            GL.DrawArrays(PrimitiveType.Lines, 0, linesData.Count / 8);
            GL.BindVertexArray(0);
            GL.LineWidth(1.0f);
            GL.UseProgram(0);
        }

        private void DrawClothColliders(Matrix4 mvp)
        {
            LoadedMesh clothMesh = _loadedMeshes.Find(m => m.IsCloth && m.ClothPos != null);
            var caps = BuildBodyCapsules(clothMesh);
            if (caps == null || caps.Count == 0) return;

            var lines = new List<float>();
            const int segs = 12;

            foreach (var cap in caps)
            {
                Vector3 aGl = (cap.A - _refCenter) * _refScale;
                Vector3 bGl = (cap.B - _refCenter) * _refScale;
                float raGl = cap.RA * _refScale;
                float rbGl = cap.RB * _refScale;

                Vector3 ab = bGl - aGl;
                float len = ab.Length;
                if (len < 1e-4f) continue;
                Vector3 axis = ab / len;

                Vector3 temp = Math.Abs(axis.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitY;
                Vector3 u = Vector3.Normalize(Vector3.Cross(axis, temp));
                Vector3 w = Vector3.Normalize(Vector3.Cross(axis, u));

                // Rings at A and B
                Vector3 prevA = aGl + u * raGl;
                Vector3 prevB = bGl + u * rbGl;

                for (int k = 1; k <= segs; k++)
                {
                    float angle = (float)(k * 2 * Math.PI / segs);
                    Vector3 dir = u * (float)Math.Cos(angle) + w * (float)Math.Sin(angle);
                    Vector3 curA = aGl + dir * raGl;
                    Vector3 curB = bGl + dir * rbGl;

                    AddLineSegment(lines, prevA, curA);
                    AddLineSegment(lines, prevB, curB);

                    prevA = curA;
                    prevB = curB;
                }

                // 4 longitudinal struts
                AddLineSegment(lines, aGl + u * raGl, bGl + u * rbGl);
                AddLineSegment(lines, aGl - u * raGl, bGl - u * rbGl);
                AddLineSegment(lines, aGl + w * raGl, bGl + w * rbGl);
                AddLineSegment(lines, aGl - w * raGl, bGl - w * rbGl);

                // End caps (hemispherical arcs)
                for (int k = 0; k < segs / 2; k++)
                {
                    float a0 = (float)(k * Math.PI / (segs / 2));
                    float a1 = (float)((k + 1) * Math.PI / (segs / 2));
                    Vector3 arc0A = aGl - axis * (float)Math.Sin(a0) * raGl + u * (float)Math.Cos(a0) * raGl;
                    Vector3 arc1A = aGl - axis * (float)Math.Sin(a1) * raGl + u * (float)Math.Cos(a1) * raGl;
                    AddLineSegment(lines, arc0A, arc1A);

                    Vector3 arc0B = bGl + axis * (float)Math.Sin(a0) * rbGl + u * (float)Math.Cos(a0) * rbGl;
                    Vector3 arc1B = bGl + axis * (float)Math.Sin(a1) * rbGl + u * (float)Math.Cos(a1) * rbGl;
                    AddLineSegment(lines, arc0B, arc1B);
                }
            }

            DrawDebugLines(mvp, lines, new Vector4(0.2f, 1.0f, 0.4f, 0.95f), 2.0f);
        }

        private void DrawBones(Matrix4 mvp)
        {
            var lines = new List<float>();

            foreach (var m in _loadedMeshes)
            {
                if (m.WorldMatrices == null || m.Bones == null || m.Bones.Count < 2 || m.Hidden) continue;
                int bn = m.Bones.Count;
                for (int i = 0; i < bn; i++)
                {
                    int p = m.Bones[i].ParentId;
                    if (p < 0 || p >= bn || p == i) continue;
                    if (i >= m.WorldMatrices.Length || p >= m.WorldMatrices.Length) continue;

                    var wa = m.WorldMatrices[p];
                    var wb = m.WorldMatrices[i];
                    Vector3 A = new Vector3(wa.M41, wa.M42, wa.M43);
                    Vector3 B = new Vector3(wb.M41, wb.M42, wb.M43);

                    Vector3 aGl = (A - _refCenter) * _refScale;
                    Vector3 bGl = (B - _refCenter) * _refScale;

                    AddLineSegment(lines, aGl, bGl);

                    // Small joint cross
                    float s = 0.015f;
                    AddLineSegment(lines, bGl - Vector3.UnitX * s, bGl + Vector3.UnitX * s);
                    AddLineSegment(lines, bGl - Vector3.UnitY * s, bGl + Vector3.UnitY * s);
                    AddLineSegment(lines, bGl - Vector3.UnitZ * s, bGl + Vector3.UnitZ * s);
                }
            }

            DrawDebugLines(mvp, lines, new Vector4(1.0f, 0.85f, 0.2f, 1.0f), 2.5f);
        }

        private void DrawClothHeatmap(Matrix4 mvp)
        {
            var clothMesh = _loadedMeshes.Find(m => m.IsCloth && m.ClothPos != null && m.ClothEdgeA != null);
            if (clothMesh == null) return;

            var redLines = new List<float>();
            var yellowLines = new List<float>();
            var greenLines = new List<float>();
            var blueLines = new List<float>();

            int ec = clothMesh.ClothEdgeA.Length;
            for (int e = 0; e < ec; e++)
            {
                int ia = clothMesh.ClothEdgeA[e], ib = clothMesh.ClothEdgeB[e];
                if (ia < 0 || ib < 0 || ia >= clothMesh.ClothPos.Length || ib >= clothMesh.ClothPos.Length) continue;

                Vector3 paGl = (clothMesh.ClothPos[ia] - _refCenter) * _refScale;
                Vector3 pbGl = (clothMesh.ClothPos[ib] - _refCenter) * _refScale;

                float wa = clothMesh.ClothPinWeight != null && ia < clothMesh.ClothPinWeight.Length ? clothMesh.ClothPinWeight[ia] : 0f;
                float wb = clothMesh.ClothPinWeight != null && ib < clothMesh.ClothPinWeight.Length ? clothMesh.ClothPinWeight[ib] : 0f;
                float w = (wa + wb) * 0.5f;

                if (w >= 0.90f)
                    AddLineSegment(redLines, paGl, pbGl);
                else if (w >= 0.65f)
                    AddLineSegment(yellowLines, paGl, pbGl);
                else if (w >= 0.48f)
                    AddLineSegment(greenLines, paGl, pbGl);
                else
                    AddLineSegment(blueLines, paGl, pbGl);
            }

            if (redLines.Count > 0) DrawDebugLines(mvp, redLines, new Vector4(1.0f, 0.15f, 0.15f, 1.0f), 2.0f);
            if (yellowLines.Count > 0) DrawDebugLines(mvp, yellowLines, new Vector4(1.0f, 0.85f, 0.1f, 1.0f), 2.0f);
            if (greenLines.Count > 0) DrawDebugLines(mvp, greenLines, new Vector4(0.2f, 0.95f, 0.3f, 1.0f), 2.0f);
            if (blueLines.Count > 0) DrawDebugLines(mvp, blueLines, new Vector4(0.1f, 0.7f, 1.0f, 1.0f), 2.0f);
        }

// ==================== VIEW CUBE (standard camera presets) ====================
        // A small clickable cube fixed in the corner of the editor view that rotates
        // with the camera. Clicking a face snaps the camera to that standard view:
        // the character faces +Y, so +Y = DEVANT, -X = GAUCHE, +Z = HAUT, etc.

        private static readonly Vector3[] ViewCubeFaceNormals =
        {
            new Vector3(0, 1, 0),    // 0 DEVANT
            new Vector3(0, -1, 0),   // 1 DERR
            new Vector3(-1, 0, 0),   // 2 GAUCHE
            new Vector3(1, 0, 0),    // 3 DROIT
            new Vector3(0, 0, 1),    // 4 HAUT
            new Vector3(0, 0, -1)    // 5 BAS
        };
        private static readonly string[] ViewCubeFaceLabels = { "DEVANT", "DERR", "GAUCHE", "DROIT", "HAUT", "BAS" };

        // Screen-space (u = right, v = up) basis for each cube face, chosen so that
        // looking at the face from OUTSIDE reads the label upright, and u x v == n
        // (right-handed, viewed from outside). The vertical faces keep +Z as "up".
        private static void ViewCubeFaceTangents(int f, out Vector3 u, out Vector3 v)
        {
            switch (f)
            {
                case 0: u = new Vector3(-1, 0, 0); v = new Vector3(0, 0, 1); break;  // +Y DEVANT
                case 1: u = new Vector3(1, 0, 0); v = new Vector3(0, 0, 1); break;   // -Y DERR
                case 2: u = new Vector3(0, -1, 0); v = new Vector3(0, 0, 1); break;  // -X GAUCHE
                case 3: u = new Vector3(0, 1, 0); v = new Vector3(0, 0, 1); break;   // +X DROIT
                case 4: u = new Vector3(1, 0, 0); v = new Vector3(0, 1, 0); break;   // +Z HAUT
                default: u = new Vector3(1, 0, 0); v = new Vector3(0, -1, 0); break; // -Z BAS
            }
        }

        private void EnsureViewCube()
        {
            if (_viewCubeVao != 0 && _viewCubeTex != 0) return;

            var data = new List<float>(6 * 6 * 8);
            for (int f = 0; f < 6; f++)
            {
                var n = ViewCubeFaceNormals[f];
                ViewCubeFaceTangents(f, out var u, out var v);
                float col = f % 3, row = f / 3;
                float uL = col / 3f, uR = (col + 1f) / 3f;
                // No bitmap flip on upload -> bitmap row 0 sits at v=0. Screen-TOP face
                // vertices (n + u + v, since v is the "up" tangent) sample the SMALL v so
                // labels read upright.
                float vTop = row * 0.5f, vBot = (row + 1) * 0.5f;
                var ps = new[] { n - u - v, n + u - v, n + u + v, n - u + v };
                // Labels were mirroring left/right, so map the small-u vertex to the large U.
                var uu = new[] { uR, uL, uL, uR };
                var vv = new[] { vBot, vBot, vTop, vTop };
                int[] tri = { 0, 1, 2, 0, 2, 3 };
                foreach (int i in tri)
                {
                    data.Add(ps[i].X); data.Add(ps[i].Y); data.Add(ps[i].Z);
                    data.Add(n.X); data.Add(n.Y); data.Add(n.Z);
                    data.Add(uu[i]); data.Add(vv[i]);
                }
            }

            _viewCubeVao = GL.GenVertexArray();
            GL.BindVertexArray(_viewCubeVao);
            _viewCubeVbo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ArrayBuffer, _viewCubeVbo);
            GL.BufferData(BufferTarget.ArrayBuffer, data.Count * sizeof(float), data.ToArray(), BufferUsageHint.StaticDraw);
            int stride = 8 * sizeof(float);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
            GL.EnableVertexAttribArray(2);
            GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
            GL.BindVertexArray(0);

            _viewCubeTex = BuildViewCubeTexture();
        }

        private int BuildViewCubeTexture()
        {
            const int tile = 128;
            using var bmp = new Bitmap(tile * 3, tile * 2);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(40, 44, 52));
                var bgs = new[]
                {
                    Color.FromArgb(70, 110, 160),   // DEVANT
                    Color.FromArgb(50, 70, 110),    // DERR
                    Color.FromArgb(70, 130, 80),    // GAUCHE
                    Color.FromArgb(160, 90, 60),    // DROIT
                    Color.FromArgb(180, 160, 60),   // HAUT
                    Color.FromArgb(120, 80, 150)    // BAS
                };
                using var font = new Font("Segoe UI", 15f, FontStyle.Bold);
                using var border = new Pen(Color.FromArgb(20, 22, 28), 3f);
                for (int i = 0; i < 6; i++)
                {
                    int x = (i % 3) * tile, y = (i / 3) * tile;
                    using var br = new SolidBrush(bgs[i]);
                    g.FillRectangle(br, x, y, tile, tile);
                    g.DrawRectangle(border, x + 1.5f, y + 1.5f, tile - 3, tile - 3);
                    var sz = g.MeasureString(ViewCubeFaceLabels[i], font);
                    g.DrawString(ViewCubeFaceLabels[i], font, Brushes.White,
                        x + (tile - sz.Width) / 2f, y + (tile - sz.Height) / 2f);
                }
            }
            // NOTE: no RotateFlip here. UploadBitmap forwards bitmap row 0 to GL texture
            // row 0, and v=0 samples that row, so bitmap-top maps to small v. The cube UVs
            // (EnsureViewCube) therefore put the screen-TOP face vertices at the SMALL v.
            return UploadBitmap(bmp);
        }

        private void DrawViewCube()
        {
            if (_shaderProg == 0) return;
            GetEditorViewportRect(out int vx, out int vy, out int vw, out int vh);
            if (vw < ViewCubePx + 24 || vh < ViewCubePx + 24) return;
            EnsureViewCube();
            if (_viewCubeVao == 0 || _viewCubeTex == 0) return;

            Matrix4 mvp = ViewCubeMvp(vx, vy, vw, vh);

            // Draw the cube in its own little window: clear depth there (scissored, so
            // the scene is untouched) then enable depth test so only the front faces
            // show, giving a solid 3D cube instead of superimposed faces.
            int cx = vx + ViewCubeMargin;
            int cy = vy + vh - ViewCubePx - ViewCubeMargin;
            GL.Enable(EnableCap.ScissorTest);
            GL.Scissor(cx, cy, ViewCubePx, ViewCubePx);
            GL.Clear(ClearBufferMask.DepthBufferBit);
            // Restore the editor scissor so nothing else leaks out of the editor half.
            GL.Scissor(vx, vy, vw, vh);
            GL.Enable(EnableCap.DepthTest);
            GL.UseProgram(_shaderProg);

            // The cube itself: per-face shading (uUnlit=0 + a light dir) so the three
            // visible sides have distinct brightness and read as a real 3D cube. The
            // per-face texture already paints a dark border + label.
            GL.UniformMatrix4(_uMvp, false, ref mvp);
            GL.Uniform1(_uTex, 0);
            GL.Uniform1(_uHasTex, 1);
            GL.Uniform1(_uUnlit, 0);
            GL.Uniform3(_uLightDir, 0.35f, 0.35f, 0.9f);
            GL.Uniform1(_uAlphaCut, 0.2f);
            GL.Uniform1(_uOpacity, 1f);
            GL.Uniform1(_uBrightness, 1.15f);
            GL.Uniform3(_uColor, 1f, 1f, 1f);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, _viewCubeTex);
            GL.BindVertexArray(_viewCubeVao);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 36);
            GL.BindVertexArray(0);
            GL.UseProgram(0);
        }

        // Cube MVP: world-axis-aligned unit cube, rotated by the camera (rotation
        // only, so it stays fixed in the corner while orbiting), scaled to ViewCubePx
        // and placed at the TOP-LEFT corner of the editor viewport. Pixel coordinates
        // are mapped to NDC over the whole viewport so the cube keeps its real size.
        private Matrix4 ViewCubeMvp(int vx, int vy, int vw, int vh)
        {
            int cornerX = vx + ViewCubeMargin;
            int cornerY = vy + vh - ViewCubePx - ViewCubeMargin;   // top edge of bottom-left box
            float half = ViewCubePx / 2f;
            // Shrink so the cube floats inside its box (its rotated silhouette is wider
            // than its side: a unit cube's diagonal is ~1.73x a side).
            float cubeHalf = half * 0.62f;

            Matrix4 rot = _camera.GetViewMatrix();
            rot.Row3 = new Vector4(0, 0, 0, 1);   // keep rotation only
            Matrix4 s = Matrix4.CreateScale(cubeHalf, cubeHalf, cubeHalf);
            // Place the cube center in PIXEL coordinates, then map pixels -> NDC over
            // the whole viewport (px 0 -> -1, px w -> +1). XY keep the real pixel size.
            // Z is compressed separately: the scale above gives z in pixels (±cubeHalf);
            // the 1/(2*cubeHalf) factor maps it to ±0.5 in NDC so all faces stay inside
            // the [-1,1] depth range while keeping distinct depths for the depth test.
            Matrix4 t = Matrix4.CreateTranslation(cornerX + half, cornerY + half, 0f);
            Matrix4 pixelToNdc = Matrix4.CreateScale(2f / vw, 2f / vh, 1f / (2f * cubeHalf))
                               * Matrix4.CreateTranslation(-1f, -1f, 0f);
            return rot * s * t * pixelToNdc;
        }

        private bool TryViewCubeClick(MouseEventArgs e)
        {
            GetEditorViewportRect(out int vx, out int vy, out int vw, out int vh);
            if (vw < ViewCubePx + 24 || vh < ViewCubePx + 24) return false;

            float mx = e.X;
            float my = glControl.Height - e.Y;   // GL space (origin bottom-left)
            int cornerX = vx + ViewCubeMargin;
            int cornerY = vy + vh - ViewCubePx - ViewCubeMargin;
            if (mx < cornerX || mx > cornerX + ViewCubePx || my < cornerY || my > cornerY + ViewCubePx)
                return false;

            Matrix4 rot = _camera.GetViewMatrix();
            rot.Row3 = new Vector4(0, 0, 0, 1);
            float half = ViewCubePx / 2f;
            float cubeHalf = half * 0.62f;   // must match ViewCubeMvp
            Vector3 eye = _camera.EyePosition();

            // Front-facing face under the cursor wins.
            for (int f = 0; f < 6; f++)
            {
                var n = ViewCubeFaceNormals[f];
                if (Vector3.Dot(n, eye - n) <= 0f) continue;   // face center = n (half-size 1)
                ViewCubeFaceTangents(f, out var u, out var v);
                var pts = new Vector3[4];
                var signs = new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) };
                for (int k = 0; k < 4; k++)
                {
                    Vector3 p = Vector3.TransformPosition(n + signs[k].Item1 * u + signs[k].Item2 * v, rot);
                    pts[k] = new Vector3(
                        cornerX + half + p.X * cubeHalf,
                        cornerY + half + p.Y * cubeHalf,
                        0f);
                }
                if (PointInConvexQuad(mx, my, pts))
                {
                    ApplyViewCubeFace(f);
                    return true;
                }
            }
            return false;
        }

        private static bool PointInConvexQuad(float x, float y, Vector3[] q)
        {
            bool? sign = null;
            for (int i = 0; i < 4; i++)
            {
                var a = q[i];
                var b = q[(i + 1) % 4];
                float cross = (b.X - a.X) * (y - a.Y) - (b.Y - a.Y) * (x - a.X);
                if (Math.Abs(cross) < 1e-6f) continue;
                bool s = cross > 0f;
                if (sign == null) sign = s;
                else if (sign.Value != s) return false;
            }
            return sign != null;
        }

        private void ApplyViewCubeFace(int f)
        {
            switch (f)
            {
                case 0: _camera.ViewFront(); break;
                case 1: _camera.ViewBack(); break;
                case 2: _camera.ViewLeft(); break;
                case 3: _camera.ViewRight(); break;
                case 4: _camera.ViewTop(); break;
                case 5: _camera.ViewBottom(); break;
            }
            Log($"ViewCube : vue standard n°{f} ({(f < ViewCubeFaceLabels.Length ? ViewCubeFaceLabels[f] : "?")})");
            if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();
        }

        private void DrawUoView(int x, int w, int h)
        {
            // One shared frame for every direction so the model keeps a constant size
            // when rotating (per-direction framing made N/S look bigger than E/O).
            float frameSpan = ComputeFrameSpan();
            if (_uoSheet)
            {
                const int n = 5;
                int cw = Math.Max(1, w / n);
                for (int k = 0; k < n; k++)
                {
                    int cxk = x + k * cw;
                    int cwk = (k == n - 1) ? (x + w - cxk) : cw;
                    DrawUoDirection(cxk, cwk, h, k, frameSpan);
                }
            }
            else
            {
                DrawUoDirection(x, w, h, _uoDirection, frameSpan);
            }
        }

        // Max projected vertical span across all 5 directions (direction-independent frame).
        // Deliberately computed WITHOUT the vertical-scale deform: the frame (zoom) must
        // stay put while the user squashes the mesh. If the frame followed the deform it
        // re-zoomed to refit, which magnified the silhouette sideways and made the profile
        // views look like they were filmed from above instead of simply squashed.
        private float ComputeFrameSpan()
        {
            float best = 0.1f;
            for (int d = 0; d < 5; d++)
            {
                float vmin, vmax;
                ViewVerticalRange(UoViewMatrix(d), out vmin, out vmax);
                best = Math.Max(best, vmax - vmin);
            }
            return best;
        }

        private void DrawUoDirection(int x, int w, int h, int dir, float frameSpan)
        {
            GL.Viewport(x, 0, w, h);
            GL.Scissor(x, 0, w, h);

            Matrix4 view = UoViewMatrix(dir);
            Matrix4 vscale = Matrix4.CreateScale(1f, 1f, _uoVertScale);
            Matrix4 modelToView = vscale * view; // row-vector: apply vscale then view

            // Anchor a stable "ground" point: the center of the bottom plane
            // (0, 0, zMin). Its projection does not depend on the view direction,
            // so the model no longer bobs up/down when rotating.
            float anchorZ = _uoFeetOnLine ? _normMin.Z : (_normMin.Z + _normMax.Z) * 0.5f;
            // Anchor at the model's true footprint origin (raw world 0,0,0), not the
            // geometric center: for quadrupeds (horse) _refCenter.Y != 0, so (0,0)
            // would place the sprite a few px too low. Normalized origin = -refCenter*scale.
            Vector3 anchor = new Vector3(-_refCenter.X * _refScale, -_refCenter.Y * _refScale, anchorZ);
            Vector3 va = Vector3.TransformPosition(anchor, modelToView);

            float shiftX = _uoOffX;
            float shiftY = _uoOffY - va.Y;
            float totalZoom = Math.Max(0.01f, _uoZoom * _uoGlobalZoom);
            float viewH = _uoFeetOnLine
                ? frameSpan * 2f * 1.08f / totalZoom
                : frameSpan * 1.15f / totalZoom;

            float aspect = (float)w / Math.Max(1, h);
            float viewW = viewH * aspect;
            Matrix4 shift = Matrix4.CreateTranslation(shiftX, shiftY, 0f);
            Matrix4 proj = Matrix4.CreateOrthographic(viewW, viewH, -5000f, 5000f);
            Matrix4 mvp = modelToView * shift * proj;
            // UO sprites use hard (binary) transparency -> high cutout.
            DrawMeshSet(mvp, unlit: true, alphaCut: 0.5f, opacity: 1f, brightness: _uoBrightness);

            if (_uoRefShow)
                DrawUoReference(x, w, h, dir);

            // Center cross to check the framing (like UOFiddler's preview):
            // X axis = red horizontal line, Y axis = green vertical line.
            int cx = x + w / 2;
            int cy = h / 2;
            GL.Scissor(x, cy, w, 1);
            GL.ClearColor(1f, 0.25f, 0.25f, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit);
            GL.Scissor(cx, 0, 1, h);
            GL.ClearColor(0.2f, 1f, 0.2f, 1f);
            GL.Clear(ClearBufferMask.ColorBufferBit);
        }

        // Min/max view-space Y of the model's (normalized) bounding box corners.
        private void ViewVerticalRange(Matrix4 modelToView, out float vmin, out float vmax)
        {
            Vector3 a = _normMin, b = _normMax;
            vmin = float.MaxValue;
            vmax = float.MinValue;
            for (int i = 0; i < 8; i++)
            {
                Vector3 c = new Vector3(
                    (i & 1) == 0 ? a.X : b.X,
                    (i & 2) == 0 ? a.Y : b.Y,
                    (i & 4) == 0 ? a.Z : b.Z);
                Vector3 v = Vector3.TransformPosition(c, modelToView);
                if (v.Y < vmin) vmin = v.Y;
                if (v.Y > vmax) vmax = v.Y;
            }
            if (vmin > vmax) { vmin = -1f; vmax = 1f; }
        }

        // ---- Reference overlay: UOFiddler animation frame (anim.mul body 400 = c_man) ----

        private void EnsureUoReference()
        {
            int key = _uoRefBody * 1000000 + _uoRefAction * 1000 + _uoRefFrame;
            if (key == _uoRefKey) return;

            for (int i = 0; i < 5; i++)
            {
                if (_uoRefTex[i] > 0) { GL.DeleteTexture(_uoRefTex[i]); _uoRefTex[i] = 0; }
            }
            _uoRefKey = key;

            try
            {
                for (int d = 0; d < 5; d++)
                {
                    int hue = 0;
                    Ultima.AnimationFrame[] frames =
                        Animations.GetAnimation(_uoRefBody, _uoRefAction, d, ref hue, false, false);
                    if (frames == null || frames.Length == 0) continue;

                    int fi = Math.Min(_uoRefFrame, frames.Length - 1);
                    if (fi < 0) fi = 0;
                    Ultima.AnimationFrame fr = frames[fi];
                    if (fr == null || fr.Bitmap == null) continue;

                    Bitmap src = fr.Bitmap;
                    Bitmap argb = src.PixelFormat == System.Drawing.Imaging.PixelFormat.Format32bppArgb
                        ? new Bitmap(src)
                        : src.Clone(new Rectangle(0, 0, src.Width, src.Height),
                                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                    _uoRefTex[d] = UploadBitmap(argb);
                    _uoRefW[d] = argb.Width;
                    _uoRefH[d] = argb.Height;
                    _uoRefPivot[d] = fr.Center;
                    argb.Dispose();
                }
                Log($"UoRef loaded body={_uoRefBody} action={_uoRefAction} frame={_uoRefFrame} " +
                    $"tex0={_uoRefTex[0]} size0={_uoRefW[0]}x{_uoRefH[0]} pivot0={_uoRefPivot[0]}");
            }
            catch (Exception ex)
            {
                Log("UoRef load error: " + ex.Message);
            }
        }

        private int UploadBitmap(Bitmap bmp)
        {
            BitmapData bd = bmp.LockBits(
                new Rectangle(0, 0, bmp.Width, bmp.Height),
                ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            int tex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, tex);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
                bmp.Width, bmp.Height, 0, OpenTK.Graphics.OpenGL.PixelFormat.Bgra,
                PixelType.UnsignedByte, bd.Scan0);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            bmp.UnlockBits(bd);
            return tex;
        }

        private void EnsureQuad()
        {
            if (_quadVao != 0) return;
            // Unit quad (0..1) with position / normal / uv, y-down friendly.
            float[] v =
            {
                0f,0f,0f, 0f,0f,1f, 0f,0f,
                1f,0f,0f, 0f,0f,1f, 1f,0f,
                1f,1f,0f, 0f,0f,1f, 1f,1f,
                0f,0f,0f, 0f,0f,1f, 0f,0f,
                1f,1f,0f, 0f,0f,1f, 1f,1f,
                0f,1f,0f, 0f,0f,1f, 0f,1f
            };
            _quadVao = GL.GenVertexArray();
            GL.BindVertexArray(_quadVao);
            _quadVbo = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ArrayBuffer, _quadVbo);
            GL.BufferData(BufferTarget.ArrayBuffer, v.Length * sizeof(float), v, BufferUsageHint.StaticDraw);
            int stride = 8 * sizeof(float);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
            GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
            GL.EnableVertexAttribArray(2);
            GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
            GL.BindVertexArray(0);
        }

        private void DrawUoReference(int x, int w, int h, int dir)
        {
            EnsureUoReference();
            int tex = (dir >= 0 && dir < 5) ? _uoRefTex[dir] : 0;
            if (tex <= 0) return;

            EnsureQuad();

            float s = _uoRefScale * _uoGlobalZoom;
            int fw = _uoRefW[dir], fh = _uoRefH[dir];
            float bw = fw * s, bh = fh * s;
            Point piv = _uoRefPivot[dir];

            // Pivot (feet anchor) at the viewport center, exactly like UOFiddler's
            // drawX = pivotX - Center.X ; drawY = pivotY - Center.Y - Height.
            float cx = w * 0.5f;
            float cy = h * 0.5f;
            float drawX = cx - piv.X * s;
            float drawY = cy - (piv.Y + fh) * s;

            Matrix4 orthoPix = Matrix4.CreateOrthographicOffCenter(0f, w, h, 0f, -1f, 1f);
            Matrix4 modelM = Matrix4.CreateScale(bw, bh, 1f) * Matrix4.CreateTranslation(drawX, drawY, 0f);
            Matrix4 mvp = modelM * orthoPix;

            GL.Disable(EnableCap.DepthTest);
            GL.UseProgram(_shaderProg);
            GL.UniformMatrix4(_uMvp, false, ref mvp);
            GL.Uniform1(_uTex, 0);
            GL.Uniform1(_uHasTex, 1);
            GL.Uniform1(_uAlphaCut, 0.01f);
            GL.Uniform1(_uUnlit, 1);
            GL.Uniform1(_uOpacity, _uoRefOpacity);
            GL.Uniform1(_uBrightness, 1f);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, tex);
            GL.BindVertexArray(_quadVao);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 6);
            GL.BindVertexArray(0);
            GL.UseProgram(0);
            GL.Enable(EnableCap.DepthTest);
        }

        private Matrix4 UoViewMatrix(int dir)
        {
            float elev = MathHelper.DegreesToRadians(_uoElevationDeg);
            // dir increases the same way as UO's direction index; with the default
            // yaw offset (180) this maps dir d -> world azimuth (180 - d*45).
            float az = MathHelper.DegreesToRadians(_uoYawOffsetDeg - dir * 45f);
            Vector3 target = Vector3.Zero; // vertices are already recentered on the origin
            const float dist = 1000f;
            Vector3 eye = target + new Vector3(
                dist * (float)Math.Cos(elev) * (float)Math.Sin(az),
                dist * (float)Math.Cos(elev) * (float)Math.Cos(az),
                dist * (float)Math.Sin(elev));
            return Matrix4.LookAt(eye, target, Vector3.UnitZ);
        }

        private void DrawMeshSet(Matrix4 mvp, bool unlit, float alphaCut, float opacity = 1f, float brightness = 1f,
            Func<LoadedMesh, bool> filter = null)
        {
            GL.UseProgram(_shaderProg);
            GL.UniformMatrix4(_uMvp, false, ref mvp);
            GL.Uniform3(_uLightDir, 0.5f, 0.5f, 1f);
            GL.Uniform1(_uTex, 0);
            GL.Uniform1(_uAlphaCut, alphaCut);
            GL.Uniform1(_uUnlit, unlit ? 1 : 0);
            GL.Uniform1(_uOpacity, opacity);
            GL.Uniform1(_uBrightness, brightness);
            GL.ActiveTexture(TextureUnit.Texture0);

            foreach (var lm in _loadedMeshes)
            {
                if (lm.Vao == 0 || lm.Hidden) continue;
                if (filter != null && !filter(lm)) continue;

                bool tex = lm.HasTexture && lm.TextureId > 0;
                GL.BindTexture(TextureTarget.Texture2D, tex ? lm.TextureId : 0);
                GL.Uniform1(_uHasTex, tex ? 1 : 0);
                GL.Uniform3(_uColor, lm.MeshColor.X, lm.MeshColor.Y, lm.MeshColor.Z);

                GL.BindVertexArray(lm.Vao);
                GL.DrawArrays(PrimitiveType.Triangles, 0, lm.Indices.Length);
            }

            GL.BindVertexArray(0);
            GL.UseProgram(0);
        }

        // ==================== VD EXPORT (bake 3D -> UO frames) ====================

        // Animations offered as sources: the current creature's own .grn clips
        // (same folder + creature prefix), or every known animation as a fallback.
        private List<string> GatherExportAnimSources(out string prefix)
        {
            var list = new List<string>();
            string folder = !string.IsNullOrEmpty(_currentModelPath) ? Path.GetDirectoryName(_currentModelPath) : null;
            prefix = null;

            if (!string.IsNullOrEmpty(_currentModelPath))
            {
                string modelName = Path.GetFileNameWithoutExtension(_currentModelPath);
                if (modelName.StartsWith("H_Male", StringComparison.OrdinalIgnoreCase)) prefix = "H_Male_";
                else if (modelName.StartsWith("H_Female", StringComparison.OrdinalIgnoreCase)) prefix = "H_Female_";
                else prefix = DeriveCreature(modelName);
            }

            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                CollectAnims(folder, prefix, list);

            if (list.Count == 0)
            {
                prefix = null;
                foreach (var f in CategoryFolders)
                    CollectAnims(Path.Combine(BasePath, f), null, list);
            }

            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        // Every animation available anywhere under the model root: base .grn clips of
        // every creature/folder plus imported .anim clips (Others). Used by the export
        // mapping dialog's "ALL anim" toggle so any clip can be baked, not only the
        // current model's own folder.
        private List<string> GatherAllExportAnimSources(out List<string> display)
        {
            var list = new List<string>();
            display = new List<string>();
            foreach (var f in CategoryFolders)
            {
                string folder = Path.Combine(BasePath, f);
                bool others = string.Equals(f, "Others", StringComparison.OrdinalIgnoreCase);
                // Native clips live beside each creature; the "Others" folder holds
                // equipment parts, not clips, so only its .anim imports are offered.
                var group = new List<string>();
                if (!others) CollectAnims(folder, null, group, "*.grn");
                CollectAnims(folder, null, group, "*.anim");
                group.Sort(StringComparer.OrdinalIgnoreCase);
                string label = others ? "Importés (Mixamo)" : f;
                foreach (var p in group)
                {
                    list.Add(p);
                    display.Add(label + " \u25B8 " + Path.GetFileNameWithoutExtension(p));
                }
            }
            return list;
        }

        // ---- Cutout export helpers ----
        // A cutout spec = (occluder, target, bounds):
        //  - target  : meshes rendered into the sprite.
        //  - occluder: meshes drawn depth-only first, carving the target.
        //  - bounds  : meshes whose projected bounds frame the canvas (the target role).
        // NOTE: the mount is NOT part of the base occluder here; BakeAnimFrames adds it
        // only for the mounted actions (23-29) via allowMount.
        private (Func<LoadedMesh, bool> occluder, Func<LoadedMesh, bool> target, Func<LoadedMesh, bool> bounds)
            BuildCutoutFilter()
        {
            Func<LoadedMesh, bool> target = lm => lm.CutoutRole == MeshCutoutRole.Body;
            Func<LoadedMesh, bool> occluder = lm => lm.CutoutRole == MeshCutoutRole.Accessory;
            return (occluder, target, target);
        }

        // Inverse layer: export the ACCESSORY, carved by the body (and the mount on mounted
        // actions). Lets the user produce the clothing/armor layer with the body removed.
        private (Func<LoadedMesh, bool> occluder, Func<LoadedMesh, bool> target, Func<LoadedMesh, bool> bounds)
            BuildAccessoryCutoutFilter()
        {
            Func<LoadedMesh, bool> target = lm => lm.CutoutRole == MeshCutoutRole.Accessory;
            Func<LoadedMesh, bool> occluder = lm => lm.CutoutRole == MeshCutoutRole.Body;
            return (occluder, target, target);
        }

        // Cutout of only the mount's back (the rider is the target). Used so the
        // Mount base models (LOD2) available under the Animals folder. Each entry is
        // the full path to the mount's base mesh; its own clips share the prefix.
        private List<string> GatherMountModels()
        {
            var list = new List<string>();
            string folder = Path.Combine(BasePath, "Animals");
            if (!Directory.Exists(folder)) return list;
            string[] mountPrefixes = {
                "Equines_", "Ostards_", "Llamas_", "Ridgeback", "Skeletal_Mount",
                "Swamp_Dragon", "Giant_Beetle", "MondainSteed", "Kirin_", "CuShidhe",
                "Hai_Riyo", "Raptalon"
            };
            foreach (var f in Directory.GetFiles(folder, "*.grn", SearchOption.AllDirectories))
            {
                string n = Path.GetFileName(f);
                // Base mount meshes only (Lod2); skip the animation clips.
                if (n.IndexOf("Lod2", StringComparison.OrdinalIgnoreCase) < 0 &&
                    n.IndexOf("LOD2", StringComparison.OrdinalIgnoreCase) < 0) continue;
                bool isMount = false;
                foreach (var mp in mountPrefixes)
                {
                    if (n.StartsWith(mp, StringComparison.OrdinalIgnoreCase))
                    {
                        isMount = true;
                        break;
                    }
                }
                if (isMount) list.Add(f);
            }
            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        // Mount clips (walk/run/idle/...) under Animals/Monsters matching the mount's prefix or family fallback.
        private List<string> GatherMountClips(string mountBasePath)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(mountBasePath)) return list;

            string fileName = Path.GetFileName(mountBasePath);
            string fileLower = fileName.ToLowerInvariant();

            // Candidate directories: the folder containing the mount, plus Animals and Monsters
            var dirs = new List<string>();
            string dir = Path.GetDirectoryName(mountBasePath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dirs.Add(dir);

            string animalsDir = Path.Combine(BasePath, "Animals");
            if (Directory.Exists(animalsDir) && !dirs.Exists(d => string.Equals(d, animalsDir, StringComparison.OrdinalIgnoreCase)))
                dirs.Add(animalsDir);

            string monstersDir = Path.Combine(BasePath, "Monsters");
            if (Directory.Exists(monstersDir) && !dirs.Exists(d => string.Equals(d, monstersDir, StringComparison.OrdinalIgnoreCase)))
                dirs.Add(monstersDir);

            // Candidate prefixes in priority order:
            var prefixes = new List<string>();

            // 1. Direct prefix from file name before Lod
            string baseNoExt = Path.GetFileNameWithoutExtension(fileName);
            int li = baseNoExt.LastIndexOf('_');
            if (li > 0)
                prefixes.Add(baseNoExt.Substring(0, li + 1));

            // 2. Family fallbacks
            if (fileLower.StartsWith("equines_"))
                prefixes.Add("Equines_Horse_Dappled_Brown_");
            else if (fileLower.StartsWith("ostards_"))
                prefixes.Add("Ostards_Ostard_Desert_");
            else if (fileLower.StartsWith("llamas_"))
                prefixes.Add("Llamas_Llama_");
            else if (fileLower.StartsWith("ridgeback"))
                prefixes.Add("Ridgeback_Ridgeback_");
            else if (fileLower.StartsWith("swamp_dragon"))
                prefixes.Add("Swamp_Dragon_");
            else if (fileLower.StartsWith("giant_beetle"))
                prefixes.Add("Giant_Beetle_");
            else if (fileLower.StartsWith("skeletal_mount"))
                prefixes.Add("Skeletal_Mount_");
            else if (fileLower.StartsWith("mondainsteed"))
                prefixes.Add("MondainSteed01_");
            else if (fileLower.StartsWith("cushidhe"))
                prefixes.Add("CuShidhe_");
            else if (fileLower.StartsWith("hai_riyo"))
                prefixes.Add("Hai_Riyo_");
            else if (fileLower.StartsWith("raptalon"))
                prefixes.Add("Raptalon_");
            else if (fileLower.StartsWith("kirin"))
            {
                prefixes.Add("Kirin_Kirin_");
                prefixes.Add("Equines_Horse_Dappled_Brown_");
            }

            var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pref in prefixes)
            {
                list.Clear();
                seenFiles.Clear();
                foreach (var d in dirs)
                {
                    if (!Directory.Exists(d)) continue;
                    foreach (var f in Directory.GetFiles(d, "*.grn", SearchOption.TopDirectoryOnly))
                    {
                        string fn = Path.GetFileName(f);
                        if (fn.StartsWith(pref, StringComparison.OrdinalIgnoreCase) &&
                            fn.IndexOf("Lod", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            if (seenFiles.Add(fn))
                                list.Add(f);
                        }
                    }
                }
                if (list.Count > 0)
                    break;
            }

            list.Sort(StringComparer.OrdinalIgnoreCase);
            return list;
        }

        // VD action index -> mount clip keyword (human / equipment target). Only the
        // mounted actions carry a mount; every other action exports without it.
        //   23 Horse_Walk_01  -> walk      24 Horse_Run_01   -> run
        //   25 Horse_Idle_01  -> idle      26 Horse_Attack1H -> idle
        //   27 Horse_AttackBow-> fidget    28 Horse_AttackXB -> idle
        //   29 Horse_Attack2H -> fidget
        private static string MountClipKeywordForAction(int action, VdTargetDef target = null)
        {
            if (target != null && target.Uop)
            {
                switch (action)
                {
                    case 29: return "walk";
                    case 30: return "run";
                    case 31: return "idle";
                }
            }
            switch (action)
            {
                case 23: return "walk";
                case 24: return "run";
                case 25: return "idle";
                case 26: return "idle";
                case 27: return "fidget";
                case 28: return "idle";
                case 29: return "fidget";
                default: return null;
            }
        }

        // Find the mount clip whose name contains the given keyword (e.g. "fidget", "idle", "walk").
        private string FindMountClip(string keyword)
        {
            if (_mountMesh == null) return null;
            var clips = GatherMountClips(_mountMesh.SourcePath);
            if (clips.Count == 0) return null;
            string kl = (keyword ?? "").ToLowerInvariant();

            // Direct search
            foreach (var c in clips)
            {
                string fn = Path.GetFileNameWithoutExtension(c).ToLowerInvariant();
                if (fn.Contains(kl)) return c;
            }

            // Keyword specific aliases/fallbacks
            if (kl == "idle")
            {
                foreach (var c in clips)
                {
                    string fn = Path.GetFileNameWithoutExtension(c).ToLowerInvariant();
                    if (fn.Contains("stand")) return c;
                }
            }
            else if (kl == "fidget")
            {
                // Fall back to idle if mount has no fidget clip
                return FindMountClip("idle");
            }
            else if (kl == "gethit")
            {
                foreach (var c in clips)
                {
                    string fn = Path.GetFileNameWithoutExtension(c).ToLowerInvariant();
                    if (fn.Contains("takedamage") || fn.Contains("get_hit")) return c;
                }
            }

            return clips[0];
        }

        // Pick the mount clip whose action keyword matches the rider clip.
        // Preserves the subtlety: forced idle for rider weapon attacks (attack1h, attackxb, generic attack),
        // fidget for bow/2h attacks, and matching walk/run/die/gethit/idle.
        private string SelectMountClip(string riderClipPath)
        {
            if (_mountMesh == null) return null;
            if (string.IsNullOrEmpty(riderClipPath))
                return FindMountClip("idle");

            string rl = Path.GetFileNameWithoutExtension(riderClipPath).ToLowerInvariant();

            // Mounted weapon actions (fidget for bow/2h, forced idle for 1h/xb/generic attack)
            if (rl.Contains("attackbow") || rl.Contains("attack2h"))
                return FindMountClip("fidget");
            if (rl.Contains("attack1h") || rl.Contains("attackxb") || rl.Contains("attackcrossbow") || rl.Contains("attack"))
                return FindMountClip("idle");

            if (rl.Contains("walk"))
                return FindMountClip("walk");
            if (rl.Contains("run"))
                return FindMountClip("run");
            if (rl.Contains("die"))
                return FindMountClip("die");
            if (rl.Contains("gethit") || rl.Contains("get_hit") || rl.Contains("takedamage"))
                return FindMountClip("gethit");
            if (rl.Contains("fidget"))
                return FindMountClip("fidget");

            return FindMountClip("idle");
        }

        // In the interactive preview the mount must play its OWN clip (matching the
        // current rider action) instead of being dragged by the rider's skeleton.
        private void RefreshMountAnimation()
        {
            if (_mountMesh == null) return;
            float dur = _currentAnimation != null ? _currentAnimation.Duration : 1f;
            EnsureMountAnimation(_currentAnimPath, dur);
        }

        // Bind the mount's own clip (matching the rider action) into AnimOverride so the
        // mount animates in sync with the rider. Scale keeps both cycles aligned.
        private void EnsureMountAnimation(string riderClipPath, float riderDur, string keyword = null)
        {
            if (_mountMesh == null) return;
            string clip = keyword != null ? FindMountClip(keyword) : SelectMountClip(riderClipPath);
            if (string.IsNullOrEmpty(clip)) return;
            if (_mountClipPath != clip || _mountMesh.AnimOverride == null)
            {
                var mp = new GrannyFormatParser();
                if (!mp.Load(clip) || mp.Animation == null) return;
                foreach (var lm in _loadedMeshes)
                {
                    if (lm.CutoutRole != MeshCutoutRole.Mount) continue;
                    lm.AnimOverride = mp.Animation;
                    if (lm.AnimOverrideTrack == null || lm.AnimOverrideTrack.Length != lm.Bones.Count)
                        lm.AnimOverrideTrack = new int[lm.Bones.Count];
                    int matched = BindMeshToAnimationTrack(lm, mp.Animation, lm.AnimOverrideTrack);
                    Log($"Mount clip: {Path.GetFileName(clip)} matched={matched}/{lm.Bones.Count} dur={mp.Animation.Duration:F2}");
                }
                _mountClipPath = clip;
            }
            if (_mountMesh.AnimOverride != null)
            {
                float mountDur = _mountMesh.AnimOverride.Duration;
                float scale = (mountDur > 0.0001f && riderDur > 0.0001f) ? (mountDur / riderDur) : 1f;
                foreach (var lm in _loadedMeshes)
                {
                    if (lm.CutoutRole != MeshCutoutRole.Mount) continue;
                    lm.AnimOverrideTimeScale = scale;
                    lm.AnimOverrideTimeBase = 0f;
                }
            }
        }

        // Like BindMeshToAnimation but writes the mapping into a caller array (used for
        // the mount's AnimOverrideTrack, leaving the rider's BoneToAnimTrack intact).
        private static int BindMeshToAnimationTrack(LoadedMesh lm, GrannyAnimation anim, int[] target)
        {
            int boneCount = lm.Bones.Count;
            for (int i = 0; i < target.Length && i < boneCount; i++) target[i] = -1;
            var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < boneCount; i++)
                if (!string.IsNullOrEmpty(lm.Bones[i].Name)) byName[lm.Bones[i].Name] = i;
            int matched = 0;
            for (int ti = 0; ti < anim.Tracks.Count; ti++)
            {
                var tr = anim.Tracks[ti];
                if (string.IsNullOrEmpty(tr.Name)) continue;

                if (byName.TryGetValue(tr.Name, out int bi))
                {
                    if (bi < target.Length) { target[bi] = ti; matched++; }
                }
                else if (MixamoRetarget.TryGetBoneAlias(tr.Name, out string alias) &&
                         byName.TryGetValue(alias, out bi))
                {
                    if (bi < target.Length) { target[bi] = ti; matched++; }
                }
                else if (tr.Name.StartsWith("master", StringComparison.OrdinalIgnoreCase))
                {
                    // Fallback for root motion master bone (e.g. Master_Equines_Horse -> master_equines_horse_dark_brown)
                    for (int j = 0; j < boneCount; j++)
                    {
                        if (j < target.Length && target[j] < 0 &&
                            lm.Bones[j].Name != null &&
                            lm.Bones[j].Name.StartsWith("master", StringComparison.OrdinalIgnoreCase))
                        {
                            target[j] = ti;
                            matched++;
                            break;
                        }
                    }
                }
            }
            return matched;
        }

        // Load a mount .grn into the scene as an occluder mesh with CutoutRole=Mount.
        private LoadedMesh LoadMount(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            if (!_glReady || glControl == null || !glControl.IsHandleCreated)
            {
                Log("LoadMount aborted: GL not ready");
                return null;
            }
            glControl.MakeCurrent();
            var parser = new GrannyFormatParser();
            if (!parser.Load(path)) { Log($"LoadMount parse failed: {path}"); return null; }

            // Drop any previously loaded mount meshes.
            RemoveMount();

            LoadedMesh first = null;
            foreach (var mesh in parser.Meshes)
            {
                if (mesh.Vertices.Count == 0 || mesh.Indices.Count == 0) continue;
                var lm = new LoadedMesh
                {
                    Name = $"[Mount] {Path.GetFileName(path)}",
                    SourcePath = path,
                    SourceMesh = mesh,
                    Bones = new List<GrannyBone>(parser.Bones),
                    BaseVertices = mesh.Vertices.ToArray(),
                    Indices = mesh.Indices.ToArray(),
                    Skin = mesh.Skin.Count > 0 ? mesh.Skin.ToArray() : null,
                    CutoutRole = MeshCutoutRole.Mount,
                    MountOffset = new Vector3((float)nudMountOffX.Value, (float)nudMountOffY.Value, (float)nudMountOffZ.Value),
                    MountScale = (float)nudMountScale.Value
                };
                BuildCornerData(lm);
                SetupMeshVBO(lm);
                lm.TextureId = TryLoadTexture(path, parser.TextureName, out lm.TextureFilePath);
                lm.HasTexture = lm.TextureId > 0;
                lm.BaseTextureId = lm.TextureId;
                lm.BaseMeshColor = lm.MeshColor;
                _loadedMeshes.Add(lm);
                if (first == null) first = lm;
            }
            _mountMesh = first;
            _mountClipPath = null;
            Log($"LoadMount OK: {Path.GetFileName(path)} role=Mount");
            RefreshLoadedList();
            PopulateBoneCombo();
            if (_currentAnimation != null) { EnsureAnimationBindings(_currentAnimation); }
            RefreshMountAnimation();
            if (_currentAnimation != null) ApplyAnimationPose(_currentTime);
            else if (_glReady && glControl != null && glControl.IsHandleCreated) glControl.Invalidate();
            return first;
        }

        // Remove all Mount-role meshes from the scene.
        private void RemoveMount()
        {
            bool removed = false;
            for (int i = _loadedMeshes.Count - 1; i >= 0; i--)
            {
                if (_loadedMeshes[i].CutoutRole == MeshCutoutRole.Mount)
                {
                    _loadedMeshes.RemoveAt(i);
                    removed = true;
                }
            }
            _mountMesh = null;
            _mountClipPath = null;
            if (removed)
            {
                RefreshLoadedList();
                PopulateBoneCombo();
                if (_glReady && glControl != null && glControl.IsHandleCreated) glControl.Invalidate();
            }
        }

        // Render a single preview frame for VdRemapForm showing the live cutout / render result.
        private Bitmap RenderExportPreviewFrame(string animPath, int direction, float timeNorm,
            Vector3 actionOffset,
            (Func<LoadedMesh, bool> occluder, Func<LoadedMesh, bool> target, Func<LoadedMesh, bool> bounds)? cutout,
            int size = 0)
        {
            if (!_glReady || glControl == null || !glControl.IsHandleCreated || _loadedMeshes.Count == 0)
                return null;

            var savedAnim = _currentAnimation;
            float savedTime = _currentTime;
            bool savedPlaying = _playing;
            _playing = false;

            try
            {
                GrannyAnimation anim = null;
                if (!string.IsNullOrEmpty(animPath) && File.Exists(animPath))
                {
                    anim = LoadAnyAnimation(animPath);
                }
                if (anim == null) anim = savedAnim;

                if (anim != null)
                {
                    _currentAnimation = anim;
                    foreach (var lm in _loadedMeshes) lm.BoneToAnimTrack = null;
                }

                glControl.MakeCurrent();

                float dur = _currentAnimation != null ? _currentAnimation.Duration : 1f;
                if (dur <= 0.0001f) dur = 1f;
                float t = Math.Max(0f, Math.Min(dur, dur * timeNorm));

                // Pose skeletons
                PoseSkeletons(t, actionOffset);

                // Apply cloth if available
                foreach (var lm in _loadedMeshes)
                {
                    if (lm.IsCloth && lm.ClothPos != null && lm.ClothBake != null)
                    {
                        ApplyBakeCloth(lm, t);
                    }
                }
                RefreshVertexBuffers(false, actionOffset);

                // Camera & View
                Matrix4 vscale = Matrix4.CreateScale(1f, 1f, _uoVertScale);
                Matrix4 modelToView = vscale * UoViewMatrix(direction);

                // Cutout filters
                var cut = cutout ?? default;
                if (cutout.HasValue)
                {
                    var baseOccluder = cutout.Value.occluder;
                    Func<LoadedMesh, bool> occ = lm => baseOccluder(lm) || lm.CutoutRole == MeshCutoutRole.Mount;
                    cut = (occ, cutout.Value.target, cutout.Value.bounds);
                }

                Func<LoadedMesh, bool> boundsFilter = cutout.HasValue
                    ? cut.bounds
                    : (Func<LoadedMesh, bool>)(lm => lm.CutoutRole != MeshCutoutRole.Mount);

                float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
                AccumulateBounds(modelToView, ref minX, ref maxX, ref minY, ref maxY, boundsFilter);
                if (minX > maxX) { minX = -1f; maxX = 1f; minY = -1f; maxY = 1f; }

                float bw = Math.Max(1e-4f, maxX - minX);
                float bh = Math.Max(1e-4f, maxY - minY);

                float frameSpan = ComputeFrameSpan();
                float totalZoom = Math.Max(0.01f, _uoZoom);
                float previewViewH = _uoFeetOnLine
                    ? frameSpan * 2f * 1.08f / totalZoom
                    : frameSpan * 1.15f / totalZoom;
                int previewH = Math.Max(1, glControl.Height);
                float previewPpu = Math.Max(1e-6f, previewH / previewViewH);

                const int pad = 2;
                float ppu = (size > 0) ? Math.Max(1e-6f, size / bh) : previewPpu;
                int cw = Math.Max(8, (int)Math.Ceiling(bw * ppu) + 2 * pad);
                int ch = Math.Max(8, (int)Math.Ceiling(bh * ppu) + 2 * pad);

                // 2x supersampling for nice crisp preview
                const int ss = 2;
                int rw = cw * ss, rh = ch * ss;
                const int maxR = 2048;
                if (rw > maxR || rh > maxR)
                {
                    float f = Math.Min(maxR / (float)rw, maxR / (float)rh);
                    ppu *= f;
                    cw = Math.Max(8, (int)Math.Ceiling(bw * ppu) + 2 * pad);
                    ch = Math.Max(8, (int)Math.Ceiling(bh * ppu) + 2 * pad);
                    rw = cw * ss; rh = ch * ss;
                }

                float viewW = cw / ppu, viewH = ch / ppu;
                float shiftX = -(minX + maxX) * 0.5f;
                float shiftY = -(minY + maxY) * 0.5f;
                Matrix4 proj = Matrix4.CreateOrthographic(viewW, viewH, -5000f, 5000f);
                Matrix4 mvp = modelToView * Matrix4.CreateTranslation(shiftX, shiftY, 0f) * proj;

                using (var big = cutout.HasValue
                           ? RenderFrameCutout(rw, rh, mvp, cut.occluder, cut.target)
                           : RenderFrame(rw, rh, mvp, lm2 => lm2.CutoutRole != MeshCutoutRole.Mount))
                {
                    return DownsampleBox(big, cw, ch);
                }
            }
            catch (Exception ex)
            {
                Log($"Preview error: {ex.Message}");
                return null;
            }
            finally
            {
                _currentAnimation = savedAnim;
                _currentTime = savedTime;
                _playing = savedPlaying;
                foreach (var lm in _loadedMeshes) lm.BoneToAnimTrack = null;
                RefreshMountAnimation();
                try { if (savedAnim != null) ApplyAnimationPose(savedTime); } catch { }
                if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();
            }
        }

        private void ExportVdDialog((Func<LoadedMesh, bool> occluder, Func<LoadedMesh, bool> target, Func<LoadedMesh, bool> bounds)? cutout = null, string exportModeTitle = null)
        {
            if (_loadedMeshes.Count == 0) { Log("Export .vd : aucun modele charge."); return; }
            if (!_glReady || glControl == null || !glControl.IsHandleCreated) return;

            var sources = GatherExportAnimSources(out string prefix);
            if (sources.Count == 0) { Log("Export .vd : aucune animation source trouvee."); return; }
            var allSources = GatherAllExportAnimSources(out var allDisplay);

            Func<string, int, float, Vector3, int, Bitmap> previewRenderer = (animPath, dir, time, actOff, size) =>
            {
                return RenderExportPreviewFrame(animPath, dir, time, actOff, cutout, size);
            };

            string roleSummary;
            bool hasWarning = false;
            string warningText = null;

            if (!cutout.HasValue)
            {
                exportModeTitle ??= "Export .vd complet (sans cutout)";
                int vis = _loadedMeshes.Count(m => !m.Hidden && m.CutoutRole != MeshCutoutRole.Mount);
                roleSummary = $"Mode standard : tous les meshes visibles ({vis}) sont exportés.";
            }
            else
            {
                var targetMeshes = _loadedMeshes.Where(m => !m.Hidden && cutout.Value.target(m)).ToList();
                var occluderMeshes = _loadedMeshes.Where(m => !m.Hidden && cutout.Value.occluder(m)).ToList();

                bool isBodyTarget = exportModeTitle != null && exportModeTitle.IndexOf("Corps", StringComparison.OrdinalIgnoreCase) >= 0;
                exportModeTitle ??= isBodyTarget ? "Export Cutout : Corps (découpé)" : "Export Cutout : Accessoire (découpé)";

                var sb = new StringBuilder();
                sb.AppendLine($"Cible (exporté) : {targetMeshes.Count} mesh(es)");
                foreach (var tm in targetMeshes.Take(3))
                    sb.AppendLine($" • {Path.GetFileName(tm.SourcePath)} [{tm.CutoutRole}]");
                if (targetMeshes.Count > 3) sb.AppendLine($" • ... (+{targetMeshes.Count - 3})");

                sb.AppendLine($"Découpeur (masque) : {occluderMeshes.Count} mesh(es)");
                foreach (var om in occluderMeshes.Take(3))
                    sb.AppendLine($" • {Path.GetFileName(om.SourcePath)} [{om.CutoutRole}]");
                if (occluderMeshes.Count > 3) sb.AppendLine($" • ... (+{occluderMeshes.Count - 3})");

                roleSummary = sb.ToString();

                if (targetMeshes.Count == 0)
                {
                    hasWarning = true;
                    warningText = isBodyTarget
                        ? "⚠️ Aucun mesh n'a le rôle 'Body' !\nDéfinissez-le dans l'onglet Équipement."
                        : "⚠️ Aucun mesh n'a le rôle 'Accessory' !\nDéfinissez-le dans l'onglet Équipement.";
                }
                else if (occluderMeshes.Count == 0)
                {
                    warningText = "ℹ️ Aucun occluder présent (pas de découpage).";
                }
            }

            float frameSpan = ComputeFrameSpan();
            float totalZoom = Math.Max(0.01f, _uoZoom);
            float previewViewH = _uoFeetOnLine
                ? frameSpan * 2f * 1.08f / totalZoom
                : frameSpan * 1.15f / totalZoom;
            int previewH = Math.Max(1, glControl.Height);
            float previewPpu = Math.Max(1e-6f, previewH / previewViewH);
            int cameraOffY = (int)Math.Round(_uoOffY * previewPpu);

            using (var form = new VdRemapForm(VdExport.Targets(), sources, prefix, allSources, allDisplay, BasePath,
                previewRenderer, exportModeTitle, roleSummary, hasWarning, warningText, cameraOffY))
            {
                if (form.ShowDialog(this) != DialogResult.OK) return;
                var target = form.Target;
                if (target == null) return;

                var mapping = form.GetMapping();     // action -> source path or null
                var offsets = form.GetOffsets();      // action -> 3D offset (X, Y, Z)
                var frameCounts = form.GetFrameCounts(); // action -> forced frame count (0 = auto)
                int fps = form.Fps, size = form.FrameSize, offsetY = form.OffsetY;

                int mapped = 0;
                for (int i = 0; i < mapping.Length; i++) if (mapping[i] != null) mapped++;
                if (mapped == 0) { Log("Export .vd : aucune action mappee."); return; }

                var byActionDir = new VdAnimBlock[target.ActionCount * VdExport.DirCount];
                var cache = new Dictionary<string, VdAnimBlock[]>(StringComparer.OrdinalIgnoreCase);

                Cursor = Cursors.WaitCursor;
                try
                {
                    for (int a = 0; a < target.ActionCount; a++)
                    {
                        string src = (a < mapping.Length) ? mapping[a] : null;
                        if (string.IsNullOrEmpty(src)) continue;

                        Vector3 actOff = (offsets != null && a < offsets.Length) ? offsets[a] : Vector3.Zero;
                        int targetFrames = (frameCounts != null && a < frameCounts.Length) ? frameCounts[a] : 0;

                        // Mount clip is action-specific, so cache per (source, mount keyword, action offset, targetFrames).
                        string kw = MountClipKeywordForAction(a, target);
                        string cacheKey = (kw != null)
                            ? $"{src}|{kw}|{actOff.X:F3}_{actOff.Y:F3}_{actOff.Z:F3}|fr_{targetFrames}"
                            : $"{src}|{actOff.X:F3}_{actOff.Y:F3}_{actOff.Z:F3}|fr_{targetFrames}";
                        if (!cache.TryGetValue(cacheKey, out var blocks))
                        {
                            blocks = BakeAnimFrames(src, size, fps, cutout, kw, offsetY, actOff, targetFrames);
                            if (blocks == null) continue;
                            cache[cacheKey] = blocks;
                        }

                        for (int d = 0; d < VdExport.DirCount; d++)
                            byActionDir[a * VdExport.DirCount + d] = blocks[d];
                    }

                    using (var sfd = new SaveFileDialog())
                    {
                        sfd.Filter = target.Uop
                            ? "UO animation (UOP/VD)|*.vd;*.uop|Tous les fichiers|*.*"
                            : "UO animation (MUL/VD)|*.vd|Tous les fichiers|*.*";
                        sfd.FileName = target.Key + ".vd";
                        if (sfd.ShowDialog(this) != DialogResult.OK) return;

                        if (target.Uop)
                            VdExport.WriteUopVd(sfd.FileName, target.AnimType, target.ActionCount, byActionDir);
                        else
                            VdExport.WriteMulVd(sfd.FileName, target.AnimType, target.ActionCount, byActionDir);

                        Log($"Export .vd OK : {sfd.FileName} cible={target.Key} actions={mapped} fps={fps} size={size}");
                    }
                }
                catch (Exception ex)
                {
                    Log($"Export .vd error: {ex.Message}\n{ex}");
                }
                finally { Cursor = Cursors.Default; }
            }
        }

        // Bake one source clip into 5 directional VdAnimBlocks (cached per source path
        // so several target actions sharing a clip are rendered only once). When cutout
        // is non-null the render uses depth occlusion (body-only UO layering) instead of
        // the plain full-model render.
        private VdAnimBlock[] BakeAnimFrames(string animPath, int size, int fps,
            (Func<LoadedMesh, bool> occluder, Func<LoadedMesh, bool> target, Func<LoadedMesh, bool> bounds)? cutout = null,
            string mountKeyword = null, int offsetY = 0, Vector3 actionOffset = default, int targetFrames = 0)
        {
            var savedAnim = _currentAnimation;
            float savedTime = _currentTime;
            bool savedPlaying = _playing;
            _playing = false;

            try
            {
                var anim = LoadAnyAnimation(animPath);
                if (anim == null) return null;
                _currentAnimation = anim;

                foreach (var lm in _loadedMeshes) lm.BoneToAnimTrack = null;

                glControl.MakeCurrent();

                float dur = _currentAnimation.Duration;
                if (dur <= 0.0001f) dur = 1f / Math.Max(1, fps);
                int n = targetFrames > 0 ? Math.Clamp(targetFrames, 1, 64) : Math.Max(1, (int)Math.Round(dur * fps));
                if (n > 64) n = 64;

                // Ensure cloth meshes are simulated or baked for this animation clip
                var clothMeshes = new List<LoadedMesh>();
                foreach (var m in _loadedMeshes)
                {
                    if (m.IsCloth && m.ClothPos != null) clothMeshes.Add(m);
                }

                if (clothMeshes.Count > 0)
                {
                    bool needSim = false;
                    foreach (var clm in clothMeshes)
                    {
                        if (clm.ClothBake == null || !ReferenceEquals(clm.ClothBakeAnim, _currentAnimation) || clm.ClothBakeFrames != n)
                        {
                            needSim = true;
                            break;
                        }
                    }

                    if (needSim)
                    {
                        float animDt = dur / n;
                        ResetAllCloth();
                        foreach (var clm in clothMeshes)
                        {
                            clm.ClothBakeRaw = new Vector3[n][];
                            clm.ClothBakeFrames = n;
                            clm.ClothBakeDt = animDt;
                        }

                        for (int cycle = 0; cycle < 2; cycle++)
                        {
                            for (int f = 0; f < n; f++)
                            {
                                float t = f * animDt;
                                PoseSkeletons(t, actionOffset);
                                RefreshVertexBuffers(false, actionOffset);
                                foreach (var clm in clothMeshes) StepClothForMesh(clm, animDt);
                                if (cycle == 1)
                                {
                                    foreach (var clm in clothMeshes)
                                        clm.ClothBakeRaw[f] = (Vector3[])clm.ClothPos.Clone();
                                }
                            }
                        }

                        int blendPct = nudLoopBlend != null ? (int)nudLoopBlend.Value : 10;
                        foreach (var clm in clothMeshes)
                        {
                            ApplyLoopBlend(clm, blendPct);
                            clm.ClothBakeAnim = _currentAnimation;
                            clm.ClothBakeGravity = clm.ClothGravity;
                        }
                    }
                }

                // A selected mount is an occluder only on the mounted actions (23-29),
                // each using its own clip (walk/run/idle/fidget). Other actions export
                // with no mount at all.
                bool allowMount = cutout.HasValue && _mountMesh != null && !string.IsNullOrEmpty(mountKeyword);
                if (allowMount)
                {
                    EnsureMountAnimation(animPath, dur, mountKeyword);
                    glControl.MakeCurrent();
                }

                // Effective occluder: the caller's filter, plus the mount whenever one is
                // loaded (the mount always carves the rider).
                (Func<LoadedMesh, bool> occluder, Func<LoadedMesh, bool> target, Func<LoadedMesh, bool> bounds) cut = default;
                if (cutout.HasValue)
                {
                    var baseOccluder = cutout.Value.occluder;
                    Func<LoadedMesh, bool> occ = lm =>
                        baseOccluder(lm) || (allowMount && lm.CutoutRole == MeshCutoutRole.Mount);
                    cut = (occ, cutout.Value.target, cutout.Value.bounds);
                }

                const int ss = 4;                    // supersample factor (anti-aliasing)
                const int pad = 2;                  // margin (final px) so nothing clips

                // Match the preview's on-screen scale so the exported sprite is the
                // same size the user sees in the UO view (calibrated against the c_man
                // reference) instead of zooming in to fill the canvas. "size" > 0 is an
                // explicit target character height in px; 0 = follow the preview.
                float frameSpan = ComputeFrameSpan();
                // The export scale follows the UO-calibrated "Zoom" only. The global
                // zoom (%) is a pure on-screen magnifier (model + reference overlay):
                // inheriting it made the export balloon by that factor (e.g. 300% -> 3x).
                float totalZoom = Math.Max(0.01f, _uoZoom);
                float previewViewH = _uoFeetOnLine
                    ? frameSpan * 2f * 1.08f / totalZoom
                    : frameSpan * 1.15f / totalZoom;
                int previewH = Math.Max(1, glControl != null ? glControl.Height : 600);
                float previewPpu = Math.Max(1e-6f, previewH / previewViewH);

                Log($"  bake scale: zoom={_uoZoom:F3} totalZoom={totalZoom:F3} frameSpan={frameSpan:F3} ppu={previewPpu:F3} refScale={_refScale:F4} ppuRaw={(previewPpu / Math.Max(1e-6f, _refScale)):F3} refCenter=({_refCenter.X:F3},{_refCenter.Y:F3},{_refCenter.Z:F3}) rawMinZ={(_refCenter.Z + _normMin.Z / Math.Max(1e-6f, _refScale)):F3} rawMaxZ={(_refCenter.Z + _normMax.Z / Math.Max(1e-6f, _refScale)):F3} feetOnLine={_uoFeetOnLine} meshes={_loadedMeshes.Count}");
                foreach (var lmx in _loadedMeshes)
                    Log($"  bake scene mesh '{Path.GetFileName(lmx.SourcePath)}' role={lmx.CutoutRole} hidden={lmx.Hidden}");

                Matrix4 vscale = Matrix4.CreateScale(1f, 1f, _uoVertScale);

                // The cutout frame is sized to the exported layer only (accessories/mount
                // are occluders, not part of the sprite) so the layer keeps the preview
                // scale and the canvas stays tight. UO layers align via the frame Center.
                Func<LoadedMesh, bool> boundsFilter = cutout.HasValue
                    ? cut.bounds
                    : (Func<LoadedMesh, bool>)(lm => lm.CutoutRole != MeshCutoutRole.Mount);

                var blocks = new VdAnimBlock[VdExport.DirCount];
                for (int d = 0; d < VdExport.DirCount; d++)
                {
                    Matrix4 modelToView = vscale * UoViewMatrix(d);

                    // Pass 1: exact projected bounds over every frame of this direction,
                    // so the whole silhouette fits whatever the pose (fixes parts being
                    // cut below the ground line).
                    float minX = float.MaxValue, maxX = float.MinValue, minY = float.MaxValue, maxY = float.MinValue;
                    for (int f = 0; f < n; f++)
                    {
                        float t = dur * f / n;
                        PoseSkeletons(t, actionOffset);
                        foreach (var clm in clothMeshes) ApplyBakeCloth(clm, t);
                        RefreshVertexBuffers(false, actionOffset);
                        AccumulateBounds(modelToView, ref minX, ref maxX, ref minY, ref maxY, boundsFilter);
                    }
                    if (minX > maxX) { minX = -1f; maxX = 1f; minY = -1f; maxY = 1f; }

                    float bw = Math.Max(1e-4f, maxX - minX);
                    float bh = Math.Max(1e-4f, maxY - minY);

                    float ppu = (size > 0) ? Math.Max(1e-6f, size / bh) : previewPpu;
                    int cw = Math.Max(8, (int)Math.Ceiling(bw * ppu) + 2 * pad);
                    int ch = Math.Max(8, (int)Math.Ceiling(bh * ppu) + 2 * pad);
                    // Clamp the supersampled FBO to a safe size; shrink ppu so the
                    // model still fits the same world extent (no cropping).
                    const int maxR = 2048;
                    if (cw * ss > maxR || ch * ss > maxR)
                    {
                        float f = Math.Min(maxR / (float)(cw * ss), maxR / (float)(ch * ss));
                        ppu *= f;
                        cw = Math.Max(8, (int)Math.Ceiling(bw * ppu) + 2 * pad);
                        ch = Math.Max(8, (int)Math.Ceiling(bh * ppu) + 2 * pad);
                    }
                    int rw = cw * ss, rh = ch * ss;

                    float viewW = cw / ppu, viewH = ch / ppu;
                    float shiftX = -(minX + maxX) * 0.5f;
                    float shiftY = -(minY + maxY) * 0.5f;
                    Matrix4 proj = Matrix4.CreateOrthographic(viewW, viewH, -5000f, 5000f);
                    Matrix4 mvp = modelToView * Matrix4.CreateTranslation(shiftX, shiftY, 0f) * proj;

                    // Feet pivot -> UO Center (origin row = Center.Y + Height).
                    float anchorZ = _uoFeetOnLine ? _normMin.Z : (_normMin.Z + _normMax.Z) * 0.5f;
                    Vector3 va = Vector3.TransformPosition(
                        new Vector3(-_refCenter.X * _refScale, -_refCenter.Y * _refScale, anchorZ), modelToView);
                    // Rotation axis X is always 0 in view space (model rotates around normalized origin 0,0),
                    // matching DrawUoDirection which uses shiftX = _uoOffX without subtracting va.X.
                    // va.X was non-zero for quadrupeds/centaurs (whose Y center != 0), causing an unwanted
                    // horizontal shift in directions 1 (SW), 2 (W), 3 (NW).
                    int centerX = (int)Math.Round((0.5f + shiftX / viewW) * cw);
                    int centerY = (int)Math.Round((0.5f - (va.Y + shiftY) / viewH) * ch) - ch;

                    // Apply camera offset from the interactive UO viewport (right preview)
                    // so the exported .vd sprite matches the visual alignment exactly.
                    int cameraOffX = (int)Math.Round(_uoOffX * ppu);
                    int cameraOffY = (int)Math.Round(_uoOffY * ppu);
                    centerX -= cameraOffX;
                    centerY += cameraOffY;
                    centerY -= offsetY;

                    // Pass 2: render every frame, then box-downsample (SSAA).
                    var frames = new List<Bitmap>(n);
                    for (int f = 0; f < n; f++)
                    {
                        float t = dur * f / n;
                        PoseSkeletons(t, actionOffset);
                        foreach (var clm in clothMeshes) ApplyBakeCloth(clm, t);
                        RefreshVertexBuffers(false, actionOffset);
                        using (var big = cutout.HasValue
                                   ? RenderFrameCutout(rw, rh, mvp, cut.occluder, cut.target)
                                   : RenderFrame(rw, rh, mvp, lm2 => lm2.CutoutRole != MeshCutoutRole.Mount))
                            frames.Add(DownsampleBox(big, cw, ch));
                    }
                    blocks[d] = VdExport.BuildBlock(frames, centerX, centerY);
                    foreach (var b in frames) b.Dispose();
                    Log($"  bake dir={d} frames={n} px={cw}x{ch} ss={ss} center=({centerX},{centerY}) camOff=({cameraOffX},{cameraOffY}) offsetY={offsetY} actOff=({actionOffset.X:F2},{actionOffset.Y:F2},{actionOffset.Z:F2}) ({Path.GetFileName(animPath)})");
                }
                return blocks;
            }
            finally
            {
                _currentAnimation = savedAnim;
                _currentTime = savedTime;
                _playing = savedPlaying;
                foreach (var lm in _loadedMeshes) lm.BoneToAnimTrack = null;
                RefreshMountAnimation();
                try { if (savedAnim != null) ApplyAnimationPose(savedTime); } catch { }
                if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();
            }
        }

        // Accumulate the projected (view-space X/Y) bounds of every visible mesh
        // vertex for the current pose. Interleaved already holds normalized positions.
        private void AccumulateBounds(Matrix4 modelToView, ref float minX, ref float maxX, ref float minY, ref float maxY,
            Func<LoadedMesh, bool> filter = null)
        {
            foreach (var lm in _loadedMeshes)
            {
                if (lm.Interleaved == null || lm.Hidden) continue;
                if (filter != null && !filter(lm)) continue;
                float[] f = lm.Interleaved;
                for (int c = 0; c + 7 < f.Length; c += 8)
                {
                    Vector3 v = Vector3.TransformPosition(new Vector3(f[c], f[c + 1], f[c + 2]), modelToView);
                    if (v.X < minX) minX = v.X; if (v.X > maxX) maxX = v.X;
                    if (v.Y < minY) minY = v.Y; if (v.Y > maxY) maxY = v.Y;
                }
            }
        }

        // Render the current pose (already uploaded by RefreshVertexBuffers) through an
        // offscreen FBO and read it back as a top-down ARGB bitmap at render resolution.
        private Bitmap RenderFrame(int w, int h, Matrix4 mvp, Func<LoadedMesh, bool> filter = null)
        {
            int fbo = GL.GenFramebuffer();
            int color = GL.GenTexture();
            int depth = GL.GenRenderbuffer();

            GL.BindTexture(TextureTarget.Texture2D, color);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, w, h, 0,
                OpenTK.Graphics.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, color, 0);
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, w, h);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                RenderbufferTarget.Renderbuffer, depth);

            GL.Viewport(0, 0, w, h);
            GL.Disable(EnableCap.ScissorTest);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Lequal);
            GL.Disable(EnableCap.CullFace);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            GL.ClearColor(0f, 0f, 0f, 0f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            DrawMeshSet(mvp, unlit: true, alphaCut: 0.5f, opacity: 1f, brightness: _uoBrightness, filter: filter);

            byte[] buf = new byte[w * h * 4];
            GL.ReadPixels<byte>(0, 0, w, h, OpenTK.Graphics.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, buf);

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.DeleteFramebuffer(fbo);
            GL.DeleteRenderbuffer(depth);
            GL.DeleteTexture(color);

            // GL origin is bottom-left -> flip vertically into a top-down ARGB bitmap.
            var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                unsafe
                {
                    for (int y = 0; y < h; y++)
                    {
                        byte* dst = (byte*)bd.Scan0 + (long)y * bd.Stride;
                        int srcRow = (h - 1 - y) * w * 4;
                        for (int x = 0; x < w; x++)
                        {
                            int si = srcRow + x * 4;
                            dst[x * 4 + 0] = buf[si + 2]; // B
                            dst[x * 4 + 1] = buf[si + 1]; // G
                            dst[x * 4 + 2] = buf[si + 0]; // R
                            dst[x * 4 + 3] = buf[si + 3]; // A
                        }
                    }
                }
            }
            finally { bmp.UnlockBits(bd); }
            return bmp;
        }

        // Read the current FBO back as a top-down ARGB bitmap (GL bottom-left origin
        // flipped). Shared by RenderFrame and RenderFrameCutout.
        private static Bitmap ReadFboToBitmap(int w, int h)
        {
            byte[] buf = new byte[w * h * 4];
            GL.ReadPixels<byte>(0, 0, w, h, OpenTK.Graphics.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, buf);

            var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                unsafe
                {
                    for (int y = 0; y < h; y++)
                    {
                        byte* dst = (byte*)bd.Scan0 + (long)y * bd.Stride;
                        int srcRow = (h - 1 - y) * w * 4;
                        for (int x = 0; x < w; x++)
                        {
                            int si = srcRow + x * 4;
                            dst[x * 4 + 0] = buf[si + 2]; // B
                            dst[x * 4 + 1] = buf[si + 1]; // G
                            dst[x * 4 + 2] = buf[si + 0]; // R
                            dst[x * 4 + 3] = buf[si + 3]; // A
                        }
                    }
                }
            }
            finally { bmp.UnlockBits(bd); }
            return bmp;
        }

        // Render only the target meshes, carving away (via depth occlusion) every
        // part that is behind an occluder mesh. Two passes in one FBO:
        //   1) occluders -> depth buffer only (color writes off).
        //   2) target    -> drawn with depth test; fragments behind occluders fail.
        // This yields the UO-style layered "cutout": the body without the regions
        // covered by armor/mount.
        private Bitmap RenderFrameCutout(int w, int h, Matrix4 mvp,
            Func<LoadedMesh, bool> occluder, Func<LoadedMesh, bool> target)
        {
            int fbo = GL.GenFramebuffer();
            int color = GL.GenTexture();
            int depth = GL.GenRenderbuffer();

            GL.BindTexture(TextureTarget.Texture2D, color);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, w, h, 0,
                OpenTK.Graphics.OpenGL.PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D, color, 0);
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer, depth);
            GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer, RenderbufferStorage.DepthComponent24, w, h);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.DepthAttachment,
                RenderbufferTarget.Renderbuffer, depth);

            GL.Viewport(0, 0, w, h);
            GL.Disable(EnableCap.ScissorTest);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthFunc(DepthFunction.Lequal);
            GL.DepthMask(true);
            GL.Disable(EnableCap.CullFace);
            GL.Disable(EnableCap.Blend);
            GL.ClearColor(0f, 0f, 0f, 0f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            // Pass 1: occluders fill the depth buffer only (no color).
            GL.ColorMask(false, false, false, false);
            DrawMeshSet(mvp, unlit: true, alphaCut: 0.5f, opacity: 1f, brightness: 1f, filter: occluder);

            // Pass 2: target drawn where NOT behind an occluder (depth Less).
            GL.ColorMask(true, true, true, true);
            GL.DepthFunc(DepthFunction.Less);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
            DrawMeshSet(mvp, unlit: true, alphaCut: 0.5f, opacity: 1f, brightness: _uoBrightness, filter: target);

            Bitmap bmp = ReadFboToBitmap(w, h);

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.DeleteFramebuffer(fbo);
            GL.DeleteRenderbuffer(depth);
            GL.DeleteTexture(color);
            GL.DepthFunc(DepthFunction.Lequal);
            GL.DepthMask(true);
            return bmp;
        }

        // Alpha-aware box downscale (proper SSAA): average the premultiplied colours so
        // transparent texels don't darken the edges, then un-premultiply. Rectangular.
        private static Bitmap DownsampleBox(Bitmap src, int dw, int dh)
        {
            if (dw < 1) dw = 1;
            if (dh < 1) dh = 1;
            int ss = Math.Max(1, src.Width / dw);
            if (src.Height / dh < ss) ss = Math.Max(1, src.Height / dh);
            var dst = new Bitmap(dw, dh, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var sd = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var dd = dst.LockBits(new Rectangle(0, 0, dw, dh), ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            try
            {
                unsafe
                {
                    for (int y = 0; y < dh; y++)
                    {
                        byte* drow = (byte*)dd.Scan0 + (long)y * dd.Stride;
                        for (int x = 0; x < dw; x++)
                        {
                            long sr = 0, sg = 0, sb = 0, sa = 0; int cnt = 0;
                            for (int j = 0; j < ss; j++)
                            {
                                int sy = y * ss + j;
                                if (sy >= src.Height) break;
                                byte* srow = (byte*)sd.Scan0 + (long)sy * sd.Stride;
                                for (int i = 0; i < ss; i++)
                                {
                                    int sx = x * ss + i;
                                    if (sx >= src.Width) break;
                                    byte* p = srow + (long)sx * 4;
                                    int a = p[3];
                                    sb += (long)p[0] * a; sg += (long)p[1] * a; sr += (long)p[2] * a; sa += a; cnt++;
                                }
                            }
                            byte* d = drow + (long)x * 4;
                            if (sa > 0 && cnt > 0)
                            {
                                d[0] = (byte)Math.Min(255, sb / sa);
                                d[1] = (byte)Math.Min(255, sg / sa);
                                d[2] = (byte)Math.Min(255, sr / sa);
                                d[3] = (byte)Math.Min(255, sa / cnt);
                            }
                            else { d[0] = d[1] = d[2] = d[3] = 0; }
                        }
                    }
                }
            }
            finally { src.UnlockBits(sd); dst.UnlockBits(dd); }
            return dst;
        }

        private void CompileShaders()
        {
            if (_shaderProg != 0) return;

            const string vs = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNormal;
layout(location=2) in vec2 aUV;
uniform mat4 uMvp;
out vec3 vNormal;
out vec2 vUV;
void main(){ vNormal = aNormal; vUV = aUV; gl_Position = uMvp * vec4(aPos, 1.0); }";

            const string fs = @"#version 330 core
in vec3 vNormal;
in vec2 vUV;
uniform sampler2D uTex;
uniform int uHasTex;
uniform vec3 uColor;
uniform vec3 uLightDir;
uniform float uAlphaCut;
uniform int uUnlit;
uniform float uOpacity;
uniform float uBrightness;
out vec4 FragColor;
void main(){
    vec3 n = normalize(vNormal);
    float d = max(dot(n, normalize(uLightDir)), 0.0);
    vec4 tex = texture(uTex, vUV);
    float a = (uHasTex == 1) ? tex.a : 1.0;
    if (a < uAlphaCut) discard;
    vec3 base = (uHasTex == 1) ? tex.rgb : uColor;
    float shade = (uUnlit == 1) ? 1.0 : (0.45 + 0.75 * d);
    FragColor = vec4(clamp(base * shade * uBrightness, 0.0, 1.0), a * uOpacity);
}";

            int v = GL.CreateShader(ShaderType.VertexShader);
            GL.ShaderSource(v, vs);
            GL.CompileShader(v);
            GL.GetShader(v, ShaderParameter.CompileStatus, out int vsOk);
            if (vsOk == 0) Log("VS compile error: " + GL.GetShaderInfoLog(v));

            int f = GL.CreateShader(ShaderType.FragmentShader);
            GL.ShaderSource(f, fs);
            GL.CompileShader(f);
            GL.GetShader(f, ShaderParameter.CompileStatus, out int fsOk);
            if (fsOk == 0) Log("FS compile error: " + GL.GetShaderInfoLog(f));

            _shaderProg = GL.CreateProgram();
            GL.AttachShader(_shaderProg, v);
            GL.AttachShader(_shaderProg, f);
            GL.LinkProgram(_shaderProg);
            GL.GetProgram(_shaderProg, GetProgramParameterName.LinkStatus, out int linkOk);
            if (linkOk == 0) Log("Link error: " + GL.GetProgramInfoLog(_shaderProg));
            GL.DeleteShader(v);
            GL.DeleteShader(f);

            _uMvp = GL.GetUniformLocation(_shaderProg, "uMvp");
            _uHasTex = GL.GetUniformLocation(_shaderProg, "uHasTex");
            _uColor = GL.GetUniformLocation(_shaderProg, "uColor");
            _uTex = GL.GetUniformLocation(_shaderProg, "uTex");
            _uLightDir = GL.GetUniformLocation(_shaderProg, "uLightDir");
            _uAlphaCut = GL.GetUniformLocation(_shaderProg, "uAlphaCut");
            _uUnlit = GL.GetUniformLocation(_shaderProg, "uUnlit");
            _uOpacity = GL.GetUniformLocation(_shaderProg, "uOpacity");
            _uBrightness = GL.GetUniformLocation(_shaderProg, "uBrightness");
            Log($"Shaders compiled prog={_shaderProg} vsOk={vsOk} fsOk={fsOk} link={linkOk}");
        }

        // ==================== INPUT ====================

        private void GlControl_MouseDown(object sender, MouseEventArgs e)
        {
            glControl.Focus();
            if (e.Button == MouseButtons.Right)
            {
                _glRightDrag = false;
                _glRightDownPos = e.Location;
            }
            if (_uoView)
            {
                int sx = UoSplitX();
                if (Math.Abs(e.X - sx) <= 5) { _uoDragSplit = true; return; }
                if (e.X > sx) return;   // right (UO) half: no orbit
            }
            // View cube click (standard view presets) takes priority over orbiting.
            if (e.Button == MouseButtons.Left && TryViewCubeClick(e)) return;
            _camera.OnMouseDown(e);
        }

        private void GlControl_MouseUp(object sender, MouseEventArgs e)
        {
            _uoDragSplit = false;
            _camera.OnMouseUp(e);
        }

        private void GlControl_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                if (Math.Abs(e.X - _glRightDownPos.X) > 5 || Math.Abs(e.Y - _glRightDownPos.Y) > 5)
                {
                    _glRightDrag = true;
                }
            }
            if (_uoDragSplit)
            {
                _uoSplit = MathHelper.Clamp((float)e.X / Math.Max(1, glControl.Width), 0.15f, 0.85f);
                glControl.Invalidate();
                return;
            }
            int sx = UoSplitX();
            if (_uoView && Math.Abs(e.X - sx) <= 5) { Cursor = Cursors.SizeWE; return; }
            Cursor = Cursors.Default;
            if (_uoView && e.X > sx) return;   // right (UO) half
            _camera.OnMouseMove(e);
            glControl.Invalidate();
        }

        private void GlContextMenu_Opening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_glRightDrag)
            {
                _glRightDrag = false;
                e.Cancel = true;
                return;
            }

            glContextMenu.Items.Clear();

            var applyMixamoItem = new ToolStripMenuItem("⚡ Convert/create rig to UO (Adapter au squelette UO Biped)...", null, (s, a) =>
            {
                string target = !string.IsNullOrEmpty(_currentModelPath) ? _currentModelPath : null;
                ApplyMixamoForUo(target);
            });
            applyMixamoItem.Font = new Font(glContextMenu.Font, FontStyle.Bold);
            glContextMenu.Items.Add(applyMixamoItem);

            glContextMenu.Items.Add("Importer un Character Mixamo (FBX)...", null, BtnImportModel_Click);
            glContextMenu.Items.Add("Importer une animation FBX Mixamo...", null, BtnImportAnim_Click);
            glContextMenu.Items.Add(new ToolStripSeparator());

            if (!string.IsNullOrEmpty(_currentModelPath))
            {
                glContextMenu.Items.Add("Assigner / Modifier les textures...", null, (s, a) =>
                {
                    OpenTextureManagerForModel(_currentModelPath);
                });
            }

            glContextMenu.Items.Add("Recentrer la caméra", null, (s, a) =>
            {
                _camera.Target = Vector3.Zero;
                _camera.Distance = 5f;
                _camera.Yaw = 0f;
                _camera.Pitch = 0f;
                glControl.Invalidate();
            });

            var vuesMenu = new ToolStripMenuItem("Vues standard");
            vuesMenu.DropDownItems.Add("Devant", null, (s, a) => { _camera.ViewFront(); glControl.Invalidate(); });
            vuesMenu.DropDownItems.Add("Derrière", null, (s, a) => { _camera.ViewBack(); glControl.Invalidate(); });
            vuesMenu.DropDownItems.Add("Gauche", null, (s, a) => { _camera.ViewLeft(); glControl.Invalidate(); });
            vuesMenu.DropDownItems.Add("Droite", null, (s, a) => { _camera.ViewRight(); glControl.Invalidate(); });
            vuesMenu.DropDownItems.Add("Dessus", null, (s, a) => { _camera.ViewTop(); glControl.Invalidate(); });
            vuesMenu.DropDownItems.Add("Dessous", null, (s, a) => { _camera.ViewBottom(); glControl.Invalidate(); });
            vuesMenu.DropDownItems.Add("Isométrique", null, (s, a) => { _camera.ViewIso(); glControl.Invalidate(); });
            glContextMenu.Items.Add(vuesMenu);

            glContextMenu.Items.Add("Réinitialiser la pose (Stop)", null, BtnStop_Click);
            glContextMenu.Items.Add("T-Pose", null, BtnTPose_Click);
        }

        private void GlControl_MouseWheel(object sender, MouseEventArgs e)
        {
            if (_uoView && e.X > UoSplitX())
            {
                _uoZoom = MathHelper.Clamp(_uoZoom * (e.Delta > 0 ? 1.1f : 0.9f), 0.1f, 6f);
                if (nudUoZoom != null) nudUoZoom.Value = (decimal)Math.Round(_uoZoom, 2);
                glControl.Invalidate();
                return;
            }
            _camera.OnMouseWheel(e);
            glControl.Invalidate();
        }

        private void BtnPlay_Click(object sender, EventArgs e)
        {
            if (_currentAnimation == null) return;
            _playing = true;
            btnPlay.Enabled = false;
            btnPause.Enabled = true;
            btnStop.Enabled = true;
            _lastFrameTime = DateTime.Now;
        }

        private void BtnPause_Click(object sender, EventArgs e)
        {
            _playing = false;
            btnPlay.Enabled = true;
            btnPause.Enabled = false;
            btnStop.Enabled = true;
        }

        private void BtnStop_Click(object sender, EventArgs e)
        {
            _playing = false;
            _currentTime = 0f;
            trackTime.Value = 0;
            lblTime.Text = "Time: 0.00s";
            ApplyAnimationPose(0f);
            btnPlay.Enabled = true;
            btnPause.Enabled = false;
            btnStop.Enabled = false;
            glControl.Invalidate();
        }

        private void TrackTime_Scroll(object sender, EventArgs e)
        {
            _currentTime = trackTime.Value / 1000f;
            lblTime.Text = $"Time: {_currentTime:F2}s";
            ApplyAnimationPose(_currentTime);
            glControl.Invalidate();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            _renderTimer?.Stop();

            // Persist the current panel layout so it is restored on next open.
            try
            {
                if (_mainSplit != null) LayoutMainSplit = _mainSplit.SplitterDistance;
                if (_leftSplit != null) LayoutLeftSplit = _leftSplit.SplitterDistance;
                if (_animSplit != null) LayoutAnimSplit = _animSplit.SplitterDistance;
                SaveSettings();
            }
            catch { }

            if (glControl != null && glControl.IsHandleCreated)
            {
                try
                {
                    glControl.MakeCurrent();
                    foreach (var lm in _loadedMeshes)
                        DestroyMesh(lm);
                }
                catch { }
            }
            base.OnHandleDestroyed(e);
        }

        private void ApplyCameraPreset(UoCameraPreset p)
        {
            if (p == null) return;
            _uoZoom = p.Zoom;
            if (nudUoZoom != null) nudUoZoom.Value = (decimal)Math.Max(nudUoZoom.Minimum, Math.Min(nudUoZoom.Maximum, (decimal)p.Zoom));

            _uoElevationDeg = p.ElevationDeg;
            if (nudUoElev != null) nudUoElev.Value = (decimal)Math.Max(nudUoElev.Minimum, Math.Min(nudUoElev.Maximum, (decimal)p.ElevationDeg));

            _uoVertScale = p.VertScale;
            if (nudUoVert != null) nudUoVert.Value = (decimal)Math.Max(nudUoVert.Minimum, Math.Min(nudUoVert.Maximum, (decimal)p.VertScale));

            _uoBrightness = p.Brightness;
            if (nudUoBright != null) nudUoBright.Value = (decimal)Math.Max(nudUoBright.Minimum, Math.Min(nudUoBright.Maximum, (decimal)p.Brightness));

            _uoFeetOnLine = p.FeetOnLine;
            if (chkUoFeet != null) chkUoFeet.Checked = p.FeetOnLine;

            _uoOffX = p.OffX;
            if (nudUoOffX != null) nudUoOffX.Value = (decimal)Math.Max(nudUoOffX.Minimum, Math.Min(nudUoOffX.Maximum, (decimal)p.OffX));

            _uoOffY = p.OffY;
            if (nudUoOffY != null) nudUoOffY.Value = (decimal)Math.Max(nudUoOffY.Minimum, Math.Min(nudUoOffY.Maximum, (decimal)p.OffY));

            _uoYawOffsetDeg = p.YawOffsetDeg;
            if (nudUoYaw != null) nudUoYaw.Value = (decimal)Math.Max(nudUoYaw.Minimum, Math.Min(nudUoYaw.Maximum, (decimal)p.YawOffsetDeg));

            _uoGlobalZoom = p.GlobalZoom;
            if (nudUoGlobal != null) nudUoGlobal.Value = (decimal)Math.Max(nudUoGlobal.Minimum, Math.Min(nudUoGlobal.Maximum, (decimal)(p.GlobalZoom * 100f)));

            if (p.Direction >= 0 && cmbUoDir != null && p.Direction < cmbUoDir.Items.Count)
            {
                cmbUoDir.SelectedIndex = p.Direction;
            }

            if (p.Mode >= 0 && cmbUoMode != null && p.Mode < cmbUoMode.Items.Count)
            {
                cmbUoMode.SelectedIndex = p.Mode;
            }

            if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();
        }

        private UoCameraPreset CaptureCameraPreset()
        {
            return new UoCameraPreset
            {
                Zoom = _uoZoom,
                ElevationDeg = _uoElevationDeg,
                VertScale = _uoVertScale,
                Brightness = _uoBrightness,
                FeetOnLine = _uoFeetOnLine,
                OffX = _uoOffX,
                OffY = _uoOffY,
                YawOffsetDeg = _uoYawOffsetDeg,
                GlobalZoom = _uoGlobalZoom,
                Direction = _uoDirection,
                Mode = _uoSheet ? 1 : 0
            };
        }

        private void SaveCameraPresetDialog()
        {
            using var sfd = new SaveFileDialog
            {
                Title = "Sauvegarder les réglages de la caméra UO",
                Filter = "Caméra UO (*.uocam;*.json)|*.uocam;*.json|Tous les fichiers (*.*)|*.*",
                DefaultExt = "uocam",
                FileName = "camera_uo.uocam"
            };

            if (sfd.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                var preset = CaptureCameraPreset();
                string json = JsonSerializer.Serialize(preset, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(sfd.FileName, json, Encoding.UTF8);
                MessageBox.Show(this, $"Réglages caméra enregistrés :\n{Path.GetFileName(sfd.FileName)}",
                    "Sauvegarde Caméra", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Erreur lors de la sauvegarde : {ex.Message}", "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void LoadCameraPresetDialog()
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Charger les réglages de la caméra UO",
                Filter = "Caméra UO (*.uocam;*.json)|*.uocam;*.json|Tous les fichiers (*.*)|*.*",
                DefaultExt = "uocam"
            };

            if (ofd.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                string json = File.ReadAllText(ofd.FileName, Encoding.UTF8);
                var preset = JsonSerializer.Deserialize<UoCameraPreset>(json);
                if (preset == null) throw new InvalidOperationException("Fichier caméra invalide ou vide.");
                ApplyCameraPreset(preset);
                MessageBox.Show(this, $"Réglages caméra appliqués avec succès depuis :\n{Path.GetFileName(ofd.FileName)}",
                    "Chargement Caméra", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Erreur lors du chargement : {ex.Message}", "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private string ResolveResourcePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (File.Exists(path)) return path;

            string fn = Path.GetFileName(path);
            if (string.IsNullOrEmpty(fn)) return null;

            if (!string.IsNullOrEmpty(BasePath) && Directory.Exists(BasePath))
            {
                string direct = Path.Combine(BasePath, fn);
                if (File.Exists(direct)) return direct;

                string others = Path.Combine(BasePath, "Others", fn);
                if (File.Exists(others)) return others;

                string equips = Path.Combine(BasePath, "Equipments", fn);
                if (File.Exists(equips)) return equips;

                string monsters = Path.Combine(BasePath, "Monsters", fn);
                if (File.Exists(monsters)) return monsters;

                try
                {
                    var found = Directory.GetFiles(BasePath, fn, SearchOption.AllDirectories);
                    if (found != null && found.Length > 0) return found[0];
                }
                catch { }
            }
            return null;
        }

        private void SaveCharacterPresetDialog()
        {
            using var sfd = new SaveFileDialog
            {
                Title = "Sauvegarder le personnage multipartie",
                Filter = "Préréglage Personnage (*.charpreset;*.json)|*.charpreset;*.json|Tous les fichiers (*.*)|*.*",
                DefaultExt = "charpreset",
                FileName = $"perso_{(_charGender == 2 ? "female" : (_charGender == 1 ? "male" : "custom"))}.charpreset"
            };

            if (sfd.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                var preset = new CharacterPreset
                {
                    Name = Path.GetFileNameWithoutExtension(sfd.FileName),
                    Gender = _charGender,
                    BaseModelPath = _currentModelPath,
                    SkinBase = _skinOverride
                };

                if (cbCharFace?.SelectedItem is PartItem pFace) preset.FacePath = pFace.Path;
                if (cbCharHair?.SelectedItem is PartItem pHair) preset.HairPath = pHair.Path;
                if (cbCharBeard?.SelectedItem is PartItem pBeard) preset.BeardPath = pBeard.Path;
                if (cbCharTorso?.SelectedItem is PartItem pTorso) preset.TorsoPath = pTorso.Path;

                // Save attached accessories / equipment meshes
                foreach (var lm in _loadedMeshes)
                {
                    if (lm.Hidden) continue;
                    // Skip base human slot parts (they are restored via slot paths)
                    if (!string.IsNullOrEmpty(lm.CharSlot))
                    {
                        string s = lm.CharSlot.ToLowerInvariant();
                        if (s == "face" || s == "hair" || s == "beard" || s == "torso") continue;
                    }

                    var p = new CharacterPresetPart
                    {
                        Name = lm.Name,
                        SourcePath = lm.SourcePath,
                        CharSlot = lm.CharSlot,
                        AttachBoneName = lm.AttachBoneName,
                        AttachInBindSpace = lm.AttachInBindSpace,
                        MirrorX = lm.MirrorX,
                        MirrorY = lm.MirrorY,
                        MirrorZ = lm.MirrorZ,
                        MirrorArms = lm.MirrorArms,
                        AttachRotX = lm.AttachRotation.X,
                        AttachRotY = lm.AttachRotation.Y,
                        AttachRotZ = lm.AttachRotation.Z,
                        AttachOffX = lm.AttachOffset.X,
                        AttachOffY = lm.AttachOffset.Y,
                        AttachOffZ = lm.AttachOffset.Z,
                        PartScale = lm.PartScale,
                        Thickness = lm.Thickness,
                        CutoutRole = (int)lm.CutoutRole,
                        HueId = lm.HueId,
                        PartialHue = lm.PartialHue,
                        Desaturate = lm.Desaturate,
                        MountOffX = lm.MountOffset.X,
                        MountOffY = lm.MountOffset.Y,
                        MountOffZ = lm.MountOffset.Z,
                        MountScale = lm.MountScale
                    };
                    preset.Equipment.Add(p);
                }

                string json = JsonSerializer.Serialize(preset, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(sfd.FileName, json, Encoding.UTF8);
                MessageBox.Show(this, $"Personnage sauvegardé avec succès !\n{preset.Equipment.Count} équipements/accessoires inclus.",
                    "Sauvegarde personnage", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Erreur lors de la sauvegarde : {ex.Message}", "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void LoadCharacterPresetDialog()
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Charger un personnage multipartie",
                Filter = "Préréglage Personnage (*.charpreset;*.json)|*.charpreset;*.json|Tous les fichiers (*.*)|*.*",
                DefaultExt = "charpreset"
            };

            if (ofd.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                string json = File.ReadAllText(ofd.FileName, Encoding.UTF8);
                var preset = JsonSerializer.Deserialize<CharacterPreset>(json);
                if (preset == null) throw new InvalidOperationException("Fichier personnage invalide ou vide.");

                // 1) Load base model
                string basePath = ResolveResourcePath(preset.BaseModelPath);
                if (basePath == null && preset.Gender > 0)
                {
                    string targetGrn = preset.Gender == 2 ? "H_Female.grn" : "H_Male.grn";
                    basePath = ResolveResourcePath(targetGrn);
                }

                if (!string.IsNullOrEmpty(basePath) && File.Exists(basePath))
                {
                    LoadModel(basePath);
                }

                // 2) Restore slots
                if (!string.IsNullOrEmpty(preset.FacePath))
                {
                    string p = ResolveResourcePath(preset.FacePath);
                    if (p != null) SetCharacterPart("face", p);
                }
                if (!string.IsNullOrEmpty(preset.HairPath))
                {
                    string p = ResolveResourcePath(preset.HairPath);
                    if (p != null) SetCharacterPart("hair", p);
                }
                if (!string.IsNullOrEmpty(preset.BeardPath))
                {
                    string p = ResolveResourcePath(preset.BeardPath);
                    if (p != null) SetCharacterPart("beard", p);
                }
                if (!string.IsNullOrEmpty(preset.TorsoPath))
                {
                    string p = ResolveResourcePath(preset.TorsoPath);
                    if (p != null) SetCharacterPart("torso", p);
                }
                if (!string.IsNullOrEmpty(preset.SkinBase))
                {
                    ApplySkin(preset.SkinBase);
                }

                // Refresh slot combos
                PreselectSlot(cbCharFace, "face");
                PreselectSlot(cbCharHair, "hair");
                PreselectSlot(cbCharBeard, "beard");
                PreselectSlot(cbCharTorso, "torso");
                if (cbCharSkin != null && !string.IsNullOrEmpty(preset.SkinBase))
                {
                    for (int i = 0; i < cbCharSkin.Items.Count; i++)
                    {
                        if (cbCharSkin.Items[i] is SkinItem si && string.Equals(si.Base, preset.SkinBase, StringComparison.OrdinalIgnoreCase))
                        {
                            cbCharSkin.SelectedIndex = i;
                            break;
                        }
                    }
                }

                // 3) Restore equipment / attached meshes
                int equipLoaded = 0;
                if (preset.Equipment != null)
                {
                    foreach (var ep in preset.Equipment)
                    {
                        string epPath = ResolveResourcePath(ep.SourcePath);
                        if (string.IsNullOrEmpty(epPath) || !File.Exists(epPath)) continue;

                        int startIdx = _loadedMeshes.Count;
                        AddToScene(epPath, (MeshCutoutRole)ep.CutoutRole);

                        for (int k = startIdx; k < _loadedMeshes.Count; k++)
                        {
                            var lm = _loadedMeshes[k];
                            lm.AttachBoneName = ep.AttachBoneName;
                            lm.AttachInBindSpace = ep.AttachInBindSpace;
                            lm.MirrorX = ep.MirrorX;
                            lm.MirrorY = ep.MirrorY;
                            lm.MirrorZ = ep.MirrorZ;
                            lm.MirrorArms = ep.MirrorArms;
                            lm.AttachRotation = new Vector3(ep.AttachRotX, ep.AttachRotY, ep.AttachRotZ);
                            lm.AttachOffset = new Vector3(ep.AttachOffX, ep.AttachOffY, ep.AttachOffZ);
                            lm.PartScale = ep.PartScale;
                            lm.Thickness = ep.Thickness;
                            lm.CutoutRole = (MeshCutoutRole)ep.CutoutRole;
                            lm.HueId = ep.HueId;
                            lm.PartialHue = ep.PartialHue;
                            lm.Desaturate = ep.Desaturate;
                            if (ep.HueId > 0 || ep.Desaturate)
                            {
                                ApplyHueToMesh(lm, ep.HueId, ep.PartialHue, ep.Desaturate);
                            }
                            lm.MountOffset = new Vector3(ep.MountOffX, ep.MountOffY, ep.MountOffZ);
                            lm.MountScale = ep.MountScale > 0 ? ep.MountScale : 1f;
                        }
                        equipLoaded++;
                    }
                }

                RefreshLoadedList();
                PopulateBoneCombo();
                if (_currentAnimation != null) EnsureAnimationBindings(_currentAnimation);
                ApplyAnimationPose(_currentTime);
                ComputeReferenceBounds();
                RefreshVertexBuffers(true);
                UpdateModelInfo();
                if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();

                MessageBox.Show(this, $"Personnage chargé avec succès !\n{equipLoaded} pièces d'équipement attachées.",
                    "Chargement Personnage", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Erreur lors du chargement : {ex.Message}", "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ResetCharacterToDefault()
        {
            try
            {
                string targetPath = null;
                if (_charGender == 1) targetPath = ResolveResourcePath("H_Male.grn");
                else if (_charGender == 2) targetPath = ResolveResourcePath("H_Female.grn");
                else if (!string.IsNullOrEmpty(_currentModelPath)) targetPath = _currentModelPath;

                if (!string.IsNullOrEmpty(targetPath) && File.Exists(targetPath))
                {
                    LoadModel(targetPath);
                    MessageBox.Show(this, "Personnage réinitialisé à l'état par défaut.", "Réinitialisation", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show(this, "Impossible de trouver le modèle de base pour réinitialiser.", "Réinitialisation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Erreur lors de la réinitialisation : {ex.Message}", "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ExportUnifiedCharModelDialog()
        {
            if (_loadedMeshes == null || _loadedMeshes.Count == 0)
            {
                MessageBox.Show(this, "Aucun mesh chargé à exporter.", "Export .char", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            using var sfd = new SaveFileDialog
            {
                Title = "Exporter le personnage en modèle unifié .char",
                Filter = "Modèle UO unifié (*.char)|*.char|Tous les fichiers (*.*)|*.*",
                DefaultExt = "char",
                FileName = $"{(_charGender == 2 ? "H_Female" : (_charGender == 1 ? "H_Male" : "Character"))}_assembled.char"
            };

            if (sfd.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                var model = new GrannyModelFile
                {
                    Name = Path.GetFileNameWithoutExtension(sfd.FileName)
                };

                // Find skeleton from host or full humanoid
                LoadedMesh skeletonHost = _loadedMeshes.Find(m => m.Bones != null && m.Bones.Count >= 20)
                    ?? _loadedMeshes.Find(m => m.Bones != null && m.Bones.Count > 0);

                if (skeletonHost != null && skeletonHost.Bones != null)
                {
                    model.Bones = new List<GrannyBone>(skeletonHost.Bones);
                }

                // Add visible meshes as parts
                int partsCount = 0;
                foreach (var lm in _loadedMeshes)
                {
                    if (lm.Hidden) continue;
                    if (lm.BaseVertices == null || lm.BaseVertices.Length == 0) continue;

                    var part = new GrannyModelPart
                    {
                        Name = lm.Name,
                        MaterialName = !string.IsNullOrEmpty(lm.TextureName) ? lm.TextureName : "DefaultMaterial",
                        TextureName = lm.TextureName,
                        Color = lm.MeshColor,
                        Vertices = lm.BaseVertices,
                        Normals = lm.BaseVertexNormals ?? Array.Empty<Vector3>(),
                        Indices = lm.Indices,
                        Skin = lm.Skin ?? Array.Empty<VertexInfluence[]>(),
                        AttachBoneName = lm.AttachBoneName ?? string.Empty,
                        AttachInBindSpace = lm.AttachInBindSpace,
                        CutoutRole = lm.CutoutRole
                    };

                    if (lm.CornerUVs != null && lm.CornerVertex != null && lm.CornerVertex.Length > 0)
                    {
                        var uvs = new Vector2[lm.BaseVertices.Length];
                        for (int c = 0; c < lm.CornerVertex.Length && c < lm.CornerUVs.Length; c++)
                        {
                            int vIdx = lm.CornerVertex[c];
                            if (vIdx >= 0 && vIdx < uvs.Length)
                                uvs[vIdx] = lm.CornerUVs[c];
                        }
                        part.UVs = uvs;
                    }

                    model.Parts.Add(part);
                    partsCount++;
                }

                if (GrannyModelFile.Save(model, sfd.FileName))
                {
                    MessageBox.Show(this, $"Modèle .char exporté avec succès !\n{partsCount} parties et {model.Bones?.Count ?? 0} os enregistrés dans :\n{Path.GetFileName(sfd.FileName)}",
                        "Export .char unifié", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    MessageBox.Show(this, "Échec de l'enregistrement du fichier .char.", "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Erreur lors de l'export .char : {ex.Message}", "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    public class UoCameraPreset
    {
        public float Zoom { get; set; } = 0.27f;
        public float ElevationDeg { get; set; } = 26.565f;
        public float VertScale { get; set; } = 1.0f;
        public float Brightness { get; set; } = 0.8f;
        public bool FeetOnLine { get; set; } = true;
        public float OffX { get; set; } = 0f;
        public float OffY { get; set; } = -0.080f;
        public float YawOffsetDeg { get; set; } = 180f;
        public float GlobalZoom { get; set; } = 1.0f;
        public int Direction { get; set; } = 4;
        public int Mode { get; set; } = 0;
    }

    public class CharacterPresetPart
    {
        public string Name { get; set; } = string.Empty;
        public string SourcePath { get; set; } = string.Empty;
        public string CharSlot { get; set; }
        public string AttachBoneName { get; set; }
        public bool AttachInBindSpace { get; set; }
        public bool MirrorX { get; set; }
        public bool MirrorY { get; set; }
        public bool MirrorZ { get; set; }
        public bool MirrorArms { get; set; }
        public float AttachRotX { get; set; }
        public float AttachRotY { get; set; }
        public float AttachRotZ { get; set; }
        public float AttachOffX { get; set; }
        public float AttachOffY { get; set; }
        public float AttachOffZ { get; set; }
        public float PartScale { get; set; } = 1f;
        public float Thickness { get; set; } = 0f;
        public int CutoutRole { get; set; } = 1;
        public int HueId { get; set; } = 0;
        public bool PartialHue { get; set; } = true;
        public bool Desaturate { get; set; } = false;
        public float MountOffX { get; set; }
        public float MountOffY { get; set; }
        public float MountOffZ { get; set; }
        public float MountScale { get; set; } = 1f;
    }

    public class CharacterPreset
    {
        public string Name { get; set; } = "Character";
        public int Gender { get; set; } = 1;
        public string BaseModelPath { get; set; }
        public string FacePath { get; set; }
        public string HairPath { get; set; }
        public string BeardPath { get; set; }
        public string TorsoPath { get; set; }
        public string SkinBase { get; set; }
        public List<CharacterPresetPart> Equipment { get; set; } = new List<CharacterPresetPart>();
    }
}
