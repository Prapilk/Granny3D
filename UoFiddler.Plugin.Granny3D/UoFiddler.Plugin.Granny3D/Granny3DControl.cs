using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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
        public string SourcePath = string.Empty;
        public GrannyMesh SourceMesh;
        public List<GrannyBone> Bones = new List<GrannyBone>();
        public VertexInfluence[][] Skin;
        public Vector3[] BaseVertices;
        public int[] Indices;

        // De-indexed (per triangle corner) buffers. Corner c corresponds to Indices[c].
        public int[] CornerVertex;
        public float[] Interleaved;      // per corner: pos(3) + normal(3) + uv(2)
        public Vector3[] BaseCornerNormals;

        public int Vbo;
        public int Vao;
        public int TextureId = -1;
        public string TextureName = string.Empty;
        public bool HasTexture = false;
        public Vector3 MeshColor = new Vector3(0.72f, 0.66f, 0.55f);

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
        public Vector3 AttachRotation;  // degrees
        public Vector3 AttachOffset;    // world units

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
    }

    // Camera controller for orbit / pan / zoom.
    public class CameraController
    {
        public Vector3 Target = Vector3.Zero;
        public float Distance = 5f;
        public float Yaw = 0f;
        public float Pitch = -0.35f;
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

        private Vector3 EyePosition()
        {
            Vector3 offset = new Vector3(
                (float)(Distance * Math.Cos(Pitch) * Math.Sin(Yaw)),
                (float)(-Distance * Math.Cos(Pitch) * Math.Cos(Yaw)),
                (float)(Distance * Math.Sin(Pitch)));
            return Target + offset;
        }

        public void OnMouseWheel(MouseEventArgs e)
        {
            Distance = MathHelper.Clamp(Distance * (1f - e.Delta * 0.001f), MinDistance, MaxDistance);
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
        public const string BasePath = @"d:\Games\sphere\PROGRAMMES\3D_UO\Models\";
        private const string MapsPath = BasePath + "Maps";

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
        private ListBox lstLoaded;
        private GroupBox grpLoaded;
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
        private CheckBox cbMirrorX, cbMirrorY, cbMirrorZ;
        private NumericUpDown nudRotX, nudRotY, nudRotZ, nudOffX, nudOffY, nudOffZ, nudScale, nudScaleX, nudScaleY, nudScaleZ, nudThickness, nudClothGrav;
        private Button btnEquipReset, btnSkinWrap, btnSubdiv, btnClothBake;
        private CheckBox chkCloth;
        private NumericUpDown nudBakeFps, nudLoopBlend;
        private bool _suppressEquip;
        // UI - multipart character menu (Visage / Coiffure / Barbe / Torse)
        private GroupBox grpChar;
        private GroupBox grpCamera;
        private Label lblCharInfo;
        private ComboBox cbCharFace, cbCharHair, cbCharBeard, cbCharTorso, cbCharSkin;
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

            var pnlAnimTools = new Panel { Dock = DockStyle.Bottom, Height = 28 };
            btnImportAnim = new Button
            {
                Text = "Importer un FBX Mixamo…",
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(48, 48, 48),
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat
            };
            btnImportAnim.Click += BtnImportAnim_Click;
            pnlAnimTools.Controls.Add(btnImportAnim);

            grpAnimations.Controls.Add(tvAnims);
            grpAnimations.Controls.Add(pnlAnimTools);
            grpAnimations.Controls.Add(txtAnimFilter);
            grpAnimations.Controls.Add(lblAnimHeader);

            lblAnimations = new Label { Visible = false };

            // ---- Loaded meshes list + bone attach ----
            grpLoaded = new GroupBox
            {
                Text = "Meshes affiches (double-clic = retirer ; Ctrl/Shift = multi-sélection)",
                Dock = DockStyle.Bottom,
                Height = 210,
                ForeColor = Color.Gainsboro,
                Padding = new Padding(4)
            };
            lstLoaded = new ListBox
            {
                Dock = DockStyle.Fill,
                IntegralHeight = false,
                SelectionMode = SelectionMode.MultiExtended,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle
            };
            lstLoadedMenu = new ContextMenuStrip();
            lstLoadedMenu.Opening += LstLoadedMenu_Opening;
            lstLoaded.ContextMenuStrip = lstLoadedMenu;
            lstLoaded.MouseDown += (s, e) =>
            {
                if (e.Button == MouseButtons.Right)
                {
                    int idx = lstLoaded.IndexFromPoint(e.Location);
                    if (idx >= 0) lstLoaded.SelectedIndex = idx;
                }
            };
            lstLoaded.DoubleClick += LstLoaded_DoubleClick;
            lstLoaded.SelectedIndexChanged += LstLoaded_SelectedIndexChanged;

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
            grpLoaded.Controls.Add(lstLoaded);
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

            var animSplit = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal
            };
            animSplit.Panel1.Controls.Add(grpAnimations);
            animSplit.Panel2.Controls.Add(grpLoaded);

            var leftSplit = new SplitContainer
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
            var pnlRightBottom = new Panel { Dock = DockStyle.Bottom, Height = 620, Padding = new Padding(0, 6, 0, 0) };

            var grpPlayback = new GroupBox
            {
                Text = "Playback Controls",
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
            btnExportVd.Click += (s, e) => ExportVdDialog();

            // Cutout export: only the visible body is kept; armor/mount carve it away
            // (UO layered animation, body-only layer).
            btnExportCutout = new Button { Text = "Exporter cutout (corps)", Location = new Point(8, 226), Width = 110, Height = 28, BackColor = Color.FromArgb(78, 84, 96) };
            btnExportCutout.Click += (s, e) =>
            {
                foreach (var lm in _loadedMeshes)
                    Log($"cutout role: {lm.CutoutRole}  {lm.Name}");
                ExportVdDialog(BuildCutoutFilter());
            };

            // Inverse layer: export the accessory, carved by the body (+ mount).
            btnExportCutoutAcc = new Button { Text = "cutout accessoire", Location = new Point(126, 226), Width = 110, Height = 28, BackColor = Color.FromArgb(96, 84, 78) };
            btnExportCutoutAcc.Click += (s, e) =>
            {
                foreach (var lm in _loadedMeshes)
                    Log($"cutout role: {lm.CutoutRole}  {lm.Name}");
                ExportVdDialog(BuildAccessoryCutoutFilter());
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

            var grpInfo = new GroupBox
            {
                Text = "Model Info",
                Dock = DockStyle.Fill,
                ForeColor = Color.Gainsboro,
                Padding = new Padding(8)
            };
            lblModelInfo = new Label
            {
                Text = "Bones: 0 | Tracks: 0 | Meshes: 0",
                Dock = DockStyle.Fill,
                ForeColor = Color.Gainsboro
            };
            grpInfo.Controls.Add(lblModelInfo);

            // ---- Equipment controller: mirror / rotation / offset for an attached item ----
            var grpEquip = new GroupBox
            {
                Text = "Équipement (attache) — placement",
                Dock = DockStyle.Fill,
                ForeColor = Color.Gainsboro,
                Padding = new Padding(8)
            };
            grpEquip.Controls.Add(new Label { Text = "Sélectionnez un mesh attaché dans « Meshes affiches »", Location = new Point(8, 20), Width = 380, ForeColor = Color.Gray });

            NumericUpDown MakeNud(int x, int y, decimal min, decimal max, decimal inc, int dec)
            {
                var n = new NumericUpDown
                {
                    Location = new Point(x, y),
                    Width = 90,
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

            nudOffX = MakeNud(262, 112, -1, 1, 0.01m, 3);
            nudOffY = MakeNud(262, 138, -1, 1, 0.01m, 3);
            nudOffZ = MakeNud(262, 164, -1, 1, 0.01m, 3);
            grpEquip.Controls.Add(new Label { Text = "X", Location = new Point(242, 116), Width = 18, ForeColor = Color.Gainsboro });
            grpEquip.Controls.Add(new Label { Text = "Y", Location = new Point(242, 142), Width = 18, ForeColor = Color.Gainsboro });
            grpEquip.Controls.Add(new Label { Text = "Z", Location = new Point(242, 168), Width = 18, ForeColor = Color.Gainsboro });

            // Uniform scale + per-axis scale: inflate/deflate a piece so the body no
            // longer pokes through it (skinned armor) or resize a weapon.
            grpEquip.Controls.Add(new Label { Text = "Échelle (globale)", Location = new Point(8, 200), Width = 120, ForeColor = Color.Gainsboro });
            nudScale = MakeNud(140, 197, 0.2m, 5m, 0.01m, 3);

            grpEquip.Controls.Add(new Label { Text = "Échelle X/Y/Z", Location = new Point(8, 229), Width = 120, ForeColor = Color.Gainsboro });
            nudScaleX = MakeNud(100, 226, 0.2m, 5m, 0.01m, 3);
            nudScaleY = MakeNud(195, 226, 0.2m, 5m, 0.01m, 3);
            nudScaleZ = MakeNud(290, 226, 0.2m, 5m, 0.01m, 3);
            grpEquip.Controls.Add(new Label { Text = "X", Location = new Point(86, 229), Width = 14, ForeColor = Color.Gray });
            grpEquip.Controls.Add(new Label { Text = "Y", Location = new Point(181, 229), Width = 14, ForeColor = Color.Gray });
            grpEquip.Controls.Add(new Label { Text = "Z", Location = new Point(276, 229), Width = 14, ForeColor = Color.Gray });

            // Thickness: push the piece outward along its normals (anti poke-through).
            grpEquip.Controls.Add(new Label { Text = "Épaisseur (normale)", Location = new Point(8, 258), Width = 130, ForeColor = Color.Gainsboro });
            nudThickness = MakeNud(140, 255, 0m, 0.5m, 0.002m, 3);
            grpEquip.Controls.Add(new Label { Text = "0 = aucune", Location = new Point(240, 258), Width = 120, ForeColor = Color.Gray });

            // Real-time cloth simulation (capes / robes / skirts).
            chkCloth = new CheckBox { Text = "Tissu (simulation)", Location = new Point(8, 284), Width = 170, ForeColor = Color.Gainsboro };
            chkCloth.CheckedChanged += (s, e) => { if (!_suppressEquip) SetClothSelected(chkCloth.Checked); };
            grpEquip.Controls.Add(chkCloth);
            grpEquip.Controls.Add(new Label { Text = "Grav.", Location = new Point(190, 286), Width = 34, ForeColor = Color.Gray });
            nudClothGrav = MakeNud(230, 281, 0m, 30m, 0.5m, 2);
            nudClothGrav.Value = 9.8m;

            btnSkinWrap = new Button { Text = "Skin wrap (copier la peau du corps)", Location = new Point(30, 312), Width = 250, Height = 24, BackColor = Color.FromArgb(70, 70, 90), ForeColor = Color.Gainsboro, FlatStyle = FlatStyle.Flat };
            btnSkinWrap.Click += (s, e) => SkinWrapSelected();
            grpEquip.Controls.Add(btnSkinWrap);

            btnEquipReset = new Button { Text = "Réinitialiser le placement", Location = new Point(30, 342), Width = 250, Height = 24, BackColor = Color.FromArgb(60, 60, 62), ForeColor = Color.Gainsboro, FlatStyle = FlatStyle.Flat };
            btnEquipReset.Click += (s, e) => ResetEquipControls();
            grpEquip.Controls.Add(btnEquipReset);

            btnSubdiv = new Button { Text = "Subdiviser (1 → 4)  [plus de verts]", Location = new Point(30, 372), Width = 250, Height = 24, BackColor = Color.FromArgb(60, 70, 60), ForeColor = Color.Gainsboro, FlatStyle = FlatStyle.Flat };
            btnSubdiv.Click += (s, e) => SubdivideSelectedMesh();
            grpEquip.Controls.Add(btnSubdiv);

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

            grpEquip.Controls.AddRange(new Control[] { lblEquipTarget, cbMirrorX, cbMirrorY, cbMirrorZ, nudRotX, nudRotY, nudRotZ, nudOffX, nudOffY, nudOffZ, nudScale, nudScaleX, nudScaleY, nudScaleZ, nudThickness, chkCloth, nudClothGrav, btnSkinWrap, btnEquipReset, btnSubdiv, nudBakeFps, nudLoopBlend, btnClothBake, cmbCutoutRole });

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
            grpChar.Controls.AddRange(new Control[] { lblCharInfo, cbCharFace, cbCharHair, cbCharBeard, cbCharTorso, cbCharSkin });

            // ---- Camera / UO render options (docked right of the character menu) ----
            grpCamera = new GroupBox
            {
                Text = "Caméra (rendu UO)",
                Dock = DockStyle.Right,
                Width = 250,
                ForeColor = Color.Gainsboro,
                Padding = new Padding(8)
            };

            chkUoView = new CheckBox { Text = "Afficher le rendu UO", Location = new Point(8, 22), Width = 232, Checked = true, ForeColor = Color.Gainsboro };
            chkUoView.CheckedChanged += (s, e) => { _uoView = chkUoView.Checked; if (glControl != null) glControl.Invalidate(); };

            grpCamera.Controls.Add(new Label { Text = "Mode", Location = new Point(8, 52), Width = 232, ForeColor = Color.Gray });
            cmbUoMode = new ComboBox { Location = new Point(8, 70), Width = 232, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro };
            cmbUoMode.Items.AddRange(new object[] { "Vue UO (1 direction)", "Planche (5 vues)" });
            cmbUoMode.SelectedIndex = 0;
            cmbUoMode.SelectedIndexChanged += (s, e) => { _uoSheet = cmbUoMode.SelectedIndex == 1; if (glControl != null) glControl.Invalidate(); };

            grpCamera.Controls.Add(new Label { Text = "Direction", Location = new Point(8, 100), Width = 232, ForeColor = Color.Gray });
            cmbUoDir = new ComboBox { Location = new Point(8, 118), Width = 232, DropDownStyle = ComboBoxStyle.DropDownList, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro };
            cmbUoDir.Items.AddRange(new object[] { "N (0)", "NE (1)", "E (2)", "SE (3)", "S (4)" });
            cmbUoDir.SelectedIndex = 4;
            cmbUoDir.SelectedIndexChanged += (s, e) => { _uoDirection = cmbUoDir.SelectedIndex; if (glControl != null) glControl.Invalidate(); };

            grpCamera.Controls.Add(new Label { Text = "Zoom", Location = new Point(8, 150), Width = 100, ForeColor = Color.Gray });
            nudUoZoom = new NumericUpDown { Location = new Point(8, 168), Width = 100, Minimum = 0.1m, Maximum = 6m, Increment = 0.1m, DecimalPlaces = 2, Value = 0.27m, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            nudUoZoom.ValueChanged += (s, e) => { _uoZoom = (float)nudUoZoom.Value; if (glControl != null) glControl.Invalidate(); };

            grpCamera.Controls.Add(new Label { Text = "Inclinaison (°)", Location = new Point(120, 150), Width = 120, ForeColor = Color.Gray });
            nudUoElev = new NumericUpDown { Location = new Point(120, 168), Width = 100, Minimum = 5m, Maximum = 60m, Increment = 0.5m, DecimalPlaces = 2, Value = 26.565m, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            nudUoElev.ValueChanged += (s, e) => { _uoElevationDeg = (float)nudUoElev.Value; if (glControl != null) glControl.Invalidate(); };

            grpCamera.Controls.Add(new Label { Text = "Échelle verticale", Location = new Point(8, 200), Width = 120, ForeColor = Color.Gray });
            nudUoVert = new NumericUpDown { Location = new Point(8, 218), Width = 100, Minimum = 0.2m, Maximum = 3m, Increment = 0.05m, DecimalPlaces = 2, Value = 1m, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            nudUoVert.ValueChanged += (s, e) => { _uoVertScale = (float)nudUoVert.Value; if (glControl != null) glControl.Invalidate(); };

            grpCamera.Controls.Add(new Label { Text = "Luminosité", Location = new Point(120, 200), Width = 110, ForeColor = Color.Gray });
            nudUoBright = new NumericUpDown { Location = new Point(120, 218), Width = 100, Minimum = 0.2m, Maximum = 2m, Increment = 0.05m, DecimalPlaces = 2, Value = 0.8m, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            nudUoBright.ValueChanged += (s, e) => { _uoBrightness = (float)nudUoBright.Value; if (glControl != null) glControl.Invalidate(); };

            chkUoFeet = new CheckBox { Text = "Pieds sur la ligne", Location = new Point(8, 248), Width = 232, Checked = true, ForeColor = Color.Gainsboro };
            chkUoFeet.CheckedChanged += (s, e) => { _uoFeetOnLine = chkUoFeet.Checked; if (glControl != null) glControl.Invalidate(); };

            grpCamera.Controls.Add(new Label { Text = "Décalage X", Location = new Point(8, 276), Width = 110, ForeColor = Color.Gray });
            nudUoOffX = new NumericUpDown { Location = new Point(8, 294), Width = 100, Minimum = -2m, Maximum = 2m, Increment = 0.02m, DecimalPlaces = 3, Value = 0m, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            nudUoOffX.ValueChanged += (s, e) => { _uoOffX = (float)nudUoOffX.Value; if (glControl != null) glControl.Invalidate(); };

            grpCamera.Controls.Add(new Label { Text = "Décalage Y", Location = new Point(120, 276), Width = 110, ForeColor = Color.Gray });
            nudUoOffY = new NumericUpDown { Location = new Point(120, 294), Width = 100, Minimum = -2m, Maximum = 2m, Increment = 0.02m, DecimalPlaces = 3, Value = -0.080m, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            nudUoOffY.ValueChanged += (s, e) => { _uoOffY = (float)nudUoOffY.Value; if (glControl != null) glControl.Invalidate(); };

            // Reference overlay (UOFiddler animation, body 400 = c_man).
            chkUoRef = new CheckBox { Text = "Référence c_man (superposée)", Location = new Point(8, 328), Width = 232, Checked = false, ForeColor = Color.Gainsboro };
            chkUoRef.CheckedChanged += (s, e) => { _uoRefShow = chkUoRef.Checked; if (glControl != null) glControl.Invalidate(); };

            grpCamera.Controls.Add(new Label { Text = "Action", Location = new Point(8, 356), Width = 100, ForeColor = Color.Gray });
            nudUoRefAction = new NumericUpDown { Location = new Point(8, 374), Width = 100, Minimum = 0m, Maximum = 34m, Increment = 1m, DecimalPlaces = 0, Value = 0m, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            nudUoRefAction.ValueChanged += (s, e) => { _uoRefAction = (int)nudUoRefAction.Value; _uoRefKey = int.MinValue; if (glControl != null) glControl.Invalidate(); };

            grpCamera.Controls.Add(new Label { Text = "Frame", Location = new Point(120, 356), Width = 110, ForeColor = Color.Gray });
            nudUoRefFrame = new NumericUpDown { Location = new Point(120, 374), Width = 100, Minimum = 0m, Maximum = 40m, Increment = 1m, DecimalPlaces = 0, Value = 0m, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            nudUoRefFrame.ValueChanged += (s, e) => { _uoRefFrame = (int)nudUoRefFrame.Value; _uoRefKey = int.MinValue; if (glControl != null) glControl.Invalidate(); };

            grpCamera.Controls.Add(new Label { Text = "Éch. réf.", Location = new Point(8, 406), Width = 100, ForeColor = Color.Gray });
            nudUoRefScale = new NumericUpDown { Location = new Point(8, 424), Width = 100, Minimum = 0.05m, Maximum = 40m, Increment = 0.05m, DecimalPlaces = 2, Value = 1m, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            nudUoRefScale.ValueChanged += (s, e) => { _uoRefScale = (float)nudUoRefScale.Value; if (glControl != null) glControl.Invalidate(); };

            grpCamera.Controls.Add(new Label { Text = "Opacité %", Location = new Point(120, 406), Width = 110, ForeColor = Color.Gray });
            nudUoRefOpacity = new NumericUpDown { Location = new Point(120, 424), Width = 100, Minimum = 0m, Maximum = 100m, Increment = 5m, DecimalPlaces = 0, Value = 55m, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            nudUoRefOpacity.ValueChanged += (s, e) => { _uoRefOpacity = (float)nudUoRefOpacity.Value / 100f; if (glControl != null) glControl.Invalidate(); };

            grpCamera.Controls.Add(new Label { Text = "Rotation (°)", Location = new Point(8, 452), Width = 100, ForeColor = Color.Gray });
            nudUoYaw = new NumericUpDown { Location = new Point(8, 470), Width = 100, Minimum = -180m, Maximum = 180m, Increment = 5m, DecimalPlaces = 0, Value = 180m, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            nudUoYaw.ValueChanged += (s, e) => { _uoYawOffsetDeg = (float)nudUoYaw.Value; if (glControl != null) glControl.Invalidate(); };

            grpCamera.Controls.Add(new Label { Text = "Zoom global %", Location = new Point(120, 452), Width = 110, ForeColor = Color.Gray });
            nudUoGlobal = new NumericUpDown { Location = new Point(120, 470), Width = 100, Minimum = 10m, Maximum = 500m, Increment = 25m, DecimalPlaces = 0, Value = 100m, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            nudUoGlobal.ValueChanged += (s, e) => { _uoGlobalZoom = (float)nudUoGlobal.Value / 100f; if (glControl != null) glControl.Invalidate(); };

            grpCamera.Controls.AddRange(new Control[] { chkUoView, cmbUoMode, cmbUoDir, nudUoZoom, nudUoElev, nudUoVert, nudUoBright, chkUoFeet, nudUoOffX, nudUoOffY, chkUoRef, nudUoRefAction, nudUoRefFrame, nudUoRefScale, nudUoRefOpacity, nudUoYaw, nudUoGlobal });

            var pnlPlayRow = new Panel { Dock = DockStyle.Top, Height = 500 };
            pnlPlayRow.Controls.Add(grpEquip);      // Fill
            pnlPlayRow.Controls.Add(grpCamera);     // Right (docked first -> further right)
            pnlPlayRow.Controls.Add(grpChar);       // Right
            pnlPlayRow.Controls.Add(grpPlayback);   // Left

            pnlRightBottom.Controls.Add(grpInfo);     // Fill
            pnlRightBottom.Controls.Add(pnlPlayRow);  // Top

            pnlRight.Controls.Add(pnlRightBottom);
            pnlRight.Controls.Add(glControl);

            // Size the GL viewport explicitly above the bottom strip so it never
            // extends behind the controls (deterministic, independent of z-order).
            pnlRight.Resize += (s, e) => LayoutViewport(pnlRight, pnlRightBottom);
            LayoutViewport(pnlRight, pnlRightBottom);

            // ===== MAIN SPLIT: left tree | right viewport+playback =====
            var mainSplit = new SplitContainer
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

            // Initial splitter positions: models column doubled (360 -> 720), and the
            // animations / meshes zones get a resizable share of the left column.
            SetSplitterDistance(mainSplit, 720);
            SetSplitterDistance(leftSplit, 430);
            SetSplitterDistance(animSplit, 190);

            LoadCategoryTree();
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
                        && string.Equals(Path.GetFileName(info.Folder), "Others", StringComparison.OrdinalIgnoreCase);
                    bool looksCharacter = Path.GetFileName(info.MeshPath)
                        .StartsWith("Character_", StringComparison.OrdinalIgnoreCase);
                    bool isCustomCharacter = GrannyModelFile.IsModelFile(info.MeshPath) ||
                        info.MeshPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) ||
                        info.MeshPath.EndsWith(".char", StringComparison.OrdinalIgnoreCase);
                    if (isEquipCategory && !looksCharacter && !isCustomCharacter && _loadedMeshes.Count > 0)
                    {
                        AddToScene(info.MeshPath);
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

                tvMenu.Items.Add("Importer un Character Mixamo (FBX)...", null, BtnImportModel_Click);
                tvMenu.Items.Add("Importer un mesh (FBX / OBJ)...", null, BtnImportMesh_Click);
                tvMenu.Items.Add("Importer une animation FBX Mixamo...", null, BtnImportAnim_Click);
                tvMenu.Items.Add(new ToolStripSeparator());
                tvMenu.Items.Add("Rafraîchir la liste des modèles", null, (s, a) => RestoreModelsTree());
                return;
            }

            if (info.IsFolder)
            {
                tvMenu.Items.Add(node.IsExpanded ? "Réduire" : "Développer", null, (s, a) =>
                {
                    if (!info.Populated) AddCreatureNodes(node, info.Folder);
                    node.Toggle();
                });

                tvMenu.Items.Add(new ToolStripSeparator());
                var applyMixamoFolder = new ToolStripMenuItem("⚡ Convert/create rig to UO (Rig Studio)...", null, (s, a) =>
                {
                    ApplyMixamoForUo(info.Folder);
                });
                applyMixamoFolder.Font = new Font(tvMenu.Font, FontStyle.Bold);
                tvMenu.Items.Add(applyMixamoFolder);
            }
            else if (info.LeafPath != null)
            {
                tvMenu.Items.Add("Lire l'animation", null, (s, a) =>
                {
                    var parentInfo = node.Parent?.Tag as TreeNodeInfo;
                    EnsureMeshLoaded(parentInfo?.MeshPath);
                    LoadAnimation(info.LeafPath);
                });
                tvMenu.Items.Add("Charger le modèle de la créature", null, (s, a) =>
                {
                    var parentInfo = node.Parent?.Tag as TreeNodeInfo;
                    if (parentInfo?.MeshPath != null) LoadModel(parentInfo.MeshPath);
                });

                tvMenu.Items.Add(new ToolStripSeparator());
                var applyMixamoLeaf = new ToolStripMenuItem("⚡ Convert/create rig to UO (Rig Studio)...", null, (s, a) =>
                {
                    var parentInfo = node.Parent?.Tag as TreeNodeInfo;
                    ApplyMixamoForUo(parentInfo?.MeshPath);
                });
                applyMixamoLeaf.Font = new Font(tvMenu.Font, FontStyle.Bold);
                tvMenu.Items.Add(applyMixamoLeaf);
            }
            else
            {
                tvMenu.Items.Add("Charger (remplacer la scène)", null, (s, a) =>
                {
                    if (info.MeshPath != null) LoadModel(info.MeshPath);
                });
                tvMenu.Items.Add("Ajouter à la scène (équipement)", null, (s, a) =>
                {
                    if (info.MeshPath != null) AddToScene(info.MeshPath);
                });

                tvMenu.Items.Add(new ToolStripSeparator());
                var applyMixamoModel = new ToolStripMenuItem("⚡ Convert/create rig to UO (Adapter au squelette UO Biped)...", null, (s, a) =>
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

                tvMenu.Items.Add("Assigner / Modifier les textures...", null, (s, a) =>
                {
                    OpenTextureManagerForModel(info.MeshPath);
                });

                tvMenu.Items.Add("Renommer...", null, (s, a) =>
                {
                    RenameCustomModel(node, info);
                });

                bool isCustomMeshCat = (node.Parent?.Tag as TreeNodeInfo)?.IsCustomMeshCategory == true || info.IsCustomMeshCategory;
                bool isOthersCat = (node.Parent?.Tag as TreeNodeInfo)?.IsOthersCategory == true || info.IsOthersCategory;

                if (isOthersCat)
                {
                    tvMenu.Items.Add("Déplacer vers 'Meshs custom'", null, (s, a) =>
                    {
                        MoveCustomModel(node, info, Path.Combine(BasePath, "Meshs custom"), "Meshs custom");
                    });
                }
                else if (isCustomMeshCat)
                {
                    tvMenu.Items.Add("Déplacer vers 'Others'", null, (s, a) =>
                    {
                        MoveCustomModel(node, info, Path.Combine(BasePath, "Others"), "Others");
                    });
                }

                if (!string.IsNullOrEmpty(info.MeshPath) && info.MeshPath.EndsWith(GrannyModelFile.Extension, StringComparison.OrdinalIgnoreCase))
                {
                    tvMenu.Items.Add("Extraire des pièces vers 'Meshs custom'...", null, (s, a) =>
                    {
                        OpenExtractPartsDialog(info.MeshPath);
                    });
                }

                string delLabel = isCustomMeshCat
                    ? "Supprimer ce mesh"
                    : "Supprimer ce character";
                var deleteItem = new ToolStripMenuItem(delLabel, null, (s, a) =>
                {
                    DeleteCustomCharacter(node, info);
                });
                deleteItem.ForeColor = Color.Crimson;
                tvMenu.Items.Add(deleteItem);
            }

            tvMenu.Items.Add(new ToolStripSeparator());
            tvMenu.Items.Add("Retirer le dernier mesh", null, (s, a) =>
                RemoveMeshAt(_loadedMeshes.Count - 1));
            tvMenu.Items.Add("Vider la scène", null, (s, a) => ClearScene());
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

                if (string.IsNullOrEmpty(outFolder))
                    outFolder = Path.Combine(BasePath, "Others");

                // Pass BOTH sourceFile AND active _loadedMeshes so AutoRigDialog has the 3D model immediately
                using var dlg = new AutoRigDialog(sourceFile, BasePath, outFolder, _loadedMeshes);
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
                foreach (var part in model.Parts)
                {
                    if (part.Vertices.Length == 0 || part.Indices.Length == 0) continue;

                    var gmesh = new GrannyMesh();
                    gmesh.Vertices.AddRange(part.Vertices);
                    gmesh.Indices.AddRange(part.Indices);
                    if (part.UVs != null) gmesh.UVs.AddRange(part.UVs);
                    if (part.Skin != null) gmesh.Skin.AddRange(part.Skin);

                    var lm = new LoadedMesh
                    {
                        Name = string.IsNullOrEmpty(part.Name) ? Path.GetFileNameWithoutExtension(path) : part.Name,
                        SourcePath = path,
                        SourceMesh = gmesh,
                        Bones = new List<GrannyBone>(model.Bones),
                        BaseVertices = part.Vertices,
                        Indices = part.Indices,
                        Skin = (part.Skin != null && part.Skin.Length > 0) ? part.Skin : null,
                        MeshColor = part.Color,
                        CutoutRole = role
                    };

                    BuildCornerData(lm);
                    SetupMeshVBO(lm);
                    lm.TextureId = !string.IsNullOrEmpty(part.TextureName) ? TryLoadTexture(path, part.TextureName) : -1;
                    lm.HasTexture = lm.TextureId > 0;
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

                if (_currentAnimation != null)
                {
                    EnsureAnimationBindings(_currentAnimation);
                    ApplyAnimationPose(_currentTime);
                }
                else
                {
                    RefreshVertexBuffers(true);
                }

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

                var lm = new LoadedMesh
                {
                    Name = $"{Path.GetFileName(path)} ({_loadedMeshes.Count})",
                    SourcePath = path,
                    SourceMesh = mesh,
                    Bones = new List<GrannyBone>(parser.Bones),
                    BaseVertices = mesh.Vertices.ToArray(),
                    Indices = mesh.Indices.ToArray(),
                    Skin = mesh.Skin.Count > 0 ? mesh.Skin.ToArray() : null,
                    CutoutRole = role
                };

                BuildCornerData(lm);
                SetupMeshVBO(lm);
                lm.TextureId = TryLoadTexture(path, parser.TextureName);
                lm.HasTexture = lm.TextureId > 0;
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

            if (_currentAnimation != null)
            {
                EnsureAnimationBindings(_currentAnimation);
                ApplyAnimationPose(_currentTime);
            }
            else
            {
                RefreshVertexBuffers(true);
            }

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
                lm.TextureId = TryLoadTexture(path, texName);
                lm.HasTexture = lm.TextureId > 0;
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
                int tex = TryLoadTexture(lm.SourcePath, name);
                if (tex <= 0) continue;
                if (lm.TextureId > 0) GL.DeleteTexture(lm.TextureId);
                lm.TextureId = tex;
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

        private void TvAnims_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
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

            if (cat != null)
            {
                tvAnimMenu.Items.Add(node.IsExpanded ? "Réduire" : "Développer", null, (s, a) => node.Toggle());
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

            tvAnimMenu.Items.Add("Lire l'animation", null, (s, a) => PlayAnimationFile(animPath));
            tvAnimMenu.Items.Add(new ToolStripSeparator());
            var applyMixamoClip = new ToolStripMenuItem("⚡ Convert/create rig to UO (Importer une animation FBX)...", null, (s, a) => BtnImportAnim_Click(s, a));
            applyMixamoClip.Font = new Font(tvAnimMenu.Font, FontStyle.Bold);
            tvAnimMenu.Items.Add(applyMixamoClip);

            bool isCustomAnim = animPath.EndsWith(".anim", StringComparison.OrdinalIgnoreCase) ||
                                (node.Parent?.Tag as AnimCat)?.Pattern == "*.anim";

            if (isCustomAnim)
            {
                tvAnimMenu.Items.Add(new ToolStripSeparator());
                var deleteItem = new ToolStripMenuItem("Supprimer cette animation", null, (s, a) =>
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

        private void TvAnims_AfterSelect(object sender, TreeViewEventArgs e)
        {
            if (_suppressAnimSelect) return;
            if (e.Node?.Tag is string path)
                PlayAnimationFile(path);
        }

        // Import a Mixamo FBX: retarget it onto the UO Biped skeleton, write a
        // standalone ".anim" clip into the "Others" folder, then play it.
        private void BtnImportAnim_Click(object sender, EventArgs e)
        {
            try
            {
                using var ofd = new OpenFileDialog
                {
                    Title = "Importer une animation Mixamo (FBX)",
                    Filter = "FBX (*.fbx)|*.fbx|Tous les fichiers (*.*)|*.*"
                };
                if (ofd.ShowDialog() != DialogResult.OK) return;

                string refPath = Path.Combine(BasePath, "Humans", "H_Female_Lod2.grn");
                if (!File.Exists(refPath)) refPath = Path.Combine(BasePath, "Humans", "H_Male_Lod2.grn");
                if (!File.Exists(refPath))
                {
                    MessageBox.Show("Modèle de référence Biped introuvable (H_Female_Lod2.grn).");
                    return;
                }

                var refParser = new GrannyFormatParser();
                if (!refParser.Load(refPath) || refParser.Bones == null || refParser.Bones.Count == 0)
                {
                    MessageBox.Show("Impossible de charger le squelette de référence.");
                    return;
                }

                string clip = Path.GetFileNameWithoutExtension(ofd.FileName);
                if (!MixamoRetarget.TryImport(ofd.FileName, refParser.Bones, clip, out var anim, out string err) || anim == null)
                {
                    MessageBox.Show("Import Mixamo échoué : " + (err ?? "raison inconnue"));
                    return;
                }

                string outPath = Path.Combine(BasePath, "Others", clip + GrannyAnimFile.Extension);
                if (!GrannyAnimFile.Save(anim, outPath))
                {
                    MessageBox.Show("Écriture impossible : " + outPath);
                    return;
                }

                Log($"Import Mixamo OK: {Path.GetFileName(outPath)} tracks={anim.Tracks.Count} dur={anim.Duration:F2}s mapped={MixamoRetarget.MappedBoneCount(refParser.Bones)}");

                BuildAnimCategories();
                RevealAnimFile(outPath);
            }
            catch (Exception ex)
            {
                Log($"Import Mixamo error: {ex.Message}\n{ex}");
                MessageBox.Show("Erreur d'import : " + ex.Message);
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

                string meshName = Path.GetFileNameWithoutExtension(ofd.FileName);
                string outFolder = Path.Combine(BasePath, "Meshs custom");
                if (!Directory.Exists(outFolder)) Directory.CreateDirectory(outFolder);

                string refPath = Path.Combine(BasePath, "Humans", "H_Male_Lod2.grn");
                if (!File.Exists(refPath)) refPath = Path.Combine(BasePath, "Humans", "H_Female_Lod2.grn");

                if (!MixamoCharacterImport.TryImport(ofd.FileName, outFolder, refPath, out var model, out string err) || model == null)
                {
                    MessageBox.Show("Import du mesh échoué : " + (err ?? "raison inconnue"));
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
                foreach (TreeNode catNode in tvAnims.Nodes)
                {
                    var cat = catNode.Tag as AnimCat;
                    if (cat == null || cat.IsAll) continue;
                    if (!string.Equals(cat.Pattern, "*.anim", StringComparison.OrdinalIgnoreCase)) continue;

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
            PlayAnimationFile(path);
        }

        // Picking an animation loads the creature it belongs to (if needed) and plays it.
        private void PlayAnimationFile(string animPath)
        {
            try
            {
                if (string.IsNullOrEmpty(animPath) || !File.Exists(animPath)) return;

                string baseMesh = NaturalBaseMesh(animPath);
                if (!string.IsNullOrEmpty(baseMesh) && !File.Exists(baseMesh)) baseMesh = null;

                // Only auto-load a base mesh when the scene is empty. If a character
                // (and its equipment) is already displayed, play the animation on it
                // instead of resetting the scene.
                if (_loadedMeshes.Count == 0 && baseMesh != null)
                    LoadModel(baseMesh);

                LoadAnimation(animPath);

                if (lblAnimHeader != null)
                {
                    string b = Path.GetFileNameWithoutExtension(baseMesh ?? _currentModelPath ?? string.Empty);
                    lblAnimHeader.Text = $"{Path.GetFileNameWithoutExtension(animPath)}   ({b})";
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
            if (lstLoaded != null) lstLoaded.Items.Clear();
            _currentModelPath = path;

            // H_Male_Lod2 / H_Female_Lod2 only contain the 43-bone skeleton and a
            // 19-vertex placeholder box; the real body is assembled from parts.
            bool humanPlaceholder = IsHumanPlaceholder(path, out bool humanFemale);
            ResetCharMenu();

            foreach (var mesh in _modelParser.Meshes)
            {
                if (mesh.Vertices.Count == 0 || mesh.Indices.Count == 0) continue;

                var lm = new LoadedMesh
                {
                    Name = $"{Path.GetFileName(path)} ({_loadedMeshes.Count})",
                    SourcePath = path,
                    SourceMesh = mesh,
                    Bones = new List<GrannyBone>(_modelParser.Bones),
                    BaseVertices = mesh.Vertices.ToArray(),
                    Indices = mesh.Indices.ToArray(),
                    Skin = mesh.Skin.Count > 0 ? mesh.Skin.ToArray() : null
                };

                BuildCornerData(lm);
                SetupMeshVBO(lm);
                lm.TextureId = TryLoadTexture(path, _modelParser.TextureName);
                lm.HasTexture = lm.TextureId > 0;
                lm.Hidden = humanPlaceholder;   // keep the 43-bone skeleton as host, don't draw the box
                if (!lm.Hidden) TryAutoAttachUnskinned(lm);
                _loadedMeshes.Add(lm);
                if (lstLoaded != null) lstLoaded.Items.Add(lm.Name);
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
            ComputeReferenceBounds();
            RefreshVertexBuffers(true);
            _camera.Target = Vector3.Zero;
            _camera.Distance = 5f;
            _camera.Yaw = 0f;
            _camera.Pitch = -0.35f;
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
            if (lstLoaded != null) lstLoaded.Items.Clear();
            _currentModelPath = path;
            ResetCharMenu();

            foreach (var part in model.Parts)
            {
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

                var lm = new LoadedMesh
                {
                    Name = string.IsNullOrEmpty(part.Name) ? Path.GetFileNameWithoutExtension(path) : part.Name,
                    SourcePath = path,
                    SourceMesh = gmesh,
                    Bones = new List<GrannyBone>(model.Bones),
                    BaseVertices = part.Vertices,
                    Indices = part.Indices,
                    Skin = (part.Skin != null && part.Skin.Length > 0) ? part.Skin : null,
                    MeshColor = part.Color,
                    TextureName = part.TextureName ?? string.Empty
                };

                BuildCornerData(lm);
                SetupMeshVBO(lm);
                lm.TextureId = !string.IsNullOrEmpty(part.TextureName) ? TryLoadTexture(path, part.TextureName) : -1;
                lm.HasTexture = lm.TextureId > 0;
                _loadedMeshes.Add(lm);
                if (lstLoaded != null) lstLoaded.Items.Add(lm.Name);
                Log($"  built custom mesh vbo={lm.Vbo} vao={lm.Vao} verts={lm.BaseVertices.Length} corners={lm.Indices.Length} color={lm.MeshColor}");
            }

            RefreshLoadedList();
            PopulateBoneCombo();
            ComputeReferenceBounds();
            RefreshVertexBuffers(true);
            _camera.Target = Vector3.Zero;
            _camera.Distance = 5f;
            _camera.Yaw = 0f;
            _camera.Pitch = -0.35f;
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

            var uvs = lm.SourceMesh.UVs;
            var uvIdx = lm.SourceMesh.UVIndices;

            for (int c = 0; c < cornerCount; c++)
            {
                int vi = lm.Indices[c];
                lm.CornerVertex[c] = vi;

                float u = 0f, v = 0f;
                if (explicitCornerUVs != null && c < explicitCornerUVs.Length)
                {
                    u = explicitCornerUVs[c].X; v = explicitCornerUVs[c].Y;
                }
                else if (uvIdx != null && uvIdx.Count > 0 && uvs.Count > 0)
                {
                    int ui = (c < uvIdx.Count) ? uvIdx[c] : -1;
                    if (ui >= 0 && ui < uvs.Count) { u = uvs[ui].X; v = uvs[ui].Y; }
                }
                else if (uvs.Count == lm.BaseVertices.Length && vi >= 0 && vi < uvs.Count)
                {
                    u = uvs[vi].X; v = uvs[vi].Y;
                }

                lm.Interleaved[c * 8 + 6] = u;
                lm.Interleaved[c * 8 + 7] = v;
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
                foreach (var v in lm.BaseVertices) c += v;
                lm.BindCentroid = c / lm.BaseVertices.Length;
                lm.HasBindCentroid = true;
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
        private void SubdivideSelectedMesh()
        {
            int si = lstLoaded?.SelectedIndex ?? -1;
            if (si < 0 || si >= _loadedMeshes.Count) { Log("Subdiviser : aucun mesh sélectionné."); return; }
            var lm = _loadedMeshes[si];
            if (lm.BaseVertices == null || lm.Indices == null || lm.CornerVertex == null || lm.Interleaved == null) return;

            int V = lm.BaseVertices.Length;
            int cc = lm.CornerVertex.Length;
            var oldV = lm.BaseVertices;
            var oldSkin = lm.Skin;

            int oldCorners = lm.Indices.Length;
            var cornerUV = new Vector2[oldCorners];
            for (int c = 0; c < oldCorners; c++)
                cornerUV[c] = new Vector2(lm.Interleaved[c * 8 + 6], lm.Interleaved[c * 8 + 7]);

            var newV = new List<Vector3>(V * 4);
            for (int v = 0; v < V; v++) newV.Add(oldV[v]);
            var newSkin = new List<VertexInfluence[]>(V * 4);
            for (int v = 0; v < V; v++) newSkin.Add(oldSkin != null && v < oldSkin.Length ? oldSkin[v] : null);

            var midMap = new Dictionary<long, int>();
            var outIdx = new List<int>(cc * 4);
            var outUV = new List<Vector2>(cc * 4);

            for (int t = 0; t + 2 < cc; t += 3)
            {
                int v0 = lm.CornerVertex[t], v1 = lm.CornerVertex[t + 1], v2 = lm.CornerVertex[t + 2];
                Vector2 u0 = cornerUV[t], u1 = cornerUV[t + 1], u2 = cornerUV[t + 2];

                int e01 = GetMidVertex(midMap, newV, newSkin, oldV, oldSkin, v0, v1);
                int e12 = GetMidVertex(midMap, newV, newSkin, oldV, oldSkin, v1, v2);
                int e20 = GetMidVertex(midMap, newV, newSkin, oldV, oldSkin, v2, v0);
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
            lm.Indices = outIdx.ToArray();
            BuildCornerData(lm, outUV.ToArray());
            SetupMeshVBO(lm);
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
                                        Vector3[] oldV, VertexInfluence[][] oldSkin, int ia, int ib)
        {
            if (ia < 0 || ib < 0 || ia >= oldV.Length || ib >= oldV.Length) return ia >= 0 ? ia : ib;
            long key = ia < ib ? ((long)ia << 32) | (uint)ib : ((long)ib << 32) | (uint)ia;
            if (map.TryGetValue(key, out int id)) return id;
            int nid = newV.Count;
            newV.Add((oldV[ia] + oldV[ib]) * 0.5f);
            newSkin.Add(AverageSkin(oldSkin, ia, ib));
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
            if (lm.TextureId > 0) { GL.DeleteTexture(lm.TextureId); lm.TextureId = -1; }
        }

        private void LstLoaded_DoubleClick(object sender, EventArgs e)
        {
            try
            {
                RemoveMeshAt(lstLoaded.SelectedIndex);
            }
            catch (Exception ex)
            {
                Log($"LstLoaded_DoubleClick error: {ex.Message}\n{ex}");
            }
        }

        // Fill the bone combo from the base (host) skeleton so the user can pick a
        // bone to attach a weapon/armor piece to.
        private void PopulateBoneCombo()
        {
            if (cbBones == null) return;
            string prev = cbBones.Text;
            List<GrannyBone> bones = null;
            foreach (var lm in _loadedMeshes)
            {
                if (lm.Bones != null && lm.Bones.Count > 0 &&
                    (bones == null || lm.Bones.Count > bones.Count))
                    bones = lm.Bones;
            }

            cbBones.BeginUpdate();
            cbBones.Items.Clear();
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
                string def = null;
                foreach (var pref in new[] { "CP_Grasp_Rhand", "CP_Grasp_Lhand", "Bip01 R Hand", "Bip01 L Hand", "Bip01 Head", "Bip01 Spine" })
                {
                    foreach (var it in cbBones.Items)
                        if (string.Equals(it.ToString(), pref, StringComparison.OrdinalIgnoreCase)) { def = it.ToString(); break; }
                    if (def != null) break;
                }
                if (cbBones.Items.Count > 0)
                    SelectBoneByName(def ?? cbBones.Items[0].ToString());
                else
                    cbBones.SelectedIndex = -1;
            }
        }

        // Select a bone in the (DropDownList) combo by name.
        private void SelectBoneByName(string name)
        {
            if (cbBones == null || string.IsNullOrEmpty(name)) return;
            for (int i = 0; i < cbBones.Items.Count; i++)
            {
                if (string.Equals(cbBones.Items[i].ToString(), name, StringComparison.OrdinalIgnoreCase))
                {
                    cbBones.SelectedIndex = i;
                    return;
                }
            }
        }

        private void RefreshLoadedList()
        {
            if (lstLoaded == null) return;
            int sel = lstLoaded.SelectedIndex;
            lstLoaded.BeginUpdate();
            lstLoaded.Items.Clear();
            foreach (var lm in _loadedMeshes)
                lstLoaded.Items.Add(string.IsNullOrEmpty(lm.AttachBoneName)
                    ? lm.Name
                    : $"{lm.Name}  →  {lm.AttachBoneName}");
            lstLoaded.EndUpdate();
            if (sel >= 0 && sel < lstLoaded.Items.Count) lstLoaded.SelectedIndex = sel;
        }

        private void LstLoaded_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (lstLoaded != null)
            {
                int i = lstLoaded.SelectedIndex;
                if (i >= 0 && i < _loadedMeshes.Count)
                {
                    var lm = _loadedMeshes[i];
                    if (!string.IsNullOrEmpty(lm.AttachBoneName) && cbBones != null)
                        SelectBoneByName(lm.AttachBoneName);
                }
            }
            LoadEquipControlsFromMesh();
        }

        // Indices of the meshes currently selected in the "Meshes affiches" list
        // (supports Ctrl/Shift multi-selection for batch placement tweaks).
        private List<int> GetSelectedMeshIndices()
        {
            var list = new List<int>();
            if (lstLoaded == null) return list;
            foreach (int i in lstLoaded.SelectedIndices)
                if (i >= 0 && i < _loadedMeshes.Count) list.Add(i);
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
            lm.AttachRotation = new Vector3((float)nudRotX.Value, (float)nudRotY.Value, (float)nudRotZ.Value);
            lm.AttachOffset = new Vector3((float)nudOffX.Value, (float)nudOffY.Value, (float)nudOffZ.Value);
            lm.PartScale = nudScale != null ? (float)nudScale.Value : 1f;
            lm.PartScaleVec = new Vector3(
                nudScaleX != null ? (float)nudScaleX.Value : 1f,
                nudScaleY != null ? (float)nudScaleY.Value : 1f,
                nudScaleZ != null ? (float)nudScaleZ.Value : 1f);
            lm.Thickness = nudThickness != null ? (float)nudThickness.Value : 0f;
            if (lm.IsCloth && nudClothGrav != null)
            {
                float g = (float)nudClothGrav.Value;
                if (lm.ClothBake != null && Math.Abs(g - lm.ClothBakeGravity) > 1e-4f) ClearClothBake(lm);
                lm.ClothGravity = g;
            }
        }

        private void ResetEquipControls()
        {
            _suppressEquip = true;
            try
            {
                cbMirrorX.Checked = cbMirrorY.Checked = cbMirrorZ.Checked = false;
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

        // Reflect the selected mesh's stored placement tweak in the controller widgets.
        private void LoadEquipControlsFromMesh()
        {
            if (nudRotX == null) return;
            _suppressEquip = true;
            try
            {
                int i = lstLoaded?.SelectedIndex ?? -1;
                bool ok = i >= 0 && i < _loadedMeshes.Count;
                var lm = ok ? _loadedMeshes[i] : null;

                // Multiple meshes selected: keep the current widget values (do not
                // clobber them from a single mesh) and show a batch target label.
                int nSel = lstLoaded?.SelectedIndices.Count ?? 0;
                if (nSel > 1)
                {
                    if (lblEquipTarget != null)
                        lblEquipTarget.Text = $"Cible : {nSel} meshes sélectionnés (édition par lot)";
                    return;
                }

                if (lblEquipTarget != null)
                    lblEquipTarget.Text = ok
                        ? $"Cible : {lm.Name}" + (string.IsNullOrEmpty(lm.AttachBoneName) ? "  (non attaché)" : $"  → {lm.AttachBoneName}")
                        : "Cible : (aucun)";

                cbMirrorX.Checked = ok && lm.MirrorX;
                cbMirrorY.Checked = ok && lm.MirrorY;
                cbMirrorZ.Checked = ok && lm.MirrorZ;
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

        // Skin-wrap: bind the selected accessory to the SURFACE of the body meshes by
        // copying, for each accessory vertex, the skin weights of the nearest body
        // vertex (in bind space). The accessory then deforms exactly like the body,
        // regardless of its own (often mis-named/mis-bound) skeleton.
        private void SkinWrapSelected()
        {
            int i = lstLoaded?.SelectedIndex ?? -1;
            if (i < 0 || i >= _loadedMeshes.Count)
            {
                Log("Skin wrap : aucun mesh sélectionné.");
                return;
            }
            var target = _loadedMeshes[i];
            if (target.BaseVertices == null || target.BaseVertices.Length == 0)
            {
                Log("Skin wrap : le mesh sélectionné n'a pas de sommets.");
                return;
            }

            if (target.WrapSource != null)
            {
                Log($"Skin wrap : '{target.Name}' détaché de '{target.WrapSource.Name}'.");
                target.WrapSource = null;
                target.WrapSkin = null;
                ApplyAnimationPose(_currentTime);
                if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();
                return;
            }

            // Candidate sources: other visible, skinned meshes with bones & weights.
            var sources = new List<LoadedMesh>();
            for (int j = 0; j < _loadedMeshes.Count; j++)
            {
                if (j == i) continue;
                var m = _loadedMeshes[j];
                if (m.Hidden) continue;
                if (m.WrapSource != null) continue;                 // don't chain wraps
                if (m.Skin == null || m.Skin.Length == 0) continue;
                if (m.Bones == null || m.Bones.Count == 0) continue;
                if (m.BaseVertices == null || m.BaseVertices.Length == 0) continue;
                sources.Add(m);
            }
            if (sources.Count == 0)
            {
                Log("Skin wrap : aucun mesh de corps skinné disponible comme source.");
                return;
            }

            int n = target.BaseVertices.Length;

            // Pass 1: global nearest over all sources -> hits per source.
            var hits = new int[sources.Count];
            var nearSrc = new int[n];
            var nearIdx = new int[n];
            for (int v = 0; v < n; v++)
            {
                Vector3 p = target.BaseVertices[v];
                float best = float.MaxValue;
                int bs = -1, bi = -1;
                for (int s = 0; s < sources.Count; s++)
                {
                    var sv = sources[s].BaseVertices;
                    for (int k = 0; k < sv.Length; k++)
                    {
                        float d = Vector3.DistanceSquared(p, sv[k]);
                        if (d < best) { best = d; bs = s; bi = k; }
                    }
                }
                nearSrc[v] = bs; nearIdx[v] = bi;
                if (bs >= 0) hits[bs]++;
            }

            int win = -1, winHits = 0;
            for (int s = 0; s < sources.Count; s++)
                if (hits[s] > winHits) { winHits = hits[s]; win = s; }
            if (win < 0 || winHits == 0)
            {
                Log("Skin wrap : aucun sommet du corps assez proche.");
                return;
            }

            var source = sources[win];
            var wrap = new VertexInfluence[n][];
            var srcVerts = source.BaseVertices;
            int svN = srcVerts.Length;
            const int K = 4;   // neighbours blended per vertex
            var acc = new Dictionary<int, float>();
            var tmpBone = new int[K];
            var tmpDist = new float[K];
            int wrapped = 0;
            for (int v = 0; v < n; v++)
            {
                Vector3 p = target.BaseVertices[v];

                // K nearest body vertices within the winning source (small sorted list).
                for (int k = 0; k < K; k++) { tmpBone[k] = -1; tmpDist[k] = float.MaxValue; }
                for (int k = 0; k < svN; k++)
                {
                    float d = Vector3.DistanceSquared(p, srcVerts[k]);
                    if (d < tmpDist[K - 1])
                    {
                        int pos = K - 1;
                        while (pos > 0 && d < tmpDist[pos - 1])
                        {
                            tmpDist[pos] = tmpDist[pos - 1];
                            tmpBone[pos] = tmpBone[pos - 1];
                            pos--;
                        }
                        tmpDist[pos] = d;
                        tmpBone[pos] = k;
                    }
                }

                // Blend the K neighbours' influences, weighted by inverse distance, so the
                // accessory follows the body SURFACE (not just one vertex) -> no joint
                // divergence.
                acc.Clear();
                for (int k = 0; k < K; k++)
                {
                    int idx = tmpBone[k];
                    if (idx < 0 || idx >= source.Skin.Length) continue;
                    var inf = source.Skin[idx];
                    if (inf == null || inf.Length == 0) continue;
                    float wdist = 1f / (tmpDist[k] + 1e-6f);
                    for (int m = 0; m < inf.Length; m++)
                    {
                        float cur;
                        acc[inf[m].BoneId] = acc.TryGetValue(inf[m].BoneId, out cur) ? cur + inf[m].Weight * wdist : inf[m].Weight * wdist;
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
                wrapped++;
            }

            target.WrapSource = source;
            target.WrapSkin = wrap;
            target.AttachMatrix = Matrix4.Identity;
            target.HasAttachMatrix = false;

            Log($"Skin wrap : '{target.Name}' ← '{source.Name}' ({wrapped}/{n} sommets liés, lissé K={K}, {source.Bones.Count} os).");
            ApplyAnimationPose(_currentTime);
            if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();
        }

        // Attach the mesh selected in the list to the chosen bone (or detach if it is
        // already attached to that bone). An attached mesh rides the bone and follows
        // the current animation.
        private void AttachSelectedMeshToBone()
        {
            try
            {
                int i = lstLoaded?.SelectedIndex ?? -1;
                if (i < 0 || i >= _loadedMeshes.Count) { Log("Attach: no mesh selected"); return; }
                var lm = _loadedMeshes[i];
                string bone = cbBones?.Text?.Trim();
                if (string.IsNullOrEmpty(bone)) { Log("Attach: no bone chosen"); return; }

                // A skinned mesh (armor sleeves, hair, body parts) is already bound to
                // the whole skeleton and follows the animation; forcing a single-bone
                // attach would tear it. Refuse politely.
                if (lm.Skin != null && lm.Skin.Length > 0)
                {
                    Log($"Attach ignored: {lm.Name} is skinned (already follows the body)");
                    if (lblModelInfo != null) lblModelInfo.Text = "Mesh skinné : suit déjà le squelette";
                    return;
                }

                if (string.Equals(lm.AttachBoneName, bone, StringComparison.OrdinalIgnoreCase))
                {
                    lm.AttachBoneName = null;
                    lm.HasAttachMatrix = false;
                    Log($"Detached {lm.Name}");
                }
                else
                {
                    lm.AttachBoneName = bone;
                    Log($"Attached {lm.Name} -> {bone}");
                }

                RefreshLoadedList();
                LoadEquipControlsFromMesh();
                // ApplyAnimationPose also handles the no-animation (bind) case, so the
                // attach is visible immediately.
                ApplyAnimationPose(_currentTime);
                if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();
            }
            catch (Exception ex)
            {
                Log($"Attach error: {ex.Message}\n{ex}");
            }
        }

        // Retire un mesh de l'affichage (double-clic dans la liste des meshes).
        private void RemoveMeshAt(int index)
        {
            if (index < 0 || index >= _loadedMeshes.Count) return;
            if (glControl == null || !glControl.IsHandleCreated) return;
            glControl.MakeCurrent();
            var lm = _loadedMeshes[index];
            DestroyMesh(lm);
            // Any mesh wrapped to this one loses its borrowed weights.
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
            RefreshLoadedList();
            UpdateModelInfo();
            glControl.Invalidate();
            Log($"RemoveMeshAt index={index} -> remaining={_loadedMeshes.Count}");
        }

        private int TryLoadTexture(string modelPath, string embeddedName = null)
        {
            string dir = Path.GetDirectoryName(modelPath);
            string baseName = Path.GetFileNameWithoutExtension(modelPath);
            string[] exts = { ".png", ".jpg", ".jpeg", ".bmp", ".tga" };

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
                        if (File.Exists(direct)) { int t = LoadTexture(direct); if (t > 0) return t; }
                        foreach (var ext in exts)
                        {
                            string p = Path.Combine(dir, emb + ext);
                            if (File.Exists(p)) { int t = LoadTexture(p); if (t > 0) return t; }
                        }
                    }
                    if (Directory.Exists(MapsPath))
                    {
                        string mapDirect = Path.Combine(MapsPath, emb);
                        if (File.Exists(mapDirect)) { int t = LoadTexture(mapDirect); if (t > 0) return t; }
                        foreach (var ext in exts)
                        {
                            string p = Path.Combine(MapsPath, emb + ext);
                            if (File.Exists(p)) { int t = LoadTexture(p); if (t > 0) return t; }
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
                                    int tex = LoadTexture(f);
                                    if (tex > 0) return tex;
                                }
                            }
                        }
                        catch { }
                    }
                }
            }

            // 1) same folder, same base name (or _Lod0 variant)
            foreach (var ext in exts)
            {
                foreach (var candidate in new[] { baseName + ext, baseName.Replace("_Lod2", "_Lod0") + ext })
                {
                    string p = Path.Combine(dir, candidate);
                    if (!File.Exists(p)) continue;
                    int tex = LoadTexture(p);
                    if (tex > 0) return tex;
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
                        if (!File.Exists(p)) continue;
                        int tex = LoadTexture(p);
                        if (tex > 0) return tex;
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
                        if (!File.Exists(p)) continue;
                        int tex = LoadTexture(p);
                        if (tex > 0) return tex;
                    }

                    // First Map file starting with the creature name (skip _M normal maps)
                    try
                    {
                        foreach (var f in Directory.GetFiles(MapsPath, cname + "*", SearchOption.TopDirectoryOnly))
                        {
                            string fn = Path.GetFileName(f);
                            if (fn.EndsWith("_M.tga", StringComparison.OrdinalIgnoreCase)) continue;
                            if (!fn.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)) continue;
                            int tex = LoadTexture(f);
                            if (tex > 0) return tex;
                        }
                    }
                    catch { }
                }
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
            int idx = lstLoaded.SelectedIndex;
            if (idx < 0 || idx >= _loadedMeshes.Count)
            {
                var applyMixamoFallback = new ToolStripMenuItem("⚡ Convert/create rig to UO (Adapter au squelette UO Biped)...", null, (s, a) =>
                {
                    string target = !string.IsNullOrEmpty(_currentModelPath) ? _currentModelPath : null;
                    ApplyMixamoForUo(target);
                });
                applyMixamoFallback.Font = new Font(lstLoadedMenu.Font, FontStyle.Bold);
                lstLoadedMenu.Items.Add(applyMixamoFallback);

                lstLoadedMenu.Items.Add("Importer un Character Mixamo (FBX)...", null, BtnImportModel_Click);
                lstLoadedMenu.Items.Add("Importer un mesh...", null, BtnImportMesh_Click);
                return;
            }
            var lm = _loadedMeshes[idx];

            var headerItem = new ToolStripMenuItem($"Mesh : {lm.Name}") { Enabled = false };
            lstLoadedMenu.Items.Add(headerItem);
            lstLoadedMenu.Items.Add(new ToolStripSeparator());

            lstLoadedMenu.Items.Add("Assigner une texture...", null, (s, a) =>
            {
                AssignTextureToLoadedMesh(lm);
            });

            if (lm.HasTexture && lm.TextureId > 0)
            {
                lstLoadedMenu.Items.Add("Retirer la texture", null, (s, a) =>
                {
                    RemoveTextureFromLoadedMesh(lm);
                });
            }

            if (!string.IsNullOrEmpty(lm.SourcePath) && lm.SourcePath.EndsWith(".char", StringComparison.OrdinalIgnoreCase))
            {
                lstLoadedMenu.Items.Add(new ToolStripSeparator());
                lstLoadedMenu.Items.Add("Gérer toutes les textures du modèle...", null, (s, a) =>
                {
                    OpenTextureManagerForModel(lm.SourcePath);
                });
            }

            lstLoadedMenu.Items.Add(new ToolStripSeparator());
            var applyMixamoLoaded = new ToolStripMenuItem("⚡ Convert/create rig to UO (Adapter au squelette UO Biped)...", null, (s, a) =>
            {
                string target = !string.IsNullOrEmpty(lm.SourcePath) ? lm.SourcePath : _currentModelPath;
                ApplyMixamoForUo(target);
            });
            applyMixamoLoaded.Font = new Font(lstLoadedMenu.Font, FontStyle.Bold);
            lstLoadedMenu.Items.Add(applyMixamoLoaded);

            lstLoadedMenu.Items.Add("Retirer ce mesh de la scène", null, (s, a) =>
            {
                RemoveMeshAt(idx);
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
                if (lm.TextureId > 0 && lm.TextureId != newTex) GL.DeleteTexture(lm.TextureId);
                lm.TextureId = newTex;
                lm.HasTexture = true;
            }

            if (!string.IsNullOrEmpty(lm.SourcePath) && File.Exists(lm.SourcePath) && lm.SourcePath.EndsWith(".char", StringComparison.OrdinalIgnoreCase))
            {
                var model = GrannyModelFile.Load(lm.SourcePath);
                if (model != null)
                {
                    var part = model.Parts.Find(p => string.Equals(p.Name, lm.Name, StringComparison.OrdinalIgnoreCase));
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
                lm.TextureId = -1;
                lm.HasTexture = false;
            }

            if (!string.IsNullOrEmpty(lm.SourcePath) && File.Exists(lm.SourcePath) && lm.SourcePath.EndsWith(".char", StringComparison.OrdinalIgnoreCase))
            {
                var model = GrannyModelFile.Load(lm.SourcePath);
                if (model != null)
                {
                    var part = model.Parts.Find(p => string.Equals(p.Name, lm.Name, StringComparison.OrdinalIgnoreCase));
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
                lblModelInfo.Text = $"Bones: {totalBones}\nTracks: {trackCount}\nMeshes: {_loadedMeshes.Count}";
        }

        // ==================== ANIMATION ====================

        // Name-only binding: map each track to the model bone with the same name.
        // Returns the number of bones matched.
        private int BindMeshToAnimation(LoadedMesh lm, GrannyAnimation anim)
        {
            int boneCount = lm.Bones.Count;
            lm.BoneToAnimTrack = new int[boneCount];
            for (int i = 0; i < boneCount; i++) lm.BoneToAnimTrack[i] = -1;

            var modelBoneByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < boneCount; i++)
                if (!string.IsNullOrEmpty(lm.Bones[i].Name))
                    modelBoneByName[lm.Bones[i].Name] = i;

            int matched = 0;
            for (int trackIdx = 0; trackIdx < anim.Tracks.Count; trackIdx++)
            {
                var track = anim.Tracks[trackIdx];
                if (string.IsNullOrEmpty(track.Name)) continue;

                if (modelBoneByName.TryGetValue(track.Name, out int modelBoneIdx))
                {
                    lm.BoneToAnimTrack[modelBoneIdx] = trackIdx;
                    matched++;
                }
                else if (MixamoRetarget.TryGetBoneAlias(track.Name, out string alias) &&
                         modelBoneByName.TryGetValue(alias, out modelBoneIdx))
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

            try {
                foreach (var lm in _loadedMeshes) {
                    if (lm.DeformedCache == null) continue;
                    float mnX=float.MaxValue,mnY=float.MaxValue,mnZ=float.MaxValue,mxX=float.MinValue,mxY=float.MinValue,mxZ=float.MinValue; int moved=0;
                    for (int v=0; v<lm.DeformedCache.Length; v++){ var d=lm.DeformedCache[v]; var o=lm.BaseVertices[v];
                        if ((d-o).Length>0.01f) moved++;
                        mnX=Math.Min(mnX,d.X);mxX=Math.Max(mxX,d.X);mnY=Math.Min(mnY,d.Y);mxY=Math.Max(mxY,d.Y);mnZ=Math.Min(mnZ,d.Z);mxZ=Math.Max(mxZ,d.Z); }
                    Log($"Pose t={t:F2} defBounds x({mnX:F3}~{mxX:F3}) y({mnY:F3}~{mxY:F3}) z({mnZ:F3}~{mxZ:F3}) moved={moved}/{lm.DeformedCache.Length}");
                }
            } catch { }
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

                for (int i = 0; i < boneCount; i++)
                {
                    int trackIdx = (trackMap != null && i < trackMap.Length) ? trackMap[i] : -1;
                    if (anim != null && trackIdx >= 0 && trackIdx < anim.Tracks.Count)
                    {
                        var tr = anim.Tracks[trackIdx];
                        var b = lm.Bones[i];
                        int p = b.ParentId;
                        bool isRoot = p < 0 || p >= boneCount || p == i ||
                                      b.Name.Equals("Bip01", StringComparison.OrdinalIgnoreCase) ||
                                      (p >= 0 && p < boneCount && (lm.Bones[p].Name.Equals("__Root", StringComparison.OrdinalIgnoreCase) ||
                                                                   lm.Bones[p].Name.StartsWith("master_", StringComparison.OrdinalIgnoreCase)));

                        Vector3 pos;
                        if (!isCustomModel)
                        {
                            pos = tr.EvaluatePos(meshT);
                        }
                        else
                        {
                            // Custom imported models (e.g. Mixamo characters with custom limb/finger lengths):
                            // Root/pelvis (Bip01), attachment sockets (CP_*), and bones with actual position animation
                            // evaluate their animated track, while static limb bones preserve the custom model's local bone lengths.
                            bool useAnimPos = isRoot ||
                                              b.Name.StartsWith("CP_", StringComparison.OrdinalIgnoreCase) ||
                                              TrackPositionVaries(tr, 0.002f);
                            pos = useAnimPos ? tr.EvaluatePos(meshT) : b.LocalPosition;
                        }

                        lm.LocalMatrices[i] = Matrix4.CreateFromQuaternion(tr.EvaluateRot(meshT)) * Matrix4.CreateTranslation(pos);
                    }
                    else
                    {
                        var b = lm.Bones[i];
                        lm.LocalMatrices[i] = Matrix4.CreateFromQuaternion(b.LocalRotation) * Matrix4.CreateTranslation(b.LocalPosition);
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
                bool inBodySpace = lm.AttachInBindSpace || (lm.Skin != null && lm.Skin.Length > 0);
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
            Matrix4 rot = Matrix4.CreateFromQuaternion(Quaternion.FromEulerAngles(
                MathHelper.DegreesToRadians(lm.AttachRotation.X),
                MathHelper.DegreesToRadians(lm.AttachRotation.Y),
                MathHelper.DegreesToRadians(lm.AttachRotation.Z)));
            Matrix4 tr = Matrix4.CreateTranslation(lm.AttachOffset);
            // row-vector order: scale, then rotate, then translate
            return scale * rot * tr;
        }

        // ---- Real-time cloth simulation (verlet) ----

        private struct ClothCapsule { public Vector3 A, B; public float R; }

        // Reset every cloth mesh to its rest (bind) shape. Note: ClothPos/Prev are
        // indexed by PARTICLE, while BaseVertices is indexed by vertex, so copy from
        // the per-particle rest array (ClothPos0).
        private void ResetAllCloth()
        {
            foreach (var lm in _loadedMeshes)
            {
                if (lm.IsCloth && lm.ClothPos != null && lm.ClothPos0 != null)
                {
                    Array.Copy(lm.ClothPos0, lm.ClothPos, lm.ClothPos.Length);
                    Array.Copy(lm.ClothPos0, lm.ClothPrev, lm.ClothPrev.Length);
                }
            }
        }

        // Lazily build the cloth representation from the mesh's bind geometry:
        // particles = WELDED vertices (coincident copies share one), constraints =
        // triangle edges, pins = the top band.
        private void BuildClothFromMesh(LoadedMesh lm)
        {
            if (lm.BaseVertices == null || lm.BaseVertices.Length == 0) return;
            if (lm.Weld == null) BuildWeld(lm);
            int P = Math.Max(1, lm.WeldCount);

            // Representative original vertex + rest position for each particle.
            var rep = new int[P];
            var rest = new Vector3[P];
            for (int p = 0; p < P; p++) rep[p] = -1;
            for (int v = 0; v < lm.Weld.Length; v++)
            {
                int p = lm.Weld[v];
                if (rep[p] < 0) { rep[p] = v; rest[p] = lm.BaseVertices[v]; }
            }
            lm.ClothRep = rep;
            lm.ClothPos0 = rest;
            lm.ClothPos = (Vector3[])rest.Clone();
            lm.ClothPrev = (Vector3[])rest.Clone();
            lm.ClothPin = new bool[P];

            // Pin the top band (closest to the shoulders/neck) = highest bind z.
            float minZ = float.MaxValue, maxZ = float.MinValue;
            for (int p = 0; p < P; p++) { float z = rest[p].Z; if (z < minZ) minZ = z; if (z > maxZ) maxZ = z; }
            float thr = maxZ - 0.18f * Math.Max(1e-4f, maxZ - minZ);
            for (int p = 0; p < P; p++) lm.ClothPin[p] = rest[p].Z >= thr;

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
                AddClothEdge(edges, a, b, w0, w1);
                AddClothEdge(edges, a, b, w1, w2);
                AddClothEdge(edges, a, b, w2, w0);
            }
            lm.ClothEdgeA = a.ToArray();
            lm.ClothEdgeB = b.ToArray();
            lm.ClothRest = new float[a.Count];
            for (int e = 0; e < a.Count; e++)
                lm.ClothRest[e] = Vector3.Distance(rest[a[e]], rest[b[e]]);
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

        // Skinned position of a pinned particle (uses the mesh's own skin, which for
        // a cape is the correct spine binding). p = particle id.
        private Vector3 ClothPinTarget(LoadedMesh lm, int p)
        {
            int v = (lm.ClothRep != null && p >= 0 && p < lm.ClothRep.Length) ? lm.ClothRep[p] : -1;
            if (v < 0 || lm.BaseVertices == null || v >= lm.BaseVertices.Length) return Vector3.Zero;
            Vector3 orig = lm.BaseVertices[v];
            if (lm.Skin == null || v >= lm.Skin.Length) return orig;
            var inf = lm.Skin[v];
            if (inf == null || inf.Length == 0 || lm.SkinMatrices == null) return orig;
            Vector3 def = Vector3.Zero; float tw = 0f;
            int bc = lm.Bones.Count;
            for (int k = 0; k < inf.Length; k++)
            {
                int bid = inf[k].BoneId;
                if (bid >= 0 && bid < bc && bid < lm.SkinMatrices.Length)
                { def += Vector3.TransformPosition(orig, lm.SkinMatrices[bid]) * inf[k].Weight; tw += inf[k].Weight; }
            }
            return tw > 1e-4f ? def / tw : orig;
        }

        private void SetClothSelected(bool on)
        {
            int i = lstLoaded?.SelectedIndex ?? -1;
            if (i < 0 || i >= _loadedMeshes.Count)
            {
                if (on) Log("Tissu : aucun mesh sélectionné.");
                return;
            }
            var lm = _loadedMeshes[i];
            lm.IsCloth = on;
            if (on)
            {
                lm.WrapSource = null; lm.WrapSkin = null;      // cloth supersedes wrap
                lm.HasAttachMatrix = false;
                ClearClothBake(lm);                            // rebuild -> (re)bake needed
                BuildClothFromMesh(lm);
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

        // Collect bone capsules of every OTHER visible skinned mesh (the body), so
        // the cloth is pushed out of the legs/torso instead of passing through them.
        private List<ClothCapsule> BuildBodyCapsules(LoadedMesh clothMesh)
        {
            var caps = new List<ClothCapsule>();
            foreach (var m in _loadedMeshes)
            {
                if (ReferenceEquals(m, clothMesh)) continue;
                if (m.IsCloth) continue;
                if (m.WorldMatrices == null || m.Bones == null || m.Bones.Count < 2) continue;
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
                    float len = Vector3.Distance(A, B);
                    if (len < 1e-4f) continue;
                    float r = Math.Max(CapsuleRadiusFor(m.Bones[i].Name), CapsuleRadiusFor(m.Bones[p].Name));
                    caps.Add(new ClothCapsule { A = A, B = B, R = r });
                }
            }
            return caps;
        }

        // Rough body-envelope radius per bone. A capsule must be at least as fat as
        // the limb it represents, otherwise the cloth is pushed to a surface INSIDE
        // the leg and the leg still pokes through.
        private static float CapsuleRadiusFor(string boneName)
        {
            if (string.IsNullOrEmpty(boneName)) return 0.09f;
            string s = boneName.ToLowerInvariant();
            if (s.Contains("thigh")) return 0.13f;
            if (s.Contains("calf") || s.Contains("knee") || s.Contains("shin")) return 0.10f;
            if (s.Contains("foot") || s.Contains("toe")) return 0.08f;
            if (s.Contains("pelvis") || s.Contains("hip")) return 0.15f;
            if (s.Contains("spine")) return 0.13f;
            if (s.Contains("neck")) return 0.10f;
            if (s.Contains("head")) return 0.11f;
            if (s.Contains("upperarm")) return 0.10f;
            if (s.Contains("forearm") || s.Contains("clavicle")) return 0.09f;
            if (s.Contains("hand") || s.Contains("finger")) return 0.06f;
            return 0.10f;
        }

        private static void PushOutOfCapsules(LoadedMesh lm, List<ClothCapsule> caps)
        {
            for (int v = 0; v < lm.ClothPos.Length; v++)
            {
                if (lm.ClothPin[v]) continue;
                Vector3 p = lm.ClothPos[v];
                for (int c = 0; c < caps.Count; c++)
                {
                    Vector3 ab = caps[c].B - caps[c].A;
                    float abLen2 = ab.LengthSquared;
                    float t = abLen2 > 1e-8f ? Vector3.Dot(p - caps[c].A, ab) / abLen2 : 0f;
                    if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
                    Vector3 closest = caps[c].A + ab * t;
                    Vector3 d = p - closest;
                    float dist = d.Length;
                    if (dist < caps[c].R)
                    {
                        if (dist < 1e-5f) d = new Vector3(0f, 0f, 1f);
                        else d /= dist;
                        p = closest + d * caps[c].R;
                    }
                }
                lm.ClothPos[v] = p;
            }
        }

        // Collect the ACTUAL deformed surface of every other visible skinned mesh:
        // each vertex plus each triangle centroid. Bone capsules only approximate the
        // limbs around the bone centre line, so a thick limb (or one whose surface sits
        // off the bone axis) still pokes through; the real surface does not.
        private List<Vector3> BuildBodyPoints(LoadedMesh clothMesh)
        {
            var pts = new List<Vector3>();
            foreach (var m in _loadedMeshes)
            {
                if (ReferenceEquals(m, clothMesh)) continue;
                if (m.IsCloth || m.Hidden) continue;
                var dc = m.DeformedCache;
                if (dc == null) continue;
                for (int i = 0; i < dc.Length; i++) pts.Add(dc[i]);
                var cv = m.CornerVertex;
                if (cv != null)
                {
                    for (int t = 0; t + 2 < cv.Length; t += 3)
                    {
                        int i0 = cv[t], i1 = cv[t + 1], i2 = cv[t + 2];
                        if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= dc.Length || i1 >= dc.Length || i2 >= dc.Length) continue;
                        Vector3 a = dc[i0], b = dc[i1], c = dc[i2];
                        // Densify: centroid + the three edge midpoints, so the cloth
                        // particle can't slip through a low-poly limb (e.g. the heel).
                        pts.Add((a + b + c) * (1f / 3f));
                        pts.Add((a + b) * 0.5f);
                        pts.Add((b + c) * 0.5f);
                        pts.Add((c + a) * 0.5f);
                    }
                }
            }
            return pts;
        }

        private static void PushOutOfPoints(LoadedMesh lm, List<Vector3> pts, float R)
        {
            float r2 = R * R;
            for (int v = 0; v < lm.ClothPos.Length; v++)
            {
                if (lm.ClothPin[v]) continue;
                Vector3 p = lm.ClothPos[v];
                for (int i = 0; i < pts.Count; i++)
                {
                    Vector3 d = p - pts[i];
                    float d2 = d.LengthSquared;
                    if (d2 < r2 && d2 > 1e-12f)
                    {
                        float dist = (float)Math.Sqrt(d2);
                        p = pts[i] + d * (R / dist);
                    }
                }
                lm.ClothPos[v] = p;
            }
        }

        // Bake the real-time solver ONCE over the whole animation: pose + collision at
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
        }

        private void StepClothForMesh(LoadedMesh lm, float dt)
        {
            if (lm.ClothPos == null || lm.ClothEdgeA == null) return;
            if (dt <= 0f) return;
            if (dt > 0.1f) dt = 0.1f;
            int n = lm.ClothPos.Length;

            for (int v = 0; v < n; v++)
            {
                if (lm.ClothPin[v])
                {
                    Vector3 target = ClothPinTarget(lm, v);
                    lm.ClothPos[v] = target;
                    lm.ClothPrev[v] = target;
                }
            }

            float dt2 = dt * dt;
            float damp = 0.99f;
            Vector3 acc = new Vector3(0f, 0f, -lm.ClothGravity);
            for (int v = 0; v < n; v++)
            {
                if (lm.ClothPin[v]) continue;
                Vector3 p = lm.ClothPos[v];
                Vector3 vel = (p - lm.ClothPrev[v]) * damp;
                lm.ClothPrev[v] = p;
                lm.ClothPos[v] = p + vel + acc * dt2;
            }

            var caps = BuildBodyCapsules(lm);
            var bodyPts = BuildBodyPoints(lm);
            const float PR = 0.05f;   // cloth stand-off from the body surface

            // Periodic diagnostic: are the cloth particles and the body colliders in
            // the same space/scale, and how many actually collide?
            if ((++_clothDbg % 120) == 0)
            {
                float by0 = float.MaxValue, by1 = float.MinValue, bz0 = float.MaxValue, bz1 = float.MinValue;
                foreach (var q in bodyPts)
                {
                    by0 = Math.Min(by0, q.Y); by1 = Math.Max(by1, q.Y);
                    bz0 = Math.Min(bz0, q.Z); bz1 = Math.Max(bz1, q.Z);
                }
                float pz0 = float.MaxValue, pz1 = float.MinValue, py0 = float.MaxValue, py1 = float.MinValue;
                for (int v = 0; v < n; v++)
                {
                    if (lm.ClothPin[v]) continue;
                    var q = lm.ClothPos[v];
                    pz0 = Math.Min(pz0, q.Z); pz1 = Math.Max(pz1, q.Z);
                    py0 = Math.Min(py0, q.Y); py1 = Math.Max(py1, q.Y);
                }
                int inside = 0;
                for (int v = 0; v < n; v++)
                {
                    if (lm.ClothPin[v]) continue;
                    Vector3 q = lm.ClothPos[v];
                    for (int i = 0; i < bodyPts.Count; i++)
                        if ((q - bodyPts[i]).LengthSquared < PR * PR) { inside++; break; }
                }
                Log($"ClothDbg '{lm.Name}' caps={caps.Count} pts={bodyPts.Count} ptY=[{by0:F2},{by1:F2}] ptZ=[{bz0:F2},{bz1:F2}] " +
                    $"partY=[{py0:F2},{py1:F2}] partZ=[{pz0:F2},{pz1:F2}] insidePts={inside}");
            }

            int iterations = Math.Max(1, lm.ClothSimIterations);
            for (int it = 0; it < iterations; it++)
            {
                for (int e = 0; e < lm.ClothEdgeA.Length; e++)
                {
                    int ia = lm.ClothEdgeA[e], ib = lm.ClothEdgeB[e];
                    Vector3 pa = lm.ClothPos[ia], pb = lm.ClothPos[ib];
                    Vector3 d = pb - pa;
                    float len = d.Length;
                    if (len < 1e-6f) continue;
                    float diff = (len - lm.ClothRest[e]) / len;
                    bool fa = !lm.ClothPin[ia], fb = !lm.ClothPin[ib];
                    if (fa && fb) { pa += d * (0.5f * diff); pb -= d * (0.5f * diff); }
                    else if (fa) { pa += d * diff; }
                    else if (fb) { pb -= d * diff; }
                    else continue;
                    lm.ClothPos[ia] = pa; lm.ClothPos[ib] = pb;
                }
                if (caps.Count > 0) PushOutOfCapsules(lm, caps);
                if (bodyPts.Count > 0) PushOutOfPoints(lm, bodyPts, PR);
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

                // Effective per-axis scale = global * per-axis.
                Vector3 pscale = new Vector3(
                    lm.PartScale * lm.PartScaleVec.X,
                    lm.PartScale * lm.PartScaleVec.Y,
                    lm.PartScale * lm.PartScaleVec.Z);
                bool scaleTweaked = pscale.X != 1f || pscale.Y != 1f || pscale.Z != 1f;

                bool hasRot = lm.AttachRotation != Vector3.Zero;
                bool hasMirror = lm.MirrorX || lm.MirrorY || lm.MirrorZ;
                bool hasOffset = lm.AttachOffset != Vector3.Zero;
                bool hasTweak = scaleTweaked || hasRot || hasMirror || hasOffset;

                Matrix4 rotMat = hasRot
                    ? Matrix4.CreateFromQuaternion(Quaternion.FromEulerAngles(
                        MathHelper.DegreesToRadians(lm.AttachRotation.X),
                        MathHelper.DegreesToRadians(lm.AttachRotation.Y),
                        MathHelper.DegreesToRadians(lm.AttachRotation.Z)))
                    : Matrix4.Identity;

                Vector3 scalePivot = lm.HasBindCentroid ? lm.BindCentroid : Vector3.Zero;
                Vector3 rotPivot = (lm.CutoutRole == MeshCutoutRole.Accessory) ? scalePivot : Vector3.Zero;

                if (cloth)
                {
                    // Positions come from the cloth solver (per particle); every
                    // coincident vertex reads the same particle -> the piece can't tear.
                    for (int v = 0; v < vcount; v++)
                    {
                        int p = v < lm.Weld.Length ? lm.Weld[v] : 0;
                        if (p < 0 || p >= lm.ClothPos.Length) p = 0;
                        lm.DeformedCache[v] = lm.ClothPos[p];
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
                        Vector3 pos = orig;
                        if (hasTweak)
                        {
                            Vector3 r = pos - scalePivot;
                            if (scaleTweaked)
                                r = new Vector3(r.X * pscale.X, r.Y * pscale.Y, r.Z * pscale.Z);
                            pos = scalePivot + r;

                            Vector3 rRot = pos - rotPivot;
                            if (hasMirror)
                                rRot = new Vector3(rRot.X * (lm.MirrorX ? -1f : 1f), rRot.Y * (lm.MirrorY ? -1f : 1f), rRot.Z * (lm.MirrorZ ? -1f : 1f));
                            if (hasRot)
                                rRot = Vector3.TransformNormal(rRot, rotMat);
                            pos = rotPivot + rRot + lm.AttachOffset;
                        }
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
                        Vector3 pos = orig;
                        if (hasTweak)
                        {
                            Vector3 r = pos - scalePivot;
                            if (scaleTweaked)
                                r = new Vector3(r.X * pscale.X, r.Y * pscale.Y, r.Z * pscale.Z);
                            pos = scalePivot + r;

                            Vector3 rRot = pos - rotPivot;
                            if (hasMirror)
                                rRot = new Vector3(rRot.X * (lm.MirrorX ? -1f : 1f), rRot.Y * (lm.MirrorY ? -1f : 1f), rRot.Z * (lm.MirrorZ ? -1f : 1f));
                            if (hasRot)
                                rRot = Vector3.TransformNormal(rRot, rotMat);
                            pos = rotPivot + rRot + lm.AttachOffset;
                        }
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

                    // Per-part tweak: scale about bind centroid, mirror and rotation about rotPivot + offset
                    if (hasTweak)
                    {
                        Vector3 r = skinnedPos - scalePivot;
                        if (scaleTweaked)
                            r = new Vector3(r.X * pscale.X, r.Y * pscale.Y, r.Z * pscale.Z);
                        Vector3 pos = scalePivot + r;

                        Vector3 rRot = pos - rotPivot;
                        if (hasMirror)
                            rRot = new Vector3(rRot.X * (lm.MirrorX ? -1f : 1f), rRot.Y * (lm.MirrorY ? -1f : 1f), rRot.Z * (lm.MirrorZ ? -1f : 1f));
                        if (hasRot)
                            rRot = Vector3.TransformNormal(rRot, rotMat);
                        skinnedPos = rotPivot + rRot + lm.AttachOffset;
                    }

                    // Thickness: push the piece outward along the (skinned) surface
                    // normal so residual body/armor mismatches never poke through.
                    if (thick)
                    {
                        if (hasMirror)
                            dnorm = new Vector3(dnorm.X * (lm.MirrorX ? -1f : 1f), dnorm.Y * (lm.MirrorY ? -1f : 1f), dnorm.Z * (lm.MirrorZ ? -1f : 1f));
                        if (hasRot)
                            dnorm = Vector3.TransformNormal(dnorm, rotMat);
                        if (dnorm.LengthSquared > 1e-12f) dnorm = Vector3.Normalize(dnorm);
                        else dnorm = Vector3.Zero;
                        skinnedPos += dnorm * lm.Thickness;
                    }
                    lm.DeformedCache[v] = skinnedPos;
                }

                int cornerCount = lm.Indices.Length;

                // Cloth normals are recomputed from the simulated geometry each frame
                // (the corner-loop transform below is skipped for cloth meshes).
                if (cloth)
                {
                    int cn = lm.CornerVertex.Length;
                    for (int t = 0; t + 2 < cn; t += 3)
                    {
                        int i0 = lm.CornerVertex[t], i1 = lm.CornerVertex[t + 1], i2 = lm.CornerVertex[t + 2];
                        if (i0 < 0 || i1 < 0 || i2 < 0) continue;
                        if (i0 >= vcount || i1 >= vcount || i2 >= vcount) continue;
                        Vector3 va = lm.DeformedCache[i0], vb = lm.DeformedCache[i1], vc = lm.DeformedCache[i2];
                        Vector3 fn = Vector3.Cross(vb - va, vc - va);
                        if (fn.LengthSquared > 1e-12f) fn = Vector3.Normalize(fn);
                        for (int k = 0; k < 3; k++)
                        {
                            int ci = t + k;
                            lm.Interleaved[ci * 8 + 3] = fn.X;
                            lm.Interleaved[ci * 8 + 4] = fn.Y;
                            lm.Interleaved[ci * 8 + 5] = fn.Z;
                        }
                    }
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
                lm.TextureId = TryLoadTexture(path, parser.TextureName);
                lm.HasTexture = lm.TextureId > 0;
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

        private void ExportVdDialog((Func<LoadedMesh, bool> occluder, Func<LoadedMesh, bool> target, Func<LoadedMesh, bool> bounds)? cutout = null)
        {
            if (_loadedMeshes.Count == 0) { Log("Export .vd : aucun modele charge."); return; }
            if (!_glReady || glControl == null || !glControl.IsHandleCreated) return;

            var sources = GatherExportAnimSources(out string prefix);
            if (sources.Count == 0) { Log("Export .vd : aucune animation source trouvee."); return; }
            var allSources = GatherAllExportAnimSources(out var allDisplay);

            using (var form = new VdRemapForm(VdExport.Targets(), sources, prefix, allSources, allDisplay, BasePath))
            {
                if (form.ShowDialog(this) != DialogResult.OK) return;
                var target = form.Target;
                if (target == null) return;

                var mapping = form.GetMapping();     // action -> source path or null
                var offsets = form.GetOffsets();      // action -> 3D offset (X, Y, Z)
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

                        // Mount clip is action-specific, so cache per (source, mount keyword, action offset).
                        string kw = MountClipKeywordForAction(a, target);
                        string cacheKey = (kw != null)
                            ? $"{src}|{kw}|{actOff.X:F3}_{actOff.Y:F3}_{actOff.Z:F3}"
                            : $"{src}|{actOff.X:F3}_{actOff.Y:F3}_{actOff.Z:F3}";
                        if (!cache.TryGetValue(cacheKey, out var blocks))
                        {
                            blocks = BakeAnimFrames(src, size, fps, cutout, kw, offsetY, actOff);
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
            string mountKeyword = null, int offsetY = 0, Vector3 actionOffset = default)
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
                int n = Math.Max(1, (int)Math.Round(dur * fps));
                if (n > 64) n = 64;

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
                        PoseSkeletons(dur * f / n, actionOffset);
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
                    int centerX = (int)Math.Round((0.5f + (va.X + shiftX) / viewW) * cw);
                    int centerY = (int)Math.Round((0.5f - (va.Y + shiftY) / viewH) * ch) - ch;
                    centerY -= offsetY;

                    // Pass 2: render every frame, then box-downsample (SSAA).
                    var frames = new List<Bitmap>(n);
                    for (int f = 0; f < n; f++)
                    {
                        PoseSkeletons(dur * f / n, actionOffset);
                        RefreshVertexBuffers(false, actionOffset);
                        using (var big = cutout.HasValue
                                   ? RenderFrameCutout(rw, rh, mvp, cut.occluder, cut.target)
                                   : RenderFrame(rw, rh, mvp, lm2 => lm2.CutoutRole != MeshCutoutRole.Mount))
                            frames.Add(DownsampleBox(big, cw, ch));
                    }
                    blocks[d] = VdExport.BuildBlock(frames, centerX, centerY);
                    foreach (var b in frames) b.Dispose();
                    Log($"  bake dir={d} frames={n} px={cw}x{ch} ss={ss} center=({centerX},{centerY}) offsetY={offsetY} actOff=({actionOffset.X:F2},{actionOffset.Y:F2},{actionOffset.Z:F2}) ({Path.GetFileName(animPath)})");
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
                _camera.Pitch = -0.35f;
                glControl.Invalidate();
            });

            glContextMenu.Items.Add("Réinitialiser la pose (Stop)", null, BtnStop_Click);
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
    }
}
