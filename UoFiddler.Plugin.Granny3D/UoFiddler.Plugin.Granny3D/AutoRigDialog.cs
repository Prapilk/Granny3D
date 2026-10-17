using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Assimp;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using OpenTK.WinForms;

namespace UoFiddler.Plugin.Granny3D
{
    public class AutoRigDialog : Form
    {
        public class JointMarker
        {
            public string BoneName { get; set; } = string.Empty;
            public string DisplayName { get; set; } = string.Empty;
            public Vector3 Position { get; set; }
            public Vector3 RotationDegrees { get; set; } = Vector3.Zero; // X = Roll (Torsion), Y = Pitch, Z = Yaw
            public Color Color { get; set; } = Color.Cyan;
            public string? ParentName { get; set; }
            public string? SymmetricBoneName { get; set; }
        }

        private readonly string _basePath;
        private string? _currentModelPath;
        private string _outFolder;
        private string _refPath;
        private List<LoadedMesh>? _liveMeshes;
        private List<GrannyBone>? _liveOriginalBones;

        // Visual controls
        private GLControl glControl = null!;
        private ComboBox cbJoints = null!;
        private CheckBox chkSymmetry = null!;
        private CheckBox chkForceAutoSkin = null!;
        private CheckBox chkShowSkeleton = null!;
        private CheckBox chkShowMesh = null!;
        private RadioButton rbMale = null!;
        private RadioButton rbFemale = null!;
        private NumericUpDown nudX = null!;
        private NumericUpDown nudY = null!;
        private NumericUpDown nudZ = null!;
        private NumericUpDown nudRoll = null!;
        private NumericUpDown nudPitch = null!;
        private NumericUpDown nudYaw = null!;
        private Panel pnlRotControls = null!;
        private Panel pnlRotBtns = null!;
        private Label lblRotTip = null!;
        private NumericUpDown nudScale = null!;
        private Label lblModelInfo = null!;
        private Label lblBoundsInfo = null!;
        private ComboBox cbAnimTest = null!;
        private Button btnPlayAnim = null!;
        private Button btnStopAnim = null!;
        private TrackBar tbAnimScrub = null!;
        private Button btnValidate = null!;

        // 3D Scene / GL State
        private int _shaderMeshProg;
        private int _shaderLineProg;
        private int _meshVao;
        private int _meshVbo;
        private int _meshVertexCount;
        private int _boneVao;
        private int _boneVbo;
        private bool _meshNeedsUpload = false;

        // Raw geometry (from file or live scene) before user transforms
        private Vector3[]? _rawVertices;
        private Vector3[]? _rawNormals;
        private Vector2[]? _rawUVs;
        private int[]? _indices;
        private VertexInfluence[][]? _rawSkin;

        // Baked geometry (after user transform: scale, rotation, offset, mirror)
        private Vector3[]? _baseVertices;
        private Vector3[]? _baseNormals;
        private float[]? _interleaved;
        private float[]? _animatedInterleaved;
        private GrannyModelFile? _previewModel;
        private string? _textureName;

        // Transform parameters
        private float _scale = 1.0f;
        private float _rotX = 0f;
        private float _rotY = 0f;
        private float _rotZ = 0f;
        private bool _mirrorX = false;
        private Vector3 _offset = Vector3.Zero;

        // Joint markers list
        public enum RigViewMode
        {
            Front,   // Vue de Face: Z/X
            Profile, // Vue de Profil: Z/Y
            Back,    // Vue Dos: Z/X
            Free3D   // 3D Libre
        }

        private RigViewMode _currentViewMode = RigViewMode.Front;
        private Button btnVueFace = null!;
        private Button btnVueProfil = null!;
        private Button btnVueDos = null!;
        private Button btnVue3D = null!;
        private bool _isUpdatingNud = false;
        private Vector3 _dragStartMarkerPos = Vector3.Zero;
        private Vector3 _dragGrabOffset = Vector3.Zero;

        private readonly List<JointMarker> _markers = new();
        private int _selectedMarkerIndex = -1;
        private bool _isDraggingMarker = false;
        private Vector3 _dragPlanePoint;
        private Vector3 _dragPlaneNormal;
        private Point _lastMousePos;
        private bool _isOrbiting = false;
        private bool _isPanning = false;

        // Camera
        private Vector3 _cameraTarget = new Vector3(0, 0, 1.0f);
        private float _cameraYaw = 0.0f; // degrees (0 = front)
        private float _cameraPitch = 0.0f; // degrees (0 = front level)
        private float _cameraDist = 2.8f;
        private Vector3 _cameraPan = Vector3.Zero;

        // Animation preview
        private GrannyAnimation? _testAnim;
        private System.Windows.Forms.Timer? _animTimer;
        private float _animTime = 0.0f;
        private bool _isPlayingAnim = false;
        private Matrix4[]? _boneWorldMatrices;
        private List<GrannyBone> _refBones = new();

        public string? GeneratedCharPath { get; private set; }

        public AutoRigDialog(string? initialModelPath, string basePath, string? initialOutFolder = null, List<LoadedMesh>? liveMeshes = null)
        {
            _currentModelPath = initialModelPath;
            _basePath = basePath ?? string.Empty;
            _outFolder = !string.IsNullOrEmpty(initialOutFolder) ? initialOutFolder : Path.Combine(_basePath, "Others");
            _liveMeshes = liveMeshes;

            string maleRef = Path.Combine(_basePath, "Humans", "H_Male_Lod2.grn");
            string femaleRef = Path.Combine(_basePath, "Humans", "H_Female_Lod2.grn");
            if (!File.Exists(maleRef)) maleRef = @"d:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn";
            if (!File.Exists(femaleRef)) femaleRef = @"d:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Female_Lod2.grn";
            _refPath = File.Exists(maleRef) ? maleRef : femaleRef;

            InitializeDefaultMarkers();
            InitializeComponentCustom();
            LoadReferenceBones();
            PopulateTestAnimations();

            // 1) If liveMeshes are present in UOFiddler's active scene, load them directly!
            if (_liveMeshes != null && _liveMeshes.Count > 0 && _liveMeshes.Any(m => m.BaseVertices != null && m.BaseVertices.Length > 0))
            {
                LoadFromLiveMeshes(_liveMeshes);
            }
            // 2) If no liveMeshes or if initialModelPath is specified and valid, load model file
            else if (!string.IsNullOrEmpty(_currentModelPath) && File.Exists(_currentModelPath))
            {
                LoadModelFile(_currentModelPath);
            }

            EnsurePreviewModel();
        }

        private void InitializeDefaultMarkers()
        {
            _markers.Clear();

            // Anatomical markers corresponding to UO Biped key joints
            _markers.Add(new JointMarker { BoneName = "Bip01 Head", DisplayName = "Tête (Head)", Position = new Vector3(0.0f, 0.015f, 1.60f), Color = Color.FromArgb(255, 140, 0), ParentName = "Bip01 Neck" });
            _markers.Add(new JointMarker { BoneName = "Bip01 Neck", DisplayName = "Cou (Neck)", Position = new Vector3(0.0f, 0.01f, 1.48f), Color = Color.FromArgb(255, 215, 0), ParentName = "Bip01 Spine2" });
            _markers.Add(new JointMarker { BoneName = "Bip01 Spine2", DisplayName = "Poitrine (Chest)", Position = new Vector3(0.0f, 0.015f, 1.35f), Color = Color.FromArgb(124, 252, 0), ParentName = "Bip01 Spine1" });
            _markers.Add(new JointMarker { BoneName = "Bip01 Spine", DisplayName = "Ventre (Abdomen)", Position = new Vector3(0.0f, 0.015f, 1.15f), Color = Color.FromArgb(50, 205, 50), ParentName = "Bip01 Pelvis" });
            _markers.Add(new JointMarker { BoneName = "Bip01 Pelvis", DisplayName = "Bassin / Hanches (Pelvis)", Position = new Vector3(0.0f, 0.018f, 0.95f), Color = Color.FromArgb(0, 206, 209), ParentName = null });

            // Arms (Left / Right) - G = Gauche (X < 0, gauche), D = Droite (X > 0, droite)
            _markers.Add(new JointMarker { BoneName = "Bip01 L Clavicle", DisplayName = "Clavicule G", Position = new Vector3(-0.12f, 0.01f, 1.40f), Color = Color.FromArgb(30, 144, 255), ParentName = "Bip01 Spine2", SymmetricBoneName = "Bip01 R Clavicle" });
            _markers.Add(new JointMarker { BoneName = "Bip01 R Clavicle", DisplayName = "Clavicule D", Position = new Vector3(0.12f, 0.01f, 1.40f), Color = Color.FromArgb(255, 69, 0), ParentName = "Bip01 Spine2", SymmetricBoneName = "Bip01 L Clavicle" });

            _markers.Add(new JointMarker { BoneName = "Bip01 L UpperArm", DisplayName = "Épaule G (Shoulder)", Position = new Vector3(-0.24f, 0.02f, 1.38f), Color = Color.FromArgb(65, 105, 225), ParentName = "Bip01 L Clavicle", SymmetricBoneName = "Bip01 R UpperArm" });
            _markers.Add(new JointMarker { BoneName = "Bip01 R UpperArm", DisplayName = "Épaule D (Shoulder)", Position = new Vector3(0.24f, 0.02f, 1.38f), Color = Color.FromArgb(220, 20, 60), ParentName = "Bip01 R Clavicle", SymmetricBoneName = "Bip01 L UpperArm" });

            _markers.Add(new JointMarker { BoneName = "Bip01 L Forearm", DisplayName = "Coude G (Elbow)", Position = new Vector3(-0.44f, 0.03f, 1.35f), Color = Color.FromArgb(100, 149, 237), ParentName = "Bip01 L UpperArm", SymmetricBoneName = "Bip01 R Forearm" });
            _markers.Add(new JointMarker { BoneName = "Bip01 R Forearm", DisplayName = "Coude D (Elbow)", Position = new Vector3(0.44f, 0.03f, 1.35f), Color = Color.FromArgb(205, 92, 92), ParentName = "Bip01 R UpperArm", SymmetricBoneName = "Bip01 L Forearm" });

            _markers.Add(new JointMarker { BoneName = "Bip01 L Hand", DisplayName = "Poignet G (Wrist)", Position = new Vector3(-0.63f, 0.045f, 1.33f), Color = Color.FromArgb(0, 191, 255), ParentName = "Bip01 L Forearm", SymmetricBoneName = "Bip01 R Hand" });
            _markers.Add(new JointMarker { BoneName = "Bip01 R Hand", DisplayName = "Poignet D (Wrist)", Position = new Vector3(0.63f, 0.045f, 1.33f), Color = Color.FromArgb(255, 99, 71), ParentName = "Bip01 R Forearm", SymmetricBoneName = "Bip01 L Hand" });

            // Legs (Left / Right) - G = Gauche (X < 0), D = Droite (X > 0)
            _markers.Add(new JointMarker { BoneName = "Bip01 L Thigh", DisplayName = "Hanche G (Hip)", Position = new Vector3(-0.12f, 0.015f, 0.90f), Color = Color.FromArgb(72, 209, 204), ParentName = "Bip01 Pelvis", SymmetricBoneName = "Bip01 R Thigh" });
            _markers.Add(new JointMarker { BoneName = "Bip01 R Thigh", DisplayName = "Hanche D (Hip)", Position = new Vector3(0.12f, 0.015f, 0.90f), Color = Color.FromArgb(218, 112, 214), ParentName = "Bip01 Pelvis", SymmetricBoneName = "Bip01 L Thigh" });

            _markers.Add(new JointMarker { BoneName = "Bip01 L Calf", DisplayName = "Genou G (Knee)", Position = new Vector3(-0.12f, 0.02f, 0.50f), Color = Color.FromArgb(32, 178, 170), ParentName = "Bip01 L Thigh", SymmetricBoneName = "Bip01 R Calf" });
            _markers.Add(new JointMarker { BoneName = "Bip01 R Calf", DisplayName = "Genou D (Knee)", Position = new Vector3(0.12f, 0.02f, 0.50f), Color = Color.FromArgb(186, 85, 211), ParentName = "Bip01 R Thigh", SymmetricBoneName = "Bip01 L Calf" });

            _markers.Add(new JointMarker { BoneName = "Bip01 L Foot", DisplayName = "Cheville G (Ankle)", Position = new Vector3(-0.10f, 0.05f, 0.10f), Color = Color.FromArgb(0, 139, 139), ParentName = "Bip01 L Calf", SymmetricBoneName = "Bip01 R Foot" });
            _markers.Add(new JointMarker { BoneName = "Bip01 R Foot", DisplayName = "Cheville D (Ankle)", Position = new Vector3(0.10f, 0.05f, 0.10f), Color = Color.FromArgb(255, 105, 180), ParentName = "Bip01 R Calf", SymmetricBoneName = "Bip01 L Foot" });
        }

        private void InitializeComponentCustom()
        {
            Text = "⚡ UO Auto-Rigging & Adaptateur Mixamo (Rig Studio)";
            Size = new Size(1500, 960);
            MinimumSize = new Size(1100, 750);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(26, 26, 28);
            ForeColor = Color.Gainsboro;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);

            // Split Container: Left = 3D Viewport, Right = Sidebar Controls
            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                FixedPanel = FixedPanel.Panel2,
                IsSplitterFixed = false,
                SplitterWidth = 6,
                BackColor = Color.FromArgb(50, 50, 54)
            };

