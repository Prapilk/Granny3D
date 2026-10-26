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
        private CheckBox chkWireframe = null!;
        private RadioButton rbMale = null!;
        private RadioButton rbFemale = null!;
        private RadioButton rbCustom = null!;
        private Button btnChooseSkeleton = null!;
        private Label lblSkeletonName = null!;
        private string _customSkeletonPath = string.Empty;
        private string _customSkeletonName = string.Empty;
        private bool _isInternalSkeletonSwitch = false;
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
        private TrackBar tbFingerFlex = null!;
        private Label lblFingerFlex = null!;
        private float _fingerFlexAmount = 0.0f;

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
        private bool _isInternalScaleChange = false;

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
        private readonly HashSet<int> _selectedMarkerIndices = new();
        private readonly Dictionary<int, Vector3> _dragStartMarkerPositions = new();
        private bool _isBoxSelecting = false;
        private Point _boxSelectStart;
        private Point _boxSelectCurrent;
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

        // Multi-mesh preservation structures
        public class RawPartData
        {
            public string Name { get; set; } = string.Empty;
            public string MaterialName { get; set; } = string.Empty;
            public Vector3 Color { get; set; } = new Vector3(0.72f, 0.66f, 0.55f);
            public string TextureName { get; set; } = string.Empty;
            public Vector3[] RawVertices { get; set; } = Array.Empty<Vector3>();
            public Vector3[] RawNormals { get; set; } = Array.Empty<Vector3>();
            public Vector2[] UVs { get; set; } = Array.Empty<Vector2>();
            public int[] Indices { get; set; } = Array.Empty<int>();
            public VertexInfluence[][]? Skin { get; set; }
        }

        private class AnimTestItem
        {
            public string DisplayName { get; set; } = string.Empty;
            public string? FilePath { get; set; }
            public GrannyAnimation? PreloadedAnim { get; set; }
            public int FbxAnimIndex { get; set; } = 0;

            public override string ToString() => DisplayName;
        }

        private readonly List<RawPartData> _rawParts = new();
        private readonly List<GrannyModelPart> _modelParts = new();
        private GrannyAnimation? _activeSceneAnim;
        private string? _activeSceneAnimPath;

        // Animation preview
        private GrannyAnimation? _testAnim;
        private System.Windows.Forms.Timer? _animTimer;
        private float _animTime = 0.0f;
        private bool _isPlayingAnim = false;
        private Matrix4[]? _boneWorldMatrices;
        private List<GrannyBone> _refBones = new();

        public string? GeneratedCharPath { get; private set; }

        public AutoRigDialog(string? initialModelPath, string basePath, string? initialOutFolder = null, List<LoadedMesh>? liveMeshes = null, GrannyAnimation? activeAnim = null, string? activeAnimPath = null)
        {
            _currentModelPath = initialModelPath;
            _basePath = basePath ?? string.Empty;
            string othersFolder = Path.Combine(_basePath, "Others");
            if (!Directory.Exists(othersFolder)) Directory.CreateDirectory(othersFolder);

            // Redirige automatiquement vers Others si outFolder est vide ou pointe vers Meshs custom
            if (string.IsNullOrEmpty(initialOutFolder) ||
                initialOutFolder.IndexOf("Meshs custom", StringComparison.OrdinalIgnoreCase) >= 0 ||
                initialOutFolder.IndexOf("Mesh custom", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                _outFolder = othersFolder;
            }
            else
            {
                _outFolder = initialOutFolder;
            }

            _liveMeshes = liveMeshes;
            _activeSceneAnim = activeAnim;
            _activeSceneAnimPath = activeAnimPath;

            string maleRef = Path.Combine(_basePath, "Humans", "H_Male_Lod2.grn");
            string femaleRef = Path.Combine(_basePath, "Humans", "H_Female_Lod2.grn");
            if (!File.Exists(maleRef)) maleRef = Path.Combine(Granny3DControl.BasePath, "Humans", "H_Male_Lod2.grn");
            if (!File.Exists(femaleRef)) femaleRef = Path.Combine(Granny3DControl.BasePath, "Humans", "H_Female_Lod2.grn");
            _refPath = File.Exists(maleRef) ? maleRef : femaleRef;

            InitializeDefaultMarkers();
            InitializeComponentCustom();
            LoadReferenceBones();
            PopulateTestAnimations();

            // 1) Si initialModelPath est spécifié et est un fichier .char, charger prioritairement le modèle et son rig !
            if (!string.IsNullOrEmpty(_currentModelPath) && Path.GetExtension(_currentModelPath).Equals(".char", StringComparison.OrdinalIgnoreCase) && File.Exists(_currentModelPath))
            {
                LoadModelFile(_currentModelPath);
            }
            // 2) Sinon si liveMeshes est présent dans la scène active d'UOFiddler, le charger directement
            else if (_liveMeshes != null && _liveMeshes.Count > 0 && _liveMeshes.Any(m => m.BaseVertices != null && m.BaseVertices.Length > 0))
            {
                LoadFromLiveMeshes(_liveMeshes);
            }
            // 3) Sinon charger le fichier spécifié
            else if (!string.IsNullOrEmpty(_currentModelPath) && File.Exists(_currentModelPath))
            {
                LoadModelFile(_currentModelPath);
            }

            EnsurePreviewModel();
        }

        private void InitializeDefaultMarkers()
        {
            _markers.Clear();

            // Anatomical markers corresponding to UO Biped key joints (pivots d'articulation)
            _markers.Add(new JointMarker { BoneName = "Bip01 Head", DisplayName = Localization.Tr("marker_head", "Tête (Pivot nuque / base crâne)"), Position = new Vector3(0.0f, 0.029f, 1.441f), Color = Color.FromArgb(255, 140, 0), ParentName = "Bip01 Neck" });
            _markers.Add(new JointMarker { BoneName = "Bip01 Neck", DisplayName = Localization.Tr("marker_neck", "Cou (Pivot base cou / haut sternum)"), Position = new Vector3(0.0f, 0.032f, 1.369f), Color = Color.FromArgb(255, 215, 0), ParentName = "Bip01 Spine2" });
            _markers.Add(new JointMarker { BoneName = "Bip01 Spine2", DisplayName = Localization.Tr("marker_chest", "Poitrine / Cage Thoracique"), Position = new Vector3(0.0f, 0.041f, 1.214f), Color = Color.FromArgb(124, 252, 0), ParentName = "Bip01 Spine1" });
            _markers.Add(new JointMarker { BoneName = "Bip01 Spine", DisplayName = Localization.Tr("marker_spine", "Abdomen (Taille / nombril)"), Position = new Vector3(0.0f, 0.037f, 1.021f), Color = Color.FromArgb(50, 205, 50), ParentName = "Bip01 Pelvis" });
            _markers.Add(new JointMarker { BoneName = "Bip01 Pelvis", DisplayName = Localization.Tr("marker_pelvis", "Bassin / Hanches (Pelvis)"), Position = new Vector3(0.0f, 0.037f, 0.937f), Color = Color.FromArgb(0, 206, 209), ParentName = null });

            // Right Arm (Vue de face = Écran Gauche, X < 0)
            _markers.Add(new JointMarker { BoneName = "Bip01 R Clavicle", DisplayName = Localization.Tr("marker_r_clavicle", "Clavicule D (Écran Gauche)"), Position = new Vector3(-0.042f, 0.031f, 1.339f), Color = Color.FromArgb(255, 69, 0), ParentName = "Bip01 Spine2", SymmetricBoneName = "Bip01 L Clavicle" });
            _markers.Add(new JointMarker { BoneName = "Bip01 R UpperArm", DisplayName = Localization.Tr("marker_r_upperarm", "Épaule D (Écran Gauche)"), Position = new Vector3(-0.160f, 0.038f, 1.336f), Color = Color.FromArgb(220, 20, 60), ParentName = "Bip01 R Clavicle", SymmetricBoneName = "Bip01 L UpperArm" });
            _markers.Add(new JointMarker { BoneName = "Bip01 R Forearm", DisplayName = Localization.Tr("marker_r_forearm", "Coude D (Écran Gauche)"), Position = new Vector3(-0.380f, 0.031f, 1.335f), Color = Color.FromArgb(205, 92, 92), ParentName = "Bip01 R UpperArm", SymmetricBoneName = "Bip01 L Forearm" });
            _markers.Add(new JointMarker { BoneName = "Bip01 R Hand", DisplayName = Localization.Tr("marker_r_hand", "Poignet D (Écran Gauche)"), Position = new Vector3(-0.580f, 0.003f, 1.332f), Color = Color.FromArgb(255, 99, 71), ParentName = "Bip01 R Forearm", SymmetricBoneName = "Bip01 L Hand" });
            _markers.Add(new JointMarker { BoneName = "Bip01 R Finger0", DisplayName = Localization.Tr("marker_r_thumb", "Pouce D (Écran Gauche)"), Position = new Vector3(-0.621f, -0.032f, 1.324f), Color = Color.FromArgb(255, 140, 0), ParentName = "Bip01 R Hand", SymmetricBoneName = "Bip01 L Finger0" });
            _markers.Add(new JointMarker { BoneName = "Bip01 R Finger1", DisplayName = Localization.Tr("marker_r_fingers", "Doigts D (Écran Gauche)"), Position = new Vector3(-0.655f, -0.013f, 1.327f), Color = Color.FromArgb(0, 206, 209), ParentName = "Bip01 R Hand", SymmetricBoneName = "Bip01 L Finger1" });

            // Left Arm (Vue de face = Écran Droite, X > 0)
            _markers.Add(new JointMarker { BoneName = "Bip01 L Clavicle", DisplayName = Localization.Tr("marker_l_clavicle", "Clavicule G (Écran Droite)"), Position = new Vector3(0.042f, 0.031f, 1.339f), Color = Color.FromArgb(30, 144, 255), ParentName = "Bip01 Spine2", SymmetricBoneName = "Bip01 R Clavicle" });
            _markers.Add(new JointMarker { BoneName = "Bip01 L UpperArm", DisplayName = Localization.Tr("marker_l_upperarm", "Épaule G (Écran Droite)"), Position = new Vector3(0.160f, 0.038f, 1.336f), Color = Color.FromArgb(65, 105, 225), ParentName = "Bip01 L Clavicle", SymmetricBoneName = "Bip01 R UpperArm" });
            _markers.Add(new JointMarker { BoneName = "Bip01 L Forearm", DisplayName = Localization.Tr("marker_l_forearm", "Coude G (Écran Droite)"), Position = new Vector3(0.380f, 0.031f, 1.335f), Color = Color.FromArgb(100, 149, 237), ParentName = "Bip01 L UpperArm", SymmetricBoneName = "Bip01 R Forearm" });
            _markers.Add(new JointMarker { BoneName = "Bip01 L Hand", DisplayName = Localization.Tr("marker_l_hand", "Poignet G (Écran Droite)"), Position = new Vector3(0.580f, 0.003f, 1.332f), Color = Color.FromArgb(0, 191, 255), ParentName = "Bip01 L Forearm", SymmetricBoneName = "Bip01 R Hand" });
            _markers.Add(new JointMarker { BoneName = "Bip01 L Finger0", DisplayName = Localization.Tr("marker_l_thumb", "Pouce G (Écran Droite)"), Position = new Vector3(0.621f, -0.032f, 1.324f), Color = Color.FromArgb(255, 191, 0), ParentName = "Bip01 L Hand", SymmetricBoneName = "Bip01 R Finger0" });
            _markers.Add(new JointMarker { BoneName = "Bip01 L Finger1", DisplayName = Localization.Tr("marker_l_fingers", "Doigts G (Écran Droite)"), Position = new Vector3(0.655f, -0.013f, 1.327f), Color = Color.FromArgb(0, 255, 255), ParentName = "Bip01 L Hand", SymmetricBoneName = "Bip01 R Finger1" });

            // Right Leg (Vue de face = Écran Gauche, X < 0)
            _markers.Add(new JointMarker { BoneName = "Bip01 R Thigh", DisplayName = Localization.Tr("marker_r_thigh", "Hanche D (Écran Gauche)"), Position = new Vector3(-0.086f, 0.037f, 0.900f), Color = Color.FromArgb(218, 112, 214), ParentName = "Bip01 Pelvis", SymmetricBoneName = "Bip01 L Thigh" });
            _markers.Add(new JointMarker { BoneName = "Bip01 R Calf", DisplayName = Localization.Tr("marker_r_calf", "Genou D (Écran Gauche)"), Position = new Vector3(-0.092f, 0.041f, 0.500f), Color = Color.FromArgb(186, 85, 211), ParentName = "Bip01 R Thigh", SymmetricBoneName = "Bip01 L Calf" });
            _markers.Add(new JointMarker { BoneName = "Bip01 R Foot", DisplayName = Localization.Tr("marker_r_foot", "Cheville D (Écran Gauche)"), Position = new Vector3(-0.097f, 0.070f, 0.100f), Color = Color.FromArgb(255, 105, 180), ParentName = "Bip01 R Calf", SymmetricBoneName = "Bip01 L Foot" });

            // Left Leg (Vue de face = Écran Droite, X > 0)
            _markers.Add(new JointMarker { BoneName = "Bip01 L Thigh", DisplayName = Localization.Tr("marker_l_thigh", "Hanche G (Écran Droite)"), Position = new Vector3(0.086f, 0.037f, 0.900f), Color = Color.FromArgb(72, 209, 204), ParentName = "Bip01 Pelvis", SymmetricBoneName = "Bip01 R Thigh" });
            _markers.Add(new JointMarker { BoneName = "Bip01 L Calf", DisplayName = Localization.Tr("marker_l_calf", "Genou G (Écran Droite)"), Position = new Vector3(0.092f, 0.041f, 0.500f), Color = Color.FromArgb(32, 178, 170), ParentName = "Bip01 L Thigh", SymmetricBoneName = "Bip01 R Calf" });
            _markers.Add(new JointMarker { BoneName = "Bip01 L Foot", DisplayName = Localization.Tr("marker_l_foot", "Cheville G (Écran Droite)"), Position = new Vector3(0.097f, 0.070f, 0.100f), Color = Color.FromArgb(0, 139, 139), ParentName = "Bip01 L Calf", SymmetricBoneName = "Bip01 R Foot" });

            RefreshJointsCombo();
        }

        private void RefreshJointsCombo()
        {
            if (cbJoints == null) return;
            cbJoints.Items.Clear();
            foreach (var m in _markers) cbJoints.Items.Add(m.DisplayName);
            if (_selectedMarkerIndex >= 0 && _selectedMarkerIndex < cbJoints.Items.Count)
                cbJoints.SelectedIndex = _selectedMarkerIndex;
            else if (cbJoints.Items.Count > 0)
                cbJoints.SelectedIndex = 0;

            _selectedMarkerIndices.Clear();
            if (_selectedMarkerIndex >= 0) _selectedMarkerIndices.Add(_selectedMarkerIndex);
        }

        private void InitializeComponentCustom()
        {
            Text = Localization.Tr("rig_title", "⚡ Convert/create rig to UO (Rig Studio)");
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

            btnVueFace = CreateCamBtn(Localization.Tr("cam_front", "👁 Vue Face (Z/X)"), () => SetViewMode(RigViewMode.Front));
            btnVueProfil = CreateCamBtn(Localization.Tr("cam_profile", "👁 Vue Profil (Z/Y)"), () => SetViewMode(RigViewMode.Profile));
            btnVueDos = CreateCamBtn(Localization.Tr("cam_back", "👁 Vue Dos"), () => SetViewMode(RigViewMode.Back));
            btnVue3D = CreateCamBtn(Localization.Tr("cam_3d", "🎥 3D Persp"), () => SetViewMode(RigViewMode.Free3D));

            pnlCamTools.Controls.Add(btnVueFace);
            pnlCamTools.Controls.Add(btnVueProfil);
            pnlCamTools.Controls.Add(btnVueDos);
            pnlCamTools.Controls.Add(btnVue3D);
            pnlCamTools.Controls.Add(CreateCamBtn(Localization.Tr("cam_center", "🎯 Centrer Caméra"), () => { _cameraDist = 2.8f; _cameraPan = Vector3.Zero; }));
            UpdateViewButtonsUI();

            chkWireframe = new CheckBox
            {
                Text = Localization.Tr("chk_wireframe", "Fil de fer"),
                ForeColor = Color.Gainsboro,
                AutoSize = true,
                Margin = new Padding(16, 6, 0, 0)
            };
            chkWireframe.CheckedChanged += (s, e) => glControl.Invalidate();
            pnlCamTools.Controls.Add(chkWireframe);

            chkShowSkeleton = new CheckBox
            {
                Text = Localization.Tr("chk_skeleton", "Squelette"),
                Checked = true,
                ForeColor = Color.LimeGreen,
                AutoSize = true,
                Margin = new Padding(12, 6, 0, 0)
            };
            chkShowSkeleton.CheckedChanged += (s, e) => glControl.Invalidate();
            pnlCamTools.Controls.Add(chkShowSkeleton);

            chkShowMesh = new CheckBox
            {
                Text = Localization.Tr("chk_mesh", "Modèle 3D"),
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
                Text = Localization.Tr("rig_grp_source", "1. Modèle 3D Source (Actif / Importé)"),
                Dock = DockStyle.Top,
                Height = 135,
                ForeColor = Color.Gold,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Padding = new Padding(10)
            };

            var pnlSourceBtns = new Panel { Dock = DockStyle.Top, Height = 34 };
            var btnBrowseModel = new Button
            {
                Text = Localization.Tr("rig_btn_browse", "📂 Ouvrir un autre modèle 3D…"),
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
                Text = Localization.Tr("rig_btn_reload_preview", "🔄 Recharger la preview active"),
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
                Text = Localization.Tr("rig_lbl_loading_model", "Chargement du modèle…"),
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
                Text = Localization.Tr("rig_grp_transform", "2. Orientation, Échelle & Alignement du Modèle"),
                Dock = DockStyle.Top,
                Height = 285,
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
            pnlTransformInner.Controls.Add(CreateTBtn(Localization.Tr("rig_btn_stand_up", "🧍 Redresser Debout (Z-up)"), Color.FromArgb(30, 90, 140), AutoStandUp));
            pnlTransformInner.Controls.Add(CreateTBtn(Localization.Tr("rig_btn_ground", "⚡ Pieds au sol (Z=0) & Centrer"), Color.FromArgb(20, 100, 70), AutoCenterAndGround));
            pnlTransformInner.Controls.Add(CreateTBtn(Localization.Tr("rig_btn_fit_skel", "⚡ Ajuster taille au squelette (1.75m)"), Color.FromArgb(90, 40, 110), AutoScaleToSkeleton));

            // Row 2: Rotations & Mirror
            pnlTransformInner.Controls.Add(CreateTBtn("↻ Rot X +90°", Color.FromArgb(45, 45, 50), () => RotateModel(90f, 0, 0)));
            pnlTransformInner.Controls.Add(CreateTBtn("↺ Rot X -90°", Color.FromArgb(45, 45, 50), () => RotateModel(-90f, 0, 0)));
            pnlTransformInner.Controls.Add(CreateTBtn("↻ Rot Y +90°", Color.FromArgb(45, 45, 50), () => RotateModel(0, 90f, 0)));
            pnlTransformInner.Controls.Add(CreateTBtn("↺ Rot Y -90°", Color.FromArgb(45, 45, 50), () => RotateModel(0, -90f, 0)));
            pnlTransformInner.Controls.Add(CreateTBtn("↻ Rot Z +90°", Color.FromArgb(45, 45, 50), () => RotateModel(0, 0, 90f)));
            pnlTransformInner.Controls.Add(CreateTBtn("↺ Rot Z -90°", Color.FromArgb(45, 45, 50), () => RotateModel(0, 0, -90f)));
            pnlTransformInner.Controls.Add(CreateTBtn("↻ 180°", Color.FromArgb(45, 45, 50), () => RotateModel(0, 0, 180f)));
            pnlTransformInner.Controls.Add(CreateTBtn(Localization.Tr("rig_btn_mirror_x", "⇋ Miroir X"), Color.FromArgb(70, 50, 40), ToggleMirrorX));

            // Row 3: Position Offset / Nudge
            var pnlNudge = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Margin = new Padding(0, 4, 0, 0)
            };
            var lblNudge = new Label { Text = Localization.Tr("rig_lbl_nudge", "Position Modèle :"), AutoSize = true, ForeColor = Color.Gainsboro, Padding = new Padding(0, 6, 4, 0) };
            pnlNudge.Controls.Add(lblNudge);
            pnlNudge.Controls.Add(CreateTBtn("X: ◀ -5cm", Color.FromArgb(40, 40, 45), () => NudgeOffset(-0.05f, 0, 0)));
            pnlNudge.Controls.Add(CreateTBtn("▶ +5cm", Color.FromArgb(40, 40, 45), () => NudgeOffset(0.05f, 0, 0)));
            pnlNudge.Controls.Add(CreateTBtn("Y: ▼ -5cm", Color.FromArgb(40, 40, 45), () => NudgeOffset(0, -0.05f, 0)));
            pnlNudge.Controls.Add(CreateTBtn("▲ +5cm", Color.FromArgb(40, 40, 45), () => NudgeOffset(0, 0.05f, 0)));
            pnlNudge.Controls.Add(CreateTBtn("Z: ▼ -5cm", Color.FromArgb(40, 40, 45), () => NudgeOffset(0, 0, -0.05f)));
            pnlNudge.Controls.Add(CreateTBtn("▲ +5cm", Color.FromArgb(40, 40, 45), () => NudgeOffset(0, 0, 0.05f)));
            pnlTransformInner.Controls.Add(pnlNudge);

            // Row 4: Manual scale input
            var pnlScale = new FlowLayoutPanel
            {
                AutoSize = true,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                Margin = new Padding(0, 4, 0, 0)
            };
            var lblScale = new Label { Text = Localization.Tr("rig_lbl_scale", "Échelle Modèle :"), AutoSize = true, ForeColor = Color.Gainsboro, Padding = new Padding(0, 6, 4, 0) };
            nudScale = new NumericUpDown { DecimalPlaces = 4, Minimum = 0.0001m, Maximum = 1000m, Value = 1.0m, Increment = 0.01m, Width = 85, BackColor = Color.FromArgb(22, 22, 24), ForeColor = Color.White, Margin = new Padding(0, 3, 4, 0) };
            nudScale.ValueChanged += (s, e) => OnMeshScaleChanged();

            pnlScale.Controls.Add(lblScale);
            pnlScale.Controls.Add(nudScale);
            pnlScale.Controls.Add(CreateTBtn("-10%", Color.FromArgb(40, 50, 60), () => SetModelScale(_scale * 0.9f)));
            pnlScale.Controls.Add(CreateTBtn("+10%", Color.FromArgb(40, 50, 60), () => SetModelScale(_scale * 1.1f)));
            pnlScale.Controls.Add(CreateTBtn("-5%", Color.FromArgb(35, 45, 55), () => SetModelScale(_scale * 0.95f)));
            pnlScale.Controls.Add(CreateTBtn("+5%", Color.FromArgb(35, 45, 55), () => SetModelScale(_scale * 1.05f)));
            pnlScale.Controls.Add(CreateTBtn("x0.01 (cm→m)", Color.FromArgb(30, 55, 75), () => SetModelScale(_scale * 0.01f)));
            pnlScale.Controls.Add(CreateTBtn("x100 (m→cm)", Color.FromArgb(30, 55, 75), () => SetModelScale(_scale * 100f)));
            pnlScale.Controls.Add(CreateTBtn("1.0 (Reset)", Color.FromArgb(50, 50, 55), () => SetModelScale(1.0f)));
            pnlTransformInner.Controls.Add(pnlScale);

            // Row 5: Dimensions display
            lblBoundsInfo = new Label
            {
                Text = Localization.Tr("rig_lbl_calc_bounds", "Dimensions : calcul en cours…"),
                Size = new Size(500, 20),
                ForeColor = Color.LightSkyBlue,
                Margin = new Padding(0, 4, 0, 0)
            };
            pnlTransformInner.Controls.Add(lblBoundsInfo);

            grpTransform.Controls.Add(pnlTransformInner);

            // 3. Skeleton & Rigging Options Group
            var grpSkeleton = new GroupBox
            {
                Text = Localization.Tr("rig_grp_skeleton", "3. Squelette UO (Humains, Monstres, Animaux) & Options"),
                Dock = DockStyle.Top,
                Height = 135,
                ForeColor = Color.LimeGreen,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Padding = new Padding(10)
            };

            rbMale = new RadioButton { Text = Localization.Tr("rig_rb_male", "Homme (H_Male)"), Checked = true, Location = new Point(15, 22), AutoSize = true, ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
            rbFemale = new RadioButton { Text = Localization.Tr("rig_rb_female", "Femme (H_Female)"), Location = new Point(155, 22), AutoSize = true, ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
            rbCustom = new RadioButton { Text = Localization.Tr("rig_rb_creature", "Autre créature UO"), Location = new Point(295, 22), AutoSize = true, ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 9F, FontStyle.Bold) };

            btnChooseSkeleton = new Button
            {
                Text = Localization.Tr("rig_btn_choose_skel", "🔍 Choisir squelette..."),
                Location = new Point(455, 19),
                Size = new Size(200, 26),
                BackColor = Color.FromArgb(40, 75, 110),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold)
            };

            lblSkeletonName = new Label
            {
                Text = "Squelette actif : Homme (H_Male_Lod2.grn)",
                Location = new Point(15, 50),
                Size = new Size(650, 20),
                ForeColor = Color.LightGreen,
                Font = new Font("Segoe UI", 8.5F, FontStyle.Italic)
            };

            rbMale.CheckedChanged += (s, e) =>
            {
                if (_isInternalSkeletonSwitch) return;
                if (rbMale.Checked)
                {
                    lblSkeletonName.Text = "Squelette actif : Homme (H_Male_Lod2.grn)";
                    OnSkeletonChanged(false);
                }
            };
            rbFemale.CheckedChanged += (s, e) =>
            {
                if (_isInternalSkeletonSwitch) return;
                if (rbFemale.Checked)
                {
                    lblSkeletonName.Text = "Squelette actif : Femme (H_Female_Lod2.grn)";
                    OnSkeletonChanged(true);
                }
            };
            rbCustom.CheckedChanged += (s, e) =>
            {
                if (_isInternalSkeletonSwitch) return;
                if (rbCustom.Checked)
                {
                    if (string.IsNullOrEmpty(_customSkeletonPath) || !File.Exists(_customSkeletonPath))
                        ChooseCustomSkeleton();
                    else
                        OnSkeletonChangedCreature(_customSkeletonPath, _customSkeletonName);
                }
            };
            btnChooseSkeleton.Click += (s, e) => ChooseCustomSkeleton();

            chkSymmetry = new CheckBox
            {
                Text = Localization.Tr("rig_chk_symmetry", "Symétrie X automatique (déplacer G met à jour D)"),
                Checked = true,
                Location = new Point(15, 75),
                AutoSize = true,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular)
            };

            chkForceAutoSkin = new CheckBox
            {
                Text = Localization.Tr("rig_chk_force_skin", "Forcer le skinning volumétrique UO (recommandé si modèle statique)"),
                Checked = false,
                Location = new Point(15, 101),
                AutoSize = true,
                ForeColor = Color.LightYellow,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular)
            };

            grpSkeleton.Controls.Add(rbMale);
            grpSkeleton.Controls.Add(rbFemale);
            grpSkeleton.Controls.Add(rbCustom);
            grpSkeleton.Controls.Add(btnChooseSkeleton);
            grpSkeleton.Controls.Add(lblSkeletonName);
            grpSkeleton.Controls.Add(chkSymmetry);
            grpSkeleton.Controls.Add(chkForceAutoSkin);

            // 4. Markers Adjustments Group
            var grpMarkers = new GroupBox
            {
                Text = Localization.Tr("rig_grp_markers", "4. Positionnement des Articulations (Croix 3D)"),
                Dock = DockStyle.Top,
                Height = 425,
                ForeColor = Color.Orange,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Padding = new Padding(10)
            };

            var lblJointTip = new Label
            {
                Text = Localization.Tr("rig_lbl_joint_tip", "💡 Les croix 3D marquent les PIVOTS d'articulation (rotation), pas le milieu des volumes."),
                Dock = DockStyle.Top,
                Height = 20,
                ForeColor = Color.Khaki,
                Font = new Font("Segoe UI", 8F, FontStyle.Italic)
            };

            var pnlTopRigBtns = new Panel { Dock = DockStyle.Top, Height = 32 };
            var btnAutoDetect = new Button
            {
                Text = Localization.Tr("rig_btn_auto_detect", "🎯 Auto-Placement Mixamo (5 repères)"),
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
                Text = Localization.Tr("rig_btn_orig_skel", "🦴 Squelette d'Origine"),
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
                    if (rbCustom.Checked)
                    {
                        InitializeMarkersFromCreatureBones(_liveOriginalBones);
                    }
                    else
                    {
                        InitMarkersFromLiveBones(_liveOriginalBones);
                    }
                    EnsurePreviewModel();
                    glControl.Invalidate();
                }
                else
                {
                    MessageBox.Show(Localization.Tr("rig_msg_no_orig_skel", "Aucun squelette d'origine en mémoire pour ce modèle."), "Auto-Rig Studio", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            };
            pnlTopRigBtns.Controls.Add(btnAutoDetect);
            pnlTopRigBtns.Controls.Add(btnRestoreLiveBones);

            // Skinning Action Buttons Panel
            var pnlSkinBtns = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(0, 4, 0, 0) };
            var btnRecalcArms = new Button
            {
                Text = Localization.Tr("rig_btn_recalc_arms", "💪 Recalculer Skinning Bras"),
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
                Text = Localization.Tr("rig_btn_recalc_all", "🔥 Recalculer Tout le Skinning"),
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

            var lblJoint = new Label { Text = Localization.Tr("rig_lbl_sel_cross", "Croix sélectionnée :"), Location = new Point(12, 114), AutoSize = true, ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 9F, FontStyle.Regular) };
            cbJoints = new ComboBox
            {
                Location = new Point(145, 111),
                Width = 175,
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
                _selectedMarkerIndices.Clear();
                if (_selectedMarkerIndex >= 0) _selectedMarkerIndices.Add(_selectedMarkerIndex);
                UpdateNudValues();
                glControl.Invalidate();
            };

            var btnSelectAll = new Button
            {
                Text = Localization.Tr("rig_btn_sel_all", "🎯 Tout"),
                Location = new Point(325, 110),
                Size = new Size(72, 26),
                BackColor = Color.FromArgb(45, 55, 75),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8F, FontStyle.Bold)
            };
            btnSelectAll.FlatAppearance.BorderColor = Color.FromArgb(65, 75, 95);
            btnSelectAll.Click += (s, e) => SelectAllMarkers();

            var btnDeselectAll = new Button
            {
                Text = Localization.Tr("rig_btn_desel_all", "🧹 Aucun"),
                Location = new Point(402, 110),
                Size = new Size(70, 26),
                BackColor = Color.FromArgb(45, 45, 50),
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8F, FontStyle.Regular)
            };
            btnDeselectAll.FlatAppearance.BorderColor = Color.FromArgb(65, 65, 70);
            btnDeselectAll.Click += (s, e) => DeselectAllMarkers();

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

            pnlRotControls.Controls.Add(CreateLbl(Localization.Tr("rig_lbl_twist", "Torsion (Roll) :"), 5));
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
            pnlRotBtns.Controls.Add(CreateRotBtn(Localization.Tr("rig_palm_down", "Paume Bas (90°)"), 125, 115, () => SetRollExact(90f)));
            pnlRotBtns.Controls.Add(CreateRotBtn(Localization.Tr("rig_palm_back", "Paume Arr. (180°)"), 245, 125, () => SetRollExact(180f)));
            pnlRotBtns.Controls.Add(CreateRotBtn(Localization.Tr("rig_palm_default", "0° (Défaut)"), 375, 80, () => SetRollExact(0f)));

            // Skeleton Scale Presets Panel
            var pnlSkelScale = new FlowLayoutPanel
            {
                Location = new Point(12, 250),
                Size = new Size(480, 30),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0)
            };
            var lblSkelScale = new Label
            {
                Text = Localization.Tr("rig_lbl_skel_scale", "Échelle Squelette :"),
                AutoSize = true,
                ForeColor = Color.Gainsboro,
                Padding = new Padding(0, 5, 4, 0),
                Font = new Font("Segoe UI", 8.5F, FontStyle.Regular)
            };
            pnlSkelScale.Controls.Add(lblSkelScale);

            Button CreateSkelScaleBtn(string text, int width, float factor)
            {
                var b = new Button
                {
                    Text = text,
                    Size = new Size(width, 24),
                    BackColor = Color.FromArgb(30, 60, 50),
                    ForeColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                    Margin = new Padding(2, 1, 2, 0),
                    Font = new Font("Segoe UI", 8F, FontStyle.Bold)
                };
                b.FlatAppearance.BorderColor = Color.FromArgb(50, 80, 70);
                b.Click += (s, e) => ScaleSkeletonMarkers(factor);
                return b;
            }

            pnlSkelScale.Controls.Add(CreateSkelScaleBtn("-10%", 48, 0.90f));
            pnlSkelScale.Controls.Add(CreateSkelScaleBtn("+10%", 48, 1.10f));
            pnlSkelScale.Controls.Add(CreateSkelScaleBtn("-5%", 44, 0.95f));
            pnlSkelScale.Controls.Add(CreateSkelScaleBtn("+5%", 44, 1.05f));
            pnlSkelScale.Controls.Add(CreateSkelScaleBtn("x0.5", 44, 0.50f));
            pnlSkelScale.Controls.Add(CreateSkelScaleBtn("x2.0", 44, 2.00f));

            // Auto Fit & Center Skeleton Panel
            var pnlSkelFit = new FlowLayoutPanel
            {
                Location = new Point(12, 282),
                Size = new Size(480, 32),
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                Margin = new Padding(0)
            };

            var btnFitSkel = new Button
            {
                Text = Localization.Tr("rig_btn_fit_skel_to_mesh", "📏 Aligner Squelette sur Modèle (Hauteur & Sol)"),
                AutoSize = true,
                Height = 26,
                BackColor = Color.FromArgb(70, 45, 95),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(0, 2, 6, 0),
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold)
            };
            btnFitSkel.FlatAppearance.BorderColor = Color.FromArgb(90, 65, 115);
            btnFitSkel.Click += (s, e) => AutoFitSkeletonHeightToMesh();

            var btnCenterSkel = new Button
            {
                Text = Localization.Tr("rig_btn_center_skel", "🎯 Centrer"),
                Size = new Size(80, 26),
                BackColor = Color.FromArgb(40, 65, 85),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(0, 2, 2, 0),
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold)
            };
            btnCenterSkel.FlatAppearance.BorderColor = Color.FromArgb(60, 85, 105);
            btnCenterSkel.Click += (s, e) => AutoCenterSkeletonToMesh();

            pnlSkelFit.Controls.Add(btnFitSkel);
            pnlSkelFit.Controls.Add(btnCenterSkel);

            lblRotTip = new Label
            {
                Text = Localization.Tr("rig_lbl_rot_tip", "💡 Ctrl + glisser = multi-sélection. Les boutons d'échelle agissent sur la sélection (ou tout le squelette)."),
                Location = new Point(12, 318),
                AutoSize = true,
                ForeColor = Color.Orange,
                Font = new Font("Segoe UI", 8F, FontStyle.Bold)
            };

            var btnResetPose = new Button
            {
                Text = Localization.Tr("rig_btn_reset_pose", "🔄 Réinitialiser les repères Biped UO par défaut"),
                Dock = DockStyle.Bottom,
                Height = 28,
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular)
            };
            btnResetPose.Click += (s, e) =>
            {
                if (rbCustom.Checked && _refBones != null && _refBones.Count > 0)
                {
                    InitializeMarkersFromCreatureBones(_refBones);
                }
                else
                {
                    InitializeDefaultMarkers();
                    if (_refBones != null && _refBones.Count > 0)
                    {
                        InitMarkersFromLiveBones(_refBones);
                    }
                }
                UpdateNudValues();
                EnsurePreviewModel();
                glControl.Invalidate();
            };

            grpMarkers.Controls.Add(btnResetPose);
            grpMarkers.Controls.Add(lblRotTip);
            grpMarkers.Controls.Add(pnlSkelFit);
            grpMarkers.Controls.Add(pnlSkelScale);
            grpMarkers.Controls.Add(pnlRotBtns);
            grpMarkers.Controls.Add(pnlRotControls);
            grpMarkers.Controls.Add(pnlCoords);
            grpMarkers.Controls.Add(btnDeselectAll);
            grpMarkers.Controls.Add(btnSelectAll);
            grpMarkers.Controls.Add(cbJoints);
            grpMarkers.Controls.Add(lblJoint);
            grpMarkers.Controls.Add(pnlSkinBtns);
            grpMarkers.Controls.Add(pnlTopRigBtns);
            grpMarkers.Controls.Add(lblJointTip);

            // 5. Animation Preview Group
            var grpAnim = new GroupBox
            {
                Text = Localization.Tr("rig_grp_anim", "5. Prévisualisation en Direct (Test Animation UO)"),
                Dock = DockStyle.Top,
                Height = 220,
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
                Text = Localization.Tr("rig_btn_play_anim", "▶ Lancer le Test"),
                Location = new Point(0, 4),
                Width = 145,
                Height = 28,
                BackColor = Color.FromArgb(80, 40, 110),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold)
            };
            btnPlayAnim.Click += (s, e) => TogglePlayAnimation();

            btnStopAnim = new Button
            {
                Text = Localization.Tr("rig_btn_stop_anim", "⏹ T-Pose"),
                Location = new Point(152, 4),
                Width = 95,
                Height = 28,
                BackColor = Color.FromArgb(48, 48, 52),
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular)
            };
            btnStopAnim.Click += (s, e) => StopAnimation();

            var btnVictoryAnim = new Button
            {
                Text = Localization.Tr("rig_btn_victory", "✨ Test Doigts (Victoire)"),
                Location = new Point(254, 4),
                Width = 220,
                Height = 28,
                BackColor = Color.FromArgb(30, 115, 80),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold)
            };
            btnVictoryAnim.Click += (s, e) =>
            {
                for (int i = 0; i < cbAnimTest.Items.Count; i++)
                {
                    string itemStr = cbAnimTest.Items[i].ToString() ?? "";
                    if (itemStr.IndexOf("Victory_01", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        itemStr.IndexOf("Victory", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        cbAnimTest.SelectedIndex = i;
                        break;
                    }
                }
                if (!_isPlayingAnim) TogglePlayAnimation();
            };

            var btnImportAnim = new Button
            {
                Text = Localization.Tr("rig_btn_import_action", "📂 Importer Action..."),
                Location = new Point(480, 4),
                Width = 160,
                Height = 28,
                BackColor = Color.FromArgb(40, 80, 130),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold)
            };
            btnImportAnim.Click += (s, e) => PromptImportTestAnimation();

            pnlAnimBtns.Controls.Add(btnPlayAnim);
            pnlAnimBtns.Controls.Add(btnStopAnim);
            pnlAnimBtns.Controls.Add(btnVictoryAnim);
            pnlAnimBtns.Controls.Add(btnImportAnim);

            // Finger Flex Test Panel (interactive slider & quick buttons)
            var pnlFingerFlex = new Panel { Dock = DockStyle.Top, Height = 62, Padding = new Padding(0, 4, 0, 0) };
            lblFingerFlex = new Label
            {
                Text = Localization.Tr("rig_lbl_finger_flex", "✋ Test Flexion Doigts / Poing : 0%"),
                Location = new Point(0, 4),
                AutoSize = true,
                ForeColor = Color.Gold,
                Font = new Font("Segoe UI", 9F, FontStyle.Bold)
            };
            var btnFlexOpen = new Button
            {
                Text = Localization.Tr("rig_btn_flex_open", "Ouvrir"),
                Location = new Point(285, 2),
                Size = new Size(60, 22),
                BackColor = Color.FromArgb(45, 45, 48),
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8F, FontStyle.Regular)
            };
            var btnFlexFist = new Button
            {
                Text = Localization.Tr("rig_btn_flex_fist", "Poing"),
                Location = new Point(350, 2),
                Size = new Size(60, 22),
                BackColor = Color.FromArgb(120, 50, 20),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8F, FontStyle.Bold)
            };
            tbFingerFlex = new TrackBar
            {
                Location = new Point(0, 26),
                Width = 475,
                Height = 26,
                Minimum = 0,
                Maximum = 100,
                TickStyle = TickStyle.None
            };
            btnFlexOpen.Click += (s, e) =>
            {
                tbFingerFlex.Value = 0;
                _fingerFlexAmount = 0f;
                lblFingerFlex.Text = Localization.Tr("rig_lbl_finger_flex", "✋ Test Flexion Doigts / Poing : 0%");
                ApplyAnimationPose(_animTime);
                glControl.Invalidate();
            };
            btnFlexFist.Click += (s, e) =>
            {
                tbFingerFlex.Value = 100;
                _fingerFlexAmount = 1.0f;
                lblFingerFlex.Text = Localization.TrFmt("rig_lbl_finger_flex_pct", "✋ Test Flexion Doigts / Poing : {0}%", 100);
                ApplyAnimationPose(_animTime);
                glControl.Invalidate();
            };
            tbFingerFlex.Scroll += (s, e) =>
            {
                _fingerFlexAmount = tbFingerFlex.Value / 100f;
                lblFingerFlex.Text = Localization.TrFmt("rig_lbl_finger_flex_pct", "✋ Test Flexion Doigts / Poing : {0}%", tbFingerFlex.Value);
                ApplyAnimationPose(_animTime);
                glControl.Invalidate();
            };
            pnlFingerFlex.Controls.Add(lblFingerFlex);
            pnlFingerFlex.Controls.Add(btnFlexOpen);
            pnlFingerFlex.Controls.Add(btnFlexFist);
            pnlFingerFlex.Controls.Add(tbFingerFlex);

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
            grpAnim.Controls.Add(pnlFingerFlex);
            grpAnim.Controls.Add(pnlAnimBtns);
            grpAnim.Controls.Add(cbAnimTest);

            // 6. Validation Action Group
            var grpAction = new GroupBox
            {
                Text = Localization.Tr("rig_grp_action", "6. Enregistrement"),
                Dock = DockStyle.Top,
                Height = 90,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold),
                Padding = new Padding(10)
            };

            btnValidate = new Button
            {
                Text = Localization.Tr("rig_btn_validate", "⚡ Valider & Générer le Character UO (.char)"),
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
                if (rbCustom != null && rbCustom.Checked && _refBones != null && _refBones.Count > 0)
                {
                    float refMinZ = float.MaxValue, refMaxZ = float.MinValue;
                    foreach (var b in _refBones)
                    {
                        if (Math.Abs(b.InverseBindMatrix.Determinant) > 1e-5f)
                        {
                            float bz = Matrix4.Invert(b.InverseBindMatrix).Row3.Z;
                            refMinZ = Math.Min(refMinZ, bz);
                            refMaxZ = Math.Max(refMaxZ, bz);
                        }
                    }
                    if (refMaxZ > refMinZ + 0.1f)
                    {
                        targetH = (refMaxZ - refMinZ) * 1.1f;
                    }
                }

                float factor = targetH / currentH;
                SetModelScale(_scale * factor);
                AutoCenterAndGround();
            }
        }

        private void SetModelScale(float newScale)
        {
            newScale = MathHelper.Clamp(newScale, 0.0001f, 1000f);
            _scale = newScale;
            if (nudScale != null)
            {
                _isInternalScaleChange = true;
                nudScale.Value = (decimal)_scale;
                _isInternalScaleChange = false;
            }
            RebakeModelTransform();
            EnsurePreviewModel();
            glControl.Invalidate();
        }

        private void OnMeshScaleChanged()
        {
            if (_isInternalScaleChange || nudScale == null) return;
            _scale = (float)nudScale.Value;
            RebakeModelTransform();
            EnsurePreviewModel();
            glControl.Invalidate();
        }

        private void SelectAllMarkers()
        {
            _selectedMarkerIndices.Clear();
            for (int i = 0; i < _markers.Count; i++)
            {
                _selectedMarkerIndices.Add(i);
            }
            if (_markers.Count > 0)
            {
                _selectedMarkerIndex = 0;
                if (cbJoints != null && cbJoints.Items.Count > 0)
                {
                    cbJoints.SelectedIndex = 0;
                }
            }
            UpdateNudValues();
            glControl.Invalidate();
        }

        private void DeselectAllMarkers()
        {
            _selectedMarkerIndices.Clear();
            _selectedMarkerIndex = -1;
            UpdateNudValues();
            glControl.Invalidate();
        }

        private void ScaleSkeletonMarkers(float factor, bool onlySelectedIfMultiple = true)
        {
            if (_markers == null || _markers.Count == 0 || factor <= 0.0001f) return;

            List<JointMarker> targets;
            bool isSubset = onlySelectedIfMultiple && _selectedMarkerIndices.Count > 1 && _selectedMarkerIndices.Count < _markers.Count;

            if (isSubset)
            {
                targets = _selectedMarkerIndices.Where(i => i >= 0 && i < _markers.Count).Select(i => _markers[i]).ToList();
            }
            else
            {
                targets = _markers;
            }

            if (targets.Count == 0) return;

            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            float minZ = float.MaxValue, maxZ = float.MinValue;

            foreach (var m in targets)
            {
                minX = Math.Min(minX, m.Position.X); maxX = Math.Max(maxX, m.Position.X);
                minY = Math.Min(minY, m.Position.Y); maxY = Math.Max(maxY, m.Position.Y);
                minZ = Math.Min(minZ, m.Position.Z); maxZ = Math.Max(maxZ, m.Position.Z);
            }

            Vector3 pivot;
            if (isSubset)
            {
                pivot = new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, (minZ + maxZ) * 0.5f);
            }
            else
            {
                // Garder les pieds au sol pour tout le squelette
                pivot = new Vector3((minX + maxX) * 0.5f, (minY + maxY) * 0.5f, minZ);
            }

            foreach (var m in targets)
            {
                m.Position = pivot + (m.Position - pivot) * factor;

                if (isSubset && chkSymmetry != null && chkSymmetry.Checked && !string.IsNullOrEmpty(m.SymmetricBoneName))
                {
                    var sym = _markers.FirstOrDefault(x => string.Equals(x.BoneName, m.SymmetricBoneName, StringComparison.OrdinalIgnoreCase));
                    if (sym != null && !_selectedMarkerIndices.Contains(_markers.IndexOf(sym)))
                    {
                        sym.Position = new Vector3(-m.Position.X, m.Position.Y, m.Position.Z);
                    }
                }
            }

            UpdateNudValues();
            EnsurePreviewModel();
            glControl.Invalidate();
        }

        private void AutoFitSkeletonHeightToMesh()
        {
            if (_markers == null || _markers.Count == 0) return;
            if (_baseVertices == null || _baseVertices.Length == 0) return;

            float meshMinX = float.MaxValue, meshMaxX = float.MinValue;
            float meshMinY = float.MaxValue, meshMaxY = float.MinValue;
            float meshMinZ = float.MaxValue, meshMaxZ = float.MinValue;
            foreach (var v in _baseVertices)
            {
                meshMinX = Math.Min(meshMinX, v.X); meshMaxX = Math.Max(meshMaxX, v.X);
                meshMinY = Math.Min(meshMinY, v.Y); meshMaxY = Math.Max(meshMaxY, v.Y);
                meshMinZ = Math.Min(meshMinZ, v.Z); meshMaxZ = Math.Max(meshMaxZ, v.Z);
            }
            float meshH = meshMaxZ - meshMinZ;
            if (meshH < 0.001f) return;

            float skelMinX = float.MaxValue, skelMaxX = float.MinValue;
            float skelMinY = float.MaxValue, skelMaxY = float.MinValue;
            float skelMinZ = float.MaxValue, skelMaxZ = float.MinValue;
            foreach (var m in _markers)
            {
                skelMinX = Math.Min(skelMinX, m.Position.X); skelMaxX = Math.Max(skelMaxX, m.Position.X);
                skelMinY = Math.Min(skelMinY, m.Position.Y); skelMaxY = Math.Max(skelMaxY, m.Position.Y);
                skelMinZ = Math.Min(skelMinZ, m.Position.Z); skelMaxZ = Math.Max(skelMaxZ, m.Position.Z);
            }
            float skelH = skelMaxZ - skelMinZ;
            if (skelH < 0.001f) return;

            float factor = meshH / skelH;
            Vector3 pivot = new Vector3((skelMinX + skelMaxX) * 0.5f, (skelMinY + skelMaxY) * 0.5f, skelMinZ);
            foreach (var m in _markers)
            {
                m.Position = pivot + (m.Position - pivot) * factor;
            }

            float deltaX = ((meshMinX + meshMaxX) * 0.5f) - pivot.X;
            float deltaY = ((meshMinY + meshMaxY) * 0.5f) - pivot.Y;
            float deltaZ = meshMinZ - skelMinZ;

            var shift = new Vector3(deltaX, deltaY, deltaZ);
            foreach (var m in _markers)
            {
                m.Position += shift;
            }

            UpdateNudValues();
            EnsurePreviewModel();
            glControl.Invalidate();
        }

        private void AutoCenterSkeletonToMesh()
        {
            if (_markers == null || _markers.Count == 0) return;
            if (_baseVertices == null || _baseVertices.Length == 0) return;

            float meshMinX = float.MaxValue, meshMaxX = float.MinValue;
            float meshMinY = float.MaxValue, meshMaxY = float.MinValue;
            float meshMinZ = float.MaxValue, meshMaxZ = float.MinValue;
            foreach (var v in _baseVertices)
            {
                meshMinX = Math.Min(meshMinX, v.X); meshMaxX = Math.Max(meshMaxX, v.X);
                meshMinY = Math.Min(meshMinY, v.Y); meshMaxY = Math.Max(meshMinY, v.Y);
                meshMinZ = Math.Min(meshMinZ, v.Z); meshMaxZ = Math.Max(meshMaxZ, v.Z);
            }

            float skelMinX = float.MaxValue, skelMaxX = float.MinValue;
            float skelMinY = float.MaxValue, skelMaxY = float.MinValue;
            float skelMinZ = float.MaxValue, skelMaxZ = float.MinValue;
            foreach (var m in _markers)
            {
                skelMinX = Math.Min(skelMinX, m.Position.X); skelMaxX = Math.Max(skelMaxX, m.Position.X);
                skelMinY = Math.Min(skelMinY, m.Position.Y); skelMaxY = Math.Max(skelMaxY, m.Position.Y);
                skelMinZ = Math.Min(skelMinZ, m.Position.Z); skelMaxZ = Math.Max(skelMaxZ, m.Position.Z);
            }

            float deltaX = ((meshMinX + meshMaxX) * 0.5f) - ((skelMinX + skelMaxX) * 0.5f);
            float deltaY = ((meshMinY + meshMaxY) * 0.5f) - ((skelMinY + skelMaxY) * 0.5f);
            float deltaZ = meshMinZ - skelMinZ;

            var shift = new Vector3(deltaX, deltaY, deltaZ);
            foreach (var m in _markers)
            {
                m.Position += shift;
            }

            UpdateNudValues();
            EnsurePreviewModel();
            glControl.Invalidate();
        }

        private void RebakeModelTransform()
        {
            if (_rawParts.Count > 0)
            {
                var scaleMat = Matrix4.CreateScale(_mirrorX ? -_scale : _scale, _scale, _scale);
                var rotMat = Matrix4.CreateRotationX(MathHelper.DegreesToRadians(_rotX)) *
                             Matrix4.CreateRotationY(MathHelper.DegreesToRadians(_rotY)) *
                             Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(_rotZ));
                var transMat = Matrix4.CreateTranslation(_offset);
                var mat = scaleMat * rotMat * transMat;
                var normalMat = rotMat;

                _modelParts.Clear();
                var allVerts = new List<Vector3>();
                var allNorms = new List<Vector3>();
                var allUVs = new List<Vector2>();
                var allIndices = new List<int>();
                var allSkin = new List<VertexInfluence[]>();

                float minX = float.MaxValue, maxX = float.MinValue;
                float minY = float.MaxValue, maxY = float.MinValue;
                float minZ = float.MaxValue, maxZ = float.MinValue;

                foreach (var raw in _rawParts)
                {
                    int nVerts = raw.RawVertices.Length;
                    var bakedVerts = new Vector3[nVerts];
                    var bakedNorms = new Vector3[nVerts];

                    for (int i = 0; i < nVerts; i++)
                    {
                        var v = Vector3.TransformPosition(raw.RawVertices[i], mat);
                        bakedVerts[i] = v;

                        minX = Math.Min(minX, v.X); maxX = Math.Max(maxX, v.X);
                        minY = Math.Min(minY, v.Y); maxY = Math.Max(maxY, v.Y);
                        minZ = Math.Min(minZ, v.Z); maxZ = Math.Max(maxZ, v.Z);

                        if (raw.RawNormals != null && i < raw.RawNormals.Length)
                            bakedNorms[i] = Vector3.TransformNormal(raw.RawNormals[i], normalMat).Normalized();
                        else
                            bakedNorms[i] = Vector3.UnitZ;
                    }

                    var part = new GrannyModelPart
                    {
                        Name = raw.Name,
                        MaterialName = raw.MaterialName,
                        Color = raw.Color,
                        TextureName = raw.TextureName,
                        Vertices = bakedVerts,
                        Normals = bakedNorms,
                        UVs = raw.UVs,
                        Indices = raw.Indices,
                        Skin = raw.Skin != null ? (VertexInfluence[][])raw.Skin.Clone() : null
                    };
                    _modelParts.Add(part);

                    int baseIdx = allVerts.Count;
                    allVerts.AddRange(bakedVerts);
                    allNorms.AddRange(bakedNorms);
                    if (raw.UVs != null) allUVs.AddRange(raw.UVs);
                    else allUVs.AddRange(Enumerable.Repeat(Vector2.Zero, nVerts));

                    if (raw.Indices != null)
                    {
                        for (int i = 0; i < raw.Indices.Length; i++)
                            allIndices.Add(baseIdx + raw.Indices[i]);
                    }

                    if (raw.Skin != null && raw.Skin.Length == nVerts)
                        allSkin.AddRange(raw.Skin);
                    else
                        allSkin.AddRange(Enumerable.Repeat(Array.Empty<VertexInfluence>(), nVerts));
                }

                _baseVertices = allVerts.ToArray();
                _baseNormals = allNorms.ToArray();
                _rawUVs = allUVs.ToArray();
                _indices = allIndices.ToArray();
                _rawSkin = allSkin.ToArray();

                float w = maxX - minX;
                float d = maxY - minY;
                float h = maxZ - minZ;
                lblBoundsInfo.Text = $"Taille : Largeur={w:F2}m | Prof={d:F2}m | Hauteur={h:F2}m (Pieds Z: {minZ:F2}m)";

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
                return;
            }

            if (_rawVertices == null || _rawVertices.Length == 0) return;

            int n = _rawVertices.Length;
            _baseVertices = new Vector3[n];
            _baseNormals = new Vector3[n];

            var scaleMatFallback = Matrix4.CreateScale(_mirrorX ? -_scale : _scale, _scale, _scale);
            var rotMatFallback = Matrix4.CreateRotationX(MathHelper.DegreesToRadians(_rotX)) *
                         Matrix4.CreateRotationY(MathHelper.DegreesToRadians(_rotY)) *
                         Matrix4.CreateRotationZ(MathHelper.DegreesToRadians(_rotZ));
            var transMatFallback = Matrix4.CreateTranslation(_offset);
            var matFallback = scaleMatFallback * rotMatFallback * transMatFallback;
            var normalMatFallback = rotMatFallback;

            float minXf = float.MaxValue, maxXf = float.MinValue;
            float minYf = float.MaxValue, maxYf = float.MinValue;
            float minZf = float.MaxValue, maxZf = float.MinValue;

            for (int i = 0; i < n; i++)
            {
                var v = Vector3.TransformPosition(_rawVertices[i], matFallback);
                _baseVertices[i] = v;

                minXf = Math.Min(minXf, v.X); maxXf = Math.Max(maxXf, v.X);
                minYf = Math.Min(minYf, v.Y); maxYf = Math.Max(maxYf, v.Y);
                minZf = Math.Min(minZf, v.Z); maxZf = Math.Max(maxZf, v.Z);

                if (_rawNormals != null && i < _rawNormals.Length)
                {
                    _baseNormals[i] = Vector3.TransformNormal(_rawNormals[i], normalMatFallback).Normalized();
                }
                else
                {
                    _baseNormals[i] = Vector3.UnitZ;
                }
            }

            float wf = maxXf - minXf;
            float df = maxYf - minYf;
            float hf = maxZf - minZf;
            lblBoundsInfo.Text = $"Taille : Largeur={wf:F2}m | Prof={df:F2}m | Hauteur={hf:F2}m (Pieds Z: {minZf:F2}m)";

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

        private void ChooseCustomSkeleton()
        {
            using var dlg = new VdChooseMapDialog(_basePath);
            if (dlg.ShowDialog(this) == DialogResult.OK && dlg.SelectedModel != null)
            {
                _customSkeletonPath = dlg.SelectedModel.MeshPath;
                _customSkeletonName = dlg.SelectedModel.DisplayName;
                rbCustom.Text = $"Créature : {_customSkeletonName}";
                rbCustom.Checked = true;
                OnSkeletonChangedCreature(_customSkeletonPath, _customSkeletonName);
            }
            else
            {
                if (string.IsNullOrEmpty(_customSkeletonPath))
                {
                    rbMale.Checked = true;
                }
            }
        }

        private void OnSkeletonChanged(bool isFemale)
        {
            string maleRef = Path.Combine(_basePath, "Humans", "H_Male_Lod2.grn");
            string femaleRef = Path.Combine(_basePath, "Humans", "H_Female_Lod2.grn");
            if (!File.Exists(maleRef)) maleRef = Path.Combine(Granny3DControl.BasePath, "Humans", "H_Male_Lod2.grn");
            if (!File.Exists(femaleRef)) femaleRef = Path.Combine(Granny3DControl.BasePath, "Humans", "H_Female_Lod2.grn");

            _refPath = isFemale && File.Exists(femaleRef) ? femaleRef : maleRef;
            InitializeDefaultMarkers();
            LoadReferenceBones();
            _previewModel = null;
            EnsurePreviewModel();
            PopulateTestAnimations();
            glControl.Invalidate();
        }

        private void OnSkeletonChangedCreature(string meshPath, string displayName)
        {
            if (string.IsNullOrEmpty(meshPath) || !File.Exists(meshPath)) return;

            _refPath = meshPath;
            _customSkeletonPath = meshPath;
            _customSkeletonName = !string.IsNullOrEmpty(displayName) ? displayName : Path.GetFileNameWithoutExtension(meshPath);

            if (lblSkeletonName != null)
            {
                lblSkeletonName.Text = $"Squelette actif : {_customSkeletonName} ({Path.GetFileName(meshPath)})";
            }

            LoadReferenceBones();
            InitializeMarkersFromCreatureBones(_refBones);
            _previewModel = null;
            EnsurePreviewModel();
            PopulateTestAnimations();
            glControl.Invalidate();
        }

        private void InitializeMarkersFromCreatureBones(List<GrannyBone> bones)
        {
            _markers.Clear();
            if (bones == null || bones.Count == 0) return;

            int nb = bones.Count;
            for (int i = 0; i < nb; i++) bones[i].Id = i;

            var worldPos = new Vector3[nb];
            var worldMat = new Matrix4[nb];

            for (int i = 0; i < nb; i++)
            {
                var inv = bones[i].InverseBindMatrix;
                if (Math.Abs(inv.Determinant) > 1e-5f)
                {
                    worldMat[i] = Matrix4.Invert(inv);
                    worldPos[i] = worldMat[i].Row3.Xyz;
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
                if (worldPos[idx] == Vector3.Zero && Math.Abs(bones[idx].InverseBindMatrix.Determinant) <= 1e-5f)
                {
                    int p = GetParentIndex(bones, idx);
                    var local = Matrix4.CreateFromQuaternion(bones[idx].LocalRotation) * Matrix4.CreateTranslation(bones[idx].LocalPosition);
                    if (p >= 0 && p < nb && p != idx)
                        worldMat[idx] = local * worldMat[p];
                    else
                        worldMat[idx] = local;
                    worldPos[idx] = worldMat[idx].Row3.Xyz;
                }

                foreach (int child in children[idx])
                    ComputeFk(child);
            }

            foreach (int r in roots)
                ComputeFk(r);

            for (int i = 0; i < nb; i++)
            {
                var b = bones[i];
                string? pName = (b.ParentId >= 0 && b.ParentId < nb) ? bones[b.ParentId].Name : null;

                // Symmetry detection: L <-> R and Ponytail1 (Right) <-> Ponytail2 (Left)
                string? symName = null;
                string bName = b.Name;
                if (bName.Contains(" L "))
                {
                    string cand = bName.Replace(" L ", " R ");
                    if (bones.Any(x => string.Equals(x.Name, cand, StringComparison.OrdinalIgnoreCase)))
                        symName = cand;
                }
                else if (bName.Contains(" R "))
                {
                    string cand = bName.Replace(" R ", " L ");
                    if (bones.Any(x => string.Equals(x.Name, cand, StringComparison.OrdinalIgnoreCase)))
                        symName = cand;
                }
                else if (bName.Contains("Ponytail1"))
                {
                    string cand = bName.Replace("Ponytail1", "Ponytail2");
                    if (bones.Any(x => string.Equals(x.Name, cand, StringComparison.OrdinalIgnoreCase)))
                        symName = cand;
                }
                else if (bName.Contains("Ponytail2"))
                {
                    string cand = bName.Replace("Ponytail2", "Ponytail1");
                    if (bones.Any(x => string.Equals(x.Name, cand, StringComparison.OrdinalIgnoreCase)))
                        symName = cand;
                }
                else if (bName.StartsWith("Left", StringComparison.OrdinalIgnoreCase))
                {
                    string cand = "Right" + bName.Substring(4);
                    if (bones.Any(x => string.Equals(x.Name, cand, StringComparison.OrdinalIgnoreCase)))
                        symName = cand;
                }
                else if (bName.StartsWith("Right", StringComparison.OrdinalIgnoreCase))
                {
                    string cand = "Left" + bName.Substring(5);
                    if (bones.Any(x => string.Equals(x.Name, cand, StringComparison.OrdinalIgnoreCase)))
                        symName = cand;
                }

                Color col = GetCreatureMarkerColor(bName, i, nb);

                _markers.Add(new JointMarker
                {
                    BoneName = b.Name,
                    DisplayName = b.Name,
                    Position = worldPos[i],
                    ParentName = pName,
                    SymmetricBoneName = symName,
                    Color = col
                });
            }

            RefreshJointsCombo();
        }

        private static Color GetCreatureMarkerColor(string name, int idx, int total)
        {
            string l = name.ToLowerInvariant();
            if (l.Contains("head") || l.Contains("jaw") || l.Contains("neck")) return Color.FromArgb(255, 140, 0);
            if (l.Contains("spine") || l.Contains("pelvis") || l.Contains("hips") || l.Contains("tail")) return Color.FromArgb(50, 205, 50);
            if (l.Contains("ponytail1")) return Color.FromArgb(220, 20, 60); // Right arm
            if (l.Contains("ponytail2")) return Color.FromArgb(30, 144, 255); // Left arm
            if (l.Contains(" l ") || l.Contains("left") || l.EndsWith("_l")) return Color.FromArgb(30, 144, 255);
            if (l.Contains(" r ") || l.Contains("right") || l.EndsWith("_r")) return Color.FromArgb(220, 20, 60);

            int cIdx = idx % 6;
            return cIdx switch
            {
                0 => Color.FromArgb(255, 215, 0),
                1 => Color.FromArgb(0, 206, 209),
                2 => Color.FromArgb(218, 112, 214),
                3 => Color.FromArgb(255, 105, 180),
                4 => Color.FromArgb(124, 252, 0),
                _ => Color.FromArgb(255, 160, 122)
            };
        }

        private string? FindMatchingCreatureReference(List<GrannyBone> bones, string? modelPathHint, out string displayName)
        {
            displayName = "Créature";
            if (bones == null || bones.Count == 0) return null;

            string baseDir = !string.IsNullOrEmpty(_basePath) ? _basePath : Granny3DControl.BasePath;
            string[] creatureFolders = new[] { "Animals", "Monsters", "SeaCreatures" };

            var boneNameSet = new HashSet<string>(bones.Select(b => b.Name), StringComparer.OrdinalIgnoreCase);

            // 1. Détection directe si un os a le nom master_<créature>
            foreach (var b in bones)
            {
                if (b.Name.StartsWith("master_", StringComparison.OrdinalIgnoreCase))
                {
                    string clue = b.Name.Substring("master_".Length).Replace("_", "");
                    foreach (var folder in creatureFolders)
                    {
                        string p = Path.Combine(baseDir, folder);
                        if (!Directory.Exists(p)) continue;
                        foreach (var f in Directory.GetFiles(p, "*_Lod2.grn"))
                        {
                            string fnClean = Path.GetFileNameWithoutExtension(f).Replace("_Lod2", "").Replace("_lod2", "").Replace("_", "");
                            if (fnClean.IndexOf(clue, StringComparison.OrdinalIgnoreCase) >= 0 || clue.IndexOf(fnClean, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                var parser = new GrannyFormatParser();
                                if (parser.Load(f) && parser.Bones.Count == bones.Count)
                                {
                                    displayName = Path.GetFileNameWithoutExtension(f).Replace("_Lod2", "").Replace("_lod2", "");
                                    return f;
                                }
                            }
                        }
                    }
                }
            }

            // 2. Détection directe pour les équidés / chevaux
            if (bones.Any(b => b.Name.IndexOf("HorseLink", StringComparison.OrdinalIgnoreCase) >= 0) ||
                (!string.IsNullOrEmpty(modelPathHint) && (modelPathHint.IndexOf("horse", StringComparison.OrdinalIgnoreCase) >= 0 || modelPathHint.IndexOf("cheval", StringComparison.OrdinalIgnoreCase) >= 0)))
            {
                string horseCandidate = Path.Combine(baseDir, "Animals", "Equines_Horse_Dappled_Brown_Lod2.grn");
                if (File.Exists(horseCandidate))
                {
                    displayName = "Equines_Horse";
                    return horseCandidate;
                }
            }

            // 3. Recherche dans les dossiers de créatures par nombre d'os et correspondance des noms
            string? bestMatch = null;
            int bestMatchedBoneNames = 0;
            string bestDisplayName = "Créature";

            foreach (var folder in creatureFolders)
            {
                string p = Path.Combine(baseDir, folder);
                if (!Directory.Exists(p)) continue;

                foreach (var f in Directory.GetFiles(p, "*_Lod2.grn"))
                {
                    var parser = new GrannyFormatParser();
                    if (parser.Load(f) && parser.Bones.Count == bones.Count)
                    {
                        int matchCount = parser.Bones.Count(pb => boneNameSet.Contains(pb.Name));
                        if (matchCount > bestMatchedBoneNames && matchCount >= (bones.Count / 2))
                        {
                            bestMatchedBoneNames = matchCount;
                            bestMatch = f;
                            bestDisplayName = Path.GetFileNameWithoutExtension(f).Replace("_Lod2", "").Replace("_lod2", "");
                            if (matchCount == bones.Count) break;
                        }
                    }
                }
                if (bestMatchedBoneNames == bones.Count) break;
            }

            if (bestMatch != null)
            {
                displayName = bestDisplayName;
                return bestMatch;
            }

            return null;
        }

        private void ApplyExistingRig(List<GrannyBone> bones, string? modelPathHint = null)
        {
            if (bones == null || bones.Count == 0) return;

            for (int i = 0; i < bones.Count; i++) bones[i].Id = i;
            _liveOriginalBones = new List<GrannyBone>(bones);

            // Est-ce un squelette bipède humain classique (43 os ou doigts humains) ?
            bool isHumanBiped = (bones.Count == 43 || (bones.Count >= 20 && bones.Any(b => b.Name.IndexOf("Finger1", StringComparison.OrdinalIgnoreCase) >= 0))) &&
                                bones.Any(b => b.Name.IndexOf("Bip01", StringComparison.OrdinalIgnoreCase) >= 0);

            if (isHumanBiped)
            {
                bool isFemale = (!string.IsNullOrEmpty(modelPathHint) && modelPathHint.IndexOf("female", StringComparison.OrdinalIgnoreCase) >= 0) ||
                                bones.Any(b => b.Name.IndexOf("female", StringComparison.OrdinalIgnoreCase) >= 0);

                string maleRef = Path.Combine(_basePath, "Humans", "H_Male_Lod2.grn");
                string femaleRef = Path.Combine(_basePath, "Humans", "H_Female_Lod2.grn");
                if (!File.Exists(maleRef)) maleRef = Path.Combine(Granny3DControl.BasePath, "Humans", "H_Male_Lod2.grn");
                if (!File.Exists(femaleRef)) femaleRef = Path.Combine(Granny3DControl.BasePath, "Humans", "H_Female_Lod2.grn");

                _refPath = isFemale && File.Exists(femaleRef) ? femaleRef : maleRef;
                _isInternalSkeletonSwitch = true;
                try
                {
                    if (isFemale) rbFemale.Checked = true;
                    else rbMale.Checked = true;
                    lblSkeletonName.Text = $"Squelette actif : {(isFemale ? "Femme" : "Homme")} ({bones.Count} os)";
                }
                finally
                {
                    _isInternalSkeletonSwitch = false;
                }

                LoadReferenceBones();
                InitializeDefaultMarkers();
                InitMarkersFromLiveBones(bones);
            }
            else
            {
                // Squelette de créature (Cheval, Animal, Monstre, Créature marine, Custom...)
                string? matchedRef = FindMatchingCreatureReference(bones, modelPathHint, out string creatureName);
                if (!string.IsNullOrEmpty(matchedRef) && File.Exists(matchedRef))
                {
                    _refPath = matchedRef;
                    _customSkeletonPath = matchedRef;
                    _customSkeletonName = creatureName;
                    LoadReferenceBones();
                }
                else
                {
                    _refPath = modelPathHint ?? string.Empty;
                    _customSkeletonPath = _refPath;
                    _customSkeletonName = !string.IsNullOrEmpty(modelPathHint) ? Path.GetFileNameWithoutExtension(modelPathHint) : "Créature";
                    _refBones = new List<GrannyBone>(bones);
                }

                _isInternalSkeletonSwitch = true;
                try
                {
                    rbCustom.Checked = true;
                    rbCustom.Text = $"Créature : {_customSkeletonName}";
                    lblSkeletonName.Text = $"Squelette actif : {_customSkeletonName} ({bones.Count} os)";
                }
                finally
                {
                    _isInternalSkeletonSwitch = false;
                }

                InitializeMarkersFromCreatureBones(bones);
            }

            if (_previewModel == null)
            {
                _previewModel = new GrannyModelFile
                {
                    Name = !string.IsNullOrEmpty(_currentModelPath) ? Path.GetFileNameWithoutExtension(_currentModelPath) : "CustomRigModel",
                    Bones = new List<GrannyBone>()
                };
            }
            _previewModel.Bones = new List<GrannyBone>();
            for (int i = 0; i < bones.Count; i++)
            {
                var b = bones[i];
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

            PopulateTestAnimations();
        }

        private void LoadReferenceBones()
        {
            try
            {
                if (File.Exists(_refPath))
                {
                    if (_refPath.EndsWith(".char", StringComparison.OrdinalIgnoreCase))
                    {
                        var charModel = GrannyModelFile.Load(_refPath);
                        if (charModel?.Bones != null && charModel.Bones.Count > 0)
                        {
                            _refBones = new List<GrannyBone>(charModel.Bones);
                            return;
                        }
                    }
                    else
                    {
                        var parser = new GrannyFormatParser();
                        if (parser.Load(_refPath) && parser.Bones.Count > 0)
                        {
                            _refBones = new List<GrannyBone>(parser.Bones);
                            return;
                        }
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

            // 1. Current active animation from UoFiddler preview if available
            if (_activeSceneAnim != null)
            {
                string activeName = !string.IsNullOrEmpty(_activeSceneAnimPath) ? Path.GetFileNameWithoutExtension(_activeSceneAnimPath) : "Animation en cours";
                cbAnimTest.Items.Add(new AnimTestItem
                {
                    DisplayName = $"⭐ [Action active UO] {activeName}",
                    FilePath = _activeSceneAnimPath,
                    PreloadedAnim = _activeSceneAnim
                });
            }

            // 2. Embedded animation clips from the source FBX file if any
            if (!string.IsNullOrEmpty(_currentModelPath) && File.Exists(_currentModelPath) && Path.GetExtension(_currentModelPath).Equals(".fbx", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var aCtx = new AssimpContext();
                    var aScene = aCtx.ImportFile(_currentModelPath, PostProcessSteps.None);
                    if (aScene != null && aScene.AnimationCount > 0)
                    {
                        var bonesForRetarget = (_previewModel != null && _previewModel.Bones.Count > 0) ? _previewModel.Bones : _refBones;
                        for (int ai = 0; ai < aScene.AnimationCount; ai++)
                        {
                            var clip = aScene.Animations[ai];
                            string clipName = !string.IsNullOrEmpty(clip.Name) ? clip.Name : $"Clip_{ai + 1}";
                            if (bonesForRetarget != null && MixamoRetarget.TryImport(_currentModelPath, bonesForRetarget, clipName, out var fbxAnim, out _, ai) && fbxAnim != null)
                            {
                                cbAnimTest.Items.Add(new AnimTestItem
                                {
                                    DisplayName = $"🎬 [Action FBX] {clipName}",
                                    FilePath = _currentModelPath,
                                    PreloadedAnim = fbxAnim,
                                    FbxAnimIndex = ai
                                });
                            }
                        }
                    }
                }
                catch { }
            }

            // 3. User imported actions (*.anim and custom *.grn) from _outFolder, Others, and model directory
            var animFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrEmpty(_outFolder) && Directory.Exists(_outFolder)) animFolders.Add(_outFolder);
            string othersDir = Path.Combine(_basePath, "Others");
            if (!Directory.Exists(othersDir)) othersDir = @"d:\Games\sphere\PROGRAMMES\3D_UO\Models\Others";
            if (Directory.Exists(othersDir)) animFolders.Add(othersDir);
            if (!string.IsNullOrEmpty(_currentModelPath))
            {
                string? mDir = Path.GetDirectoryName(_currentModelPath);
                if (!string.IsNullOrEmpty(mDir) && Directory.Exists(mDir)) animFolders.Add(mDir);
            }

            foreach (var dir in animFolders)
            {
                try
                {
                    foreach (var af in Directory.GetFiles(dir, "*.anim"))
                    {
                        string afn = Path.GetFileName(af);
                        cbAnimTest.Items.Add(new AnimTestItem
                        {
                            DisplayName = $"📁 [Action importée] {afn}",
                            FilePath = af
                        });
                    }
                }
                catch { }
            }

            // 4. Standard UO Animations (Humans or Custom Creature)
            if (rbFemale.Checked || rbMale.Checked)
            {
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
                    {
                        bool hasFingers = HasFingerMotionAnim(f);
                        string displayName = hasFingers ? $"✨ {f} [Doigts animés]" : f;
                        cbAnimTest.Items.Add(new AnimTestItem
                        {
                            DisplayName = displayName,
                            FilePath = Path.Combine(humansDir, f)
                        });
                    }
                }
            }
            else
            {
                // Custom creature animations (e.g. Centaurs_centaur, Monsters, Animals, etc.)
                string creatureFolder = string.Empty;
                string refFn = string.Empty;

                if (!string.IsNullOrEmpty(_customSkeletonPath) && File.Exists(_customSkeletonPath) && !_customSkeletonPath.EndsWith(".char", StringComparison.OrdinalIgnoreCase))
                {
                    creatureFolder = Path.GetDirectoryName(_customSkeletonPath) ?? string.Empty;
                    refFn = Path.GetFileNameWithoutExtension(_customSkeletonPath);
                }
                else if (!string.IsNullOrEmpty(_refPath) && File.Exists(_refPath) && !_refPath.EndsWith(".char", StringComparison.OrdinalIgnoreCase))
                {
                    creatureFolder = Path.GetDirectoryName(_refPath) ?? string.Empty;
                    refFn = Path.GetFileNameWithoutExtension(_refPath);
                }

                if (!string.IsNullOrEmpty(creatureFolder) && Directory.Exists(creatureFolder))
                {
                    string cleanName = refFn;
                    int lodPos = cleanName.IndexOf("_Lod", StringComparison.OrdinalIgnoreCase);
                    if (lodPos < 0) lodPos = cleanName.IndexOf("_lod", StringComparison.OrdinalIgnoreCase);
                    if (lodPos >= 0) cleanName = cleanName.Substring(0, lodPos);

                    try
                    {
                        var grnFiles = Directory.GetFiles(creatureFolder, "*.grn")
                            .Select(Path.GetFileName)
                            .Where(fn => fn != null &&
                                         !fn.EndsWith("_Lod2.grn", StringComparison.OrdinalIgnoreCase) &&
                                         !fn.EndsWith("_Lod1.grn", StringComparison.OrdinalIgnoreCase) &&
                                         !fn.EndsWith("_Lod0.grn", StringComparison.OrdinalIgnoreCase) &&
                                         !fn.EndsWith("_lod2.grn", StringComparison.OrdinalIgnoreCase) &&
                                         !fn.EndsWith("_lod1.grn", StringComparison.OrdinalIgnoreCase) &&
                                         !fn.EndsWith("_lod0.grn", StringComparison.OrdinalIgnoreCase) &&
                                         fn.StartsWith(cleanName, StringComparison.OrdinalIgnoreCase))
                            .OrderBy(fn => fn)
                            .ToList();

                        foreach (var f in grnFiles)
                        {
                            cbAnimTest.Items.Add(new AnimTestItem
                            {
                                DisplayName = $"🐾 [{cleanName}] {f}",
                                FilePath = Path.Combine(creatureFolder, f)
                            });
                        }
                    }
                    catch { }
                }
            }

            // Select default animation
            int defaultIdx = -1;
            for (int i = 0; i < cbAnimTest.Items.Count; i++)
            {
                if (cbAnimTest.Items[i] is AnimTestItem item && (item.DisplayName.StartsWith("⭐") || item.DisplayName.StartsWith("🎬")))
                {
                    defaultIdx = i;
                    break;
                }
            }
            if (defaultIdx < 0 && (rbFemale.Checked || rbMale.Checked))
            {
                string prefix = rbFemale.Checked ? "H_Female_" : "H_Male_";
                string walkTarget = prefix + "Walk_01.grn";
                for (int i = 0; i < cbAnimTest.Items.Count; i++)
                {
                    if (cbAnimTest.Items[i] is AnimTestItem item && item.DisplayName.IndexOf(walkTarget, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        defaultIdx = i;
                        break;
                    }
                }
            }
            if (defaultIdx < 0)
            {
                string[] targets = { "Walk", "Run", "Idle", "Attack1", "Attack", "Die" };
                foreach (var t in targets)
                {
                    for (int i = 0; i < cbAnimTest.Items.Count; i++)
                    {
                        if (cbAnimTest.Items[i] is AnimTestItem item && item.DisplayName.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            defaultIdx = i;
                            break;
                        }
                    }
                    if (defaultIdx >= 0) break;
                }
            }

            if (defaultIdx >= 0)
                cbAnimTest.SelectedIndex = defaultIdx;
            else if (cbAnimTest.Items.Count > 0)
                cbAnimTest.SelectedIndex = 0;

            LoadSelectedTestAnimation();
        }

        private static bool HasFingerMotionAnim(string fn)
        {
            if (string.IsNullOrEmpty(fn)) return false;
            return fn.IndexOf("Victory", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   fn.IndexOf("Wave", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   fn.IndexOf("Spell_Summon", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   fn.IndexOf("What!", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   fn.IndexOf("Blow_Kiss", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   fn.IndexOf("Up_Yours", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   fn.IndexOf("Attackcrossbow", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void LoadSelectedTestAnimation()
        {
            if (cbAnimTest.SelectedItem is AnimTestItem item)
            {
                if (item.PreloadedAnim != null)
                {
                    _testAnim = item.PreloadedAnim;
                    tbAnimScrub.Value = 0;
                    _animTime = 0f;
                    ApplyAnimationPose(0f);
                    glControl.Invalidate();
                    return;
                }

                if (!string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
                {
                    string ext = Path.GetExtension(item.FilePath).ToLowerInvariant();
                    if (ext == ".anim")
                    {
                        _testAnim = GrannyAnimFile.Load(item.FilePath);
                    }
                    else if (ext == ".grn")
                    {
                        var parser = new GrannyFormatParser();
                        if (parser.Load(item.FilePath) && parser.Animation != null)
                            _testAnim = parser.Animation;
                    }
                    else if (ext == ".fbx")
                    {
                        var bonesForRetarget = (_previewModel != null && _previewModel.Bones.Count > 0) ? _previewModel.Bones : _refBones;
                        if (bonesForRetarget != null && MixamoRetarget.TryImport(item.FilePath, bonesForRetarget, Path.GetFileNameWithoutExtension(item.FilePath), out var anim, out _, item.FbxAnimIndex))
                        {
                            _testAnim = anim;
                        }
                    }

                    if (_testAnim != null)
                    {
                        tbAnimScrub.Value = 0;
                        _animTime = 0f;
                        ApplyAnimationPose(0f);
                        glControl.Invalidate();
                    }
                }
            }
        }

        private void PromptImportTestAnimation()
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Choisir une Action / Animation pour tester le Rig",
                Filter = "Toutes les Animations (*.anim;*.grn;*.fbx)|*.anim;*.grn;*.fbx|Animations Retarget UO (*.anim)|*.anim|Animations Granny UO (*.grn)|*.grn|Animations FBX (*.fbx)|*.fbx|Tous les fichiers (*.*)|*.*",
                InitialDirectory = Directory.Exists(_outFolder) ? _outFolder : _basePath
            };

            if (ofd.ShowDialog(this) != DialogResult.OK) return;

            string path = ofd.FileName;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            GrannyAnimation? loadedAnim = null;

            if (ext == ".anim")
            {
                loadedAnim = GrannyAnimFile.Load(path);
            }
            else if (ext == ".grn")
            {
                var parser = new GrannyFormatParser();
                if (parser.Load(path) && parser.Animation != null)
                    loadedAnim = parser.Animation;
            }
            else if (ext == ".fbx")
            {
                if (_refBones == null || _refBones.Count == 0) LoadReferenceBones();
                var bonesForRetarget = (_previewModel != null && _previewModel.Bones.Count > 0) ? _previewModel.Bones : _refBones;
                string? err = null;
                if (bonesForRetarget != null && MixamoRetarget.TryImport(path, bonesForRetarget, Path.GetFileNameWithoutExtension(path), out var anim, out err))
                {
                    loadedAnim = anim;
                }
                else
                {
                    MessageBox.Show("Impossible de retargeter l'animation FBX : " + (err ?? "erreur inconnue"), "Erreur Animation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }

            if (loadedAnim != null)
            {
                var item = new AnimTestItem
                {
                    DisplayName = $"📂 [Action importée] {Path.GetFileName(path)}",
                    FilePath = path,
                    PreloadedAnim = loadedAnim
                };
                cbAnimTest.Items.Insert(0, item);
                cbAnimTest.SelectedIndex = 0;
                if (!_isPlayingAnim) TogglePlayAnimation();
            }
            else
            {
                MessageBox.Show("Aucune piste d'animation trouvée dans ce fichier.", "Animation", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
            if (tbFingerFlex != null) tbFingerFlex.Value = 0;
            _fingerFlexAmount = 0f;
            if (lblFingerFlex != null) lblFingerFlex.Text = "✋ Test Flexion Doigts / Poing : 0%";
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

            // Populate all parts into previewModel while preserving names, textures, and individual skinning
            if (_modelParts != null && _modelParts.Count > 0)
            {
                _previewModel.Parts = new List<GrannyModelPart>(_modelParts);

                for (int pi = 0; pi < _previewModel.Parts.Count; pi++)
                {
                    var part = _previewModel.Parts[pi];
                    bool hasSkin = part.Skin != null && part.Skin.Length == part.Vertices.Length && part.Skin.Any(x => x != null && x.Length > 0);
                    if (!hasSkin || chkForceAutoSkin.Checked)
                    {
                        MixamoCharacterImport.ComputeVolumetricSkinning(part, _previewModel.Bones);
                    }
                }

                // Update combined _rawSkin for animated preview VAO
                var allSkin = new List<VertexInfluence[]>();
                foreach (var part in _previewModel.Parts)
                {
                    if (part.Skin != null) allSkin.AddRange(part.Skin);
                    else allSkin.AddRange(Enumerable.Repeat(Array.Empty<VertexInfluence>(), part.Vertices.Length));
                }
                _rawSkin = allSkin.ToArray();
            }
            else if (_baseVertices != null && _baseVertices.Length > 0 && _indices != null)
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
                int refIdx = _refBones.FindIndex(rb => string.Equals(rb.Name, _previewModel.Bones[i].Name, StringComparison.OrdinalIgnoreCase));
                var inv = (refIdx >= 0 && refIdx < _refBones.Count) ? _refBones[refIdx].InverseBindMatrix : _previewModel.Bones[i].InverseBindMatrix;
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

                    string? aimChildName = MixamoCharacterImport.GetAimChildBoneName(name);
                    if (string.IsNullOrEmpty(aimChildName) && children[idx].Count == 1)
                    {
                        int singleChild = children[idx][0];
                        if (singleChild >= 0 && singleChild < nb)
                            aimChildName = _previewModel.Bones[singleChild].Name;
                    }

                    if (!string.IsNullOrEmpty(aimChildName))
                    {
                        var childMarker = _markers.FirstOrDefault(m => string.Equals(m.BoneName, aimChildName, StringComparison.OrdinalIgnoreCase));

                        int childRefIdx = -1;
                        if (rotSourceIdx != idx)
                        {
                            string symChildName = aimChildName.Contains(" L ") ? aimChildName.Replace(" L ", " R ") : (aimChildName.Contains(" R ") ? aimChildName.Replace(" R ", " L ") : aimChildName);
                            childRefIdx = FindMatchingBoneIndex(_refBones, symChildName);
                        }
                        else
                        {
                            childRefIdx = FindMatchingBoneIndex(_refBones, aimChildName);
                        }

                        if (childMarker != null && childRefIdx >= 0 && childRefIdx < nb)
                        {
                            Vector3 pSelfRef = uoBindWorld[rotSourceIdx].Row3.Xyz;
                            Vector3 pChildRef = uoBindWorld[childRefIdx].Row3.Xyz;
                            Vector3 vRef = pChildRef - pSelfRef;

                            Vector3 pSelfTgt = marker.Position;
                            Vector3 pChildTgt = childMarker.Position;
                            Vector3 vTgt = pChildTgt - pSelfTgt;

                            if (vRef.LengthSquared > 1e-4f && vTgt.LengthSquared > 1e-4f)
                            {
                                vRef.Normalize();
                                vTgt.Normalize();
                                OpenTK.Mathematics.Quaternion qAim = MixamoCharacterImport.RotationBetweenVectors(vRef, vTgt);
                                baseRot = baseRot * Matrix4.CreateFromQuaternion(qAim);
                            }
                        }
                    }

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
                        // Check if live original bones exist to preserve artist finger joint proportions & angles!
                        if (_liveOriginalBones != null && _liveOriginalBones.Count > 0)
                        {
                            int liveIdx = FindMatchingBoneIndex(_liveOriginalBones, name);
                            int liveP = FindMatchingBoneIndex(_liveOriginalBones, _previewModel.Bones[p].Name);
                            if (liveIdx >= 0 && liveP >= 0 && liveIdx < _liveOriginalBones.Count && liveP < _liveOriginalBones.Count)
                            {
                                var invChild = _liveOriginalBones[liveIdx].InverseBindMatrix;
                                var invParent = _liveOriginalBones[liveP].InverseBindMatrix;
                                if (Math.Abs(invChild.Determinant) > 1e-5f && Math.Abs(invParent.Determinant) > 1e-5f)
                                {
                                    var liveChildWorld = Matrix4.Invert(invChild);
                                    var liveParentWorld = Matrix4.Invert(invParent);
                                    var liveLocal = liveChildWorld * Matrix4.Invert(liveParentWorld);
                                    fittedWorld[idx] = liveLocal * fittedWorld[p];
                                    goto DistalComputed;
                                }
                            }
                        }

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
            DistalComputed: ;

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
                if (lower.Contains("finger0") || lower.Contains("thumb"))
                    return bones.FindIndex(b => (b.Name.IndexOf("thumb", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("finger0", StringComparison.OrdinalIgnoreCase) >= 0) && (b.Name.IndexOf("left", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("_l", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf(" l ", StringComparison.OrdinalIgnoreCase) >= 0));
                if (lower.Contains("finger") || lower.Contains("index") || lower.Contains("middle"))
                    return bones.FindIndex(b => (b.Name.IndexOf("index", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("finger1", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("middle", StringComparison.OrdinalIgnoreCase) >= 0) && (b.Name.IndexOf("left", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("_l", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf(" l ", StringComparison.OrdinalIgnoreCase) >= 0));
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
                if (lower.Contains("finger0") || lower.Contains("thumb"))
                    return bones.FindIndex(b => (b.Name.IndexOf("thumb", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("finger0", StringComparison.OrdinalIgnoreCase) >= 0) && (b.Name.IndexOf("right", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("_r", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf(" r ", StringComparison.OrdinalIgnoreCase) >= 0));
                if (lower.Contains("finger") || lower.Contains("index") || lower.Contains("middle"))
                    return bones.FindIndex(b => (b.Name.IndexOf("index", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("finger1", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("middle", StringComparison.OrdinalIgnoreCase) >= 0) && (b.Name.IndexOf("right", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf("_r", StringComparison.OrdinalIgnoreCase) >= 0 || b.Name.IndexOf(" r ", StringComparison.OrdinalIgnoreCase) >= 0));
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

            if (_refBones == null || _refBones.Count == 0) LoadReferenceBones();

            foreach (var m in _markers)
            {
                int bIdx = FindMatchingBoneIndex(bones, m.BoneName);
                if (bIdx >= 0 && bIdx < nb)
                {
                    m.Position = boneWorldPos[bIdx];

                    // Recover rotation offset if reference bones exist
                    if (_refBones != null && _refBones.Count > 0)
                    {
                        int refIdx = FindMatchingBoneIndex(_refBones, m.BoneName);
                        if (refIdx >= 0 && refIdx < _refBones.Count)
                        {
                            int rotSourceIdx = refIdx;
                            // Check symmetrical bone reference if mirrored
                            if (m.Position.X < -0.01f && Math.Abs(_refBones[refIdx].InverseBindMatrix.Determinant) > 1e-5f)
                            {
                                var refPos = Matrix4.Invert(_refBones[refIdx].InverseBindMatrix).Row3.X;
                                if (refPos > 0.01f)
                                {
                                    int symIdx = FindMatchingBoneIndex(_refBones, m.SymmetricBoneName ?? "");
                                    if (symIdx >= 0 && symIdx < _refBones.Count) rotSourceIdx = symIdx;
                                }
                            }
                            else if (m.Position.X > 0.01f && Math.Abs(_refBones[refIdx].InverseBindMatrix.Determinant) > 1e-5f)
                            {
                                var refPos = Matrix4.Invert(_refBones[refIdx].InverseBindMatrix).Row3.X;
                                if (refPos < -0.01f)
                                {
                                    int symIdx = FindMatchingBoneIndex(_refBones, m.SymmetricBoneName ?? "");
                                    if (symIdx >= 0 && symIdx < _refBones.Count) rotSourceIdx = symIdx;
                                }
                            }

                            if (Math.Abs(_refBones[rotSourceIdx].InverseBindMatrix.Determinant) > 1e-5f && Math.Abs(boneWorldMat[bIdx].Determinant) > 1e-5f)
                            {
                                var refWorld = Matrix4.Invert(_refBones[rotSourceIdx].InverseBindMatrix);
                                var refRotMat = Matrix4.CreateFromQuaternion(refWorld.ExtractRotation());
                                var curRotMat = Matrix4.CreateFromQuaternion(boneWorldMat[bIdx].ExtractRotation());
                                var diffMat = Matrix4.Invert(refRotMat) * curRotMat;

                                float sinY = -diffMat.Row0.Z;
                                float rotY = MathF.Asin(MathHelper.Clamp(sinY, -1f, 1f));
                                float cosY = MathF.Cos(rotY);
                                float rotX = 0f; float rotZ = 0f;
                                if (MathF.Abs(cosY) > 1e-4f)
                                {
                                    rotX = MathF.Atan2(diffMat.Row1.Z, diffMat.Row2.Z);
                                    rotZ = MathF.Atan2(diffMat.Row0.Y, diffMat.Row0.X);
                                }
                                var deg = new Vector3(MathHelper.RadiansToDegrees(rotX), MathHelper.RadiansToDegrees(rotY), MathHelper.RadiansToDegrees(rotZ));
                                if (deg.LengthSquared > 0.01f)
                                {
                                    m.RotationDegrees = deg;
                                }
                            }
                        }
                    }
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
            SetPos("Bip01 Head", new Vector3(0f, midY, minZ + height * 0.88f));
            SetPos("Bip01 Neck", new Vector3(0f, midY, minZ + height * 0.82f));

            // 2. Bassin / Entrejambe (Groin / Pelvis)
            SetPos("Bip01 Spine2", new Vector3(0f, midY, minZ + height * 0.72f)); // Poitrine / Cage Thoracique
            SetPos("Bip01 Spine", new Vector3(0f, midY, minZ + height * 0.60f));  // Abdomen / Nombril
            SetPos("Bip01 Pelvis", new Vector3(0f, midY, minZ + height * 0.52f)); // Bassin / Hanches

            // 3. Bras (Clavicules, Épaules, Coudes, Poignets) - D = -X (Écran Gauche), G = +X (Écran Droite)
            float armY = midY;
            SetPos("Bip01 R Clavicle", new Vector3(-halfWidth * 0.25f, armY, minZ + height * 0.77f));
            SetPos("Bip01 L Clavicle", new Vector3(halfWidth * 0.25f, armY, minZ + height * 0.77f));

            SetPos("Bip01 R UpperArm", new Vector3(-halfWidth * 0.45f, armY, minZ + height * 0.76f));
            SetPos("Bip01 L UpperArm", new Vector3(halfWidth * 0.45f, armY, minZ + height * 0.76f));

            // Coudes (Elbows)
            SetPos("Bip01 R Forearm", new Vector3(-halfWidth * 0.72f, armY, minZ + height * 0.74f));
            SetPos("Bip01 L Forearm", new Vector3(halfWidth * 0.72f, armY, minZ + height * 0.74f));

            // Poignets (Wrists)
            SetPos("Bip01 R Hand", new Vector3(-halfWidth * 0.90f, armY, minZ + height * 0.72f));
            SetPos("Bip01 L Hand", new Vector3(halfWidth * 0.90f, armY, minZ + height * 0.72f));

            // Pouces et Doigts (Thumbs & Fingers)
            SetPos("Bip01 R Finger0", new Vector3(-halfWidth * 0.95f, armY + 0.035f, minZ + height * 0.71f));
            SetPos("Bip01 R Finger1", new Vector3(-halfWidth * 1.00f, armY - 0.015f, minZ + height * 0.71f));

            SetPos("Bip01 L Finger0", new Vector3(halfWidth * 0.95f, armY + 0.035f, minZ + height * 0.71f));
            SetPos("Bip01 L Finger1", new Vector3(halfWidth * 1.00f, armY - 0.015f, minZ + height * 0.71f));

            // 4. Jambes (Hanches, Genoux, Chevilles)
            SetPos("Bip01 R Thigh", new Vector3(-halfWidth * 0.22f, midY, minZ + height * 0.48f));
            SetPos("Bip01 L Thigh", new Vector3(halfWidth * 0.22f, midY, minZ + height * 0.48f));

            // Genoux (Knees)
            SetPos("Bip01 R Calf", new Vector3(-halfWidth * 0.22f, midY + 0.01f, minZ + height * 0.26f));
            SetPos("Bip01 L Calf", new Vector3(halfWidth * 0.22f, midY + 0.01f, minZ + height * 0.26f));

            // Chevilles (Ankles)
            SetPos("Bip01 R Foot", new Vector3(-halfWidth * 0.22f, midY + 0.03f, minZ + height * 0.04f));
            SetPos("Bip01 L Foot", new Vector3(halfWidth * 0.22f, midY + 0.03f, minZ + height * 0.04f));

            UpdateNudValues();
            EnsurePreviewModel();
            glControl.Invalidate();
        }

        private void RecalculateArmSkinning()
        {
            EnsurePreviewModel();
            if (_previewModel == null || _previewModel.Parts.Count == 0) return;
            int totalVerts = 0;
            foreach (var part in _previewModel.Parts)
            {
                MixamoCharacterImport.ComputeVolumetricSkinning(part, _previewModel.Bones);
                totalVerts += part.Vertices.Length;
            }

            var allSkin = new List<VertexInfluence[]>();
            foreach (var part in _previewModel.Parts)
            {
                if (part.Skin != null) allSkin.AddRange(part.Skin);
                else allSkin.AddRange(Enumerable.Repeat(Array.Empty<VertexInfluence>(), part.Vertices.Length));
            }
            _rawSkin = allSkin.ToArray();

            MessageBox.Show($"Skinning recalculé avec succès ({totalVerts:N0} sommets mis à jour).\nLa cage thoracique et le torse sont préservés rigides sans écrasement latéral.", "Auto-Rig Studio", MessageBoxButtons.OK, MessageBoxIcon.Information);
            glControl.Invalidate();
        }

        private void RecalculateAllSkinning()
        {
            EnsurePreviewModel();
            if (_previewModel == null || _previewModel.Parts.Count == 0) return;
            int totalVerts = 0;
            foreach (var part in _previewModel.Parts)
            {
                MixamoCharacterImport.ComputeVolumetricSkinning(part, _previewModel.Bones);
                totalVerts += part.Vertices.Length;
            }

            var allSkin = new List<VertexInfluence[]>();
            foreach (var part in _previewModel.Parts)
            {
                if (part.Skin != null) allSkin.AddRange(part.Skin);
                else allSkin.AddRange(Enumerable.Repeat(Array.Empty<VertexInfluence>(), part.Vertices.Length));
            }
            _rawSkin = allSkin.ToArray();

            MessageBox.Show($"Skinning volumétrique complet recalculé pour {_previewModel.Parts.Count} mesh(s) ({totalVerts:N0} sommets au total).", "Auto-Rig Studio", MessageBoxButtons.OK, MessageBoxIcon.Information);
            glControl.Invalidate();
        }

        private void ApplyAnimationPose(float time)
        {
            EnsurePreviewModel();
            if (_previewModel == null || _previewModel.Bones.Count == 0) return;
            if (_testAnim == null && _fingerFlexAmount <= 0.001f) return;

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
                    if (bone.Name.Equals("Bip01", StringComparison.OrdinalIgnoreCase))
                    {
                        var refBip = _refBones?.FirstOrDefault(b => string.Equals(b.Name, "Bip01", StringComparison.OrdinalIgnoreCase));
                        float refBindZ = refBip != null ? refBip.LocalPosition.Z : 0.937f;
                        var animPos = track.EvaluatePos(time);
                        float deltaZ = animPos.Z - refBindZ;
                        pos = new Vector3(bone.LocalPosition.X, bone.LocalPosition.Y, bone.LocalPosition.Z + deltaZ);
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

                // Apply Finger Flex (Fist curl) if active!
                if (_fingerFlexAmount > 0.001f)
                {
                    string bName = bone.Name;
                    bool isLeft = bName.Contains(" L ");
                    bool isRight = bName.Contains(" R ");
                    if (isLeft || isRight)
                    {
                        // In UO Biped / 3ds Max Character Studio, both Left and Right fingers curl inward around local +Z!
                        if (bName.Contains("Finger11"))
                        {
                            float angle11 = _fingerFlexAmount * MathHelper.DegreesToRadians(80f);
                            localTransform = Matrix4.CreateRotationZ(angle11) * localTransform;
                        }
                        else if (bName.Contains("Finger1"))
                        {
                            float angle1 = _fingerFlexAmount * MathHelper.DegreesToRadians(70f);
                            localTransform = Matrix4.CreateRotationZ(angle1) * localTransform;
                        }
                        else if (bName.Contains("Finger01"))
                        {
                            float angle01 = _fingerFlexAmount * MathHelper.DegreesToRadians(55f);
                            float signY = isLeft ? -1f : 1f;
                            localTransform = Matrix4.CreateRotationY(angle01 * 0.3f * signY) * Matrix4.CreateRotationZ(angle01) * localTransform;
                        }
                        else if (bName.Contains("Finger0"))
                        {
                            float angle0 = _fingerFlexAmount * MathHelper.DegreesToRadians(45f);
                            float signX = isLeft ? 1f : -1f;
                            float signY = isLeft ? -1f : 1f;
                            localTransform = Matrix4.CreateRotationX(angle0 * 0.6f * signX) *
                                             Matrix4.CreateRotationY(angle0 * 0.6f * signY) *
                                             Matrix4.CreateRotationZ(angle0 * 0.7f) * localTransform;
                        }
                    }
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
            var skinToUse = _rawSkin ?? (_previewModel.Parts.Count > 0 ? _previewModel.Parts[0].Skin : null);
            if (_baseVertices != null && _indices != null && skinToUse != null && skinToUse.Length == _baseVertices.Length)
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
                    var infs = skinToUse[vi];

                    Vector3 pAnim = Vector3.Zero;
                    float totalW = 0f;
                    if (infs != null && infs.Length > 0)
                    {
                        foreach (var inf in infs)
                        {
                            if (inf.BoneId >= 0 && inf.BoneId < nb && inf.Weight > 0.0001f)
                            {
                                pAnim += Vector3.TransformPosition(p0, skinMats[inf.BoneId]) * inf.Weight;
                                totalW += inf.Weight;
                            }
                        }
                    }
                    if (totalW > 0.001f)
                    {
                        if (MathF.Abs(totalW - 1.0f) > 0.001f)
                            pAnim /= totalW;
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
                        if (_meshVbo == 0) UploadMeshToGl();
                        if (_meshVbo != 0)
                        {
                            GL.BindBuffer(BufferTarget.ArrayBuffer, _meshVbo);
                            GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, _animatedInterleaved.Length * sizeof(float), _animatedInterleaved);
                            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
                        }
                    }
                    catch { }
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
                _rawParts.Clear();

                var meshWithBones = meshes.OrderByDescending(m => m.Bones?.Count ?? 0).FirstOrDefault(m => m.Bones != null && m.Bones.Count > 0);
                if (meshWithBones != null && meshWithBones.Bones.Count > 0)
                {
                    ApplyExistingRig(meshWithBones.Bones, _currentModelPath);
                }
                else if (_refBones == null || _refBones.Count == 0)
                {
                    LoadReferenceBones();
                }

                int totalVerts = 0;
                int totalFaces = 0;

                foreach (var lm in meshes)
                {
                    if (lm.Hidden) continue;
                    if (lm.BaseVertices == null || lm.BaseVertices.Length == 0) continue;

                    int vCount = lm.BaseVertices.Length;
                    var skinList = new List<VertexInfluence[]>();

                    for (int i = 0; i < vCount; i++)
                    {
                        if (lm.Skin != null && i < lm.Skin.Length && lm.Skin[i] != null && lm.Skin[i].Length > 0)
                        {
                            var oldInfs = lm.Skin[i];
                            if (lm.Bones != null && lm.Bones.Count > 0 && _refBones != null && _refBones.Count > 0)
                            {
                                var remapped = new List<VertexInfluence>();
                                foreach (var inf in oldInfs)
                                {
                                    if (inf.BoneId >= 0 && inf.BoneId < lm.Bones.Count)
                                    {
                                        string bName = lm.Bones[inf.BoneId].Name;
                                        int masterId = _refBones.FindIndex(rb => string.Equals(rb.Name, bName, StringComparison.OrdinalIgnoreCase));
                                        if (masterId >= 0)
                                        {
                                            remapped.Add(new VertexInfluence { BoneId = masterId, Weight = inf.Weight });
                                        }
                                    }
                                }
                                skinList.Add(remapped.Count > 0 ? remapped.ToArray() : oldInfs);
                            }
                            else
                            {
                                skinList.Add(oldInfs);
                            }
                        }
                        else
                        {
                            skinList.Add(Array.Empty<VertexInfluence>());
                        }
                    }

                    var norms = new Vector3[vCount];
                    for (int i = 0; i < vCount; i++) norms[i] = Vector3.UnitZ;
                    if (lm.BaseCornerNormals != null && lm.Indices != null)
                    {
                        for (int ci = 0; ci < lm.Indices.Length; ci++)
                        {
                            int vi = lm.Indices[ci];
                            if (vi >= 0 && vi < vCount && ci < lm.BaseCornerNormals.Length)
                                norms[vi] = lm.BaseCornerNormals[ci];
                        }
                    }

                    var uvs = (lm.SourceMesh?.UVs != null && lm.SourceMesh.UVs.Count == vCount)
                        ? lm.SourceMesh.UVs.ToArray()
                        : Enumerable.Repeat(Vector2.Zero, vCount).ToArray();

                    var raw = new RawPartData
                    {
                        Name = !string.IsNullOrEmpty(lm.Name) ? lm.Name : $"Mesh_{_rawParts.Count + 1}",
                        MaterialName = string.Empty,
                        Color = lm.MeshColor,
                        TextureName = lm.TextureName ?? string.Empty,
                        RawVertices = (Vector3[])lm.BaseVertices.Clone(),
                        RawNormals = norms,
                        UVs = uvs,
                        Indices = lm.Indices != null ? (int[])lm.Indices.Clone() : Array.Empty<int>(),
                        Skin = skinList.ToArray()
                    };
                    _rawParts.Add(raw);

                    totalVerts += vCount;
                    totalFaces += (lm.Indices?.Length ?? 0) / 3;
                }

                if (_rawParts.Count == 0) return;

                if (string.IsNullOrEmpty(_currentModelPath))
                {
                    _currentModelPath = meshes.FirstOrDefault(m => !string.IsNullOrEmpty(m.SourcePath))?.SourcePath;
                }
                _textureName = meshes.FirstOrDefault(m => !string.IsNullOrEmpty(m.TextureName))?.TextureName;
                string fn = !string.IsNullOrEmpty(_currentModelPath) ? Path.GetFileName(_currentModelPath) : "Scène UOFiddler active";

                string partNames = string.Join(", ", _rawParts.Take(4).Select(p => p.Name));
                if (_rawParts.Count > 4) partNames += "...";

                lblModelInfo.Text = $"Modèle actif : {fn}\n" +
                                    $"Meshs ({_rawParts.Count}) : {partNames}\n" +
                                    $"Sommets : {totalVerts:N0} | Faces : {totalFaces:N0}";

                _scale = 1.0f;
                nudScale.Value = 1.0m;
                _rotX = 0f; _rotY = 0f; _rotZ = 0f;
                _offset = Vector3.Zero;
                _mirrorX = false;

                RebakeModelTransform();

                if (meshWithBones == null)
                {
                    if (_refBones != null && _refBones.Count > 0)
                    {
                        _liveOriginalBones = new List<GrannyBone>(_refBones);
                        InitMarkersFromLiveBones(_refBones);
                    }
                    else
                    {
                        AutoPlaceMixamoLandmarks();
                    }
                }

                EnsurePreviewModel();
                PopulateTestAnimations();
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

                // 1) Is it a .char file?
                if (ext == ".char")
                {
                    var model = GrannyModelFile.Load(filePath);
                    if (model != null && model.Parts.Count > 0)
                    {
                        _rawParts.Clear();
                        int totalVerts = 0;
                        int totalFaces = 0;

                        foreach (var part in model.Parts)
                        {
                            _rawParts.Add(new RawPartData
                            {
                                Name = !string.IsNullOrEmpty(part.Name) ? part.Name : $"Mesh_{_rawParts.Count + 1}",
                                MaterialName = part.MaterialName,
                                Color = part.Color,
                                TextureName = part.TextureName,
                                RawVertices = (Vector3[])part.Vertices.Clone(),
                                RawNormals = part.Normals != null ? (Vector3[])part.Normals.Clone() : Enumerable.Repeat(Vector3.UnitZ, part.Vertices.Length).ToArray(),
                                UVs = part.UVs != null ? (Vector2[])part.UVs.Clone() : Enumerable.Repeat(Vector2.Zero, part.Vertices.Length).ToArray(),
                                Indices = part.Indices != null ? (int[])part.Indices.Clone() : Array.Empty<int>(),
                                Skin = part.Skin != null ? (VertexInfluence[][])part.Skin.Clone() : null
                            });
                            totalVerts += part.Vertices.Length;
                            totalFaces += (part.Indices?.Length ?? 0) / 3;
                        }

                        _textureName = model.Parts.FirstOrDefault(p => !string.IsNullOrEmpty(p.TextureName))?.TextureName;
                        _scale = 1.0f; nudScale.Value = 1.0m;
                        _rotX = 0f; _rotY = 0f; _rotZ = 0f; _offset = Vector3.Zero; _mirrorX = false;

                        string partNames = string.Join(", ", _rawParts.Take(4).Select(p => p.Name));
                        if (_rawParts.Count > 4) partNames += "...";

                        lblModelInfo.Text = $"Fichier : {fn}\n" +
                                            $"Meshs ({_rawParts.Count}) : {partNames}\n" +
                                            $"Sommets : {totalVerts:N0} | Faces : {totalFaces:N0} (Character UO)";

                        _previewModel = model;
                        RebakeModelTransform();
                        if (model.Bones != null && model.Bones.Count > 0)
                        {
                            ApplyExistingRig(model.Bones, filePath);
                        }
                        else
                        {
                            _isInternalSkeletonSwitch = true;
                            if (fn.IndexOf("female", StringComparison.OrdinalIgnoreCase) >= 0)
                                rbFemale.Checked = true;
                            else
                                rbMale.Checked = true;
                            _isInternalSkeletonSwitch = false;
                            AutoPlaceMixamoLandmarks();
                        }
                        EnsurePreviewModel();
                        PopulateTestAnimations();
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
                        if (parser.Bones != null && parser.Bones.Count > 0)
                        {
                            ApplyExistingRig(parser.Bones, filePath);
                        }
                        else if (_refBones == null || _refBones.Count == 0)
                        {
                            LoadReferenceBones();
                        }

                        var boneRemap = new Dictionary<int, int>();
                        if (parser.Bones != null && parser.Bones.Count > 0 && _refBones != null)
                        {
                            for (int pb = 0; pb < parser.Bones.Count; pb++)
                            {
                                int rIdx = _refBones.FindIndex(rb => string.Equals(rb.Name, parser.Bones[pb].Name, StringComparison.OrdinalIgnoreCase));
                                boneRemap[pb] = rIdx >= 0 ? rIdx : pb;
                            }
                        }

                        _rawParts.Clear();
                        int totalVerts = 0;
                        int totalFaces = 0;

                        foreach (var m in parser.Meshes)
                        {
                            int vCount = m.Vertices.Count;
                            var sList = new List<VertexInfluence[]>();
                            for (int i = 0; i < vCount; i++)
                            {
                                if (m.Skin != null && i < m.Skin.Count && m.Skin[i] != null && m.Skin[i].Length > 0)
                                {
                                    var infs = m.Skin[i];
                                    var remapped = new VertexInfluence[infs.Length];
                                    for (int k = 0; k < infs.Length; k++)
                                    {
                                        int targetBone = boneRemap.TryGetValue(infs[k].BoneId, out int rb) ? rb : infs[k].BoneId;
                                        remapped[k] = new VertexInfluence { BoneId = targetBone, Weight = infs[k].Weight };
                                    }
                                    sList.Add(remapped);
                                }
                                else
                                {
                                    sList.Add(Array.Empty<VertexInfluence>());
                                }
                            }

                            _rawParts.Add(new RawPartData
                            {
                                Name = $"Mesh_{_rawParts.Count + 1}",
                                MaterialName = string.Empty,
                                Color = new Vector3(0.72f, 0.66f, 0.55f),
                                TextureName = parser.TextureName ?? string.Empty,
                                RawVertices = m.Vertices.ToArray(),
                                RawNormals = Enumerable.Repeat(Vector3.UnitZ, vCount).ToArray(),
                                UVs = (m.UVs != null && m.UVs.Count == vCount) ? m.UVs.ToArray() : Enumerable.Repeat(Vector2.Zero, vCount).ToArray(),
                                Indices = m.Indices.ToArray(),
                                Skin = sList.ToArray()
                            });

                            totalVerts += vCount;
                            totalFaces += m.Indices.Count / 3;
                        }

                        _textureName = parser.TextureName;
                        _scale = 1.0f; nudScale.Value = 1.0m;
                        _rotX = 0f; _rotY = 0f; _rotZ = 0f; _offset = Vector3.Zero; _mirrorX = false;

                        string partNames = string.Join(", ", _rawParts.Take(4).Select(p => p.Name));
                        if (_rawParts.Count > 4) partNames += "...";

                        lblModelInfo.Text = $"Fichier : {fn}\n" +
                                            $"Meshs ({_rawParts.Count}) : {partNames}\n" +
                                            $"Sommets : {totalVerts:N0} | Faces : {totalFaces:N0} (Granny UO)";

                        _previewModel = new GrannyModelFile
                        {
                            Name = Path.GetFileNameWithoutExtension(filePath),
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

                        RebakeModelTransform();
                        if (_previewModel.Bones == null || _previewModel.Bones.Count == 0)
                        {
                            _isInternalSkeletonSwitch = true;
                            if (fn.IndexOf("female", StringComparison.OrdinalIgnoreCase) >= 0)
                                rbFemale.Checked = true;
                            else
                                rbMale.Checked = true;
                            _isInternalSkeletonSwitch = false;
                            AutoPlaceMixamoLandmarks();
                        }
                        EnsurePreviewModel();
                        PopulateTestAnimations();
                        glControl.Invalidate();
                        return;
                    }
                }

                var ctx = new AssimpContext();
                var scene = ctx.ImportFile(filePath, PostProcessSteps.Triangulate | PostProcessSteps.GenerateSmoothNormals);
                if (scene == null)
                {
                    MessageBox.Show("Impossible de charger le fichier 3D.", "Erreur 3D", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                if (scene.MeshCount == 0)
                {
                    if (scene.AnimationCount > 0)
                    {
                        var ask = MessageBox.Show(
                            $"Le fichier '{fn}' ne contient aucun mesh 3D, mais contient {scene.AnimationCount} animation(s) Mixamo (téléchargé 'Without Skin').\n\n" +
                            "Souhaitez-vous charger cette animation dans les actions de test pour voir bouger votre modèle actuel ?",
                            "Animation Mixamo Détectée",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Question);
                        if (ask == DialogResult.Yes)
                        {
                            var bonesForRetarget = (_previewModel != null && _previewModel.Bones.Count > 0) ? _previewModel.Bones : _refBones;
                            string aErr = null;
                            if (bonesForRetarget != null && MixamoRetarget.TryImport(filePath, bonesForRetarget, Path.GetFileNameWithoutExtension(filePath), out var anim, out aErr) && anim != null)
                            {
                                var item = new AnimTestItem
                                {
                                    DisplayName = $"🎬 [Action FBX] {Path.GetFileNameWithoutExtension(filePath)}",
                                    FilePath = filePath,
                                    PreloadedAnim = anim
                                };
                                cbAnimTest.Items.Insert(0, item);
                                cbAnimTest.SelectedIndex = 0;
                                if (!_isPlayingAnim) TogglePlayAnimation();
                            }
                            else
                            {
                                MessageBox.Show("Impossible de retargeter l'animation : " + (aErr ?? "erreur inconnue"), "Erreur Animation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            }
                        }
                    }
                    else
                    {
                        MessageBox.Show($"Le fichier '{fn}' ne contient aucun mesh 3D ni animation.", "Modèle vide", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                    return;
                }

                bool hasBones = scene.Meshes.Any(m => m.HasBones && m.BoneCount > 0);
                if (ext == ".fbx")
                {
                    if (MixamoCharacterImport.TryImport(filePath, _outFolder, _refPath, out var importedModel, out string importErr) && importedModel != null && importedModel.Parts.Count > 0)
                    {
                        _previewModel = importedModel;
                        _rawParts.Clear();
                        int totalVerts = 0;
                        int totalFaces = 0;

                        foreach (var part in importedModel.Parts)
                        {
                            _rawParts.Add(new RawPartData
                            {
                                Name = !string.IsNullOrEmpty(part.Name) ? part.Name : $"Mesh_{_rawParts.Count + 1}",
                                MaterialName = part.MaterialName,
                                Color = part.Color,
                                TextureName = part.TextureName,
                                RawVertices = (Vector3[])part.Vertices.Clone(),
                                RawNormals = part.Normals != null ? (Vector3[])part.Normals.Clone() : Enumerable.Repeat(Vector3.UnitZ, part.Vertices.Length).ToArray(),
                                UVs = part.UVs != null ? (Vector2[])part.UVs.Clone() : Enumerable.Repeat(Vector2.Zero, part.Vertices.Length).ToArray(),
                                Indices = part.Indices != null ? (int[])part.Indices.Clone() : Array.Empty<int>(),
                                Skin = part.Skin != null ? (VertexInfluence[][])part.Skin.Clone() : null
                            });
                            totalVerts += part.Vertices.Length;
                            totalFaces += (part.Indices?.Length ?? 0) / 3;
                        }

                        _textureName = importedModel.Parts.FirstOrDefault(p => !string.IsNullOrEmpty(p.TextureName))?.TextureName;
                        _scale = 1.0f; nudScale.Value = 1.0m;
                        _rotX = 0f; _rotY = 0f; _rotZ = 0f; _offset = Vector3.Zero; _mirrorX = false;

                        string partNames = string.Join(", ", _rawParts.Take(4).Select(p => p.Name));
                        if (_rawParts.Count > 4) partNames += "...";

                        lblModelInfo.Text = $"Fichier : {fn}\n" +
                                            $"Meshs ({_rawParts.Count}) : {partNames}\n" +
                                            $"Sommets : {totalVerts:N0} | Faces : {totalFaces:N0} {(hasBones ? "(FBX Rigged)" : "(FBX Mesh)")}";

                        RebakeModelTransform();
                        if (hasBones && importedModel.Bones != null && importedModel.Bones.Count > 0)
                        {
                            ApplyExistingRig(importedModel.Bones, filePath);
                        }
                        else
                        {
                            _liveOriginalBones = null;
                            _isInternalSkeletonSwitch = true;
                            if (fn.IndexOf("female", StringComparison.OrdinalIgnoreCase) >= 0)
                                rbFemale.Checked = true;
                            else
                                rbMale.Checked = true;
                            _isInternalSkeletonSwitch = false;
                            AutoPlaceMixamoLandmarks();
                        }
                        EnsurePreviewModel();
                        PopulateTestAnimations();
                        glControl.Invalidate();
                        return;
                    }
                }

                var nodes = new List<Node>();
                var nodeParent = new List<int>();
                void Collect(Node n, int par)
                {
                    int idx = nodes.Count;
                    nodes.Add(n);
                    nodeParent.Add(par);
                    foreach (var c in n.Children) Collect(c, idx);
                }
                Collect(scene.RootNode, -1);
                int nn = nodes.Count;
                var localMix = new Matrix4[nn];
                for (int i = 0; i < nn; i++)
                {
                    nodes[i].Transform.Decompose(out var sc, out var qq, out var tt);
                    localMix[i] = Matrix4.CreateScale(sc.X, sc.Y, sc.Z) *
                                  Matrix4.CreateFromQuaternion(new OpenTK.Mathematics.Quaternion(qq.X, qq.Y, qq.Z, qq.W)) *
                                  Matrix4.CreateTranslation(tt.X, tt.Y, tt.Z);
                }
                var wMix = new Matrix4[nn];
                for (int i = 0; i < nn; i++)
                {
                    int p = nodeParent[i];
                    wMix[i] = (p >= 0 && p < nn && p != i) ? localMix[i] * wMix[p] : localMix[i];
                }

                _rawParts.Clear();
                int totalVertsStatic = 0;
                int totalFacesStatic = 0;

                var bboxMin = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                var bboxMax = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                for (int mi = 0; mi < scene.MeshCount; mi++)
                {
                    var m = scene.Meshes[mi];
                    Matrix4 mMat = Matrix4.Identity;
                    for (int ni = 0; ni < nn; ni++)
                    {
                        if (nodes[ni].MeshIndices != null && nodes[ni].MeshIndices.Contains(mi))
                        {
                            mMat = wMix[ni];
                            break;
                        }
                    }
                    foreach (var v in m.Vertices)
                    {
                        var wv = new Vector4(v.X, v.Y, v.Z, 1.0f) * mMat;
                        bboxMin.X = Math.Min(bboxMin.X, wv.X); bboxMin.Y = Math.Min(bboxMin.Y, wv.Y); bboxMin.Z = Math.Min(bboxMin.Z, wv.Z);
                        bboxMax.X = Math.Max(bboxMax.X, wv.X); bboxMax.Y = Math.Max(bboxMax.Y, wv.Y); bboxMax.Z = Math.Max(bboxMax.Z, wv.Z);
                    }
                }

                float rawH_X = Math.Abs(bboxMax.X - bboxMin.X);
                float rawH_Y = Math.Abs(bboxMax.Y - bboxMin.Y);
                float rawH_Z = Math.Abs(bboxMax.Z - bboxMin.Z);
                bool isZUp = rawH_Z > rawH_Y * 1.1f;

                float maxH = Math.Max(rawH_Y, rawH_Z);
                float initialScale = 1.0f;
                if (maxH > 500f) initialScale = 0.001f;
                else if (maxH > 20f) initialScale = 0.01f;
                else initialScale = 1.0f;

                for (int mi = 0; mi < scene.MeshCount; mi++)
                {
                    var mesh = scene.Meshes[mi];
                    if (mesh.VertexCount == 0 || mesh.FaceCount == 0) continue;

                    Matrix4 meshMat = Matrix4.Identity;
                    for (int ni = 0; ni < nn; ni++)
                    {
                        if (nodes[ni].MeshIndices != null && nodes[ni].MeshIndices.Contains(mi))
                        {
                            meshMat = wMix[ni];
                            break;
                        }
                    }

                    string mName = !string.IsNullOrEmpty(mesh.Name) ? mesh.Name : $"Mesh_{mi + 1}";
                    string texName = string.Empty;
                    string matName = string.Empty;
                    Vector3 partCol = new Vector3(0.72f, 0.66f, 0.55f);

                    if (mesh.MaterialIndex >= 0 && mesh.MaterialIndex < scene.MaterialCount)
                    {
                        var mat = scene.Materials[mesh.MaterialIndex];
                        matName = mat.Name ?? string.Empty;
                        var diff = mat.ColorDiffuse;
                        if (diff.R > 0.001f || diff.G > 0.001f || diff.B > 0.001f)
                            partCol = new Vector3(diff.R, diff.G, diff.B);
                        if (mat.HasTextureDiffuse && !string.IsNullOrEmpty(mat.TextureDiffuse.FilePath))
                            texName = Path.GetFileName(mat.TextureDiffuse.FilePath);
                    }

                    var pVerts = new Vector3[mesh.VertexCount];
                    var pNorms = new Vector3[mesh.VertexCount];
                    var pUVs = new Vector2[mesh.VertexCount];

                    for (int i = 0; i < mesh.VertexCount; i++)
                    {
                        var v = mesh.Vertices[i];
                        var vWorld = new Vector4(v.X, v.Y, v.Z, 1.0f) * meshMat;
                        if (isZUp)
                        {
                            pVerts[i] = new Vector3(vWorld.X * initialScale, vWorld.Y * initialScale, vWorld.Z * initialScale);
                            if (mesh.HasNormals)
                            {
                                var n = mesh.Normals[i];
                                var nw = new Vector4(n.X, n.Y, n.Z, 0.0f) * meshMat;
                                pNorms[i] = new Vector3(nw.X, nw.Y, nw.Z).Normalized();
                            }
                            else pNorms[i] = Vector3.UnitZ;
                        }
                        else
                        {
                            pVerts[i] = new Vector3(vWorld.X * initialScale, -vWorld.Z * initialScale, vWorld.Y * initialScale);
                            if (mesh.HasNormals)
                            {
                                var n = mesh.Normals[i];
                                var nw = new Vector4(n.X, n.Y, n.Z, 0.0f) * meshMat;
                                pNorms[i] = new Vector3(nw.X, -nw.Z, nw.Y).Normalized();
                            }
                            else pNorms[i] = Vector3.UnitZ;
                        }

                        if (mesh.HasTextureCoords(0))
                        {
                            var uv = mesh.TextureCoordinateChannels[0][i];
                            pUVs[i] = new Vector2(uv.X, 1.0f - uv.Y);
                        }
                        else pUVs[i] = Vector2.Zero;
                    }

                    var pIndices = new List<int>();
                    foreach (var face in mesh.Faces)
                    {
                        if (face.IndexCount == 3)
                        {
                            pIndices.Add(face.Indices[0]);
                            pIndices.Add(face.Indices[1]);
                            pIndices.Add(face.Indices[2]);
                        }
                    }

                    _rawParts.Add(new RawPartData
                    {
                        Name = mName,
                        MaterialName = matName,
                        Color = partCol,
                        TextureName = texName,
                        RawVertices = pVerts,
                        RawNormals = pNorms,
                        UVs = pUVs,
                        Indices = pIndices.ToArray(),
                        Skin = null
                    });

                    totalVertsStatic += mesh.VertexCount;
                    totalFacesStatic += mesh.FaceCount;
                }

                if (_rawParts.Count == 0) return;

                string staticPartNames = string.Join(", ", _rawParts.Take(4).Select(p => p.Name));
                if (_rawParts.Count > 4) staticPartNames += "...";

                lblModelInfo.Text = $"Fichier : {fn}\n" +
                                    $"Meshs ({_rawParts.Count}) : {staticPartNames}\n" +
                                    $"Sommets : {totalVertsStatic:N0} | Faces : {totalFacesStatic:N0} (Statique Assimp)";

                chkForceAutoSkin.Checked = true;
                _scale = 1.0f;
                nudScale.Value = 1.0m;
                _mirrorX = false;
                _offset = Vector3.Zero;

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
                _isInternalSkeletonSwitch = true;
                if (fn.IndexOf("female", StringComparison.OrdinalIgnoreCase) >= 0)
                    rbFemale.Checked = true;
                else
                    rbMale.Checked = true;
                _isInternalSkeletonSwitch = false;
                AutoPlaceMixamoLandmarks();
                EnsurePreviewModel();
                PopulateTestAnimations();
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

            SetPos("Bip01 Head", new Vector3(0.0f, midY, minZ + height * 0.88f));
            SetPos("Bip01 Neck", new Vector3(0.0f, midY, minZ + height * 0.82f));
            SetPos("Bip01 Spine2", new Vector3(0.0f, midY, minZ + height * 0.72f));
            SetPos("Bip01 Spine", new Vector3(0.0f, midY, minZ + height * 0.60f));
            SetPos("Bip01 Pelvis", new Vector3(0.0f, midY, minZ + height * 0.52f));

            float clavX = halfWidth * 0.25f;
            SetPos("Bip01 R Clavicle", new Vector3(-clavX, midY, minZ + height * 0.77f));
            SetPos("Bip01 L Clavicle", new Vector3(clavX, midY, minZ + height * 0.77f));

            float shoulderX = halfWidth * 0.45f;
            SetPos("Bip01 R UpperArm", new Vector3(-shoulderX, midY, minZ + height * 0.76f));
            SetPos("Bip01 L UpperArm", new Vector3(shoulderX, midY, minZ + height * 0.76f));

            float elbowX = halfWidth * 0.72f;
            SetPos("Bip01 R Forearm", new Vector3(-elbowX, midY, minZ + height * 0.74f));
            SetPos("Bip01 L Forearm", new Vector3(elbowX, midY, minZ + height * 0.74f));

            float handX = halfWidth * 0.95f;
            SetPos("Bip01 R Hand", new Vector3(-handX, midY, minZ + height * 0.73f));
            SetPos("Bip01 L Hand", new Vector3(handX, midY, minZ + height * 0.73f));

            float hipX = halfWidth * 0.22f;
            SetPos("Bip01 R Thigh", new Vector3(-hipX, midY, minZ + height * 0.48f));
            SetPos("Bip01 L Thigh", new Vector3(hipX, midY, minZ + height * 0.48f));

            SetPos("Bip01 R Calf", new Vector3(-hipX, midY + 0.01f, minZ + height * 0.27f));
            SetPos("Bip01 L Calf", new Vector3(hipX, midY + 0.01f, minZ + height * 0.27f));

            SetPos("Bip01 R Foot", new Vector3(-hipX * 0.9f, midY + 0.04f, minZ + height * 0.05f));
            SetPos("Bip01 L Foot", new Vector3(hipX * 0.9f, midY + 0.04f, minZ + height * 0.05f));

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
            var newPos = new Vector3((float)nudX.Value, (float)nudY.Value, (float)nudZ.Value);
            var delta = newPos - m.Position;
            m.Position = newPos;

            if (chkSymmetry.Checked && !string.IsNullOrEmpty(m.SymmetricBoneName))
            {
                var sym = _markers.FirstOrDefault(x => string.Equals(x.BoneName, m.SymmetricBoneName, StringComparison.OrdinalIgnoreCase));
                if (sym != null)
                {
                    sym.Position = new Vector3(-m.Position.X, m.Position.Y, m.Position.Z);
                }
            }

            if (_selectedMarkerIndices.Count > 1)
            {
                foreach (int idx in _selectedMarkerIndices)
                {
                    if (idx >= 0 && idx < _markers.Count && idx != _selectedMarkerIndex)
                    {
                        var curM = _markers[idx];
                        curM.Position += delta;
                        if (chkSymmetry.Checked && !string.IsNullOrEmpty(curM.SymmetricBoneName))
                        {
                            int symIdx = _markers.FindIndex(x => string.Equals(x.BoneName, curM.SymmetricBoneName, StringComparison.OrdinalIgnoreCase));
                            if (symIdx >= 0 && !_selectedMarkerIndices.Contains(symIdx))
                            {
                                _markers[symIdx].Position = new Vector3(-curM.Position.X, curM.Position.Y, curM.Position.Z);
                            }
                        }
                    }
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
                    sym.RotationDegrees = new Vector3(-m.RotationDegrees.X, -m.RotationDegrees.Y, -m.RotationDegrees.Z);
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
                    sym.RotationDegrees = new Vector3(-m.RotationDegrees.X, -m.RotationDegrees.Y, -m.RotationDegrees.Z);
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
                    sym.RotationDegrees = new Vector3(-m.RotationDegrees.X, -m.RotationDegrees.Y, -m.RotationDegrees.Z);
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
                GrannyModelFile? model = _previewModel;

                string ext = !string.IsNullOrEmpty(_currentModelPath) ? Path.GetExtension(_currentModelPath).ToLowerInvariant() : "";
                var customJoints = _markers.ToDictionary(m => m.BoneName, m => m.Position, StringComparer.OrdinalIgnoreCase);
                bool forceAuto = chkForceAutoSkin.Checked;

                if (model == null || model.Parts.Count == 0 || model.Bones.Count == 0)
                {
                    if (ext == ".fbx" && File.Exists(_currentModelPath) && !forceAuto)
                    {
                        MixamoCharacterImport.TryImport(_currentModelPath, _outFolder, _refPath, out model, out err, customJoints, forceAuto);
                    }
                }

                if (model == null)
                {
                    MessageBox.Show("Erreur lors de la génération du rig : " + (err ?? "inconnue"), "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                string othersFolder = Path.Combine(_basePath, "Others");
                if (!Directory.Exists(othersFolder)) Directory.CreateDirectory(othersFolder);
                if (string.IsNullOrEmpty(_outFolder) ||
                    _outFolder.IndexOf("Meshs custom", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    _outFolder.IndexOf("Mesh custom", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _outFolder = othersFolder;
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
uniform int uWireframe;
out vec4 FragColor;
void main() {
    if (uWireframe != 0) {
        FragColor = vec4(0.30, 0.85, 1.0, 0.95);
        return;
    }
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

            // 3. Draw Skeleton Bone Lines & Markers
            if (chkShowSkeleton.Checked)
            {
                DrawSkeletonLines(mvp);
                DrawJointMarkers(mvp);
            }

            // 4. Draw 2D Marquee Selection Box
            if (_isBoxSelecting)
            {
                DrawScreenSelectionBox();
            }

            glControl.SwapBuffers();
            }
            catch { }
        }

        private void DrawMeshVAO(Matrix4 mvp)
        {
            GL.UseProgram(_shaderMeshProg);
            int uMvp = GL.GetUniformLocation(_shaderMeshProg, "uMvp");
            int uWire = GL.GetUniformLocation(_shaderMeshProg, "uWireframe");
            GL.UniformMatrix4(uMvp, false, ref mvp);

            bool isWireframe = chkWireframe != null && chkWireframe.Checked;
            if (uWire >= 0)
            {
                GL.Uniform1(uWire, isWireframe ? 1 : 0);
            }

            if (isWireframe)
            {
                GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Line);
                GL.Disable(EnableCap.CullFace);
                GL.LineWidth(1.2f);
            }

            GL.BindVertexArray(_meshVao);
            GL.DrawArrays(OpenTK.Graphics.OpenGL.PrimitiveType.Triangles, 0, _meshVertexCount);
            GL.BindVertexArray(0);

            if (isWireframe)
            {
                GL.PolygonMode(TriangleFace.FrontAndBack, PolygonMode.Fill);
                GL.Enable(EnableCap.CullFace);
                GL.LineWidth(1.0f);
            }
        }

        private void DrawSkeletonLines(Matrix4 mvp)
        {
            var lines = new List<float>();

            // If animation is playing or finger flex is active, draw dynamic animated bones!
            if ((_isPlayingAnim || _fingerFlexAmount > 0.001f) && _boneWorldMatrices != null && _previewModel != null)
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
                bool isPrimary = (i == _selectedMarkerIndex);
                bool isMultiSelected = _selectedMarkerIndices.Contains(i);

                Vector3 pos = m.Position;
                if ((_isPlayingAnim || _fingerFlexAmount > 0.001f) && _boneWorldMatrices != null && _previewModel != null)
                {
                    int bidx = _previewModel.Bones.FindIndex(b => string.Equals(b.Name, m.BoneName, StringComparison.OrdinalIgnoreCase));
                    if (bidx >= 0 && bidx < _boneWorldMatrices.Length)
                    {
                        pos = _boneWorldMatrices[bidx].Row3.Xyz;
                    }
                }

                if (isPrimary)
                {
                    Vector4 col = new Vector4(1f, 0.95f, 0.1f, 1f);
                    GL.Uniform4(uColor, col);
                    DrawCrossMarker(mvp, pos, 0.075f, col);
                    DrawMarkerSelectionBox(mvp, pos, 0.04f, col);
                }
                else if (isMultiSelected)
                {
                    Vector4 col = new Vector4(0.2f, 0.95f, 1.0f, 1f);
                    GL.Uniform4(uColor, col);
                    DrawCrossMarker(mvp, pos, 0.065f, col);
                    DrawMarkerSelectionBox(mvp, pos, 0.035f, col);
                }
                else
                {
                    Vector4 col = new Vector4(m.Color.R / 255f, m.Color.G / 255f, m.Color.B / 255f, 1.0f);
                    GL.Uniform4(uColor, col);
                    DrawCrossMarker(mvp, pos, 0.045f, col);
                }
            }

            GL.Enable(EnableCap.DepthTest);
        }

        private void DrawMarkerSelectionBox(Matrix4 mvp, Vector3 c, float s, Vector4 color)
        {
            float[] boxVerts = new float[]
            {
                // Bottom quad
                c.X - s, c.Y - s, c.Z - s,   c.X + s, c.Y - s, c.Z - s,
                c.X + s, c.Y - s, c.Z - s,   c.X + s, c.Y + s, c.Z - s,
                c.X + s, c.Y + s, c.Z - s,   c.X - s, c.Y + s, c.Z - s,
                c.X - s, c.Y + s, c.Z - s,   c.X - s, c.Y - s, c.Z - s,
                // Top quad
                c.X - s, c.Y - s, c.Z + s,   c.X + s, c.Y - s, c.Z + s,
                c.X + s, c.Y - s, c.Z + s,   c.X + s, c.Y + s, c.Z + s,
                c.X + s, c.Y + s, c.Z + s,   c.X - s, c.Y + s, c.Z + s,
                c.X - s, c.Y + s, c.Z + s,   c.X - s, c.Y - s, c.Z + s,
                // Vertical pillars
                c.X - s, c.Y - s, c.Z - s,   c.X - s, c.Y - s, c.Z + s,
                c.X + s, c.Y - s, c.Z - s,   c.X + s, c.Y - s, c.Z + s,
                c.X + s, c.Y + s, c.Z - s,   c.X + s, c.Y + s, c.Z + s,
                c.X - s, c.Y + s, c.Z - s,   c.X - s, c.Y + s, c.Z + s,
            };
            DrawLinesImmediate(mvp, boxVerts, color, 1.5f);
        }

        private void DrawScreenSelectionBox()
        {
            int x1 = Math.Min(_boxSelectStart.X, _boxSelectCurrent.X);
            int x2 = Math.Max(_boxSelectStart.X, _boxSelectCurrent.X);
            int y1 = Math.Min(_boxSelectStart.Y, _boxSelectCurrent.Y);
            int y2 = Math.Max(_boxSelectStart.Y, _boxSelectCurrent.Y);

            if (x2 - x1 < 2 && y2 - y1 < 2) return;

            int w = Math.Max(1, glControl.Width);
            int h = Math.Max(1, glControl.Height);
            Matrix4 ortho = Matrix4.CreateOrthographicOffCenter(0, w, h, 0, -1.0f, 1.0f);

            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);
            GL.Enable(EnableCap.Blend);
            GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

            // Semi-transparent filled quad
            float[] quadVerts = new float[]
            {
                x1, y1, 0f,
                x2, y1, 0f,
                x2, y2, 0f,

                x1, y1, 0f,
                x2, y2, 0f,
                x1, y2, 0f
            };
            DrawTrianglesImmediate(ortho, quadVerts, new Vector4(0.2f, 0.65f, 1.0f, 0.22f));

            // Solid border lines
            float[] borderVerts = new float[]
            {
                x1, y1, 0f,  x2, y1, 0f,
                x2, y1, 0f,  x2, y2, 0f,
                x2, y2, 0f,  x1, y2, 0f,
                x1, y2, 0f,  x1, y1, 0f
            };
            DrawLinesImmediate(ortho, borderVerts, new Vector4(0.35f, 0.85f, 1.0f, 0.95f), 1.5f);

            GL.Disable(EnableCap.Blend);
            GL.Enable(EnableCap.DepthTest);
            GL.Enable(EnableCap.CullFace);
        }

        private void DrawTrianglesImmediate(Matrix4 mvp, float[] vertices, Vector4 color)
        {
            GL.UseProgram(_shaderLineProg);
            int uMvp = GL.GetUniformLocation(_shaderLineProg, "uMvp");
            int uColor = GL.GetUniformLocation(_shaderLineProg, "uColor");
            GL.UniformMatrix4(uMvp, false, ref mvp);
            GL.Uniform4(uColor, color);

            if (_boneVao == 0) _boneVao = GL.GenVertexArray();
            if (_boneVbo == 0) _boneVbo = GL.GenBuffer();

            GL.BindVertexArray(_boneVao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, _boneVbo);
            GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.DynamicDraw);

            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
            GL.EnableVertexAttribArray(0);

            GL.DrawArrays(OpenTK.Graphics.OpenGL.PrimitiveType.Triangles, 0, vertices.Length / 3);

            GL.BindVertexArray(0);
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
        }

        private void UpdateMarkersInSelectionBox(bool replaceSelection)
        {
            if (glControl.Width <= 0 || glControl.Height <= 0) return;

            int minX = Math.Min(_boxSelectStart.X, _boxSelectCurrent.X);
            int maxX = Math.Max(_boxSelectStart.X, _boxSelectCurrent.X);
            int minY = Math.Min(_boxSelectStart.Y, _boxSelectCurrent.Y);
            int maxY = Math.Max(_boxSelectStart.Y, _boxSelectCurrent.Y);

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

            if (replaceSelection)
            {
                _selectedMarkerIndices.Clear();
            }

            for (int i = 0; i < _markers.Count; i++)
            {
                var p = _markers[i].Position;
                Vector4 clip = new Vector4(p, 1.0f) * mvp;
                if (clip.W <= 0.01f) continue;

                Vector3 ndc = clip.Xyz / clip.W;
                float sx = (ndc.X + 1.0f) * 0.5f * glControl.Width;
                float sy = (1.0f - ndc.Y) * 0.5f * glControl.Height;

                if (sx >= minX && sx <= maxX && sy >= minY && sy <= maxY)
                {
                    _selectedMarkerIndices.Add(i);
                }
            }

            if (_selectedMarkerIndices.Count > 0)
            {
                if (!_selectedMarkerIndices.Contains(_selectedMarkerIndex))
                {
                    _selectedMarkerIndex = _selectedMarkerIndices.First();
                }
            }
            else if (replaceSelection)
            {
                _selectedMarkerIndex = -1;
            }
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
            bool isCtrl = ModifierKeys.HasFlag(Keys.Control);

            if (e.Button == MouseButtons.Left)
            {
                int picked = PickMarkerAtScreen(e.X, e.Y);

                if (isCtrl)
                {
                    if (picked >= 0)
                    {
                        // Ctrl + Click on a marker: toggle in selection
                        if (_selectedMarkerIndices.Contains(picked))
                        {
                            _selectedMarkerIndices.Remove(picked);
                            if (_selectedMarkerIndex == picked)
                            {
                                _selectedMarkerIndex = _selectedMarkerIndices.Count > 0 ? _selectedMarkerIndices.First() : -1;
                            }
                        }
                        else
                        {
                            _selectedMarkerIndices.Add(picked);
                            _selectedMarkerIndex = picked;
                        }

                        if (_selectedMarkerIndex >= 0 && _selectedMarkerIndex < _markers.Count)
                        {
                            cbJoints.SelectedIndex = _selectedMarkerIndex;
                            UpdateNudValues();
                        }

                        SetupMarkerDrag(e.X, e.Y);
                        glControl.Invalidate();
                        return;
                    }
                    else
                    {
                        // Ctrl + Click in empty space: start marquee selection box!
                        _isBoxSelecting = true;
                        _boxSelectStart = e.Location;
                        _boxSelectCurrent = e.Location;
                        if (!ModifierKeys.HasFlag(Keys.Shift))
                        {
                            _selectedMarkerIndices.Clear();
                            _selectedMarkerIndex = -1;
                        }
                        glControl.Invalidate();
                        return;
                    }
                }
                else
                {
                    if (picked >= 0)
                    {
                        // Click on a marker without Ctrl:
                        if (!_selectedMarkerIndices.Contains(picked))
                        {
                            _selectedMarkerIndices.Clear();
                            _selectedMarkerIndices.Add(picked);
                            _selectedMarkerIndex = picked;
                        }
                        else
                        {
                            _selectedMarkerIndex = picked;
                        }

                        cbJoints.SelectedIndex = _selectedMarkerIndex;
                        UpdateNudValues();

                        SetupMarkerDrag(e.X, e.Y);
                        glControl.Invalidate();
                        return;
                    }
                    else
                    {
                        // Click in empty space without Ctrl: clear selection
                        _selectedMarkerIndices.Clear();
                        _selectedMarkerIndex = -1;
                        glControl.Invalidate();

                        if (_currentViewMode == RigViewMode.Free3D)
                        {
                            _isOrbiting = true;
                        }
                        else
                        {
                            _isPanning = true;
                            glControl.Cursor = Cursors.Hand;
                        }
                        return;
                    }
                }
            }
            else if (e.Button == MouseButtons.Right || e.Button == MouseButtons.Middle)
            {
                _isPanning = true;
                glControl.Cursor = Cursors.Hand;
            }
        }

        private void SetupMarkerDrag(int mouseX, int mouseY)
        {
            if (_selectedMarkerIndex < 0 || _selectedMarkerIndex >= _markers.Count) return;

            _isDraggingMarker = true;
            _dragStartMarkerPositions.Clear();
            foreach (int idx in _selectedMarkerIndices)
            {
                if (idx >= 0 && idx < _markers.Count)
                {
                    _dragStartMarkerPositions[idx] = _markers[idx].Position;
                }
            }

            var m = _markers[_selectedMarkerIndex];
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

            if (UnprojectMouseToPlane(mouseX, mouseY, _dragPlanePoint, _dragPlaneNormal, out Vector3 initialHit))
            {
                _dragGrabOffset = _dragStartMarkerPos - initialHit;
            }
            else
            {
                _dragGrabOffset = Vector3.Zero;
            }
        }

        private void GlControl_MouseMove(object? sender, MouseEventArgs e)
        {
            int dx = e.X - _lastMousePos.X;
            int dy = e.Y - _lastMousePos.Y;
            _lastMousePos = e.Location;

            if (_isBoxSelecting)
            {
                _boxSelectCurrent = e.Location;
                UpdateMarkersInSelectionBox(replaceSelection: !ModifierKeys.HasFlag(Keys.Shift));
                glControl.Invalidate();
                return;
            }

            // 1:1 Ray-Plane unprojection dragging with multi-marker group movement
            if (_isDraggingMarker && _selectedMarkerIndex >= 0 && _selectedMarkerIndex < _markers.Count)
            {
                if (UnprojectMouseToPlane(e.X, e.Y, _dragPlanePoint, _dragPlaneNormal, out Vector3 rawHit))
                {
                    Vector3 targetHit = rawHit + _dragGrabOffset;
                    Vector3 primaryNewPos;

                    if (_currentViewMode == RigViewMode.Front || _currentViewMode == RigViewMode.Back ||
                        ((Math.Abs(_cameraYaw) < 15f || Math.Abs(_cameraYaw - 180f) < 15f) && Math.Abs(_cameraPitch) < 15f))
                    {
                        // Front or Back view: STRICTLY X and Z move! Y is 100% locked to initial marker depth!
                        primaryNewPos = new Vector3(targetHit.X, _dragStartMarkerPos.Y, targetHit.Z);
                    }
                    else if (_currentViewMode == RigViewMode.Profile ||
                        ((Math.Abs(_cameraYaw - 90f) < 15f || Math.Abs(_cameraYaw + 90f) < 15f || Math.Abs(_cameraYaw - 270f) < 15f) && Math.Abs(_cameraPitch) < 15f))
                    {
                        // Profile view: STRICTLY Y and Z move! X is 100% locked to initial marker lateral!
                        primaryNewPos = new Vector3(_dragStartMarkerPos.X, targetHit.Y, targetHit.Z);
                    }
                    else
                    {
                        // Free 3D view: follows camera plane
                        primaryNewPos = targetHit;
                    }

                    Vector3 delta = primaryNewPos - _dragStartMarkerPos;

                    // Apply delta to all selected markers
                    foreach (var kvp in _dragStartMarkerPositions)
                    {
                        int idx = kvp.Key;
                        if (idx >= 0 && idx < _markers.Count)
                        {
                            var curM = _markers[idx];
                            curM.Position = kvp.Value + delta;

                            // Automatic X symmetry for non-selected symmetric counterpart
                            if (chkSymmetry.Checked && !string.IsNullOrEmpty(curM.SymmetricBoneName))
                            {
                                int symIdx = _markers.FindIndex(x => string.Equals(x.BoneName, curM.SymmetricBoneName, StringComparison.OrdinalIgnoreCase));
                                if (symIdx >= 0 && !_selectedMarkerIndices.Contains(symIdx))
                                {
                                    _markers[symIdx].Position = new Vector3(-curM.Position.X, curM.Position.Y, curM.Position.Z);
                                }
                            }
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
            if (_isBoxSelecting)
            {
                _isBoxSelecting = false;
                _boxSelectCurrent = e.Location;
                UpdateMarkersInSelectionBox(replaceSelection: !ModifierKeys.HasFlag(Keys.Shift));
                if (_selectedMarkerIndices.Count > 0)
                {
                    if (!_selectedMarkerIndices.Contains(_selectedMarkerIndex))
                        _selectedMarkerIndex = _selectedMarkerIndices.First();

                    cbJoints.SelectedIndex = _selectedMarkerIndex;
                    UpdateNudValues();
                }
                glControl.Invalidate();
            }

            _isDraggingMarker = false;
            _dragStartMarkerPositions.Clear();
            _isOrbiting = false;
            _isPanning = false;
            glControl.Cursor = Cursors.Default;
        }

        private void GlControl_MouseWheel(object? sender, MouseEventArgs e)
        {
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
