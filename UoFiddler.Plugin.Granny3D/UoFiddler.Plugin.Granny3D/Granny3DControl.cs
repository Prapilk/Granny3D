using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using OpenTK.WinForms;
using Ultima;

namespace UoFiddler.Plugin.Granny3D
{
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

        public int Vbo;
        public int Vao;
        public int TextureId = -1;
        public bool HasTexture = false;

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

        // Loaded for its skeleton/matrices only (e.g. the H_Male/H_Female placeholder
        // box which carries the full 43-bone skeleton, incl. CP_Grasp_* sockets) but
        // never drawn.
        public bool Hidden;

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
        public string Folder;
        public string CreatureKey;
        public string MeshPath;      // creature's base/LOD mesh file
        public string LeafPath;      // full path of a leaf .grn file
        public string Display;       // short name shown in the UI
    }

    public class Granny3DControl : UserControl
    {
        private const string BasePath = @"d:\Games\sphere\PROGRAMMES\3D_UO\Models\";
        private const string MapsPath = BasePath + "Maps";

        // Category folder -> display name
        private static readonly string[] CategoryFolders =
        {
            "Humans", "Monsters", "Animals", "SeaCreatures", "Others"
        };
        private static readonly string[] CategoryDisplay =
        {
            "Humans", "Monsters", "Animals", "Sea Creatures", "Equipment"
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
        private ListBox lbAnimations;
        private Label lblAnimations;
        private ListBox lstLoaded;
        private GroupBox grpLoaded;
        private ContextMenuStrip tvMenu;
        private bool _populatingTree;
        // UI - animations panel
        private GroupBox grpAnimations;
        private Label lblAnimHeader;
        private TextBox txtAnimFilter;
        private TreeView tvAnims;
        // animation categories (Male / Female / Animals / ...)
        private class AnimCat
        {
            public string Label;
            public string Folder;
            public string Prefix;
            public bool IsAll;
            public bool Populated;
        }
        // A selectable multipart part (display text + full .grn path).
        private class PartItem
        {
            public string Display;
            public string Path;
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
        private ComboBox cbCharFace, cbCharHair, cbCharBeard, cbCharTorso;
        private bool _suppressChar;
        private int _charGender;           // 0=none, 1=male, 2=female
        // state - animation list
        private TreeNodeInfo _currentCreature;
        private bool _suppressAnimSelect;

        // UI - viewport
        private GLControl glControl;

        // UI - playback panel
        private Button btnPlay;
        private Button btnPause;
        private Button btnStop;
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
        private float _uoZoom = 1f;
        private float _uoElevationDeg = 26.565f;
        private float _uoYawOffsetDeg = 180f;   // 180 so our camera matches UOFiddler's facing
        private float _uoVertScale = 1f;
        private float _uoOffX = 0f, _uoOffY = 0f;  // screen-space offset (normalized units)
        private bool _uoFeetOnLine = true;         // put the model's bottom on the red line
        private float _uoGlobalZoom = 1f;          // global magnification (model + reference)
        private float _uoBrightness = 1f;          // model brightness correction

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
            Width = 1100;
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
            tvAnims.AfterSelect += TvAnims_AfterSelect;
            tvAnims.BeforeExpand += TvAnims_BeforeExpand;
            grpAnimations.Controls.Add(tvAnims);
            grpAnimations.Controls.Add(txtAnimFilter);
            grpAnimations.Controls.Add(lblAnimHeader);

            lblAnimations = new Label { Visible = false };

            // ---- Loaded meshes list + bone attach ----
            grpLoaded = new GroupBox
            {
                Text = "Meshes affiches (double-clic = retirer)",
                Dock = DockStyle.Bottom,
                Height = 210,
                ForeColor = Color.Gainsboro,
                Padding = new Padding(4)
            };
            lstLoaded = new ListBox
            {
                Dock = DockStyle.Fill,
                IntegralHeight = false,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle
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

            pnlLeft.Controls.Add(tvModels);
            pnlLeft.Controls.Add(lblModels);
            pnlLeft.Controls.Add(grpAnimations);
            pnlLeft.Controls.Add(grpLoaded);

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

            grpPlayback.Controls.AddRange(new Control[] { btnPlay, btnPause, btnStop, chkLoop, lblSpeed, trackSpeed, trackTime, lblTime, lblDuration });

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

            grpEquip.Controls.AddRange(new Control[] { lblEquipTarget, cbMirrorX, cbMirrorY, cbMirrorZ, nudRotX, nudRotY, nudRotZ, nudOffX, nudOffY, nudOffZ, nudScale, nudScaleX, nudScaleY, nudScaleZ, nudThickness, chkCloth, nudClothGrav, btnSkinWrap, btnEquipReset, btnSubdiv, nudBakeFps, nudLoopBlend, btnClothBake });

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
            grpChar.Controls.AddRange(new Control[] { lblCharInfo, cbCharFace, cbCharHair, cbCharBeard, cbCharTorso });

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
            nudUoZoom = new NumericUpDown { Location = new Point(8, 168), Width = 100, Minimum = 0.1m, Maximum = 6m, Increment = 0.1m, DecimalPlaces = 2, Value = 1m, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            nudUoZoom.ValueChanged += (s, e) => { _uoZoom = (float)nudUoZoom.Value; if (glControl != null) glControl.Invalidate(); };

            grpCamera.Controls.Add(new Label { Text = "Inclinaison (°)", Location = new Point(120, 150), Width = 120, ForeColor = Color.Gray });
            nudUoElev = new NumericUpDown { Location = new Point(120, 168), Width = 100, Minimum = 5m, Maximum = 60m, Increment = 0.5m, DecimalPlaces = 2, Value = 26.565m, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            nudUoElev.ValueChanged += (s, e) => { _uoElevationDeg = (float)nudUoElev.Value; if (glControl != null) glControl.Invalidate(); };

            grpCamera.Controls.Add(new Label { Text = "Échelle verticale", Location = new Point(8, 200), Width = 120, ForeColor = Color.Gray });
            nudUoVert = new NumericUpDown { Location = new Point(8, 218), Width = 100, Minimum = 0.2m, Maximum = 3m, Increment = 0.05m, DecimalPlaces = 2, Value = 1m, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            nudUoVert.ValueChanged += (s, e) => { _uoVertScale = (float)nudUoVert.Value; if (glControl != null) glControl.Invalidate(); };

            grpCamera.Controls.Add(new Label { Text = "Luminosité", Location = new Point(120, 200), Width = 110, ForeColor = Color.Gray });
            nudUoBright = new NumericUpDown { Location = new Point(120, 218), Width = 100, Minimum = 0.2m, Maximum = 2m, Increment = 0.05m, DecimalPlaces = 2, Value = 1m, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            nudUoBright.ValueChanged += (s, e) => { _uoBrightness = (float)nudUoBright.Value; if (glControl != null) glControl.Invalidate(); };

            chkUoFeet = new CheckBox { Text = "Pieds sur la ligne", Location = new Point(8, 248), Width = 232, Checked = true, ForeColor = Color.Gainsboro };
            chkUoFeet.CheckedChanged += (s, e) => { _uoFeetOnLine = chkUoFeet.Checked; if (glControl != null) glControl.Invalidate(); };

            grpCamera.Controls.Add(new Label { Text = "Décalage X", Location = new Point(8, 276), Width = 110, ForeColor = Color.Gray });
            nudUoOffX = new NumericUpDown { Location = new Point(8, 294), Width = 100, Minimum = -2m, Maximum = 2m, Increment = 0.02m, DecimalPlaces = 3, Value = 0m, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            nudUoOffX.ValueChanged += (s, e) => { _uoOffX = (float)nudUoOffX.Value; if (glControl != null) glControl.Invalidate(); };

            grpCamera.Controls.Add(new Label { Text = "Décalage Y", Location = new Point(120, 276), Width = 110, ForeColor = Color.Gray });
            nudUoOffY = new NumericUpDown { Location = new Point(120, 294), Width = 100, Minimum = -2m, Maximum = 2m, Increment = 0.02m, DecimalPlaces = 3, Value = 0m, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
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
                SplitterDistance = 360,
                FixedPanel = FixedPanel.Panel1,
                BorderStyle = BorderStyle.FixedSingle,
                Orientation = Orientation.Vertical
            };
            mainSplit.Panel1.Controls.Add(pnlLeft);
            mainSplit.Panel2.Controls.Add(pnlRight);

            Controls.Add(mainSplit);
            ResumeLayout(false);

            LoadCategoryTree();
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
            tvModels.Nodes.Clear();

            if (!Directory.Exists(BasePath))
            {
                _initialLoadPending = false;
                return;
            }

            for (int i = 0; i < CategoryFolders.Length; i++)
            {
                string folder = Path.Combine(BasePath, CategoryFolders[i]);
                if (!Directory.Exists(folder))
                    continue;

                var info = new TreeNodeInfo { IsFolder = true, Folder = folder, Display = CategoryDisplay[i] };
                var node = new TreeNode(CategoryDisplay[i]) { Tag = info };
                node.Nodes.Add(new TreeNode("..."));
                tvModels.Nodes.Add(node);
            }

            // Defer auto-load until the OpenGL context is ready (first Paint).
            _initialLoadPending = tvModels.Nodes.Count > 0;
            BuildAnimCategories();
            if (_glReady)
                StartInitialLoad();
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
                    if (parentInfo != null)
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
                    if (isEquipCategory && !looksCharacter && _loadedMeshes.Count > 0)
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
            if (info == null) { e.Cancel = true; return; }

            if (info.IsFolder)
            {
                tvMenu.Items.Add(node.IsExpanded ? "Réduire" : "Développer", null, (s, a) =>
                {
                    if (!info.Populated) AddCreatureNodes(node, info.Folder);
                    node.Toggle();
                });
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
            }

            tvMenu.Items.Add(new ToolStripSeparator());
            tvMenu.Items.Add("Retirer le dernier mesh", null, (s, a) =>
                RemoveMeshAt(_loadedMeshes.Count - 1));
            tvMenu.Items.Add("Vider la scène", null, (s, a) => ClearScene());
        }

        // Append every mesh of a .grn file to the current scene (used to layer
        // equipment/extra pieces onto an already-loaded character). The base
        // model, camera and reference bounds are left untouched so the added
        // meshes stay aligned with the body.
        private void AddToScene(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            if (_sceneExtras.Contains(path)) { Log($"AddToScene: {Path.GetFileName(path)} already in scene"); return; }
            if (!_glReady || glControl == null || !glControl.IsHandleCreated)
            {
                Log("AddToScene aborted: GL not ready");
                return;
            }

            glControl.MakeCurrent();
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
                    Skin = mesh.Skin.Count > 0 ? mesh.Skin.ToArray() : null
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
            if (cbCharFace == null) return;
            _suppressChar = true;
            try
            {
                foreach (var cb in new[] { cbCharFace, cbCharHair, cbCharBeard, cbCharTorso })
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
                lm.TextureId = TryLoadTexture(path, parser.TextureName);
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
                foreach (var cb in new[] { cbCharFace, cbCharHair, cbCharBeard, cbCharTorso })
                    cb.Items.Clear();

                string folder = Path.Combine(BasePath, "Others");
                if (_charGender == 0 || !Directory.Exists(folder))
                {
                    foreach (var cb in new[] { cbCharFace, cbCharHair, cbCharBeard, cbCharTorso })
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
                CollectAnims(cat.Folder, cat.Prefix, files);
            }

            files.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
                node.Nodes.Add(new TreeNode(Path.GetFileNameWithoutExtension(f)) { Tag = f });
            cat.Populated = true;
        }

        private static void CollectAnims(string folder, string prefix, List<string> outFiles)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
            try
            {
                foreach (var f in Directory.GetFiles(folder, "*.grn"))
                {
                    string name = Path.GetFileName(f);
                    if (name.IndexOf("lod", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (prefix != null && !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                    outFiles.Add(f);
                }
            }
            catch { }
        }

        private void TvAnims_AfterSelect(object sender, TreeViewEventArgs e)
        {
            if (_suppressAnimSelect) return;
            if (e.Node?.Tag is string path)
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
            else if (string.Equals(folderName, "Others", StringComparison.OrdinalIgnoreCase)) want = "Equipment";

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
                else if (uvIdx != null && uvIdx.Count == cornerCount && uvs.Count > 0)
                {
                    int ui = uvIdx[c];
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

        // Push the equipment-controller values onto the selected mesh and re-pose.
        private void ApplyEquipControls()
        {
            if (_suppressEquip) return;
            int i = lstLoaded?.SelectedIndex ?? -1;
            if (i < 0 || i >= _loadedMeshes.Count) return;
            var lm = _loadedMeshes[i];

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

            // Re-pose for any mesh (skinned parts are driven by their own skeleton;
            // rigid weapons by their attach bone). The offset/scale tweak applies to
            // both paths. ApplyAnimationPose also handles the no-animation case.
            ApplyAnimationPose(_currentTime);
            if (glControl != null && glControl.IsHandleCreated) glControl.Invalidate();
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
            }
            finally { _suppressEquip = false; }
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

            // 0) Texture name embedded in the .grn (authoritative, e.g. "orcs_orc_v03").
            if (!string.IsNullOrEmpty(embeddedName) && Directory.Exists(MapsPath))
            {
                try
                {
                    foreach (var f in Directory.GetFiles(MapsPath, "*", SearchOption.TopDirectoryOnly))
                    {
                        if (!f.EndsWith(".tga", StringComparison.OrdinalIgnoreCase)) continue;
                        string fn = Path.GetFileNameWithoutExtension(f);
                        if (fn.EndsWith("_m", StringComparison.OrdinalIgnoreCase)) continue;
                        if (fn.Equals(embeddedName, StringComparison.OrdinalIgnoreCase))
                        {
                            int tex = LoadTexture(f);
                            if (tex > 0) return tex;
                        }
                    }
                }
                catch { }
                foreach (var ext in exts)
                {
                    string p = Path.Combine(dir, embeddedName + ext);
                    if (File.Exists(p)) { int t = LoadTexture(p); if (t > 0) return t; }
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

            // 2) Models/Maps: <creature>.tga (+ UT256_* human skins, armor variants)
            string creature = DeriveCreature(baseName);
            if (Directory.Exists(MapsPath))
            {
                // Try exact creature name
                foreach (var ext in exts)
                {
                    string p = Path.Combine(MapsPath, creature + ext);
                    if (!File.Exists(p)) continue;
                    int tex = LoadTexture(p);
                    if (tex > 0) return tex;
                }

                // Try human base skins
                foreach (var cand in new[]
                {
                    "UT256_H_" + creature.Replace("H_Male", "Male").Replace("H_Female", "Female") + "_V2",
                    "UT256_H_" + creature.Replace("H_Male", "Male").Replace("H_Female", "Female") + "A_V2",
                    "Ut256_H_" + creature.Replace("H_Male", "Male").Replace("H_Female", "Female"),
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
                    foreach (var f in Directory.GetFiles(MapsPath, creature + "*", SearchOption.TopDirectoryOnly))
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

        // Minimal TGA reader: 24/32-bit uncompressed (type 2) or RLE (type 10).
        private static Bitmap LoadTga(string file)
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

        private void LoadAnimationCore(string path)
        {
            if (!File.Exists(path)) return;

            _animParser = new GrannyFormatParser();
            if (!_animParser.Load(path)) return;

            _currentAnimation = _animParser.Animation;
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
                if (!string.IsNullOrEmpty(track.Name) && modelBoneByName.TryGetValue(track.Name, out int modelBoneIdx))
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
                if (lm.Bones != null && lm.Bones.Count > 0 && lm.BoneToAnimTrack == null) { need = true; break; }
            if (!need) return;

            LoadedMesh refMesh = null;
            int refScore = -1;
            foreach (var lm in _loadedMeshes)
            {
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

            RefreshVertexBuffers(false);

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

        // Compute every mesh's local/world/skin matrices for animation time t, plus the
        // rigid attach matrices. Pure CPU: it only fills the matrix arrays (no vertex
        // deformation, no GL upload), so the cloth bake can reuse it frame by frame.
        private void PoseSkeletons(float t)
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

                for (int i = 0; i < boneCount; i++)
                {
                    int trackIdx = (lm.BoneToAnimTrack != null && i < lm.BoneToAnimTrack.Length) ? lm.BoneToAnimTrack[i] : -1;
                    if (_currentAnimation != null && trackIdx >= 0 && trackIdx < _currentAnimation.Tracks.Count)
                    {
                        var tr = _currentAnimation.Tracks[trackIdx];
                        lm.LocalMatrices[i] = Matrix4.CreateFromQuaternion(tr.EvaluateRot(t)) * Matrix4.CreateTranslation(tr.EvaluatePos(t));
                    }
                    else
                    {
                        var b = lm.Bones[i];
                        lm.LocalMatrices[i] = Matrix4.CreateFromQuaternion(b.LocalRotation) * Matrix4.CreateTranslation(b.LocalPosition);
                    }
                }

                ComputeWorldMatrices(lm);

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
            var s = new Vector3((lm.MirrorX ? -1f : 1f) * ps, (lm.MirrorY ? -1f : 1f) * ps, (lm.MirrorZ ? -1f : 1f) * ps);
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

        private void RefreshVertexBuffers(bool includeNormals)
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
                        lm.DeformedCache[v] = orig;
                        continue;
                    }

                    // Wrap uses the body mesh's weights/matrices; otherwise the mesh's own.
                    var influences = hasWrap
                        ? (v < lm.WrapSkin.Length ? lm.WrapSkin[v] : null)
                        : (v < lm.Skin.Length ? lm.Skin[v] : null);
                    if (influences == null || influences.Length == 0)
                    {
                        lm.DeformedCache[v] = orig;
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

                    // Per-part tweak: per-axis scale about the bind centroid + offset,
                    // so the body no longer pokes through the piece.
                    if (scaleTweaked || lm.AttachOffset != Vector3.Zero)
                    {
                        Vector3 pivot = lm.HasBindCentroid ? lm.BindCentroid : Vector3.Zero;
                        Vector3 r = skinnedPos - pivot;
                        r = new Vector3(r.X * pscale.X, r.Y * pscale.Y, r.Z * pscale.Z);
                        skinnedPos = pivot + r + lm.AttachOffset;
                    }

                    // Thickness: push the piece outward along the (skinned) surface
                    // normal so residual body/armor mismatches never poke through.
                    if (thick)
                    {
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
                        Vector3 n = new Vector3(lm.Interleaved[c * 8 + 3], lm.Interleaved[c * 8 + 4], lm.Interleaved[c * 8 + 5]);
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
                                if (tn.LengthSquared > 1e-12f) tn = Vector3.Normalize(tn);
                                lm.Interleaved[c * 8 + 3] = tn.X;
                                lm.Interleaved[c * 8 + 4] = tn.Y;
                                lm.Interleaved[c * 8 + 5] = tn.Z;
                            }
                        }
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
        private float ComputeFrameSpan()
        {
            float best = 0.1f;
            Matrix4 vscale = Matrix4.CreateScale(1f, 1f, _uoVertScale);
            for (int d = 0; d < 5; d++)
            {
                Matrix4 modelToView = vscale * UoViewMatrix(d);
                float vmin, vmax;
                ViewVerticalRange(modelToView, out vmin, out vmax);
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
            Vector3 anchor = new Vector3(0f, 0f, anchorZ);
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

        private void DrawMeshSet(Matrix4 mvp, bool unlit, float alphaCut, float opacity = 1f, float brightness = 1f)
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

                bool tex = lm.HasTexture && lm.TextureId > 0;
                GL.BindTexture(TextureTarget.Texture2D, tex ? lm.TextureId : 0);
                GL.Uniform1(_uHasTex, tex ? 1 : 0);
                GL.Uniform3(_uColor, 0.72f, 0.66f, 0.55f);

                GL.BindVertexArray(lm.Vao);
                GL.DrawArrays(PrimitiveType.Triangles, 0, lm.Indices.Length);
            }

            GL.BindVertexArray(0);
            GL.UseProgram(0);
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