            // ---- Left: 3D Viewport ----
            var pnlViewHost = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(18, 18, 20) };

            // Camera presets floating toolbar
            var pnlCamTools = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                Height = 36,
                BackColor = Color.FromArgb(22, 22, 24),
                Padding = new Padding(6, 4, 6, 4)
            };

            Button CreateCamBtn(string text, Action onClick)
            {
                var b = new Button
                {
                    Text = text,
                    Height = 26,
                    AutoSize = true,
                    BackColor = Color.FromArgb(45, 45, 48),
                    ForeColor = Color.Gainsboro,
                    FlatStyle = FlatStyle.Flat
                };
                b.FlatAppearance.BorderColor = Color.FromArgb(65, 65, 70);
                b.Click += (s, e) => { onClick(); glControl.Invalidate(); };
                return b;
            }

            btnVueFace = CreateCamBtn("👁 Vue Face (Z/X)", () => SetViewMode(RigViewMode.Front));
            btnVueProfil = CreateCamBtn("👁 Vue Profil (Z/Y)", () => SetViewMode(RigViewMode.Profile));
            btnVueDos = CreateCamBtn("👁 Vue Dos", () => SetViewMode(RigViewMode.Back));
            btnVue3D = CreateCamBtn("🎥 3D Persp", () => SetViewMode(RigViewMode.Free3D));

            pnlCamTools.Controls.Add(btnVueFace);
            pnlCamTools.Controls.Add(btnVueProfil);
            pnlCamTools.Controls.Add(btnVueDos);
            pnlCamTools.Controls.Add(btnVue3D);
            pnlCamTools.Controls.Add(CreateCamBtn("🎯 Centrer Caméra", () => { _cameraDist = 2.8f; _cameraPan = Vector3.Zero; }));
            UpdateViewButtonsUI();

            var chkWireframe = new CheckBox
            {
                Text = "Fil de fer",
                ForeColor = Color.Gainsboro,
                AutoSize = true,
                Margin = new Padding(16, 6, 0, 0)
            };
            chkWireframe.CheckedChanged += (s, e) => glControl.Invalidate();
            pnlCamTools.Controls.Add(chkWireframe);

            chkShowSkeleton = new CheckBox
            {
                Text = "Squelette",
                Checked = true,
                ForeColor = Color.LimeGreen,
                AutoSize = true,
                Margin = new Padding(12, 6, 0, 0)
            };
            chkShowSkeleton.CheckedChanged += (s, e) => glControl.Invalidate();
            pnlCamTools.Controls.Add(chkShowSkeleton);

            chkShowMesh = new CheckBox
            {
                Text = "Modèle 3D",
                Checked = true,
                ForeColor = Color.DeepSkyBlue,
                AutoSize = true,
                Margin = new Padding(12, 6, 0, 0)
            };
            chkShowMesh.CheckedChanged += (s, e) => glControl.Invalidate();
            pnlCamTools.Controls.Add(chkShowMesh);

            glControl = new GLControl
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(18, 18, 20)
            };
            glControl.Paint += GlControl_Paint;
            glControl.Resize += (s, e) => glControl.Invalidate();
            glControl.MouseDown += GlControl_MouseDown;
            glControl.MouseMove += GlControl_MouseMove;
            glControl.MouseUp += GlControl_MouseUp;
            glControl.MouseWheel += GlControl_MouseWheel;

            pnlViewHost.Controls.Add(glControl);
            pnlViewHost.Controls.Add(pnlCamTools);
            split.Panel1.Controls.Add(pnlViewHost);

            // ---- Right: Sidebar Tools ----
            var pnlSidebar = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.FromArgb(30, 30, 32),
                Padding = new Padding(12)
            };

            // 1. Source Model Group
            var grpSource = new GroupBox
            {
                Text = "1. Modèle 3D Source (Actif / Importé)",
                Dock = DockStyle.Top,
                Height = 135,
                ForeColor = Color.Gold,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Padding = new Padding(10)
            };

            var pnlSourceBtns = new Panel { Dock = DockStyle.Top, Height = 34 };
            var btnBrowseModel = new Button
            {
                Text = "📂 Ouvrir un autre modèle 3D…",
                Location = new Point(0, 2),
                Width = 240,
                Height = 30,
                BackColor = Color.FromArgb(50, 50, 55),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold)
            };
            btnBrowseModel.Click += (s, e) => BrowseModelFile();

            var btnReloadActive = new Button
            {
                Text = "🔄 Recharger la preview active",
                Location = new Point(246, 2),
                Width = 220,
                Height = 30,
                BackColor = Color.FromArgb(35, 70, 95),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold)
            };
            btnReloadActive.Click += (s, e) =>
            {
                if (_liveMeshes != null && _liveMeshes.Count > 0)
                {
                    LoadFromLiveMeshes(_liveMeshes);
                }
                else if (!string.IsNullOrEmpty(_currentModelPath) && File.Exists(_currentModelPath))
                {
                    LoadModelFile(_currentModelPath);
                }
            };

            pnlSourceBtns.Controls.Add(btnBrowseModel);
            pnlSourceBtns.Controls.Add(btnReloadActive);

            lblModelInfo = new Label
            {
                Text = "Chargement du modèle…",
                Dock = DockStyle.Fill,
                ForeColor = Color.Gainsboro,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular),
                TextAlign = ContentAlignment.MiddleLeft
            };

            grpSource.Controls.Add(lblModelInfo);
            grpSource.Controls.Add(pnlSourceBtns);

            // 2. Transform & Orientation Group
            var grpTransform = new GroupBox
            {
                Text = "2. Orientation, Échelle & Alignement du Modèle",
                Dock = DockStyle.Top,
                Height = 255,
                ForeColor = Color.DeepSkyBlue,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Padding = new Padding(10)
            };

            var pnlTransformInner = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                AutoScroll = true,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular)
            };

            Button CreateTBtn(string text, Color bg, Action onClick)
            {
                var b = new Button
                {
                    Text = text,
                    Height = 28,
                    AutoSize = true,
                    BackColor = bg,
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Margin = new Padding(3, 3, 3, 3),
                    Font = new Font("Segoe UI", 9F, FontStyle.Bold)
                };
                b.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 75);
                b.Click += (s, e) => onClick();
                return b;
            }

            // Row 1: Fast Alignment
            pnlTransformInner.Controls.Add(CreateTBtn("🧍 Redresser Debout (Z-up)", Color.FromArgb(30, 90, 140), AutoStandUp));
            pnlTransformInner.Controls.Add(CreateTBtn("⚡ Pieds au sol (Z=0) & Centrer", Color.FromArgb(20, 100, 70), AutoCenterAndGround));
            pnlTransformInner.Controls.Add(CreateTBtn("⚡ Ajuster taille au squelette (1.75m)", Color.FromArgb(90, 40, 110), AutoScaleToSkeleton));

            // Row 2: Rotations & Mirror
            pnlTransformInner.Controls.Add(CreateTBtn("↻ Rot X +90°", Color.FromArgb(45, 45, 50), () => RotateModel(90f, 0, 0)));
            pnlTransformInner.Controls.Add(CreateTBtn("↺ Rot X -90°", Color.FromArgb(45, 45, 50), () => RotateModel(-90f, 0, 0)));
            pnlTransformInner.Controls.Add(CreateTBtn("↻ Rot Y +90°", Color.FromArgb(45, 45, 50), () => RotateModel(0, 90f, 0)));
            pnlTransformInner.Controls.Add(CreateTBtn("↺ Rot Y -90°", Color.FromArgb(45, 45, 50), () => RotateModel(0, -90f, 0)));
            pnlTransformInner.Controls.Add(CreateTBtn("↻ Rot Z +90°", Color.FromArgb(45, 45, 50), () => RotateModel(0, 0, 90f)));
            pnlTransformInner.Controls.Add(CreateTBtn("↺ Rot Z -90°", Color.FromArgb(45, 45, 50), () => RotateModel(0, 0, -90f)));
            pnlTransformInner.Controls.Add(CreateTBtn("↻ 180°", Color.FromArgb(45, 45, 50), () => RotateModel(0, 0, 180f)));
            pnlTransformInner.Controls.Add(CreateTBtn("⇋ Miroir X", Color.FromArgb(70, 50, 40), ToggleMirrorX));

            // Row 3: Position Offset / Nudge
            var pnlNudge = new Panel { Size = new Size(500, 32), Margin = new Padding(0, 4, 0, 0) };
            var lblNudge = new Label { Text = "Ajuster position :", Location = new Point(0, 6), AutoSize = true, ForeColor = Color.Gainsboro };
            pnlNudge.Controls.Add(lblNudge);
            pnlNudge.Controls.Add(CreateTBtn("X: ◀ -5cm", Color.FromArgb(40, 40, 45), () => NudgeOffset(-0.05f, 0, 0)));
            pnlNudge.Controls.Add(CreateTBtn("▶ +5cm", Color.FromArgb(40, 40, 45), () => NudgeOffset(0.05f, 0, 0)));
            pnlNudge.Controls.Add(CreateTBtn("Y: ▼ -5cm", Color.FromArgb(40, 40, 45), () => NudgeOffset(0, -0.05f, 0)));
            pnlNudge.Controls.Add(CreateTBtn("▲ +5cm", Color.FromArgb(40, 40, 45), () => NudgeOffset(0, 0.05f, 0)));
            pnlNudge.Controls.Add(CreateTBtn("Z: ▼ -5cm", Color.FromArgb(40, 40, 45), () => NudgeOffset(0, 0, -0.05f)));
            pnlNudge.Controls.Add(CreateTBtn("▲ +5cm", Color.FromArgb(40, 40, 45), () => NudgeOffset(0, 0, 0.05f)));
            pnlTransformInner.Controls.Add(pnlNudge);

            // Row 4: Manual scale input
            var pnlScale = new Panel { Size = new Size(500, 32), Margin = new Padding(0, 4, 0, 0) };
            var lblScale = new Label { Text = "Échelle globale :", Location = new Point(0, 6), AutoSize = true, ForeColor = Color.Gainsboro };
            nudScale = new NumericUpDown { DecimalPlaces = 4, Minimum = 0.0001m, Maximum = 1000m, Value = 1.0m, Increment = 0.01m, Location = new Point(110, 4), Width = 90, BackColor = Color.FromArgb(22, 22, 24), ForeColor = Color.White };
            nudScale.ValueChanged += (s, e) => { _scale = (float)nudScale.Value; RebakeModelTransform(); };

            pnlScale.Controls.Add(lblScale);
            pnlScale.Controls.Add(nudScale);
            pnlScale.Controls.Add(CreateTBtn("x0.01 (cm→m)", Color.FromArgb(40, 50, 60), () => { nudScale.Value = Math.Max(0.0001m, nudScale.Value * 0.01m); }));
            pnlScale.Controls.Add(CreateTBtn("x100 (m→cm)", Color.FromArgb(40, 50, 60), () => { nudScale.Value = Math.Min(1000m, nudScale.Value * 100m); }));
            pnlScale.Controls.Add(CreateTBtn("+10%", Color.FromArgb(40, 50, 60), () => { nudScale.Value = Math.Min(1000m, nudScale.Value * 1.1m); }));
            pnlScale.Controls.Add(CreateTBtn("-10%", Color.FromArgb(40, 50, 60), () => { nudScale.Value = Math.Max(0.0001m, nudScale.Value * 0.9m); }));
            pnlTransformInner.Controls.Add(pnlScale);

            // Row 5: Dimensions display
            lblBoundsInfo = new Label
            {
                Text = "Dimensions : calcul en cours…",
                Size = new Size(500, 20),
                ForeColor = Color.LightSkyBlue,
                Margin = new Padding(0, 4, 0, 0)
            };
            pnlTransformInner.Controls.Add(lblBoundsInfo);

            grpTransform.Controls.Add(pnlTransformInner);

            // 3. Skeleton & Rigging Options Group
            var grpSkeleton = new GroupBox
            {
                Text = "3. Squelette UO Biped & Options",
                Dock = DockStyle.Top,
                Height = 115,
                ForeColor = Color.LimeGreen,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Padding = new Padding(10)
            };

            rbMale = new RadioButton { Text = "Homme (H_Male)", Checked = true, Location = new Point(15, 24), AutoSize = true, ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
            rbFemale = new RadioButton { Text = "Femme (H_Female)", Location = new Point(170, 24), AutoSize = true, ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
            rbMale.CheckedChanged += (s, e) => { if (rbMale.Checked) OnSkeletonChanged(false); };
            rbFemale.CheckedChanged += (s, e) => { if (rbFemale.Checked) OnSkeletonChanged(true); };

            chkSymmetry = new CheckBox
            {
                Text = "Symétrie X automatique (déplacer G met à jour D)",
                Checked = true,
                Location = new Point(15, 52),
                AutoSize = true,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular)
            };

            chkForceAutoSkin = new CheckBox
            {
                Text = "Forcer le skinning volumétrique UO (recommandé si modèle statique)",
                Checked = false,
                Location = new Point(15, 78),
                AutoSize = true,
                ForeColor = Color.LightYellow,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular)
            };

            grpSkeleton.Controls.Add(rbMale);
            grpSkeleton.Controls.Add(rbFemale);
            grpSkeleton.Controls.Add(chkSymmetry);
            grpSkeleton.Controls.Add(chkForceAutoSkin);

            // 4. Markers Adjustments Group
            var grpMarkers = new GroupBox
            {
                Text = "4. Positionnement des Articulations (Croix 3D)",
                Dock = DockStyle.Top,
                Height = 330,
                ForeColor = Color.Orange,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Padding = new Padding(10)
            };

            var pnlTopRigBtns = new Panel { Dock = DockStyle.Top, Height = 32 };
            var btnAutoDetect = new Button
            {
                Text = "🎯 Auto-Placement Mixamo (5 repères)",
                Dock = DockStyle.Left,
                Width = 250,
                BackColor = Color.FromArgb(140, 70, 20),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold)
            };
            btnAutoDetect.Click += (s, e) => { AutoPlaceMixamoLandmarks(); };

            var btnRestoreLiveBones = new Button
            {
                Text = "🦴 Squelette d'Origine",
                Dock = DockStyle.Right,
                Width = 230,
                BackColor = Color.FromArgb(40, 70, 110),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold)
            };
            btnRestoreLiveBones.Click += (s, e) =>
            {
                if (_liveOriginalBones != null && _liveOriginalBones.Count > 0)
                {
                    InitMarkersFromLiveBones(_liveOriginalBones);
                    EnsurePreviewModel();
                    glControl.Invalidate();
                }
                else
                {
                    MessageBox.Show("Aucun squelette d'origine en mémoire pour ce modèle.", "Auto-Rig Studio", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            };
            pnlTopRigBtns.Controls.Add(btnAutoDetect);
            pnlTopRigBtns.Controls.Add(btnRestoreLiveBones);

            // Skinning Action Buttons Panel
            var pnlSkinBtns = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(0, 4, 0, 0) };
            var btnRecalcArms = new Button
            {
                Text = "💪 Recalculer Skinning Bras",
                Dock = DockStyle.Left,
                Width = 250,
                BackColor = Color.FromArgb(20, 100, 80),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold)
            };
            btnRecalcArms.Click += (s, e) => { RecalculateArmSkinning(); };

            var btnRecalcAllSkin = new Button
            {
                Text = "🔥 Recalculer Tout le Skinning",
                Dock = DockStyle.Right,
                Width = 230,
                BackColor = Color.FromArgb(90, 40, 100),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold)
            };
            btnRecalcAllSkin.Click += (s, e) => { RecalculateAllSkinning(); };

            pnlSkinBtns.Controls.Add(btnRecalcArms);
            pnlSkinBtns.Controls.Add(btnRecalcAllSkin);

            var lblJoint = new Label { Text = "Croix sélectionnée :", Location = new Point(12, 114), AutoSize = true, ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 9F, FontStyle.Regular) };
            cbJoints = new ComboBox
            {
                Location = new Point(150, 111),
                Width = 280,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(22, 22, 24),
                ForeColor = Color.Gainsboro,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold)
            };
            foreach (var m in _markers) cbJoints.Items.Add(m.DisplayName);
            cbJoints.SelectedIndex = 0;
            cbJoints.SelectedIndexChanged += (s, e) =>
            {
                _selectedMarkerIndex = cbJoints.SelectedIndex;
                UpdateNudValues();
                glControl.Invalidate();
            };

            var pnlCoords = new Panel { Location = new Point(12, 145), Size = new Size(480, 36), Font = new Font("Segoe UI", 9F, FontStyle.Regular) };
            Label CreateLbl(string txt, int x) => new Label { Text = txt, Location = new Point(x, 8), AutoSize = true, ForeColor = Color.Gainsboro };

            nudX = new NumericUpDown { DecimalPlaces = 3, Minimum = -10, Maximum = 10, Increment = 0.01m, Location = new Point(25, 6), Width = 85, BackColor = Color.FromArgb(22, 22, 24), ForeColor = Color.Gainsboro };
            nudY = new NumericUpDown { DecimalPlaces = 3, Minimum = -10, Maximum = 10, Increment = 0.01m, Location = new Point(140, 6), Width = 85, BackColor = Color.FromArgb(22, 22, 24), ForeColor = Color.Gainsboro };
            nudZ = new NumericUpDown { DecimalPlaces = 3, Minimum = -10, Maximum = 10, Increment = 0.01m, Location = new Point(255, 6), Width = 85, BackColor = Color.FromArgb(22, 22, 24), ForeColor = Color.Gainsboro };

            nudX.ValueChanged += (s, e) => OnNudChanged();
            nudY.ValueChanged += (s, e) => OnNudChanged();
            nudZ.ValueChanged += (s, e) => OnNudChanged();

            pnlCoords.Controls.Add(CreateLbl("X:", 5));
            pnlCoords.Controls.Add(nudX);
            pnlCoords.Controls.Add(CreateLbl("Y:", 120));
            pnlCoords.Controls.Add(nudY);
            pnlCoords.Controls.Add(CreateLbl("Z:", 235));
            pnlCoords.Controls.Add(nudZ);

            // Rotation Controls Panel
            pnlRotControls = new Panel { Location = new Point(12, 185), Size = new Size(480, 32), Font = new Font("Segoe UI", 9F, FontStyle.Regular) };
            nudRoll = new NumericUpDown { DecimalPlaces = 1, Minimum = -180, Maximum = 180, Increment = 5m, Location = new Point(115, 4), Width = 75, BackColor = Color.FromArgb(22, 22, 24), ForeColor = Color.Gold };
            nudPitch = new NumericUpDown { DecimalPlaces = 1, Minimum = -90, Maximum = 90, Increment = 5m, Location = new Point(245, 4), Width = 70, BackColor = Color.FromArgb(22, 22, 24), ForeColor = Color.Gainsboro };
            nudYaw = new NumericUpDown { DecimalPlaces = 1, Minimum = -90, Maximum = 90, Increment = 5m, Location = new Point(365, 4), Width = 70, BackColor = Color.FromArgb(22, 22, 24), ForeColor = Color.Gainsboro };

            nudRoll.ValueChanged += (s, e) => OnNudRotChanged();
            nudPitch.ValueChanged += (s, e) => OnNudRotChanged();
            nudYaw.ValueChanged += (s, e) => OnNudRotChanged();

            pnlRotControls.Controls.Add(CreateLbl("Torsion (Roll) :", 5));
            pnlRotControls.Controls.Add(nudRoll);
            pnlRotControls.Controls.Add(CreateLbl("Pitch :", 195));
            pnlRotControls.Controls.Add(nudPitch);
            pnlRotControls.Controls.Add(CreateLbl("Yaw :", 325));
            pnlRotControls.Controls.Add(nudYaw);

            // Quick Rotation Presets Panel
            pnlRotBtns = new Panel { Location = new Point(12, 218), Size = new Size(480, 28) };
            Button CreateRotBtn(string text, int x, int width, Action act)
            {
                var b = new Button
                {
                    Text = text,
                    Location = new Point(x, 1),
                    Size = new Size(width, 25),
                    BackColor = Color.FromArgb(45, 45, 48),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Font = new Font("Segoe UI", 8F, FontStyle.Regular)
                };
                b.FlatAppearance.BorderColor = Color.FromArgb(65, 65, 70);
                b.Click += (s, e) => { act(); };
                return b;
            }

            pnlRotBtns.Controls.Add(CreateRotBtn("⟲ -15°", 5, 55, () => ApplyRollOffset(-15f)));
            pnlRotBtns.Controls.Add(CreateRotBtn("⟳ +15°", 65, 55, () => ApplyRollOffset(15f)));
            pnlRotBtns.Controls.Add(CreateRotBtn("Paume Bas (90°)", 125, 115, () => SetRollExact(90f)));
            pnlRotBtns.Controls.Add(CreateRotBtn("Paume Arr. (180°)", 245, 125, () => SetRollExact(180f)));
            pnlRotBtns.Controls.Add(CreateRotBtn("0° (Défaut)", 375, 80, () => SetRollExact(0f)));

            lblRotTip = new Label
            {
                Text = "💡 Astuce : Maintenez Shift + Glisser Souris sur le poignet pour tourner la paume !",
                Location = new Point(12, 250),
                AutoSize = true,
                ForeColor = Color.DarkKhaki,
                Font = new Font("Segoe UI", 8F, FontStyle.Italic)
            };

            var btnResetPose = new Button
            {
                Text = "🔄 Réinitialiser les repères Biped UO par défaut",
                Dock = DockStyle.Bottom,
                Height = 28,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular)
            };
            btnResetPose.Click += (s, e) => { InitializeDefaultMarkers(); UpdateNudValues(); EnsurePreviewModel(); glControl.Invalidate(); };

            grpMarkers.Controls.Add(btnResetPose);
            grpMarkers.Controls.Add(lblRotTip);
            grpMarkers.Controls.Add(pnlRotBtns);
            grpMarkers.Controls.Add(pnlRotControls);
            grpMarkers.Controls.Add(pnlCoords);
            grpMarkers.Controls.Add(cbJoints);
            grpMarkers.Controls.Add(lblJoint);
            grpMarkers.Controls.Add(pnlSkinBtns);
            grpMarkers.Controls.Add(pnlTopRigBtns);

            // 5. Animation Preview Group
            var grpAnim = new GroupBox
            {
                Text = "5. Prévisualisation en Direct (Test Animation UO)",
                Dock = DockStyle.Top,
                Height = 145,
                ForeColor = Color.MediumOrchid,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Padding = new Padding(10)
            };

            cbAnimTest = new ComboBox
            {
                Dock = DockStyle.Top,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(22, 22, 24),
                ForeColor = Color.Gainsboro,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular)
            };
            cbAnimTest.SelectedIndexChanged += (s, e) => LoadSelectedTestAnimation();

            var pnlAnimBtns = new Panel { Dock = DockStyle.Top, Height = 36, Padding = new Padding(0, 6, 0, 0) };
            btnPlayAnim = new Button
            {
                Text = "▶ Lancer le Test d'Animation",
                Location = new Point(0, 4),
                Width = 220,
                Height = 28,
                BackColor = Color.FromArgb(80, 40, 110),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold)
            };
            btnPlayAnim.Click += (s, e) => TogglePlayAnimation();

            btnStopAnim = new Button
            {
                Text = "⏹ T-Pose (Rest)",
                Location = new Point(228, 4),
                Width = 140,
                Height = 28,
                BackColor = Color.FromArgb(48, 48, 52),
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular)
            };
            btnStopAnim.Click += (s, e) => StopAnimation();

            pnlAnimBtns.Controls.Add(btnPlayAnim);
            pnlAnimBtns.Controls.Add(btnStopAnim);

            tbAnimScrub = new TrackBar
            {
                Dock = DockStyle.Bottom,
                Height = 24,
                Minimum = 0,
                Maximum = 1000,
                TickStyle = TickStyle.None
            };
            tbAnimScrub.Scroll += (s, e) =>
            {
                if (_testAnim != null)
                {
                    _animTime = (tbAnimScrub.Value / 1000f) * _testAnim.Duration;
                    ApplyAnimationPose(_animTime);
                    glControl.Invalidate();
                }
            };

            grpAnim.Controls.Add(tbAnimScrub);
            grpAnim.Controls.Add(pnlAnimBtns);
            grpAnim.Controls.Add(cbAnimTest);

            // 6. Validation Action Group
            var grpAction = new GroupBox
            {
                Text = "6. Enregistrement",
                Dock = DockStyle.Top,
                Height = 90,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Padding = new Padding(10)
            };

            btnValidate = new Button
            {
                Text = "⚡ Valider & Générer le Character UO (.char)",
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(24, 120, 56),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 11F, FontStyle.Bold)
            };
            btnValidate.Click += (s, e) => GenerateAndSaveCharacter();

            grpAction.Controls.Add(btnValidate);

            // Add all groups in order (Dock=Top stacks from bottom up or top down depending on add order)
            pnlSidebar.Controls.Add(grpAction);
            pnlSidebar.Controls.Add(grpAnim);
            pnlSidebar.Controls.Add(grpMarkers);
            pnlSidebar.Controls.Add(grpSkeleton);
            pnlSidebar.Controls.Add(grpTransform);
            pnlSidebar.Controls.Add(grpSource);

            // Reorder panels from top to bottom
            grpSource.BringToFront();
            grpTransform.BringToFront();
            grpSkeleton.BringToFront();
            grpMarkers.BringToFront();
            grpAnim.BringToFront();
            grpAction.BringToFront();

            split.Panel2.Controls.Add(pnlSidebar);
            Controls.Add(split);

            Shown += (s, e) =>
            {
                try
                {
                    split.Panel1MinSize = 250;
                    split.Panel2MinSize = 380;

                    int rightPanelDesiredWidth = 540;
                    int calculatedDist = split.Width - rightPanelDesiredWidth;
                    int maxDist = split.Width - split.Panel2MinSize - 10;
                    int minDist = split.Panel1MinSize;

                    if (calculatedDist > maxDist) calculatedDist = maxDist;
                    if (calculatedDist < minDist) calculatedDist = minDist;

                    if (calculatedDist >= split.Panel1MinSize && calculatedDist <= split.Width - split.Panel2MinSize)
                    {
                        split.SplitterDistance = calculatedDist;
                    }
                }
                catch { }

                glControl.Invalidate();
            };

            FormClosing += (s, e) =>
            {
                _animTimer?.Stop();
                CleanupGlResources();
            };
        }

        private void NudgeOffset(float dx, float dy, float dz)
        {
            var delta = new Vector3(dx, dy, dz);
            _offset += delta;
            foreach (var m in _markers)
            {
                m.Position += delta;
            }
            RebakeModelTransform();
            UpdateNudValues();
            EnsurePreviewModel();
            glControl.Invalidate();
        }

        private void RotateModel(float dx, float dy, float dz)
        {
            _rotX = (_rotX + dx) % 360f;
            _rotY = (_rotY + dy) % 360f;
            _rotZ = (_rotZ + dz) % 360f;
            RebakeModelTransform();
            EnsurePreviewModel();
            glControl.Invalidate();
        }

        private void ToggleMirrorX()
        {
            _mirrorX = !_mirrorX;
            RebakeModelTransform();
            EnsurePreviewModel();
            glControl.Invalidate();
        }

        private void AutoStandUp()
        {
            if (_baseVertices == null || _baseVertices.Length == 0) return;

            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;

            foreach (var v in _baseVertices)
            {
                minX = Math.Min(minX, v.X); maxX = Math.Max(maxX, v.X);
                minY = Math.Min(minY, v.Y); maxY = Math.Max(maxY, v.Y);
                minZ = Math.Min(minZ, v.Z); maxZ = Math.Max(maxZ, v.Z);
            }

            float spanX = maxX - minX;
            float spanY = maxY - minY;
            float spanZ = maxZ - minZ;

            // If depth (Y) is much larger than height (Z), model is lying face-down or on back along Y -> rotate 90 deg around X
            if (spanY > spanZ * 1.1f)
            {
                RotateModel(90f, 0, 0);
            }
            // If width (X) is much larger than height (Z), model is lying along X -> rotate 90 deg around Y
            else if (spanX > spanZ * 1.3f)
            {
                RotateModel(0, 90f, 0);
            }
            else
            {
                // Toggle rotation 90 deg around X
                RotateModel(90f, 0, 0);
            }
            AutoCenterAndGround();
        }

        private void AutoCenterAndGround()
        {
            if (_baseVertices == null || _baseVertices.Length == 0) return;

            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;

            foreach (var v in _baseVertices)
            {
                minX = Math.Min(minX, v.X); maxX = Math.Max(maxX, v.X);
                minY = Math.Min(minY, v.Y); maxY = Math.Max(maxY, v.Y);
                minZ = Math.Min(minZ, v.Z); maxZ = Math.Max(maxZ, v.Z);
            }

            float centerX = (minX + maxX) * 0.5f;
            float centerY = (minY + maxY) * 0.5f;

            var shift = new Vector3(-centerX, -centerY, -minZ);
            _offset += shift;
            foreach (var m in _markers)
            {
                m.Position += shift;
            }

            RebakeModelTransform();
            UpdateNudValues();
            EnsurePreviewModel();
            glControl.Invalidate();
        }

        private void AutoScaleToSkeleton()
        {
            if (_baseVertices == null || _baseVertices.Length == 0) return;

            float minZ = float.MaxValue, maxZ = float.MinValue;
            foreach (var v in _baseVertices)
            {
                minZ = Math.Min(minZ, v.Z);
                maxZ = Math.Max(maxZ, v.Z);
            }

            float currentH = maxZ - minZ;
            if (currentH > 1e-4f)
            {
                float targetH = 1.75f; // Standard UO Biped humanoid height
                float factor = targetH / currentH;
                _scale *= factor;
                if (nudScale != null) nudScale.Value = (decimal)MathHelper.Clamp(_scale, 0.0001f, 1000f);
                RebakeModelTransform();
                AutoCenterAndGround();
            }
        }

        private void RebakeModelTransform()
        {
            if (_rawVertices == null || _rawVertices.Length == 0) return;

            int n = _rawVertices.Length;
            _baseVertices = new Vector3[n];
            _baseNormals = new Vector3[n];

            // Build transformation matrix: Scale -> Rotate -> Translate
            var scaleMat = Matrix4.CreateScale(_mirrorX ? -_scale : _scale, _scale, _scale);
            var rotMat = Matrix4.CreateRotationX(MathHelper.DegreesToRadians(_rotX)) *
                         Matrix4.CreateRotationY(MathHelper.DegreesToRadians(_rotY)) *
                         Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(_rotZ));
            var transMat = Matrix4.CreateTranslation(_offset);
            var mat = scaleMat * rotMat * transMat;
            var normalMat = rotMat;

            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;

            for (int i = 0; i < n; i++)
            {
                var v = Vector3.TransformPosition(_rawVertices[i], mat);
                _baseVertices[i] = v;

                minX = Math.Min(minX, v.X); maxX = Math.Max(maxX, v.X);
                minY = Math.Min(minY, v.Y); maxY = Math.Max(maxY, v.Y);
                minZ = Math.Min(minZ, v.Z); maxZ = Math.Max(maxZ, v.Z);

                if (_rawNormals != null && i < _rawNormals.Length)
                {
                    _baseNormals[i] = Vector3.TransformNormal(_rawNormals[i], normalMat).Normalized();
                }
                else
                {
                    _baseNormals[i] = Vector3.UnitZ;
                }
            }

            float w = maxX - minX;
            float d = maxY - minY;
            float h = maxZ - minZ;
            lblBoundsInfo.Text = $"Taille : Largeur={w:F2}m | Prof={d:F2}m | Hauteur={h:F2}m (Pieds Z: {minZ:F2}m)";

            // Update interleaved buffer for OpenGL
            if (_indices != null)
            {
                _meshVertexCount = _indices.Length;
                _interleaved = new float[_meshVertexCount * 8];
                for (int i = 0; i < _meshVertexCount; i++)
                {
                    int vi = _indices[i];
                    var pos = _baseVertices[vi];
                    var norm = _baseNormals[vi];
                    var uv = (_rawUVs != null && vi < _rawUVs.Length) ? _rawUVs[vi] : Vector2.Zero;

                    int off = i * 8;
                    _interleaved[off + 0] = pos.X;
                    _interleaved[off + 1] = pos.Y;
                    _interleaved[off + 2] = pos.Z;
                    _interleaved[off + 3] = norm.X;
                    _interleaved[off + 4] = norm.Y;
                    _interleaved[off + 5] = norm.Z;
                    _interleaved[off + 6] = uv.X;
                    _interleaved[off + 7] = uv.Y;
                }

                UploadMeshToGl();
            }
        }

        private void OnSkeletonChanged(bool isFemale)
        {
            string maleRef = Path.Combine(_basePath, "Humans", "H_Male_Lod2.grn");
            string femaleRef = Path.Combine(_basePath, "Humans", "H_Female_Lod2.grn");
            if (!File.Exists(maleRef)) maleRef = @"d:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Male_Lod2.grn";
            if (!File.Exists(femaleRef)) femaleRef = @"d:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans\H_Female_Lod2.grn";

            _refPath = isFemale && File.Exists(femaleRef) ? femaleRef : maleRef;
            LoadReferenceBones();
            PopulateTestAnimations();
            EnsurePreviewModel();
        }

        private void LoadReferenceBones()
        {
            try
            {
                if (File.Exists(_refPath))
                {
                    var parser = new GrannyFormatParser();
                    if (parser.Load(_refPath) && parser.Bones.Count > 0)
                    {
                        _refBones = new List<GrannyBone>(parser.Bones);
                        return;
                    }
                }
            }
            catch { }

            // Fallback bones if reference file is missing
            if (_refBones.Count == 0)
            {
                int id = 0;
                foreach (var m in _markers)
                {
                    _refBones.Add(new GrannyBone
                    {
                        Id = id++,
                        ParentId = -1,
                        Name = m.BoneName,
                        LocalPosition = m.Position,
                        LocalRotation = OpenTK.Mathematics.Quaternion.Identity,
                        InverseBindMatrix = Matrix4.Invert(Matrix4.CreateTranslation(m.Position))
                    });
                }
            }
        }

        private void PopulateTestAnimations()
        {
            cbAnimTest.Items.Clear();
            string humansDir = Path.Combine(_basePath, "Humans");
            if (!Directory.Exists(humansDir)) humansDir = @"d:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans";

            if (Directory.Exists(humansDir))
            {
                var files = Directory.GetFiles(humansDir, "*.grn")
                    .Select(Path.GetFileName)
                    .Where(fn => fn != null &&
                                 !fn.EndsWith("_Lod2.grn", StringComparison.OrdinalIgnoreCase) &&
                                 !fn.EndsWith("_Lod1.grn", StringComparison.OrdinalIgnoreCase) &&
                                 !fn.EndsWith("_Lod0.grn", StringComparison.OrdinalIgnoreCase) &&
                                 !fn.StartsWith("Character_", StringComparison.OrdinalIgnoreCase) &&
                                 !fn.StartsWith("Counselor_", StringComparison.OrdinalIgnoreCase) &&
                                 !fn.StartsWith("Dawn_Girl_", StringComparison.OrdinalIgnoreCase) &&
                                 !fn.StartsWith("H_Male_Lod", StringComparison.OrdinalIgnoreCase) &&
                                 !fn.StartsWith("H_Female_Lod", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(fn => fn)
                    .ToList();

                foreach (var f in files)
                    cbAnimTest.Items.Add(f);
            }

            // Find preferred default animation: Walk or Run
            int defaultIdx = -1;
            string prefix = rbFemale.Checked ? "H_Female_" : "H_Male_";
            string walkTarget = prefix + "Walk_01.grn";
            for (int i = 0; i < cbAnimTest.Items.Count; i++)
            {
                if (string.Equals(cbAnimTest.Items[i].ToString(), walkTarget, StringComparison.OrdinalIgnoreCase))
                {
                    defaultIdx = i;
                    break;
                }
            }
            if (defaultIdx < 0)
            {
                for (int i = 0; i < cbAnimTest.Items.Count; i++)
                {
                    if (cbAnimTest.Items[i].ToString()!.IndexOf("Walk", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        defaultIdx = i;
                        break;
                    }
                }
            }

            if (defaultIdx >= 0)
                cbAnimTest.SelectedIndex = defaultIdx;
            else if (cbAnimTest.Items.Count > 0)
                cbAnimTest.SelectedIndex = 0;

            LoadSelectedTestAnimation();
        }

        private void LoadSelectedTestAnimation()
        {
            if (cbAnimTest.SelectedItem == null) return;
            string fn = cbAnimTest.SelectedItem.ToString()!;
            string humansDir = Path.Combine(_basePath, "Humans");
            if (!Directory.Exists(humansDir)) humansDir = @"d:\Games\sphere\PROGRAMMES\3D_UO\Models\Humans";
            string fullPath = Path.Combine(humansDir, fn);

            if (File.Exists(fullPath))
            {
                var parser = new GrannyFormatParser();
                parser.Load(fullPath);
                if (parser.Animation != null)
                {
                    _testAnim = parser.Animation;
                    tbAnimScrub.Value = 0;
                    _animTime = 0f;
                }
            }
        }

        private void TogglePlayAnimation()
        {
            if (_testAnim == null)
            {
                LoadSelectedTestAnimation();
                if (_testAnim == null)
                {
                    MessageBox.Show("Veuillez sélectionner une animation UO valide.", "Test Animation", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
            }

            EnsurePreviewModel();

            if (_animTimer == null)
            {
                _animTimer = new System.Windows.Forms.Timer { Interval = 16 }; // ~60 FPS
                _animTimer.Tick += (s, e) =>
                {
                    if (_testAnim != null && _isPlayingAnim && _testAnim.Duration > 0)
                    {
                        _animTime += 0.016f;
                        if (_animTime > _testAnim.Duration) _animTime %= _testAnim.Duration;
                        tbAnimScrub.Value = (int)MathHelper.Clamp((_animTime / _testAnim.Duration) * 1000f, 0, 1000);
                        ApplyAnimationPose(_animTime);
                        glControl.Invalidate();
                    }
                };
            }

            _isPlayingAnim = !_isPlayingAnim;
            btnPlayAnim.Text = _isPlayingAnim ? "⏸ Pause" : "▶ Lancer le Test";
            btnPlayAnim.BackColor = _isPlayingAnim ? Color.FromArgb(120, 30, 30) : Color.FromArgb(80, 40, 110);
            if (_isPlayingAnim) _animTimer.Start();
            else _animTimer.Stop();
        }

        private void StopAnimation()
        {
            _isPlayingAnim = false;
            _animTimer?.Stop();
            btnPlayAnim.Text = "▶ Lancer le Test";
            btnPlayAnim.BackColor = Color.FromArgb(80, 40, 110);
            _animTime = 0f;
            tbAnimScrub.Value = 0;
            _boneWorldMatrices = null;

            // Re-upload rest pose interleaved buffer
            if (_interleaved != null) UploadMeshToGl();
            glControl.Invalidate();
        }

        private static int GetParentIndex(IList<GrannyBone> bones, int i)
        {
            if (bones == null || i < 0 || i >= bones.Count) return -1;
            int p = bones[i].ParentId;
            if (p < 0 || p == i) return -1;
            if (p < bones.Count) return p;
            for (int k = 0; k < bones.Count; k++)
            {
                if (bones[k].Id == p && k != i) return k;
            }
            return -1;
        }

        private static bool TrackPositionVaries(GrannyTrack? tr, float eps = 0.002f)
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

        private void EnsurePreviewModel()
        {
            if (_refBones == null || _refBones.Count == 0)
                LoadReferenceBones();

            for (int i = 0; i < _refBones.Count; i++) _refBones[i].Id = i;

            if (_previewModel == null)
            {
                _previewModel = new GrannyModelFile
                {
                    Name = !string.IsNullOrEmpty(_currentModelPath) ? Path.GetFileNameWithoutExtension(_currentModelPath) : "PreviewRig",
                    Bones = new List<GrannyBone>()
                };

                for (int i = 0; i < _refBones.Count; i++)
                {
                    var b = _refBones[i];
                    _previewModel.Bones.Add(new GrannyBone
                    {
                        Id = i,
                        ParentId = b.ParentId,
                        Name = b.Name,
                        LocalPosition = b.LocalPosition,
                        LocalRotation = b.LocalRotation,
                        InverseBindMatrix = b.InverseBindMatrix
                    });
                }
            }

            AdaptPreviewBonesToMarkers();

            // Setup preview part for CPU skinning deformation
            if (_baseVertices != null && _baseVertices.Length > 0 && _indices != null)
            {
                _previewModel.Parts.Clear();
                var part = new GrannyModelPart
                {
                    Name = "Mesh",
                    Vertices = _baseVertices,
                    Indices = _indices,
                    Normals = _baseNormals,
                    UVs = _rawUVs,
                    TextureName = _textureName ?? string.Empty
                };

                if (_rawSkin != null && _rawSkin.Length == _baseVertices.Length && _rawSkin.Any(x => x != null && x.Length > 0))
                {
                    part.Skin = _rawSkin;
                }
                else
                {
                    MixamoCharacterImport.ComputeVolumetricSkinning(part, _previewModel.Bones);
                    _rawSkin = part.Skin;
                }
                _previewModel.Parts.Add(part);
            }
        }

        private void AdaptPreviewBonesToMarkers()
        {
            if (_previewModel == null || _previewModel.Bones.Count == 0 || _refBones == null || _refBones.Count == 0) return;

            int nb = _previewModel.Bones.Count;
            for (int i = 0; i < nb; i++) _previewModel.Bones[i].Id = i;

            var uoBindWorld = new Matrix4[nb];
            for (int i = 0; i < nb; i++)
            {
                var inv = _refBones[i].InverseBindMatrix;
                if (Math.Abs(inv.Determinant) > 1e-5f)
                    uoBindWorld[i] = Matrix4.Invert(inv);
                else
                    uoBindWorld[i] = Matrix4.Identity;
            }

            var children = new List<int>[nb];
            for (int i = 0; i < nb; i++) children[i] = new List<int>();
            var roots = new List<int>();

            for (int i = 0; i < nb; i++)
            {
                int p = GetParentIndex(_previewModel.Bones, i);
                if (p >= 0 && p < nb && p != i)
                    children[p].Add(i);
                else
                    roots.Add(i);
            }

            var fittedWorld = new Matrix4[nb];

            void ComputeFitted(int idx)
            {
                string name = _previewModel.Bones[idx].Name;
                var marker = _markers.FirstOrDefault(m => string.Equals(m.BoneName, name, StringComparison.OrdinalIgnoreCase));
                if (marker == null)
                {
                    int mIdx = _markers.FindIndex(m => FindMatchingBoneIndex(_previewModel.Bones, m.BoneName) == idx);
                    if (mIdx >= 0) marker = _markers[mIdx];
                }

                if (marker == null && name.Equals("Bip01", StringComparison.OrdinalIgnoreCase))
                {
                    var pelvMarker = _markers.FirstOrDefault(m => string.Equals(m.BoneName, "Bip01 Pelvis", StringComparison.OrdinalIgnoreCase));
                    if (pelvMarker != null)
                    {
                        int pelvIdx = FindMatchingBoneIndex(_refBones, "Bip01 Pelvis");
                        Vector3 pelvToBip = Vector3.Zero;
                        if (pelvIdx >= 0 && pelvIdx < nb)
                            pelvToBip = uoBindWorld[idx].Row3.Xyz - uoBindWorld[pelvIdx].Row3.Xyz;
                        var rot = uoBindWorld[idx].ExtractRotation();
                        fittedWorld[idx] = Matrix4.CreateFromQuaternion(rot) * Matrix4.CreateTranslation(pelvMarker.Position + pelvToBip);
                    }
                    else
                    {
                        fittedWorld[idx] = uoBindWorld[idx];
                    }
                }
                else if (marker != null)
                {
                    int rotSourceIdx = idx;
                    // If marker is on opposite side of X compared to original UO bone:
                    // e.g. Bip01 L placed at X < -0.01f (screen left), use rotation of symmetric bone (Bip01 R) so arm points outward!
                    if (marker.Position.X < -0.01f && uoBindWorld[idx].Row3.X > 0.01f)
                    {
                        int symIdx = FindMatchingBoneIndex(_refBones, marker.SymmetricBoneName ?? "");
                        if (symIdx >= 0 && symIdx < nb) rotSourceIdx = symIdx;
                    }
                    else if (marker.Position.X > 0.01f && uoBindWorld[idx].Row3.X < -0.01f)
                    {
                        int symIdx = FindMatchingBoneIndex(_refBones, marker.SymmetricBoneName ?? "");
                        if (symIdx >= 0 && symIdx < nb) rotSourceIdx = symIdx;
                    }

                    var rot = uoBindWorld[rotSourceIdx].ExtractRotation();
                    Matrix4 baseRot = Matrix4.CreateFromQuaternion(rot);
                    if (marker.RotationDegrees != Vector3.Zero)
                    {
                        Matrix4 localRot = Matrix4.CreateRotationX(MathHelper.DegreesToRadians(marker.RotationDegrees.X)) *
                                           Matrix4.CreateRotationY(MathHelper.DegreesToRadians(marker.RotationDegrees.Y)) *
                                           Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(marker.RotationDegrees.Z));
                        baseRot = baseRot * localRot;
                    }
                    fittedWorld[idx] = baseRot * Matrix4.CreateTranslation(marker.Position);
                }
                else
                {
                    int p = GetParentIndex(_previewModel.Bones, idx);
                    if (p >= 0 && p < nb && p != idx)
                    {
                        int refSourceIdx = idx;
                        int refParentIdx = p;
                        if (fittedWorld[p].Row3.X < -0.01f && uoBindWorld[p].Row3.X > 0.01f)
                        {
                            string symName = name.Contains(" L ") ? name.Replace(" L ", " R ") : (name.Contains(" R ") ? name.Replace(" R ", " L ") : name);
                            string symPName = _previewModel.Bones[p].Name.Contains(" L ") ? _previewModel.Bones[p].Name.Replace(" L ", " R ") : (_previewModel.Bones[p].Name.Contains(" R ") ? _previewModel.Bones[p].Name.Replace(" R ", " L ") : _previewModel.Bones[p].Name);
                            int sIdx = FindMatchingBoneIndex(_refBones, symName);
                            int spIdx = FindMatchingBoneIndex(_refBones, symPName);
                            if (sIdx >= 0 && spIdx >= 0)
                            {
                                refSourceIdx = sIdx;
                                refParentIdx = spIdx;
                            }
                        }
                        else if (fittedWorld[p].Row3.X > 0.01f && uoBindWorld[p].Row3.X < -0.01f)
                        {
                            string symName = name.Contains(" L ") ? name.Replace(" L ", " R ") : (name.Contains(" R ") ? name.Replace(" R ", " L ") : name);
                            string symPName = _previewModel.Bones[p].Name.Contains(" L ") ? _previewModel.Bones[p].Name.Replace(" L ", " R ") : (_previewModel.Bones[p].Name.Contains(" R ") ? _previewModel.Bones[p].Name.Replace(" R ", " L ") : _previewModel.Bones[p].Name);
                            int sIdx = FindMatchingBoneIndex(_refBones, symName);
                            int spIdx = FindMatchingBoneIndex(_refBones, symPName);
                            if (sIdx >= 0 && spIdx >= 0)
                            {
                                refSourceIdx = sIdx;
                                refParentIdx = spIdx;
                            }
                        }

                        var uoLocal = uoBindWorld[refSourceIdx] * Matrix4.Invert(uoBindWorld[refParentIdx]);
                        fittedWorld[idx] = uoLocal * fittedWorld[p];
                    }
                    else
                    {
                        fittedWorld[idx] = uoBindWorld[idx];
                    }
                }

                foreach (int child in children[idx])
                    ComputeFitted(child);
            }

            foreach (int r in roots)
                ComputeFitted(r);

            // Recompute local transforms and inverse bind matrices for preview bones
            for (int i = 0; i < nb; i++)
            {
                int p = GetParentIndex(_previewModel.Bones, i);
                if (p >= 0 && p < nb && p != i)
                {
                    var localMat = fittedWorld[i] * Matrix4.Invert(fittedWorld[p]);
                    _previewModel.Bones[i].LocalPosition = localMat.ExtractTranslation();
                    _previewModel.Bones[i].LocalRotation = localMat.ExtractRotation();
                }
                else
                {
                    _previewModel.Bones[i].LocalPosition = fittedWorld[i].ExtractTranslation();
                    _previewModel.Bones[i].LocalRotation = fittedWorld[i].ExtractRotation();
                }
                _previewModel.Bones[i].InverseBindMatrix = Matrix4.Invert(fittedWorld[i]);
            }
        }

        private static int FindMatchingBoneIndex(List<GrannyBone> bones, string uoTargetName)
        {
            if (bones == null || bones.Count == 0 || string.IsNullOrEmpty(uoTargetName)) return -1;

            int idx = bones.FindIndex(b => string.Equals(b.Name, uoTargetName, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) return idx;

            foreach (var (mix, uo) in MixamoRetarget.BoneMap)
            {
                if (string.Equals(uo, uoTargetName, StringComparison.OrdinalIgnoreCase))
                {
                    string cleanMix = mix.StartsWith("mixamorig:") ? mix.Substring(10) : mix;
                    idx = bones.FindIndex(b =>
                        string.Equals(b.Name, mix, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(b.Name, cleanMix, StringComparison.OrdinalIgnoreCase) ||
                        b.Name.EndsWith(":" + cleanMix, StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0) return idx;
                }
            }

            string lower = uoTargetName.ToLowerInvariant();
            bool isLeft = lower.Contains(" l ") || lower.Contains("left") || lower.EndsWith("_l");
            bool isRight = lower.Contains(" r ") || lower.Contains("right") || lower.EndsWith("_r");

            if (lower.Contains("head"))
                return bones.FindIndex(b => b.Name.IndexOf("head", StringComparison.OrdinalIgnoreCase) >= 0);
            if (lower.Contains("neck"))
                return bones.FindIndex(b => b.Name.IndexOf("neck", StringComparison.OrdinalIgnoreCase) >= 0);
            if (lower.Contains("pelvis") || lower.Contains("hips"))
                return bones.FindIndex(b => b.Name.IndexOf("pelvis", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("hips", StringComparison.OrdinalIgnoreCase) >= 0);
            if (lower.Contains("spine2") || lower.Contains("chest"))
                return bones.FindIndex(b => b.Name.IndexOf("spine2", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("spine_02", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("chest", StringComparison.OrdinalIgnoreCase) >= 0);
            if (lower.Contains("spine"))
                return bones.FindIndex(b => b.Name.IndexOf("spine", StringComparison.OrdinalIgnoreCase) >= 0);

            if (isLeft)
            {
                if (lower.Contains("hand"))
                    return bones.FindIndex(b => (b.Name.IndexOf("hand", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("wrist", StringComparison.OrdinalIgnoreCase) >= 0) && (b.Name.IndexOf("left", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("_l", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf(" l ", StringComparison.OrdinalIgnoreCase) >= 0));
                if (lower.Contains("forearm"))
                    return bones.FindIndex(b => (b.Name.IndexOf("forearm", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("elbow", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("lowerarm", StringComparison.OrdinalIgnoreCase) >= 0) && (b.Name.IndexOf("left", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("_l", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf(" l ", StringComparison.OrdinalIgnoreCase) >= 0));
                if (lower.Contains("upperarm"))
                    return bones.FindIndex(b => (b.Name.IndexOf("upperarm", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("arm", StringComparison.OrdinalIgnoreCase) >= 0) && (b.Name.IndexOf("left", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("_l", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf(" l ", StringComparison.OrdinalIgnoreCase) >= 0));
                if (lower.Contains("clavicle"))
                    return bones.FindIndex(b => (b.Name.IndexOf("clavicle", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("shoulder", StringComparison.OrdinalIgnoreCase) >= 0) && (b.Name.IndexOf("left", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("_l", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf(" l ", StringComparison.OrdinalIgnoreCase) >= 0));
                if (lower.Contains("calf"))
                    return bones.FindIndex(b => (b.Name.IndexOf("calf", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("knee", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("leg", StringComparison.OrdinalIgnoreCase) >= 0) && (b.Name.IndexOf("left", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("_l", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf(" l ", StringComparison.OrdinalIgnoreCase) >= 0));
                if (lower.Contains("thigh"))
                    return bones.FindIndex(b => (b.Name.IndexOf("thigh", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("upleg", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("hip", StringComparison.OrdinalIgnoreCase) >= 0) && (b.Name.IndexOf("left", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("_l", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf(" l ", StringComparison.OrdinalIgnoreCase) >= 0));
                if (lower.Contains("foot"))
                    return bones.FindIndex(b => (b.Name.IndexOf("foot", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("ankle", StringComparison.OrdinalIgnoreCase) >= 0) && (b.Name.IndexOf("left", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("_l", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf(" l ", StringComparison.OrdinalIgnoreCase) >= 0));
            }
            else if (isRight)
            {
                if (lower.Contains("hand"))
                    return bones.FindIndex(b => (b.Name.IndexOf("hand", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("wrist", StringComparison.OrdinalIgnoreCase) >= 0) && (b.Name.IndexOf("right", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("_r", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf(" r ", StringComparison.OrdinalIgnoreCase) >= 0));
                if (lower.Contains("forearm"))
                    return bones.FindIndex(b => (b.Name.IndexOf("forearm", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("elbow", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("lowerarm", StringComparison.OrdinalIgnoreCase) >= 0) && (b.Name.IndexOf("right", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("_r", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf(" r ", StringComparison.OrdinalIgnoreCase) >= 0));
                if (lower.Contains("upperarm"))
                    return bones.FindIndex(b => (b.Name.IndexOf("upperarm", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("arm", StringComparison.OrdinalIgnoreCase) >= 0) && (b.Name.IndexOf("right", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("_r", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf(" r ", StringComparison.OrdinalIgnoreCase) >= 0));
                if (lower.Contains("clavicle"))
                    return bones.FindIndex(b => (b.Name.IndexOf("clavicle", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("shoulder", StringComparison.OrdinalIgnoreCase) >= 0) && (b.Name.IndexOf("right", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("_r", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf(" r ", StringComparison.OrdinalIgnoreCase) >= 0));
                if (lower.Contains("calf"))
                    return bones.FindIndex(b => (b.Name.IndexOf("calf", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("knee", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("leg", StringComparison.OrdinalIgnoreCase) >= 0) && (b.Name.IndexOf("right", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("_r", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf(" r ", StringComparison.OrdinalIgnoreCase) >= 0));
                if (lower.Contains("thigh"))
                    return bones.FindIndex(b => (b.Name.IndexOf("thigh", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("upleg", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("hip", StringComparison.OrdinalIgnoreCase) >= 0) && (b.Name.IndexOf("right", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("_r", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf(" r ", StringComparison.OrdinalIgnoreCase) >= 0));
                if (lower.Contains("foot"))
                    return bones.FindIndex(b => (b.Name.IndexOf("foot", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("ankle", StringComparison.OrdinalIgnoreCase) >= 0) && (b.Name.IndexOf("right", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("_r", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf(" r ", StringComparison.OrdinalIgnoreCase) >= 0));
            }

            return -1;
        }

        private void InitMarkersFromLiveBones(List<GrannyBone> bones)
        {
            if (bones == null || bones.Count == 0) return;

            int nb = bones.Count;
            for (int i = 0; i < nb; i++) bones[i].Id = i;
            var boneWorldMat = new Matrix4[nb];
            var boneWorldPos = new Vector3[nb];

            for (int i = 0; i < nb; i++)
            {
                var inv = bones[i].InverseBindMatrix;
                if (Math.Abs(inv.Determinant) > 1e-5f)
                {
                    boneWorldMat[i] = Matrix4.Invert(inv);
                    boneWorldPos[i] = boneWorldMat[i].Row3.Xyz;
                }
            }

            var children = new List<int>[nb];
            for (int i = 0; i < nb; i++) children[i] = new List<int>();
            var roots = new List<int>();

            for (int i = 0; i < nb; i++)
            {
                int p = GetParentIndex(bones, i);
                if (p >= 0 && p < nb && p != i)
                    children[p].Add(i);
                else
                    roots.Add(i);
            }

            void ComputeFk(int idx)
            {
                if (boneWorldPos[idx] == Vector3.Zero)
                {
                    int p = GetParentIndex(bones, idx);
                    var local = Matrix4.CreateFromQuaternion(bones[idx].LocalRotation) * Matrix4.CreateTranslation(bones[idx].LocalPosition);
                    if (p >= 0 && p < nb && p != idx)
                        boneWorldMat[idx] = local * boneWorldMat[p];
                    else
                        boneWorldMat[idx] = local;
                    boneWorldPos[idx] = boneWorldMat[idx].Row3.Xyz;
                }

                foreach (int child in children[idx])
                    ComputeFk(child);
            }

            foreach (int r in roots)
                ComputeFk(r);

            foreach (var m in _markers)
            {
                int bIdx = FindMatchingBoneIndex(bones, m.BoneName);
                if (bIdx >= 0 && bIdx < nb)
                {
                    m.Position = boneWorldPos[bIdx];
                }
            }

            UpdateNudValues();
        }

        private void AutoPlaceMixamoLandmarks()
        {
            if (_baseVertices == null || _baseVertices.Length == 0) return;

            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;

            foreach (var v in _baseVertices)
            {
                minX = Math.Min(minX, v.X); maxX = Math.Max(maxX, v.X);
                minY = Math.Min(minY, v.Y); maxY = Math.Max(maxY, v.Y);
                minZ = Math.Min(minZ, v.Z); maxZ = Math.Max(maxZ, v.Z);
            }

            float height = Math.Max(0.1f, maxZ - minZ);
            float halfWidth = Math.Max(0.05f, (maxX - minX) * 0.5f);
            float midY = (minY + maxY) * 0.5f;

            void SetPos(string name, Vector3 pos)
            {
                var m = _markers.FirstOrDefault(x => string.Equals(x.BoneName, name, StringComparison.OrdinalIgnoreCase));
                if (m != null) m.Position = pos;
            }

            // 1. Menton / Tête (Chin)
            SetPos("Bip01 Head", new Vector3(0f, midY, minZ + height * 0.90f));
            SetPos("Bip01 Neck", new Vector3(0f, midY, minZ + height * 0.83f));

            // 2. Bassin / Entrejambe (Groin / Pelvis)
            SetPos("Bip01 Spine2", new Vector3(0f, midY, minZ + height * 0.74f));
            SetPos("Bip01 Spine", new Vector3(0f, midY, minZ + height * 0.62f));
            SetPos("Bip01 Pelvis", new Vector3(0f, midY, minZ + height * 0.52f));

            // 3. Bras (Clavicules, Épaules, Coudes, Poignets) - G = -X, D = +X
            float armY = midY;
            SetPos("Bip01 L Clavicle", new Vector3(-halfWidth * 0.25f, armY, minZ + height * 0.76f));
            SetPos("Bip01 R Clavicle", new Vector3(halfWidth * 0.25f, armY, minZ + height * 0.76f));

            SetPos("Bip01 L UpperArm", new Vector3(-halfWidth * 0.45f, armY, minZ + height * 0.74f));
            SetPos("Bip01 R UpperArm", new Vector3(halfWidth * 0.45f, armY, minZ + height * 0.74f));

            // Coudes (Elbows)
            SetPos("Bip01 L Forearm", new Vector3(-halfWidth * 0.72f, armY, minZ + height * 0.72f));
            SetPos("Bip01 R Forearm", new Vector3(halfWidth * 0.72f, armY, minZ + height * 0.72f));

            // Poignets (Wrists)
            SetPos("Bip01 L Hand", new Vector3(-halfWidth * 0.95f, armY, minZ + height * 0.70f));
            SetPos("Bip01 R Hand", new Vector3(halfWidth * 0.95f, armY, minZ + height * 0.70f));

            // 4. Jambes (Hanches, Genoux, Chevilles)
            SetPos("Bip01 L Thigh", new Vector3(-halfWidth * 0.22f, midY, minZ + height * 0.48f));
            SetPos("Bip01 R Thigh", new Vector3(halfWidth * 0.22f, midY, minZ + height * 0.48f));

            // Genoux (Knees)
            SetPos("Bip01 L Calf", new Vector3(-halfWidth * 0.22f, midY, minZ + height * 0.26f));
            SetPos("Bip01 R Calf", new Vector3(halfWidth * 0.22f, midY, minZ + height * 0.26f));

            // Chevilles (Ankles)
            SetPos("Bip01 L Foot", new Vector3(-halfWidth * 0.22f, midY + 0.03f, minZ + height * 0.04f));
            SetPos("Bip01 R Foot", new Vector3(halfWidth * 0.22f, midY + 0.03f, minZ + height * 0.04f));

            UpdateNudValues();
            EnsurePreviewModel();
            glControl.Invalidate();
        }

        private void RecalculateArmSkinning()
        {
            if (_previewModel == null || _previewModel.Parts.Count == 0 || _baseVertices == null) return;
            EnsurePreviewModel();
            var part = _previewModel.Parts[0];
            if (part.Skin == null || part.Skin.Length != _baseVertices.Length)
            {
                MixamoCharacterImport.ComputeVolumetricSkinning(part, _previewModel.Bones);
                _rawSkin = part.Skin;
                return;
            }

            int nb = _previewModel.Bones.Count;
            var boneByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < nb; i++) boneByName[_previewModel.Bones[i].Name] = i;

            var boneWorldPos = new Vector3[nb];
            for (int i = 0; i < nb; i++)
                boneWorldPos[i] = Matrix4.Invert(_previewModel.Bones[i].InverseBindMatrix).Row3.Xyz;

            var armSegments = new List<(int BoneId, Vector3 P0, Vector3 P1)>();
            void AddSeg(string name, string endName)
            {
                int b0 = FindMatchingBoneIndex(_previewModel.Bones, name);
                int b1 = FindMatchingBoneIndex(_previewModel.Bones, endName);
                if (b0 >= 0 && b1 >= 0)
                    armSegments.Add((b0, boneWorldPos[b0], boneWorldPos[b1]));
                else if (b0 >= 0)
                    armSegments.Add((b0, boneWorldPos[b0], boneWorldPos[b0]));
            }

            AddSeg("Bip01 L Clavicle", "Bip01 L UpperArm");
            AddSeg("Bip01 L UpperArm", "Bip01 L Forearm");
            AddSeg("Bip01 L Forearm", "Bip01 L Hand");
            AddSeg("Bip01 L Hand", "Bip01 L Finger1");
            AddSeg("Bip01 L Hand", "Bip01 L Finger0");
            AddSeg("Bip01 L Finger0", "Bip01 L Finger01");
            AddSeg("Bip01 L Finger1", "Bip01 L Finger11");

            AddSeg("Bip01 R Clavicle", "Bip01 R UpperArm");
            AddSeg("Bip01 R UpperArm", "Bip01 R Forearm");
            AddSeg("Bip01 R Forearm", "Bip01 R Hand");
            AddSeg("Bip01 R Hand", "Bip01 R Finger1");
            AddSeg("Bip01 R Hand", "Bip01 R Finger0");
            AddSeg("Bip01 R Finger0", "Bip01 R Finger01");
            AddSeg("Bip01 R Finger1", "Bip01 R Finger11");

            var armBoneIndices = new HashSet<int>();
            foreach (var seg in armSegments) armBoneIndices.Add(seg.BoneId);

            int updatedVerts = 0;
            for (int v = 0; v < _baseVertices.Length; v++)
            {
                var p = _baseVertices[v];
                bool isArmVert = (MathF.Abs(p.X) > 0.16f && p.Z > 0.85f);
                if (!isArmVert && part.Skin[v] != null)
                {
                    foreach (var inf in part.Skin[v])
                    {
                        if (armBoneIndices.Contains(inf.BoneId) && inf.Weight > 0.15f)
                        {
                            isArmVert = true;
                            break;
                        }
                    }
                }

                if (!isArmVert) continue;

                var candidates = new List<(int BoneId, float Weight)>();
                foreach (var seg in armSegments)
                {
                    string bName = _previewModel.Bones[seg.BoneId].Name;
                    bool isLeft = bName.Contains(" L ");
                    bool isRight = bName.Contains(" R ");
                    if (isLeft && p.X > 0.02f) continue;
                    if (isRight && p.X < -0.02f) continue;

                    Vector3 ab = seg.P1 - seg.P0;
                    float l2 = ab.LengthSquared;
                    float dist = l2 < 1e-6f ? (p - seg.P0).Length : (p - (seg.P0 + MathHelper.Clamp(Vector3.Dot(p - seg.P0, ab) / l2, 0f, 1f) * ab)).Length;

                    float w = 1.0f / (MathF.Pow(dist, 4f) + 1e-4f);
                    candidates.Add((seg.BoneId, w));
                }

                candidates.Sort((a, b) => b.Weight.CompareTo(a.Weight));
                int count = Math.Min(4, candidates.Count);
                if (count > 0)
                {
                    float sum = 0f;
                    for (int i = 0; i < count; i++) sum += candidates[i].Weight;
                    var infs = new VertexInfluence[count];
                    for (int i = 0; i < count; i++)
                    {
                        infs[i] = new VertexInfluence
                        {
                            BoneId = candidates[i].BoneId,
                            Weight = sum > 1e-6f ? (candidates[i].Weight / sum) : (i == 0 ? 1f : 0f)
                        };
                    }
                    part.Skin[v] = infs;
                    updatedVerts++;
                }
            }

            _rawSkin = part.Skin;
            MessageBox.Show($"Skinning des bras recalculé avec succès ({updatedVerts:N0} sommets mis à jour).\nLe squelette et le reste du corps sont préservés.", "Auto-Rig Studio", MessageBoxButtons.OK, MessageBoxIcon.Information);
            glControl.Invalidate();
        }

        private void RecalculateAllSkinning()
        {
            if (_previewModel == null || _previewModel.Parts.Count == 0 || _baseVertices == null) return;
            EnsurePreviewModel();
            var part = _previewModel.Parts[0];
            MixamoCharacterImport.ComputeVolumetricSkinning(part, _previewModel.Bones);
            _rawSkin = part.Skin;
            MessageBox.Show($"Skinning volumétrique Mixamo complet recalculé pour {part.Vertices.Length:N0} sommets.", "Auto-Rig Studio", MessageBoxButtons.OK, MessageBoxIcon.Information);
            glControl.Invalidate();
        }

        private void ApplyAnimationPose(float time)
        {
            EnsurePreviewModel();
            if (_testAnim == null || _previewModel == null || _previewModel.Bones.Count == 0) return;

            int nb = _previewModel.Bones.Count;
            if (_boneWorldMatrices == null || _boneWorldMatrices.Length != nb)
                _boneWorldMatrices = new Matrix4[nb];

            var children = new List<int>[nb];
            for (int i = 0; i < nb; i++) children[i] = new List<int>();
            var roots = new List<int>();

            for (int i = 0; i < nb; i++)
            {
                int p = GetParentIndex(_previewModel.Bones, i);
                if (p >= 0 && p < nb && p != i)
                    children[p].Add(i);
                else
                    roots.Add(i);
            }

            void EvalBone(int idx)
            {
                var bone = _previewModel.Bones[idx];
                GrannyTrack? track = null;
                if (_testAnim.TracksByName != null && _testAnim.TracksByName.TryGetValue(bone.Name, out track))
                {
                }
                else
                {
                    track = _testAnim.Tracks.FirstOrDefault(t => string.Equals(t.Name, bone.Name, StringComparison.OrdinalIgnoreCase));
                }

                if (track == null)
                {
                    string bName = bone.Name;
                    foreach (var (mix, uo) in MixamoRetarget.BoneMap)
                    {
                        if (string.Equals(bName, mix, StringComparison.OrdinalIgnoreCase) ||
                            bName.EndsWith(":" + (mix.Contains(':') ? mix.Substring(mix.IndexOf(':') + 1) : mix), StringComparison.OrdinalIgnoreCase))
                        {
                            track = _testAnim.Tracks.FirstOrDefault(t => string.Equals(t.Name, uo, StringComparison.OrdinalIgnoreCase));
                            if (track != null) break;
                        }
                        else if (string.Equals(bName, uo, StringComparison.OrdinalIgnoreCase))
                        {
                            track = _testAnim.Tracks.FirstOrDefault(t => string.Equals(t.Name, mix, StringComparison.OrdinalIgnoreCase) ||
                                                                        t.Name.EndsWith(":" + (mix.Contains(':') ? mix.Substring(mix.IndexOf(':') + 1) : mix), StringComparison.OrdinalIgnoreCase));
                            if (track != null) break;
                        }
                    }
                }

                Matrix4 localTransform;
                int p = GetParentIndex(_previewModel.Bones, idx);
                bool isRoot = p < 0 || p >= nb || p == idx ||
                              bone.Name.Equals("Bip01", StringComparison.OrdinalIgnoreCase);

                if (track != null)
                {
                    bool useAnimPos = isRoot ||
                                      bone.Name.StartsWith("CP_", StringComparison.OrdinalIgnoreCase) ||
                                      TrackPositionVaries(track, 0.002f);
                    Vector3 pos;
                    if (bone.Name.Equals("Bip01", StringComparison.OrdinalIgnoreCase) && _refBones != null && idx < _refBones.Count)
                    {
                        var refBindPos = Matrix4.Invert(_refBones[idx].InverseBindMatrix).Row3.Xyz;
                        var animPos = track.EvaluatePos(time);
                        var animDelta = animPos - refBindPos;
                        pos = bone.LocalPosition + animDelta;
                    }
                    else
                    {
                        pos = useAnimPos ? track.EvaluatePos(time) : bone.LocalPosition;
                    }
                    OpenTK.Mathematics.Quaternion rot = track.EvaluateRot(time);
                    localTransform = Matrix4.CreateFromQuaternion(rot) * Matrix4.CreateTranslation(pos);
                }
                else
                {
                    localTransform = Matrix4.CreateFromQuaternion(bone.LocalRotation) * Matrix4.CreateTranslation(bone.LocalPosition);
                }

                if (p >= 0 && p < nb && p != idx)
                {
                    _boneWorldMatrices[idx] = localTransform * _boneWorldMatrices[p];
                }
                else
                {
                    _boneWorldMatrices[idx] = localTransform;
                }

                foreach (int child in children[idx])
                    EvalBone(child);
            }

            foreach (int r in roots)
                EvalBone(r);

            // CPU skinning deformation for the animated test mesh
            if (_baseVertices != null && _indices != null && _previewModel.Parts.Count > 0)
            {
                var part = _previewModel.Parts[0];
                if (part.Skin != null && part.Skin.Length == _baseVertices.Length)
                {
                    if (_animatedInterleaved == null || _animatedInterleaved.Length != _meshVertexCount * 8)
                        _animatedInterleaved = new float[_meshVertexCount * 8];

                    var skinMats = new Matrix4[nb];
                    for (int b = 0; b < nb; b++)
                        skinMats[b] = _previewModel.Bones[b].InverseBindMatrix * _boneWorldMatrices[b];

                    for (int i = 0; i < _meshVertexCount; i++)
                    {
                        int vi = _indices[i];
                        var p0 = _baseVertices[vi];
                        var infs = part.Skin[vi];

                        Vector3 pAnim = Vector3.Zero;
                        if (infs != null && infs.Length > 0)
                        {
                            foreach (var inf in infs)
                            {
                                if (inf.BoneId >= 0 && inf.BoneId < nb)
                                {
                                    pAnim += Vector3.TransformPosition(p0, skinMats[inf.BoneId]) * inf.Weight;
                                }
                            }
                        }
                        else
                        {
                            pAnim = p0;
                        }

                        int off = i * 8;
                        _animatedInterleaved[off + 0] = pAnim.X;
                        _animatedInterleaved[off + 1] = pAnim.Y;
                        _animatedInterleaved[off + 2] = pAnim.Z;
                        _animatedInterleaved[off + 3] = _interleaved != null ? _interleaved[off + 3] : 0f;
                        _animatedInterleaved[off + 4] = _interleaved != null ? _interleaved[off + 4] : 0f;
                        _animatedInterleaved[off + 5] = _interleaved != null ? _interleaved[off + 5] : 1f;
                        _animatedInterleaved[off + 6] = _interleaved != null ? _interleaved[off + 6] : 0f;
                        _animatedInterleaved[off + 7] = _interleaved != null ? _interleaved[off + 7] : 0f;
                    }

                    // Upload deformed vertices
                    if (glControl.IsHandleCreated)
                    {
                        try
                        {
                            glControl.MakeCurrent();
                            GL.BindBuffer(BufferTarget.ArrayBuffer, _meshVbo);
                            GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, _animatedInterleaved.Length * sizeof(float), _animatedInterleaved);
                            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
                        }
                        catch { }
                    }
                }
            }
        }

        private void BrowseModelFile()
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Sélectionner un modèle 3D pour le Rigging UO",
                Filter = "Modèles 3D (*.fbx;*.obj;*.char;*.grn;*.dae)|*.fbx;*.obj;*.char;*.grn;*.dae|FBX (*.fbx)|*.fbx|OBJ (*.obj)|*.obj|Character UO (*.char)|*.char|Granny (*.grn)|*.grn|Tous (*.*)|*.*",
                InitialDirectory = !string.IsNullOrEmpty(_currentModelPath) ? Path.GetDirectoryName(_currentModelPath) : _outFolder
            };

            if (ofd.ShowDialog(this) == DialogResult.OK)
            {
                LoadModelFile(ofd.FileName);
            }
        }

        private void LoadFromLiveMeshes(List<LoadedMesh> meshes)
        {
            try
            {
                _liveMeshes = meshes;
                var vertList = new List<Vector3>();
                var normList = new List<Vector3>();
                var uvList = new List<Vector2>();
                var idxList = new List<int>();
                var skinList = new List<VertexInfluence[]>();

                int totalVerts = 0;
                int totalFaces = 0;

                foreach (var lm in meshes)
                {
                    if (lm.BaseVertices == null || lm.BaseVertices.Length == 0) continue;
                    int baseIdx = vertList.Count;

                    for (int i = 0; i < lm.BaseVertices.Length; i++)
                    {
                        vertList.Add(lm.BaseVertices[i]);
                        Vector3 n = (lm.BaseCornerNormals != null && i < lm.BaseCornerNormals.Length) ? lm.BaseCornerNormals[i] : Vector3.UnitZ;
                        normList.Add(n);
                        Vector2 uv = (lm.SourceMesh?.UVs != null && i < lm.SourceMesh.UVs.Count) ? lm.SourceMesh.UVs[i] : Vector2.Zero;
                        uvList.Add(uv);
                        if (lm.Skin != null && i < lm.Skin.Length)
                            skinList.Add(lm.Skin[i]);
                        else
                            skinList.Add(Array.Empty<VertexInfluence>());
                    }

                    if (lm.Indices != null)
                    {
                        for (int i = 0; i < lm.Indices.Length; i += 3)
                        {
                            if (i + 2 < lm.Indices.Length)
                            {
                                idxList.Add(baseIdx + lm.Indices[i]);
                                idxList.Add(baseIdx + lm.Indices[i + 1]);
                                idxList.Add(baseIdx + lm.Indices[i + 2]);
                            }
                        }
                    }

                    totalVerts += lm.BaseVertices.Length;
                    totalFaces += (lm.Indices?.Length ?? 0) / 3;
                }

                if (vertList.Count == 0) return;

                _rawVertices = vertList.ToArray();
                _rawNormals = normList.ToArray();
                _rawUVs = uvList.ToArray();
                _indices = idxList.ToArray();
                _rawSkin = skinList.ToArray();

                if (string.IsNullOrEmpty(_currentModelPath))
                {
                    _currentModelPath = meshes.FirstOrDefault(m => !string.IsNullOrEmpty(m.SourcePath))?.SourcePath;
                }
                _textureName = meshes.FirstOrDefault(m => !string.IsNullOrEmpty(m.TextureName))?.TextureName;
                string fn = !string.IsNullOrEmpty(_currentModelPath) ? Path.GetFileName(_currentModelPath) : "Scène UOFiddler active";

                lblModelInfo.Text = $"Modèle actif : {fn}\n" +
                                    $"Sommets : {totalVerts:N0} | Faces : {totalFaces:N0}\n" +
                                    $"Source : Scène active UOFiddler (Preview de gauche)";

                // Live meshes in UO viewer are already Z-up meters
                _scale = 1.0f;
                nudScale.Value = 1.0m;
                _rotX = 0f; _rotY = 0f; _rotZ = 0f;
                _offset = Vector3.Zero;
                _mirrorX = false;

                RebakeModelTransform();

                var meshWithBones = meshes.FirstOrDefault(m => m.Bones != null && m.Bones.Count > 0);
                if (meshWithBones != null)
                {
                    for (int i = 0; i < meshWithBones.Bones.Count; i++) meshWithBones.Bones[i].Id = i;
                    _liveOriginalBones = new List<GrannyBone>(meshWithBones.Bones);
                    InitMarkersFromLiveBones(meshWithBones.Bones);
                }
                else
                {
                    AutoPlaceMixamoLandmarks();
                }

                EnsurePreviewModel();
                glControl.Invalidate();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erreur chargement scène active : " + ex.Message, "Auto-Rig Studio", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void LoadModelFile(string filePath)
        {
            try
            {
                _currentModelPath = filePath;
                string fn = Path.GetFileName(filePath);
                string ext = Path.GetExtension(filePath).ToLowerInvariant();

                if (fn.IndexOf("female", StringComparison.OrdinalIgnoreCase) >= 0)
                    rbFemale.Checked = true;
                else
                    rbMale.Checked = true;

                // 1) Is it a .char file?
                if (ext == ".char")
                {
                    var model = GrannyModelFile.Load(filePath);
                    if (model != null && model.Parts.Count > 0)
                    {
                        var vList = new List<Vector3>();
                        var nList = new List<Vector3>();
                        var uList = new List<Vector2>();
                        var iList = new List<int>();
                        var sList = new List<VertexInfluence[]>();

                        foreach (var part in model.Parts)
                        {
                            int baseIdx = vList.Count;
                            for (int i = 0; i < part.Vertices.Length; i++)
                            {
                                vList.Add(part.Vertices[i]);
                                nList.Add(part.Normals != null && i < part.Normals.Length ? part.Normals[i] : Vector3.UnitZ);
                                uList.Add(part.UVs != null && i < part.UVs.Length ? part.UVs[i] : Vector2.Zero);
                                sList.Add(part.Skin != null && i < part.Skin.Length ? part.Skin[i] : Array.Empty<VertexInfluence>());
                            }
                            if (part.Indices != null)
                            {
                                for (int i = 0; i < part.Indices.Length; i++)
                                    iList.Add(baseIdx + part.Indices[i]);
                            }
                        }

                        _rawVertices = vList.ToArray();
                        _rawNormals = nList.ToArray();
                        _rawUVs = uList.ToArray();
                        _indices = iList.ToArray();
                        _rawSkin = sList.ToArray();
                        _textureName = model.Parts.FirstOrDefault(p => !string.IsNullOrEmpty(p.TextureName))?.TextureName;

                        _scale = 1.0f; nudScale.Value = 1.0m;
                        _rotX = 0f; _rotY = 0f; _rotZ = 0f; _offset = Vector3.Zero; _mirrorX = false;

                        lblModelInfo.Text = $"Fichier : {fn}\n" +
                                            $"Sommets : {_rawVertices.Length:N0} | Faces : {(_indices.Length / 3):N0}\n" +
                                            $"Format : Character UO (.char) avec rig intégré";

                        RebakeModelTransform();
                        if (model.Bones != null && model.Bones.Count > 0)
                        {
                            _liveOriginalBones = new List<GrannyBone>(model.Bones);
                            InitMarkersFromLiveBones(model.Bones);
                        }
                        else
                        {
                            AutoPlaceMixamoLandmarks();
                        }
                        EnsurePreviewModel();
                        glControl.Invalidate();
                        return;
                    }
                }

                // 2) Is it a .grn file?
                if (ext == ".grn")
                {
                    var parser = new GrannyFormatParser();
                    if (parser.Load(filePath) && parser.Meshes.Count > 0)
                    {
                        var vList = new List<Vector3>();
                        var nList = new List<Vector3>();
                        var uList = new List<Vector2>();
                        var iList = new List<int>();
                        var sList = new List<VertexInfluence[]>();

                        foreach (var m in parser.Meshes)
                        {
                            int baseIdx = vList.Count;
                            for (int i = 0; i < m.Vertices.Count; i++)
                            {
                                vList.Add(m.Vertices[i]);
                                nList.Add(Vector3.UnitZ);
                                uList.Add(m.UVs != null && i < m.UVs.Count ? m.UVs[i] : Vector2.Zero);
                                sList.Add(m.Skin != null && i < m.Skin.Count ? m.Skin[i] : Array.Empty<VertexInfluence>());
                            }
                            for (int i = 0; i < m.Indices.Count; i++)
                                iList.Add(baseIdx + m.Indices[i]);
                        }

                        _rawVertices = vList.ToArray();
                        _rawNormals = nList.ToArray();
                        _rawUVs = uList.ToArray();
                        _indices = iList.ToArray();
                        _rawSkin = sList.ToArray();
                        _textureName = parser.TextureName;

                        _scale = 1.0f; nudScale.Value = 1.0m;
                        _rotX = 0f; _rotY = 0f; _rotZ = 0f; _offset = Vector3.Zero; _mirrorX = false;

                        lblModelInfo.Text = $"Fichier : {fn}\n" +
                                            $"Sommets : {_rawVertices.Length:N0} | Faces : {(_indices.Length / 3):N0}\n" +
                                            $"Format : Modèle Granny UO (.grn)";

                        RebakeModelTransform();
                        if (parser.Bones != null && parser.Bones.Count > 0)
                        {
                            _liveOriginalBones = new List<GrannyBone>(parser.Bones);
                            InitMarkersFromLiveBones(parser.Bones);
                        }
                        else
                        {
                            AutoPlaceMixamoLandmarks();
                        }
                        EnsurePreviewModel();
                        glControl.Invalidate();
                        return;
                    }
                }

                // 3) Standard 3D File (FBX, OBJ, DAE, 3DS) via Assimp
                var ctx = new AssimpContext();
                var scene = ctx.ImportFile(filePath, PostProcessSteps.Triangulate | PostProcessSteps.GenerateSmoothNormals);

                int totalVerts = 0;
                int totalFaces = 0;
                bool hasBones = false;

                var vertList = new List<Vector3>();
                var normList = new List<Vector3>();
                var uvList = new List<Vector2>();
                var idxList = new List<int>();

                // Detect scene bounding box in raw file space
                var bboxMin = new Assimp.Vector3D(float.MaxValue, float.MaxValue, float.MaxValue);
                var bboxMax = new Assimp.Vector3D(float.MinValue, float.MinValue, float.MinValue);
                foreach (var m in scene.Meshes)
                {
                    foreach (var v in m.Vertices)
                    {
                        bboxMin.X = Math.Min(bboxMin.X, v.X); bboxMin.Y = Math.Min(bboxMin.Y, v.Y); bboxMin.Z = Math.Min(bboxMin.Z, v.Z);
                        bboxMax.X = Math.Max(bboxMax.X, v.X); bboxMax.Y = Math.Max(bboxMax.Y, v.Y); bboxMax.Z = Math.Max(bboxMax.Z, v.Z);
                    }
                }

                float rawH_X = Math.Abs(bboxMax.X - bboxMin.X);
                float rawH_Y = Math.Abs(bboxMax.Y - bboxMin.Y);
                float rawH_Z = Math.Abs(bboxMax.Z - bboxMin.Z);

                // Auto-detect orientation: Y-up vs Z-up
                bool isZUp = rawH_Z > rawH_Y * 1.1f;

                // Auto-detect unit scale: cm (~180) vs meters (~1.8) vs mm (~1800)
                float maxH = Math.Max(rawH_Y, rawH_Z);
                float initialScale = 1.0f;
                if (maxH > 500f) initialScale = 0.001f;
                else if (maxH > 20f) initialScale = 0.01f;
                else initialScale = 1.0f;

                foreach (var mesh in scene.Meshes)
                {
                    totalVerts += mesh.VertexCount;
                    totalFaces += mesh.FaceCount;
                    if (mesh.HasBones && mesh.BoneCount > 0) hasBones = true;

                    int baseIdx = vertList.Count;
                    for (int i = 0; i < mesh.VertexCount; i++)
                    {
                        var v = mesh.Vertices[i];
                        vertList.Add(new Vector3(v.X * initialScale, v.Y * initialScale, v.Z * initialScale));

                        if (mesh.HasNormals)
                        {
                            var n = mesh.Normals[i];
                            normList.Add(new Vector3(n.X, n.Y, n.Z).Normalized());
                        }
                        else
                        {
                            normList.Add(Vector3.UnitZ);
                        }

                        if (mesh.HasTextureCoords(0))
                        {
                            var uv = mesh.TextureCoordinateChannels[0][i];
                            uvList.Add(new Vector2(uv.X, 1.0f - uv.Y));
                        }
                        else
                        {
                            uvList.Add(Vector2.Zero);
                        }
                    }

                    foreach (var face in mesh.Faces)
                    {
                        if (face.IndexCount == 3)
                        {
                            idxList.Add(baseIdx + face.Indices[0]);
                            idxList.Add(baseIdx + face.Indices[1]);
                            idxList.Add(baseIdx + face.Indices[2]);
                        }
                    }
                }

                _rawVertices = vertList.ToArray();
                _rawNormals = normList.ToArray();
                _rawUVs = uvList.ToArray();
                _indices = idxList.ToArray();
                _rawSkin = null;

                lblModelInfo.Text = $"Fichier : {fn}\n" +
                                    $"Sommets : {totalVerts:N0} | Faces : {totalFaces:N0}\n" +
                                    $"Squelette FBX : {(hasBones ? "OUI (os détectés)" : "NON (Modèle statique)")}";

                if (!hasBones) chkForceAutoSkin.Checked = true;

                _scale = 1.0f;
                nudScale.Value = 1.0m;
                _mirrorX = false;
                _offset = Vector3.Zero;

                // If raw file was Y-up (like standard Mixamo FBX), rotate +90 around X to stand upright in UO Z-up space
                if (!isZUp)
                {
                    _rotX = 90f; _rotY = 0f; _rotZ = 0f;
                }
                else
                {
                    _rotX = 0f; _rotY = 0f; _rotZ = 0f;
                }

                RebakeModelTransform();
                AutoCenterAndGround();
                AutoScaleToSkeleton();
                AutoPlaceMixamoLandmarks();
                EnsurePreviewModel();
                glControl.Invalidate();
            }
            catch (Exception ex)
            {
                if (_liveMeshes != null && _liveMeshes.Count > 0)
                {
                    LoadFromLiveMeshes(_liveMeshes);
                }
                else
                {
                    MessageBox.Show("Erreur lecture 3D : " + ex.Message, "Auto-Rig Studio", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void AutoFitMarkersToMesh()
        {
            if (_baseVertices == null || _baseVertices.Length == 0) return;

            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;

            foreach (var v in _baseVertices)
            {
                minX = Math.Min(minX, v.X); maxX = Math.Max(maxX, v.X);
                minY = Math.Min(minY, v.Y); maxY = Math.Max(maxY, v.Y);
                minZ = Math.Min(minZ, v.Z); maxZ = Math.Max(maxZ, v.Z);
            }

            float height = Math.Max(0.1f, maxZ - minZ);
            float midY = (minY + maxY) * 0.5f;
            float halfWidth = Math.Max(0.05f, (maxX - minX) * 0.5f);

            void SetPos(string bname, Vector3 pos)
            {
                var m = _markers.FirstOrDefault(x => string.Equals(x.BoneName, bname, StringComparison.OrdinalIgnoreCase));
                if (m != null) m.Position = pos;
            }

            SetPos("Bip01 Head", new Vector3(0.0f, midY, minZ + height * 0.90f));
            SetPos("Bip01 Neck", new Vector3(0.0f, midY, minZ + height * 0.83f));
            SetPos("Bip01 Spine2", new Vector3(0.0f, midY, minZ + height * 0.74f));
            SetPos("Bip01 Spine", new Vector3(0.0f, midY, minZ + height * 0.62f));
            SetPos("Bip01 Pelvis", new Vector3(0.0f, midY, minZ + height * 0.52f));

            float clavX = halfWidth * 0.25f;
            SetPos("Bip01 L Clavicle", new Vector3(-clavX, midY, minZ + height * 0.77f));
            SetPos("Bip01 R Clavicle", new Vector3(clavX, midY, minZ + height * 0.77f));

            float shoulderX = halfWidth * 0.45f;
            SetPos("Bip01 L UpperArm", new Vector3(-shoulderX, midY, minZ + height * 0.76f));
            SetPos("Bip01 R UpperArm", new Vector3(shoulderX, midY, minZ + height * 0.76f));

            float elbowX = halfWidth * 0.72f;
            SetPos("Bip01 L Forearm", new Vector3(-elbowX, midY, minZ + height * 0.74f));
            SetPos("Bip01 R Forearm", new Vector3(elbowX, midY, minZ + height * 0.74f));

            float handX = halfWidth * 0.95f;
            SetPos("Bip01 L Hand", new Vector3(-handX, midY, minZ + height * 0.73f));
            SetPos("Bip01 R Hand", new Vector3(handX, midY, minZ + height * 0.73f));

            float hipX = halfWidth * 0.22f;
            SetPos("Bip01 L Thigh", new Vector3(-hipX, midY, minZ + height * 0.48f));
            SetPos("Bip01 R Thigh", new Vector3(hipX, midY, minZ + height * 0.48f));

            SetPos("Bip01 L Calf", new Vector3(-hipX, midY + 0.01f, minZ + height * 0.27f));
            SetPos("Bip01 R Calf", new Vector3(hipX, midY + 0.01f, minZ + height * 0.27f));

            SetPos("Bip01 L Foot", new Vector3(-hipX * 0.9f, midY + 0.04f, minZ + height * 0.05f));
            SetPos("Bip01 R Foot", new Vector3(hipX * 0.9f, midY + 0.04f, minZ + height * 0.05f));

            _cameraTarget = new Vector3(0, midY, minZ + height * 0.5f);
            UpdateNudValues();
            glControl.Invalidate();
        }

        private void SetViewMode(RigViewMode mode)
        {
            _currentViewMode = mode;
            switch (mode)
            {
                case RigViewMode.Front:
                    _cameraYaw = 0f;
                    _cameraPitch = 0f;
                    _cameraPan = Vector3.Zero;
                    break;
                case RigViewMode.Profile:
                    _cameraYaw = 90f;
                    _cameraPitch = 0f;
                    _cameraPan = Vector3.Zero;
                    break;
                case RigViewMode.Back:
                    _cameraYaw = 180f;
                    _cameraPitch = 0f;
                    _cameraPan = Vector3.Zero;
                    break;
                case RigViewMode.Free3D:
                    _cameraYaw = 35f;
                    _cameraPitch = 15f;
                    _cameraPan = Vector3.Zero;
                    break;
            }
            UpdateViewButtonsUI();
            glControl.Invalidate();
        }

        private void UpdateViewButtonsUI()
        {
            Color activeBg = Color.FromArgb(20, 110, 80);
            Color normalBg = Color.FromArgb(45, 45, 48);

            if (btnVueFace != null) btnVueFace.BackColor = (_currentViewMode == RigViewMode.Front) ? activeBg : normalBg;
            if (btnVueProfil != null) btnVueProfil.BackColor = (_currentViewMode == RigViewMode.Profile) ? activeBg : normalBg;
            if (btnVueDos != null) btnVueDos.BackColor = (_currentViewMode == RigViewMode.Back) ? activeBg : normalBg;
            if (btnVue3D != null) btnVue3D.BackColor = (_currentViewMode == RigViewMode.Free3D) ? activeBg : normalBg;
        }

        private void UpdateNudValues()
        {
            if (_selectedMarkerIndex < 0 || _selectedMarkerIndex >= _markers.Count) return;
            _isUpdatingNud = true;
            try
            {
                var m = _markers[_selectedMarkerIndex];
                nudX.Value = (decimal)MathHelper.Clamp((float)Math.Round(m.Position.X, 3), (float)nudX.Minimum, (float)nudX.Maximum);
                nudY.Value = (decimal)MathHelper.Clamp((float)Math.Round(m.Position.Y, 3), (float)nudY.Minimum, (float)nudY.Maximum);
                nudZ.Value = (decimal)MathHelper.Clamp((float)Math.Round(m.Position.Z, 3), (float)nudZ.Minimum, (float)nudZ.Maximum);

                if (nudRoll != null)
                    nudRoll.Value = (decimal)MathHelper.Clamp((float)Math.Round(m.RotationDegrees.X, 1), (float)nudRoll.Minimum, (float)nudRoll.Maximum);
                if (nudPitch != null)
                    nudPitch.Value = (decimal)MathHelper.Clamp((float)Math.Round(m.RotationDegrees.Y, 1), (float)nudPitch.Minimum, (float)nudPitch.Maximum);
                if (nudYaw != null)
                    nudYaw.Value = (decimal)MathHelper.Clamp((float)Math.Round(m.RotationDegrees.Z, 1), (float)nudYaw.Minimum, (float)nudYaw.Maximum);
            }
            finally
            {
                _isUpdatingNud = false;
            }
        }

        private void OnNudChanged()
        {
            if (_isUpdatingNud) return;
            if (_selectedMarkerIndex < 0 || _selectedMarkerIndex >= _markers.Count) return;
            var m = _markers[_selectedMarkerIndex];
            m.Position = new Vector3((float)nudX.Value, (float)nudY.Value, (float)nudZ.Value);

            if (chkSymmetry.Checked && !string.IsNullOrEmpty(m.SymmetricBoneName))
            {
                var sym = _markers.FirstOrDefault(x => string.Equals(x.BoneName, m.SymmetricBoneName, StringComparison.OrdinalIgnoreCase));
                if (sym != null)
                {
                    sym.Position = new Vector3(-m.Position.X, m.Position.Y, m.Position.Z);
                }
            }

            EnsurePreviewModel();
            glControl.Invalidate();
        }

        private void OnNudRotChanged()
        {
            if (_isUpdatingNud) return;
            if (_selectedMarkerIndex < 0 || _selectedMarkerIndex >= _markers.Count) return;
            var m = _markers[_selectedMarkerIndex];
            m.RotationDegrees = new Vector3((float)nudRoll.Value, (float)nudPitch.Value, (float)nudYaw.Value);

            if (chkSymmetry.Checked && !string.IsNullOrEmpty(m.SymmetricBoneName))
            {
                var sym = _markers.FirstOrDefault(x => string.Equals(x.BoneName, m.SymmetricBoneName, StringComparison.OrdinalIgnoreCase));
                if (sym != null)
                {
                    sym.RotationDegrees = new Vector3(-m.RotationDegrees.X, m.RotationDegrees.Y, -m.RotationDegrees.Z);
                }
            }

            EnsurePreviewModel();
            glControl.Invalidate();
        }

        private void ApplyRollOffset(float deltaRoll)
        {
            if (_selectedMarkerIndex < 0 || _selectedMarkerIndex >= _markers.Count) return;
            var m = _markers[_selectedMarkerIndex];
            float newRoll = (m.RotationDegrees.X + deltaRoll) % 360f;
            if (newRoll > 180f) newRoll -= 360f;
            if (newRoll < -180f) newRoll += 360f;
            m.RotationDegrees = new Vector3(newRoll, m.RotationDegrees.Y, m.RotationDegrees.Z);

            if (chkSymmetry.Checked && !string.IsNullOrEmpty(m.SymmetricBoneName))
            {
                var sym = _markers.FirstOrDefault(x => string.Equals(x.BoneName, m.SymmetricBoneName, StringComparison.OrdinalIgnoreCase));
                if (sym != null)
                {
                    sym.RotationDegrees = new Vector3(-m.RotationDegrees.X, m.RotationDegrees.Y, -m.RotationDegrees.Z);
                }
            }

            UpdateNudValues();
            EnsurePreviewModel();
            glControl.Invalidate();
        }

        private void SetRollExact(float targetRoll)
        {
            if (_selectedMarkerIndex < 0 || _selectedMarkerIndex >= _markers.Count) return;
            var m = _markers[_selectedMarkerIndex];
            m.RotationDegrees = new Vector3(targetRoll, m.RotationDegrees.Y, m.RotationDegrees.Z);

            if (chkSymmetry.Checked && !string.IsNullOrEmpty(m.SymmetricBoneName))
            {
                var sym = _markers.FirstOrDefault(x => string.Equals(x.BoneName, m.SymmetricBoneName, StringComparison.OrdinalIgnoreCase));
                if (sym != null)
                {
                    sym.RotationDegrees = new Vector3(-m.RotationDegrees.X, m.RotationDegrees.Y, -m.RotationDegrees.Z);
                }
            }

            UpdateNudValues();
            EnsurePreviewModel();
            glControl.Invalidate();
        }

        private void GenerateAndSaveCharacter()
        {
            if (_baseVertices == null || _baseVertices.Length == 0)
            {
                MessageBox.Show("Veuillez charger un modèle 3D valide avant de générer le rig.", "Génération Character UO", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                Cursor = Cursors.WaitCursor;

                EnsurePreviewModel();

                string? err = null;
                GrannyModelFile? model = null;

                string ext = !string.IsNullOrEmpty(_currentModelPath) ? Path.GetExtension(_currentModelPath).ToLowerInvariant() : "";
                var customJoints = _markers.ToDictionary(m => m.BoneName, m => m.Position, StringComparer.OrdinalIgnoreCase);
                bool forceAuto = chkForceAutoSkin.Checked;

                if (_previewModel != null && _previewModel.Parts.Count > 0 && _previewModel.Bones.Count > 0)
                {
                    model = _previewModel;
                }
                else if (ext == ".fbx" && File.Exists(_currentModelPath) && !forceAuto)
                {
                    MixamoCharacterImport.TryImport(_currentModelPath, _outFolder, _refPath, out model, out err, customJoints, forceAuto);
                }

                if (model == null && _previewModel != null)
                {
                    model = _previewModel;
                }

                if (model == null)
                {
                    MessageBox.Show("Erreur lors de la génération du rig : " + (err ?? "inconnue"), "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                if (!Directory.Exists(_outFolder)) Directory.CreateDirectory(_outFolder);
                string charName = !string.IsNullOrEmpty(_currentModelPath) ? Path.GetFileNameWithoutExtension(_currentModelPath) : "CustomRigModel";
                string charPath = Path.Combine(_outFolder, charName + GrannyModelFile.Extension);

                model.Name = charName;

                if (!GrannyModelFile.Save(model, charPath))
                {
                    MessageBox.Show("Impossible d'enregistrer le fichier .char", "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                GeneratedCharPath = charPath;
                MessageBox.Show($"Character UO généré et adapté avec succès !\n\nFichier : {Path.GetFileName(charPath)}\nEmplacement : {charPath}", "Succès", MessageBoxButtons.OK, MessageBoxIcon.Information);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Erreur : " + ex.Message, "Génération Character UO", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        // ==========================================
        // OPENGL RENDERING & SHADERS
        // ==========================================
        private void EnsureShaders()
        {
            if (_shaderMeshProg != 0) return;

            const string vsMesh = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNormal;
layout(location=2) in vec2 aUV;
uniform mat4 uMvp;
out vec3 vNormal;
out vec3 vFragPos;
void main() {
    vNormal = aNormal;
    vFragPos = aPos;
    gl_Position = uMvp * vec4(aPos, 1.0);
}";

            const string fsMesh = @"#version 330 core
in vec3 vNormal;
in vec3 vFragPos;
out vec4 FragColor;
void main() {
    vec3 norm = normalize(vNormal);
    vec3 lightDir1 = normalize(vec3(0.5, 0.8, 1.0));
    vec3 lightDir2 = normalize(vec3(-0.5, -0.8, 0.5));
    float diff1 = max(dot(norm, lightDir1), 0.0);
    float diff2 = max(dot(norm, lightDir2), 0.0) * 0.4;
    vec3 ambient = vec3(0.25, 0.26, 0.28);
    vec3 baseColor = vec3(0.72, 0.72, 0.75);
    vec3 color = (ambient + diff1 + diff2) * baseColor;
    FragColor = vec4(color, 1.0);
}";

            const string vsLine = @"#version 330 core
layout(location=0) in vec3 aPos;
uniform mat4 uMvp;
void main() {
    gl_Position = uMvp * vec4(aPos, 1.0);
}";

            const string fsLine = @"#version 330 core
uniform vec4 uColor;
out vec4 FragColor;
void main() {
    FragColor = uColor;
}";

            _shaderMeshProg = CompileProgram(vsMesh, fsMesh);
            _shaderLineProg = CompileProgram(vsLine, fsLine);
        }

        private int CompileProgram(string vs, string fs)
        {
            int p = GL.CreateProgram();
            int v = CompileShader(ShaderType.VertexShader, vs);
            int f = CompileShader(ShaderType.FragmentShader, fs);
            GL.AttachShader(p, v);
            GL.AttachShader(p, f);
            GL.LinkProgram(p);
            GL.DeleteShader(v);
            GL.DeleteShader(f);
            return p;
        }

        private int CompileShader(ShaderType type, string src)
        {
            int s = GL.CreateShader(type);
            GL.ShaderSource(s, src);
            GL.CompileShader(s);
            return s;
        }

        private void UploadMeshToGl()
        {
            if (_interleaved == null || _interleaved.Length == 0) return;

            if (!glControl.IsHandleCreated)
            {
                _meshNeedsUpload = true;
                return;
            }

            try
            {
                glControl.MakeCurrent();
                EnsureShaders();

                if (_meshVao == 0) _meshVao = GL.GenVertexArray();
                if (_meshVbo == 0) _meshVbo = GL.GenBuffer();

                GL.BindVertexArray(_meshVao);
                GL.BindBuffer(BufferTarget.ArrayBuffer, _meshVbo);
                GL.BufferData(BufferTarget.ArrayBuffer, _interleaved.Length * sizeof(float), _interleaved, BufferUsageHint.DynamicDraw);

                int stride = 8 * sizeof(float);
                GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
                GL.EnableVertexAttribArray(0);

                GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
                GL.EnableVertexAttribArray(1);

                GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
                GL.EnableVertexAttribArray(2);

                GL.BindVertexArray(0);
                GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            }
            catch { }
        }

        private void GlControl_Paint(object? sender, PaintEventArgs e)
        {
            if (!glControl.IsHandleCreated || glControl.Width <= 0 || glControl.Height <= 0) return;

            try
            {
                glControl.MakeCurrent();
                EnsureShaders();

                if (_meshNeedsUpload)
                {
                    _meshNeedsUpload = false;
                    UploadMeshToGl();
                }

                GL.Viewport(0, 0, glControl.Width, glControl.Height);
                GL.ClearColor(0.12f, 0.12f, 0.14f, 1.0f);
                GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
                GL.Enable(EnableCap.DepthTest);
                GL.Enable(EnableCap.CullFace);
                GL.CullFace(TriangleFace.Back);

            float aspect = (float)glControl.Width / Math.Max(1, glControl.Height);
            Matrix4 proj = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(45f), aspect, 0.05f, 100.0f);

            float radYaw = MathHelper.DegreesToRadians(_cameraYaw);
            float radPitch = MathHelper.DegreesToRadians(_cameraPitch);

            Vector3 eyeOffset = new Vector3(
                _cameraDist * MathF.Sin(radYaw) * MathF.Cos(radPitch),
                -_cameraDist * MathF.Cos(radYaw) * MathF.Cos(radPitch),
                _cameraDist * MathF.Sin(radPitch)
            );
            Vector3 eye = _cameraTarget + _cameraPan + eyeOffset;
            Matrix4 view = Matrix4.LookAt(eye, _cameraTarget + _cameraPan, Vector3.UnitZ);
            Matrix4 mvp = view * proj;

            // 1. Draw Ground Grid
            DrawGrid(mvp);

            // 2. Draw 3D Character Mesh
            if (chkShowMesh.Checked && _meshVao != 0 && _meshVertexCount > 0)
            {
                DrawMeshVAO(mvp);
            }

            // 3. Draw Skeleton Bone Lines
            if (chkShowSkeleton.Checked)
            {
                DrawSkeletonLines(mvp);
                DrawJointMarkers(mvp);
            }

            glControl.SwapBuffers();
            }
            catch { }
        }

        private void DrawMeshVAO(Matrix4 mvp)
        {
            GL.UseProgram(_shaderMeshProg);
            int uMvp = GL.GetUniformLocation(_shaderMeshProg, "uMvp");
            GL.UniformMatrix4(uMvp, false, ref mvp);

            GL.BindVertexArray(_meshVao);
            GL.DrawArrays(OpenTK.Graphics.OpenGL.PrimitiveType.Triangles, 0, _meshVertexCount);
            GL.BindVertexArray(0);
        }

        private void DrawSkeletonLines(Matrix4 mvp)
        {
            var lines = new List<float>();

            // If animation is playing, draw dynamic animated bones!
            if (_isPlayingAnim && _boneWorldMatrices != null && _previewModel != null)
            {
                int nb = _previewModel.Bones.Count;
                for (int i = 0; i < nb; i++)
                {
                    int pIdx = GetParentIndex(_previewModel.Bones, i);
                    if (pIdx >= 0 && pIdx < _boneWorldMatrices.Length && i < _boneWorldMatrices.Length)
                    {
                        string pName = _previewModel.Bones[pIdx].Name;
                        string cName = _previewModel.Bones[i].Name;
                        if (pName.Equals("__Root", StringComparison.OrdinalIgnoreCase) ||
                            pName.Equals("master_h_male", StringComparison.OrdinalIgnoreCase) ||
                            pName.Equals("Object03", StringComparison.OrdinalIgnoreCase) ||
                            cName.Equals("Object03", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var p0 = _boneWorldMatrices[pIdx].Row3.Xyz;
                        var p1 = _boneWorldMatrices[i].Row3.Xyz;
                        float dSq = (p1 - p0).LengthSquared;
                        if (dSq > 1e-6f && dSq < 0.64f)
                        {
                            lines.Add(p0.X); lines.Add(p0.Y); lines.Add(p0.Z);
                            lines.Add(p1.X); lines.Add(p1.Y); lines.Add(p1.Z);
                        }
                    }
                }
            }
            else
            {
                // Draw rest pose skeleton connecting bones
                if (_previewModel != null && _previewModel.Bones.Count > 0)
                {
                    int nb = _previewModel.Bones.Count;
                    for (int i = 0; i < nb; i++)
                    {
                        int pIdx = GetParentIndex(_previewModel.Bones, i);
                        if (pIdx >= 0 && pIdx < nb)
                        {
                            string pName = _previewModel.Bones[pIdx].Name;
                            string cName = _previewModel.Bones[i].Name;
                            if (pName.Equals("__Root", StringComparison.OrdinalIgnoreCase) ||
                                pName.Equals("master_h_male", StringComparison.OrdinalIgnoreCase) ||
                                pName.Equals("Object03", StringComparison.OrdinalIgnoreCase) ||
                                cName.Equals("Object03", StringComparison.OrdinalIgnoreCase))
                                continue;

                            var w0 = Matrix4.Invert(_previewModel.Bones[pIdx].InverseBindMatrix).Row3.Xyz;
                            var w1 = Matrix4.Invert(_previewModel.Bones[i].InverseBindMatrix).Row3.Xyz;
                            float dSq = (w1 - w0).LengthSquared;
                            if (dSq > 1e-6f && dSq < 0.64f)
                            {
                                lines.Add(w0.X); lines.Add(w0.Y); lines.Add(w0.Z);
                                lines.Add(w1.X); lines.Add(w1.Y); lines.Add(w1.Z);
                            }
                        }
                    }
                }
                else
                {
                    // Draw rest pose marker bones
                    foreach (var m in _markers)
                    {
                        if (string.IsNullOrEmpty(m.ParentName)) continue;
                        var parent = _markers.FirstOrDefault(x => string.Equals(x.BoneName, m.ParentName, StringComparison.OrdinalIgnoreCase));
                        if (parent != null)
                        {
                            lines.Add(parent.Position.X); lines.Add(parent.Position.Y); lines.Add(parent.Position.Z);
                            lines.Add(m.Position.X); lines.Add(m.Position.Y); lines.Add(m.Position.Z);
                        }
                    }
                }
            }

            if (lines.Count > 0)
            {
                GL.Disable(EnableCap.DepthTest);
                DrawLinesImmediate(mvp, lines.ToArray(), new Vector4(0.2f, 1.0f, 0.4f, 0.95f), 3.0f);
                GL.Enable(EnableCap.DepthTest);
            }
        }

        private void DrawJointMarkers(Matrix4 mvp)
        {
            GL.Disable(EnableCap.DepthTest);
            GL.UseProgram(_shaderLineProg);
            int uMvp = GL.GetUniformLocation(_shaderLineProg, "uMvp");
            int uColor = GL.GetUniformLocation(_shaderLineProg, "uColor");
            GL.UniformMatrix4(uMvp, false, ref mvp);

            for (int i = 0; i < _markers.Count; i++)
            {
                var m = _markers[i];
                bool isSelected = (i == _selectedMarkerIndex);

                Vector3 pos = m.Position;
                if (_isPlayingAnim && _boneWorldMatrices != null && _previewModel != null)
                {
                    int bidx = _previewModel.Bones.FindIndex(b => string.Equals(b.Name, m.BoneName, StringComparison.OrdinalIgnoreCase));
                    if (bidx >= 0 && bidx < _boneWorldMatrices.Length)
                    {
                        pos = _boneWorldMatrices[bidx].Row3.Xyz;
                    }
                }

                Vector4 col = isSelected
                    ? new Vector4(1f, 1f, 0f, 1f)
                    : new Vector4(m.Color.R / 255f, m.Color.G / 255f, m.Color.B / 255f, 1.0f);

                GL.Uniform4(uColor, col);
                DrawCrossMarker(mvp, pos, isSelected ? 0.12f : 0.08f, col);
            }

            // Visual alignment aids for selected wrist: Gold line to thumb, Cyan line to fingers, orientation disc
            if (_selectedMarkerIndex >= 0 && _selectedMarkerIndex < _markers.Count)
            {
                var selM = _markers[_selectedMarkerIndex];
                if (selM.BoneName.Contains("Hand") && _previewModel != null)
                {
                    string f0Name = selM.BoneName.Contains(" L ") ? "Bip01 L Finger0" : "Bip01 R Finger0";
                    string f1Name = selM.BoneName.Contains(" L ") ? "Bip01 L Finger1" : "Bip01 R Finger1";
                    int idx0 = _previewModel.Bones.FindIndex(b => string.Equals(b.Name, f0Name, StringComparison.OrdinalIgnoreCase));
                    int idx1 = _previewModel.Bones.FindIndex(b => string.Equals(b.Name, f1Name, StringComparison.OrdinalIgnoreCase));

                    if (idx0 >= 0 && idx0 < _previewModel.Bones.Count)
                    {
                        Vector3 pThumb = Matrix4.Invert(_previewModel.Bones[idx0].InverseBindMatrix).Row3.Xyz;
                        float[] thumbLine = new float[] { selM.Position.X, selM.Position.Y, selM.Position.Z, pThumb.X, pThumb.Y, pThumb.Z };
                        DrawLinesImmediate(mvp, thumbLine, new Vector4(1f, 0.84f, 0f, 1f), 3.5f); // Gold line to thumb
                        DrawCrossMarker(mvp, pThumb, 0.035f, new Vector4(1f, 0.84f, 0f, 1f));
                    }

                    if (idx1 >= 0 && idx1 < _previewModel.Bones.Count)
                    {
                        Vector3 pFingers = Matrix4.Invert(_previewModel.Bones[idx1].InverseBindMatrix).Row3.Xyz;
                        float[] fingerLine = new float[] { selM.Position.X, selM.Position.Y, selM.Position.Z, pFingers.X, pFingers.Y, pFingers.Z };
                        DrawLinesImmediate(mvp, fingerLine, new Vector4(0f, 1f, 1f, 1f), 3.5f); // Cyan line to fingers
                        DrawCrossMarker(mvp, pFingers, 0.035f, new Vector4(0f, 1f, 1f, 1f));
                    }

                    int handIdx = _previewModel.Bones.FindIndex(b => string.Equals(b.Name, selM.BoneName, StringComparison.OrdinalIgnoreCase));
                    if (handIdx >= 0)
                    {
                        Matrix4 hWorld = Matrix4.Invert(_previewModel.Bones[handIdx].InverseBindMatrix);
                        Vector3 localY = hWorld.Row1.Xyz.Normalized();
                        Vector3 localZ = hWorld.Row2.Xyz.Normalized();
                        const int segments = 24;
                        float radius = 0.06f;
                        var circleVerts = new List<float>();
                        for (int s = 0; s < segments; s++)
                        {
                            float a0 = (s / (float)segments) * MathF.PI * 2f;
                            float a1 = ((s + 1) / (float)segments) * MathF.PI * 2f;
                            Vector3 pt0 = selM.Position + (localY * MathF.Cos(a0) + localZ * MathF.Sin(a0)) * radius;
                            Vector3 pt1 = selM.Position + (localY * MathF.Cos(a1) + localZ * MathF.Sin(a1)) * radius;
                            circleVerts.Add(pt0.X); circleVerts.Add(pt0.Y); circleVerts.Add(pt0.Z);
                            circleVerts.Add(pt1.X); circleVerts.Add(pt1.Y); circleVerts.Add(pt1.Z);
                        }
                        DrawLinesImmediate(mvp, circleVerts.ToArray(), new Vector4(1f, 0.6f, 0.1f, 0.9f), 2.0f);
                    }
                }
            }

            GL.Enable(EnableCap.DepthTest);
        }

        private void DrawCrossMarker(Matrix4 mvp, Vector3 center, float size, Vector4 color)
        {
            float[] crossVerts = new float[]
            {
                center.X - size, center.Y, center.Z,
                center.X + size, center.Y, center.Z,
                center.X, center.Y - size, center.Z,
                center.X, center.Y + size, center.Z,
                center.X, center.Y, center.Z - size,
                center.X, center.Y, center.Z + size
            };

            DrawLinesImmediate(mvp, crossVerts, color, 2.5f);
        }

        private void DrawLinesImmediate(Matrix4 mvp, float[] vertices, Vector4 color, float lineWidth)
        {
            GL.UseProgram(_shaderLineProg);
            int uMvp = GL.GetUniformLocation(_shaderLineProg, "uMvp");
            int uColor = GL.GetUniformLocation(_shaderLineProg, "uColor");
            GL.UniformMatrix4(uMvp, false, ref mvp);
            GL.Uniform4(uColor, color);

            GL.LineWidth(lineWidth);

            if (_boneVao == 0) _boneVao = GL.GenVertexArray();
            if (_boneVbo == 0) _boneVbo = GL.GenBuffer();

            GL.BindVertexArray(_boneVao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _boneVbo);
            GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.DynamicDraw);

            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
            GL.EnableVertexAttribArray(0);

            GL.DrawArrays(OpenTK.Graphics.OpenGL.PrimitiveType.Lines, 0, vertices.Length / 3);

            GL.BindVertexArray(0);
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            GL.LineWidth(1.0f);
        }

        private void DrawGrid(Matrix4 mvp)
        {
            const int extent = 5;
            const float step = 0.5f;
            var gridVerts = new List<float>();

            for (int i = -extent; i <= extent; i++)
            {
                float p = i * step;
                gridVerts.Add(p); gridVerts.Add(-extent * step); gridVerts.Add(0);
                gridVerts.Add(p); gridVerts.Add(extent * step); gridVerts.Add(0);

                gridVerts.Add(-extent * step); gridVerts.Add(p); gridVerts.Add(0);
                gridVerts.Add(extent * step); gridVerts.Add(p); gridVerts.Add(0);
            }

            DrawLinesImmediate(mvp, gridVerts.ToArray(), new Vector4(0.25f, 0.25f, 0.28f, 1.0f), 1.0f);

            float[] axes = new float[]
            {
                0, 0, 0,  1, 0, 0, // X: Red
                0, 0, 0,  0, 1, 0, // Y: Green
                0, 0, 0,  0, 0, 1  // Z: Blue
            };
            DrawLinesImmediate(mvp, axes, new Vector4(0.8f, 0.3f, 0.3f, 1.0f), 2.0f);
        }

        // ==========================================
        // MOUSE & CAMERA CONTROLS
        // ==========================================
        private void GlControl_MouseDown(object? sender, MouseEventArgs e)
        {
            _lastMousePos = e.Location;

            if (e.Button == MouseButtons.Left)
            {
                int picked = PickMarkerAtScreen(e.X, e.Y);
                if (picked >= 0)
                {
                    _selectedMarkerIndex = picked;
                    cbJoints.SelectedIndex = picked;
                    UpdateNudValues();
                    _isDraggingMarker = true;

                    var m = _markers[picked];
                    _dragStartMarkerPos = m.Position;
                    _dragPlanePoint = m.Position;

                    if (_currentViewMode == RigViewMode.Front || _currentViewMode == RigViewMode.Back ||
                        ((Math.Abs(_cameraYaw) < 15f || Math.Abs(_cameraYaw - 180f) < 15f) && Math.Abs(_cameraPitch) < 15f))
                    {
                        // Plane normal is strictly Y (plane is X/Z)
                        _dragPlaneNormal = new Vector3(0f, (_cameraYaw > 90f && _cameraYaw < 270f) ? 1f : -1f, 0f);
                    }
                    else if (_currentViewMode == RigViewMode.Profile ||
                        ((Math.Abs(_cameraYaw - 90f) < 15f || Math.Abs(_cameraYaw + 90f) < 15f || Math.Abs(_cameraYaw - 270f) < 15f) && Math.Abs(_cameraPitch) < 15f))
                    {
                        // Plane normal is strictly X (plane is Y/Z)
                        _dragPlaneNormal = new Vector3((_cameraYaw > 0f && _cameraYaw < 180f) ? 1f : -1f, 0f, 0f);
                    }
                    else
                    {
                        float radYaw = MathHelper.DegreesToRadians(_cameraYaw);
                        float radPitch = MathHelper.DegreesToRadians(_cameraPitch);
                        _dragPlaneNormal = new Vector3(
                            MathF.Sin(radYaw) * MathF.Cos(radPitch),
                            -MathF.Cos(radYaw) * MathF.Cos(radPitch),
                            MathF.Sin(radPitch)
                        ).Normalized();
                    }

                    if (UnprojectMouseToPlane(e.X, e.Y, _dragPlanePoint, _dragPlaneNormal, out Vector3 initialHit))
                    {
                        _dragGrabOffset = _dragStartMarkerPos - initialHit;
                    }
                    else
                    {
                        _dragGrabOffset = Vector3.Zero;
                    }

                    glControl.Invalidate();
                    return;
                }

                if (_currentViewMode == RigViewMode.Free3D)
                {
                    _isOrbiting = true;
                }
                else
                {
                    // In 2D views (Front, Profile, Back), left-drag on empty space pans instead of accidentally rotating the 2D view!
                    _isPanning = true;
                }
            }
            else if (e.Button == MouseButtons.Right)
            {
                _isPanning = true;
            }
        }

        private void GlControl_MouseMove(object? sender, MouseEventArgs e)
        {
            int dx = e.X - _lastMousePos.X;
            int dy = e.Y - _lastMousePos.Y;
            _lastMousePos = e.Location;

            // 1:1 Ray-Plane unprojection dragging with strict orthographic axis locking
            if (_isDraggingMarker && _selectedMarkerIndex >= 0 && _selectedMarkerIndex < _markers.Count)
            {
                var m = _markers[_selectedMarkerIndex];
                if (ModifierKeys.HasFlag(Keys.Shift) && m.BoneName.Contains("Hand"))
                {
                    ApplyRollOffset(dx * 0.8f);
                    return;
                }

                if (UnprojectMouseToPlane(e.X, e.Y, _dragPlanePoint, _dragPlaneNormal, out Vector3 rawHit))
                {
                    Vector3 targetHit = rawHit + _dragGrabOffset;

                    if (_currentViewMode == RigViewMode.Front || _currentViewMode == RigViewMode.Back ||
                        ((Math.Abs(_cameraYaw) < 15f || Math.Abs(_cameraYaw - 180f) < 15f) && Math.Abs(_cameraPitch) < 15f))
                    {
                        // Front or Back view: STRICTLY X and Z move! Y is 100% locked to initial marker depth!
                        m.Position = new Vector3(targetHit.X, _dragStartMarkerPos.Y, targetHit.Z);
                    }
                    else if (_currentViewMode == RigViewMode.Profile ||
                        ((Math.Abs(_cameraYaw - 90f) < 15f || Math.Abs(_cameraYaw + 90f) < 15f || Math.Abs(_cameraYaw - 270f) < 15f) && Math.Abs(_cameraPitch) < 15f))
                    {
                        // Profile view: STRICTLY Y and Z move! X is 100% locked to initial marker lateral!
                        m.Position = new Vector3(_dragStartMarkerPos.X, targetHit.Y, targetHit.Z);
                    }
                    else
                    {
                        // Free 3D view: follows camera plane
                        m.Position = targetHit;
                    }

                    // Automatic X symmetry
                    if (chkSymmetry.Checked && !string.IsNullOrEmpty(m.SymmetricBoneName))
                    {
                        var sym = _markers.FirstOrDefault(x => string.Equals(x.BoneName, m.SymmetricBoneName, StringComparison.OrdinalIgnoreCase));
                        if (sym != null)
                        {
                            sym.Position = new Vector3(-m.Position.X, m.Position.Y, m.Position.Z);
                        }
                    }

                    UpdateNudValues();
                    EnsurePreviewModel();
                    glControl.Invalidate();
                }
                return;
            }

            if (_isOrbiting)
            {
                _cameraYaw += dx * 0.4f;
                _cameraPitch = MathHelper.Clamp(_cameraPitch - dy * 0.4f, -85f, 85f);
                glControl.Invalidate();
            }
            else if (_isPanning)
            {
                float radYaw = MathHelper.DegreesToRadians(_cameraYaw);
                Vector3 right = new Vector3(MathF.Cos(radYaw), -MathF.Sin(radYaw), 0f);
                Vector3 up = Vector3.UnitZ;

                float panScale = 0.0025f * (_cameraDist / 2.5f);
                _cameraPan -= (right * dx * panScale) - (up * dy * panScale);
                glControl.Invalidate();
            }
        }

        private void GlControl_MouseUp(object? sender, MouseEventArgs e)
        {
            _isDraggingMarker = false;
            _isOrbiting = false;
            _isPanning = false;
        }

        private void GlControl_MouseWheel(object? sender, MouseEventArgs e)
        {
            if (ModifierKeys.HasFlag(Keys.Shift) && _selectedMarkerIndex >= 0 && _selectedMarkerIndex < _markers.Count)
            {
                var m = _markers[_selectedMarkerIndex];
                if (m.BoneName.Contains("Hand"))
                {
                    ApplyRollOffset(e.Delta > 0 ? 5f : -5f);
                    return;
                }
            }

            float zoomFactor = e.Delta > 0 ? 0.9f : 1.1f;
            _cameraDist = MathHelper.Clamp(_cameraDist * zoomFactor, 0.3f, 30.0f);
            glControl.Invalidate();
        }

        private bool UnprojectMouseToPlane(int mouseX, int mouseY, Vector3 planePoint, Vector3 planeNormal, out Vector3 hitPoint)
        {
            hitPoint = Vector3.Zero;
            if (glControl.Width <= 0 || glControl.Height <= 0) return false;

            float aspect = (float)glControl.Width / Math.Max(1, glControl.Height);
            Matrix4 proj = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(45f), aspect, 0.05f, 100.0f);

            float radYaw = MathHelper.DegreesToRadians(_cameraYaw);
            float radPitch = MathHelper.DegreesToRadians(_cameraPitch);

            Vector3 eyeOffset = new Vector3(
                _cameraDist * MathF.Sin(radYaw) * MathF.Cos(radPitch),
                -_cameraDist * MathF.Cos(radYaw) * MathF.Cos(radPitch),
                _cameraDist * MathF.Sin(radPitch)
            );
            Vector3 eye = _cameraTarget + _cameraPan + eyeOffset;
            Matrix4 view = Matrix4.LookAt(eye, _cameraTarget + _cameraPan, Vector3.UnitZ);
            Matrix4 invMvp = Matrix4.Invert(view * proj);

            float ndcX = (2.0f * mouseX) / glControl.Width - 1.0f;
            float ndcY = 1.0f - (2.0f * mouseY) / glControl.Height;

            Vector4 nearPoint4 = new Vector4(ndcX, ndcY, -1.0f, 1.0f) * invMvp;
            Vector4 farPoint4 = new Vector4(ndcX, ndcY, 1.0f, 1.0f) * invMvp;

            if (Math.Abs(nearPoint4.W) < 1e-6f || Math.Abs(farPoint4.W) < 1e-6f) return false;

            Vector3 nearPoint = nearPoint4.Xyz / nearPoint4.W;
            Vector3 farPoint = farPoint4.Xyz / farPoint4.W;
            Vector3 rayDir = (farPoint - nearPoint).Normalized();

            float denom = Vector3.Dot(rayDir, planeNormal);
            if (Math.Abs(denom) < 1e-5f) return false;

            float t = Vector3.Dot(planePoint - nearPoint, planeNormal) / denom;
            hitPoint = nearPoint + rayDir * t;
            return true;
        }

        private int PickMarkerAtScreen(int mouseX, int mouseY)
        {
            if (glControl.Width <= 0 || glControl.Height <= 0) return -1;

            float aspect = (float)glControl.Width / Math.Max(1, glControl.Height);
            Matrix4 proj = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(45f), aspect, 0.05f, 100.0f);

            float radYaw = MathHelper.DegreesToRadians(_cameraYaw);
            float radPitch = MathHelper.DegreesToRadians(_cameraPitch);

            Vector3 eyeOffset = new Vector3(
                _cameraDist * MathF.Sin(radYaw) * MathF.Cos(radPitch),
                -_cameraDist * MathF.Cos(radYaw) * MathF.Cos(radPitch),
                _cameraDist * MathF.Sin(radPitch)
            );
            Vector3 eye = _cameraTarget + _cameraPan + eyeOffset;
            Matrix4 view = Matrix4.LookAt(eye, _cameraTarget + _cameraPan, Vector3.UnitZ);
            Matrix4 mvp = view * proj;

            int bestIdx = -1;
            float maxPickRadiusSq = 28f * 28f; // 28px pick radius
            float bestDistSq = maxPickRadiusSq;
            float bestDepth = float.MaxValue;

            for (int i = 0; i < _markers.Count; i++)
            {
                var p = _markers[i].Position;
                Vector4 clip = new Vector4(p, 1.0f) * mvp;
                if (clip.W <= 0.01f) continue;

                Vector3 ndc = clip.Xyz / clip.W;
                float sx = (ndc.X + 1.0f) * 0.5f * glControl.Width;
                float sy = (1.0f - ndc.Y) * 0.5f * glControl.Height;

                float distSq = (sx - mouseX) * (sx - mouseX) + (sy - mouseY) * (sy - mouseY);
                if (distSq > maxPickRadiusSq) continue;

                float depth = clip.Z / clip.W;

                if (bestIdx < 0)
                {
                    bestIdx = i;
                    bestDistSq = distSq;
                    bestDepth = depth;
                }
                else
                {
                    // If marker i is substantially closer to the cursor in screen space, choose it
                    if (distSq < bestDistSq - 64f) // ~8px closer
                    {
                        bestIdx = i;
                        bestDistSq = distSq;
                        bestDepth = depth;
                    }
                    // If screen distances are comparable, prefer the marker closer in depth (foreground)
                    else if (Math.Abs(distSq - bestDistSq) <= 64f && depth < bestDepth)
                    {
                        bestIdx = i;
                        bestDistSq = distSq;
                        bestDepth = depth;
                    }
                }
            }

            return bestIdx;
        }

        private void CleanupGlResources()
        {
            try
            {
                if (glControl.IsHandleCreated)
                {
                    glControl.MakeCurrent();
                    if (_meshVao != 0) GL.DeleteVertexArray(_meshVao);
                    if (_meshVbo != 0) GL.DeleteBuffer(_meshVbo);
                    if (_boneVao != 0) GL.DeleteVertexArray(_boneVao);
                    if (_boneVbo != 0) GL.DeleteBuffer(_boneVbo);
                    if (_shaderMeshProg != 0) GL.DeleteProgram(_shaderMeshProg);
                    if (_shaderLineProg != 0) GL.DeleteProgram(_shaderLineProg);
                }
            }
            catch { }
        }
    }
}
