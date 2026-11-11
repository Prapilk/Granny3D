using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Assimp;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using OpenTK.WinForms;
using Quaternion = OpenTK.Mathematics.Quaternion;
using TextureWrapMode = OpenTK.Graphics.OpenGL.TextureWrapMode;
using PrimitiveType = OpenTK.Graphics.OpenGL.PrimitiveType;

namespace UoFiddler.Plugin.Granny3D
{
    public class FullSetImportDialog : Form
    {
        private readonly string _basePath;

        // UI Controls - Sidebar
        private TextBox txtSourceDir = null!;
        private ComboBox cmbModelFile = null!;
        private TextBox txtCreatureName = null!;
        private NumericUpDown nudYaw = null!;
        private NumericUpDown nudPitch = null!;
        private NumericUpDown nudRoll = null!;
        private NumericUpDown nudScale = null!;
        private NumericUpDown nudOffX = null!;
        private NumericUpDown nudOffY = null!;
        private NumericUpDown nudOffZ = null!;
        private CheckedListBox clbAnimations = null!;
        private Label lblAnimCount = null!;
        private Button btnValidate = null!;

        // UI Controls - Viewport & Playback
        private GLControl glControl = null!;
        private ComboBox cmbPreviewAnim = null!;
        private Button btnPlayPause = null!;
        private TrackBar trackScrubber = null!;
        private Label lblTime = null!;
        private CheckBox chkLoop = null!;
        private System.Windows.Forms.Timer animTimer = null!;

        // State - Model & Raw Assets
        private string? _initialStartDir;
        private string? _currentModelPath;
        private Scene? _rawModelScene;
        private readonly List<string> _detectedAnimFiles = new();
        private readonly Dictionary<string, GrannyAnimation> _loadedAnims = new(StringComparer.OrdinalIgnoreCase);

        // Preview rendering state
        private bool _glReady;
        private int _shaderMesh;
        private int _shaderLine;
        private readonly List<PreviewMesh> _previewMeshes = new();
        private readonly List<int> _glTextures = new();
        private GrannyAnimation? _activePreviewAnim;
        private float _previewTime;
        private bool _isPlaying = true;
        private bool _updatingTransform;

        // Camera
        private float _camDist = 3.0f;
        private float _camYaw = 45.0f;
        private float _camPitch = 25.0f;
        private Vector3 _camTarget = new(0, 0, 0.5f);
        private Point _lastMouse;
        private bool _isRotating;
        private bool _isPanning;

        // ViewCube (3ds Max-style cube in viewport)
        private int _viewCubeVao;
        private int _viewCubeVbo;
        private int _viewCubeTex;
        private const int ViewCubePx = 80;
        private const int ViewCubeMargin = 12;

        private static readonly Vector3[] ViewCubeFaceNormals =
        {
            new Vector3(0, 1, 0),    // 0 DEVANT (+Y)
            new Vector3(0, -1, 0),   // 1 DERR (-Y)
            new Vector3(-1, 0, 0),   // 2 GAUCHE (-X)
            new Vector3(1, 0, 0),    // 3 DROIT (+X)
            new Vector3(0, 0, 1),    // 4 HAUT (+Z)
            new Vector3(0, 0, -1)    // 5 BAS (-Z)
        };
        private static readonly string[] ViewCubeFaceLabels = { "DEVANT", "DERR", "GAUCHE", "DROIT", "HAUT", "BAS" };

        public string? ImportedModelPath { get; private set; }

        private class PreviewMesh
        {
            public string Name = "";
            public Vector3[] BaseVertices = Array.Empty<Vector3>();
            public Vector3[] BaseNormals = Array.Empty<Vector3>();
            public Vector2[] UVs = Array.Empty<Vector2>();
            public int[] Indices = Array.Empty<int>();
            public VertexInfluence[][] Skin = Array.Empty<VertexInfluence[]>();
            public Vector3 Color = new(0.72f, 0.66f, 0.55f);
            public int TextureId;
            public int Vao;
            public int Vbo;
            public int Ebo;
            public float[] DynamicBuffer = Array.Empty<float>();
        }

        public FullSetImportDialog(string basePath, string? initialFolder = null)
        {
            _basePath = basePath;
            InitializeComponent();

            string startDir = !string.IsNullOrEmpty(initialFolder) && Directory.Exists(initialFolder)
                ? initialFolder
                : @"c:\Games\Sphere\MESH_custom\ANIMATION\chien03";

            if (!Directory.Exists(startDir))
            {
                startDir = Directory.Exists(Path.Combine(basePath, "Others"))
                    ? Path.Combine(basePath, "Others")
                    : basePath;
            }

            _initialStartDir = startDir;
            txtSourceDir.Text = startDir;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!string.IsNullOrEmpty(_initialStartDir) && Directory.Exists(_initialStartDir))
            {
                ScanSourceFolder(_initialStartDir);
            }
        }

        private void InitializeComponent()
        {
            Text = "📦 Importer un Full Set Externe (Modèle 3D + Animations)";
            Size = new Size(1280, 840);
            MinimumSize = new Size(1000, 680);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(28, 28, 30);
            ForeColor = Color.Gainsboro;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular);

            // ---- Left Sidebar Panel ----
            var pnlSidebar = new Panel
            {
                Dock = DockStyle.Left,
                Width = 430,
                AutoScroll = true,
                BackColor = Color.FromArgb(32, 32, 35),
                Padding = new Padding(10)
            };

            // Section 1: Source Folder & Files
            var grpSource = new GroupBox
            {
                Text = "1. Dossier & Modèle Source",
                Dock = DockStyle.Top,
                Height = 160,
                ForeColor = Color.FromArgb(230, 200, 110),
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Padding = new Padding(8)
            };

            var lblFolder = new Label { Text = "Dossier source :", Location = new Point(8, 22), AutoSize = true, ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 8.5F) };
            txtSourceDir = new TextBox { Location = new Point(8, 40), Width = 310, BackColor = Color.FromArgb(24, 24, 26), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 8.5F) };
            txtSourceDir.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) ScanSourceFolder(txtSourceDir.Text.Trim()); };

            var btnBrowse = new Button
            {
                Text = "📁...",
                Location = new Point(324, 39),
                Size = new Size(80, 24),
                BackColor = Color.FromArgb(50, 50, 55),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8.5F)
            };
            btnBrowse.Click += (s, e) =>
            {
                using var fbd = new FolderBrowserDialog { SelectedPath = txtSourceDir.Text };
                if (fbd.ShowDialog(this) == DialogResult.OK)
                {
                    txtSourceDir.Text = fbd.SelectedPath;
                    ScanSourceFolder(fbd.SelectedPath);
                }
            };

            var lblModel = new Label { Text = "Fichier Modèle / Rig :", Location = new Point(8, 68), AutoSize = true, ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 8.5F) };
            cmbModelFile = new ComboBox
            {
                Location = new Point(8, 86),
                Width = 396,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(24, 24, 26),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8.5F)
            };
            cmbModelFile.SelectedIndexChanged += (s, e) =>
            {
                if (cmbModelFile.SelectedItem is string path && File.Exists(path))
                    LoadSelectedModel(path);
            };

            var lblName = new Label { Text = "Nom de la créature :", Location = new Point(8, 114), AutoSize = true, ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 8.5F) };
            txtCreatureName = new TextBox { Location = new Point(140, 112), Width = 264, BackColor = Color.FromArgb(24, 24, 26), ForeColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Segoe UI", 8.5F) };

            grpSource.Controls.AddRange(new Control[] { lblFolder, txtSourceDir, btnBrowse, lblModel, cmbModelFile, lblName, txtCreatureName });

            // Section 2: World Orientation & Transform
            var grpTransform = new GroupBox
            {
                Text = "2. Orientation Monde & Échelle (repère UO : +Y avant, +Z haut)",
                Dock = DockStyle.Top,
                Height = 220,
                ForeColor = Color.FromArgb(120, 190, 255),
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Padding = new Padding(8)
            };

            int yPos = 24;
            // Yaw (Rotation Z)
            var lblYaw = new Label { Text = "Rot. Z (Yaw) :", Location = new Point(8, yPos + 3), AutoSize = true, ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 8.5F) };
            nudYaw = CreateNud(-180, 180, 0, 1);
            nudYaw.Location = new Point(100, yPos);
            nudYaw.ValueChanged += (s, e) => OnTransformChanged();

            var btnYawM90 = CreateMiniBtn("-90°", 175, yPos, () => AddNud(nudYaw, -90));
            var btnYawP90 = CreateMiniBtn("+90°", 220, yPos, () => AddNud(nudYaw, 90));
            var btnYaw180 = CreateMiniBtn("180°", 265, yPos, () => AddNud(nudYaw, 180));

            yPos += 28;
            // Pitch (Rotation X)
            var lblPitch = new Label { Text = "Rot. X (Pitch) :", Location = new Point(8, yPos + 3), AutoSize = true, ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 8.5F) };
            nudPitch = CreateNud(-180, 180, 0, 1);
            nudPitch.Location = new Point(100, yPos);
            nudPitch.ValueChanged += (s, e) => OnTransformChanged();

            var btnPitchM90 = CreateMiniBtn("-90°", 175, yPos, () => AddNud(nudPitch, -90));
            var btnPitchP90 = CreateMiniBtn("+90°", 220, yPos, () => AddNud(nudPitch, 90));

            yPos += 28;
            // Roll (Rotation Y)
            var lblRoll = new Label { Text = "Rot. Y (Roll) :", Location = new Point(8, yPos + 3), AutoSize = true, ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 8.5F) };
            nudRoll = CreateNud(-180, 180, 0, 1);
            nudRoll.Location = new Point(100, yPos);
            nudRoll.ValueChanged += (s, e) => OnTransformChanged();

            var btnRollM90 = CreateMiniBtn("-90°", 175, yPos, () => AddNud(nudRoll, -90));
            var btnRollP90 = CreateMiniBtn("+90°", 220, yPos, () => AddNud(nudRoll, 90));

            // Quick Orient Presets
            var btnPresetFront = CreateMiniBtn("Face (+Y)", 315, 24, () => { nudYaw.Value = 0; nudPitch.Value = 0; nudRoll.Value = 0; }, 88);
            var btnPresetSide = CreateMiniBtn("Face (+X)", 315, 52, () => { nudYaw.Value = 90; nudPitch.Value = 0; nudRoll.Value = 0; }, 88);
            var btnPresetFlip = CreateMiniBtn("Flip 90° X", 315, 80, () => { nudPitch.Value = 90; }, 88);

            yPos += 30;
            // Scale
            var lblScale = new Label { Text = "Échelle :", Location = new Point(8, yPos + 3), AutoSize = true, ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 8.5F) };
            nudScale = CreateNud(0.0001m, 1000m, 1.0m, 4);
            nudScale.Location = new Point(100, yPos);
            nudScale.Increment = 0.05m;
            nudScale.ValueChanged += (s, e) => OnTransformChanged();

            var btnFitUo = CreateMiniBtn("Auto-Fit ~1.2m", 175, yPos, AutoFitScale, 110);
            var btnScaleCm = CreateMiniBtn("0.01 (cm)", 290, yPos, () => nudScale.Value = 0.01m, 60);
            var btnScale1 = CreateMiniBtn("1.0", 355, yPos, () => nudScale.Value = 1.0m, 48);

            yPos += 30;
            // Position Offset & Ground Alignment
            var lblOffset = new Label { Text = "Offset Z :", Location = new Point(8, yPos + 3), AutoSize = true, ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 8.5F) };
            nudOffZ = CreateNud(-500, 500, 0, 3);
            nudOffZ.Location = new Point(100, yPos);
            nudOffZ.Increment = 0.05m;
            nudOffZ.ValueChanged += (s, e) => OnTransformChanged();

            nudOffX = CreateNud(-500, 500, 0, 3);
            nudOffY = CreateNud(-500, 500, 0, 3);

            var btnDropGround = CreateMiniBtn("🐾 Poser au sol (Z=0)", 175, yPos, AlignToGround, 130);
            var btnCenter = CreateMiniBtn("🎯 Centrer XY", 310, yPos, CenterXY, 93);

            grpTransform.Controls.AddRange(new Control[]
            {
                lblYaw, nudYaw, btnYawM90, btnYawP90, btnYaw180,
                lblPitch, nudPitch, btnPitchM90, btnPitchP90,
                lblRoll, nudRoll, btnRollM90, btnRollP90,
                btnPresetFront, btnPresetSide, btnPresetFlip,
                lblScale, nudScale, btnFitUo, btnScaleCm, btnScale1,
                lblOffset, nudOffZ, btnDropGround, btnCenter
            });

            // Section 3: Animations List
            var grpAnims = new GroupBox
            {
                Text = "3. Animations Détectées",
                Dock = DockStyle.Fill,
                ForeColor = Color.FromArgb(140, 225, 140),
                Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                Padding = new Padding(8)
            };

            var pnlAnimBtns = new Panel { Dock = DockStyle.Top, Height = 28 };
            var btnCheckAll = new Button
            {
                Text = "Tout cocher",
                Location = new Point(0, 2),
                Size = new Size(95, 24),
                BackColor = Color.FromArgb(45, 45, 50),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8F)
            };
            btnCheckAll.Click += (s, e) => SetAllAnimChecked(true);

            var btnUncheckAll = new Button
            {
                Text = "Tout décocher",
                Location = new Point(100, 2),
                Size = new Size(95, 24),
                BackColor = Color.FromArgb(45, 45, 50),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8F)
            };
            btnUncheckAll.Click += (s, e) => SetAllAnimChecked(false);

            lblAnimCount = new Label { Location = new Point(205, 6), AutoSize = true, ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 8F) };
            pnlAnimBtns.Controls.AddRange(new Control[] { btnCheckAll, btnUncheckAll, lblAnimCount });

            clbAnimations = new CheckedListBox
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(24, 24, 26),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                CheckOnClick = true,
                Font = new Font("Segoe UI", 8.5F)
            };
            clbAnimations.ItemCheck += (s, e) =>
            {
                if (IsHandleCreated)
                {
                    try { BeginInvoke(new Action(UpdateAnimCountLabel)); }
                    catch { }
                }
            };

            grpAnims.Controls.Add(clbAnimations);
            grpAnims.Controls.Add(pnlAnimBtns);

            // Import Button at bottom of sidebar
            btnValidate = new Button
            {
                Text = "🚀 Valider et Importer le Full Set",
                Dock = DockStyle.Bottom,
                Height = 44,
                BackColor = Color.FromArgb(40, 110, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 10F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnValidate.Click += (s, e) => ExecuteImport();

            pnlSidebar.Controls.Add(grpAnims);
            pnlSidebar.Controls.Add(btnValidate);
            pnlSidebar.Controls.Add(grpTransform);
            pnlSidebar.Controls.Add(grpSource);

            // ---- Right Viewport & Preview Panel ----
            var pnlViewport = new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(18, 18, 20) };

            var pnlTopBar = new Panel
            {
                Dock = DockStyle.Top,
                Height = 72,
                BackColor = Color.FromArgb(28, 28, 30),
                Padding = new Padding(6)
            };

            // Row 1: Animation controls
            var lblAnim = new Label { Text = "🎬 Test Animation :", Location = new Point(8, 8), AutoSize = true, ForeColor = Color.Gold, Font = new Font("Segoe UI", 9F, FontStyle.Bold) };
            cmbPreviewAnim = new ComboBox
            {
                Location = new Point(130, 6),
                Width = 210,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(24, 24, 26),
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 8.5F)
            };
            cmbPreviewAnim.SelectedIndexChanged += (s, e) => OnPreviewAnimSelected();

            btnPlayPause = new Button
            {
                Text = "⏸ Pause",
                Location = new Point(346, 5),
                Size = new Size(72, 26),
                BackColor = Color.FromArgb(45, 45, 50),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold)
            };
            btnPlayPause.Click += (s, e) => TogglePlayPause();

            trackScrubber = new TrackBar
            {
                Location = new Point(424, 3),
                Width = 200,
                Height = 30,
                Minimum = 0,
                Maximum = 1000,
                TickStyle = TickStyle.None
            };
            trackScrubber.Scroll += (s, e) => OnScrubberScroll();

            lblTime = new Label { Text = "0.00s / 0.00s", Location = new Point(630, 9), AutoSize = true, ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 8.5F) };
            chkLoop = new CheckBox { Text = "Boucle", Location = new Point(730, 9), AutoSize = true, Checked = true, ForeColor = Color.Gainsboro, Font = new Font("Segoe UI", 8.5F) };

            // Row 2: Camera View presets
            var lblViews = new Label { Text = "🎥 Vues Caméra :", Location = new Point(8, 41), AutoSize = true, ForeColor = Color.FromArgb(120, 190, 255), Font = new Font("Segoe UI", 9F, FontStyle.Bold) };

            var btnViewFront = new Button
            {
                Text = "👤 Vue de Face (Devant)",
                Location = new Point(130, 37),
                Size = new Size(160, 27),
                BackColor = Color.FromArgb(30, 95, 80),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8.5F, FontStyle.Bold),
                Cursor = Cursors.Hand
            };
            btnViewFront.Click += (s, e) => ViewFront();

            var btnViewIso = new Button
            {
                Text = "🔄 Vue Iso (UO)",
                Location = new Point(296, 37),
                Size = new Size(110, 27),
                BackColor = Color.FromArgb(45, 55, 70),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8.5F)
            };
            btnViewIso.Click += (s, e) => ViewIso();

            var btnViewSide = new Button
            {
                Text = "◀ Profil",
                Location = new Point(412, 37),
                Size = new Size(80, 27),
                BackColor = Color.FromArgb(45, 45, 50),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8.5F)
            };
            btnViewSide.Click += (s, e) => ViewRight();

            var btnViewTop = new Button
            {
                Text = "🔝 Dessus",
                Location = new Point(498, 37),
                Size = new Size(80, 27),
                BackColor = Color.FromArgb(45, 45, 50),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8.5F)
            };
            btnViewTop.Click += (s, e) => ViewTop();

            var btnResetCam = new Button
            {
                Text = "🎥 Reset",
                Location = new Point(584, 37),
                Size = new Size(76, 27),
                BackColor = Color.FromArgb(45, 45, 50),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8.5F)
            };
            btnResetCam.Click += (s, e) => ResetCamera();

            var lblHelp = new Label
            {
                Text = "💡 En 'Vue de Face', la tête de la créature doit regarder l'écran !",
                Location = new Point(668, 42),
                AutoSize = true,
                ForeColor = Color.FromArgb(180, 220, 255),
                Font = new Font("Segoe UI", 8.5F, FontStyle.Italic)
            };

            pnlTopBar.Controls.AddRange(new Control[]
            {
                lblAnim, cmbPreviewAnim, btnPlayPause, trackScrubber, lblTime, chkLoop,
                lblViews, btnViewFront, btnViewIso, btnViewSide, btnViewTop, btnResetCam, lblHelp
            });

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

            pnlViewport.Controls.Add(glControl);
            pnlViewport.Controls.Add(pnlTopBar);

            Controls.Add(pnlViewport);
            Controls.Add(pnlSidebar);

            // Animation playback timer (~30 FPS)
            animTimer = new System.Windows.Forms.Timer { Interval = 33 };
            animTimer.Tick += (s, e) => OnAnimTick();
            animTimer.Start();
        }

        private static NumericUpDown CreateNud(decimal min, decimal max, decimal val, int dec)
        {
            return new NumericUpDown
            {
                Minimum = min,
                Maximum = max,
                Value = val,
                DecimalPlaces = dec,
                Width = 70,
                BackColor = Color.FromArgb(24, 24, 26),
                ForeColor = Color.White,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 8.5F)
            };
        }

        private static Button CreateMiniBtn(string text, int x, int y, Action onClick, int width = 42)
        {
            var btn = new Button
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(width, 24),
                BackColor = Color.FromArgb(45, 45, 50),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 8F)
            };
            btn.Click += (s, e) => onClick();
            return btn;
        }

        private static void AddNud(NumericUpDown nud, decimal delta)
        {
            decimal nv = nud.Value + delta;
            while (nv > 180) nv -= 360;
            while (nv < -180) nv += 360;
            nud.Value = Math.Max(nud.Minimum, Math.Min(nud.Maximum, nv));
        }

        // ==================== SCAN & LOAD SOURCE ====================

        private void ScanSourceFolder(string folder)
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

            cmbModelFile.Items.Clear();
            clbAnimations.Items.Clear();
            _detectedAnimFiles.Clear();
            _loadedAnims.Clear();

            string[] exts = { "*.fbx", "*.obj", "*.gltf", "*.glb", "*.grn" };
            var allFiles = new List<string>();
            foreach (var ext in exts)
            {
                try { allFiles.AddRange(Directory.GetFiles(folder, ext, SearchOption.TopDirectoryOnly)); }
                catch { }
            }

            if (allFiles.Count == 0)
            {
                lblAnimCount.Text = "Aucun fichier 3D trouvé.";
                return;
            }

            // Detect candidate model files vs animation clips
            string? bestModel = null;
            var animCandidates = new List<string>();

            foreach (var file in allFiles)
            {
                string fn = Path.GetFileNameWithoutExtension(file);
                bool isModelTag = fn.IndexOf("rig", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  fn.IndexOf("mesh", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  fn.IndexOf("tpose", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  fn.IndexOf("model", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  fn.IndexOf("bind", StringComparison.OrdinalIgnoreCase) >= 0;

                cmbModelFile.Items.Add(file);

                if (isModelTag && bestModel == null)
                    bestModel = file;
                else
                    animCandidates.Add(file);
            }

            if (bestModel == null && cmbModelFile.Items.Count > 0)
                bestModel = (string)cmbModelFile.Items[0];

            if (bestModel != null)
                cmbModelFile.SelectedItem = bestModel;

            // Animations: all other files, plus check if the model file itself has animations
            foreach (var f in animCandidates)
            {
                _detectedAnimFiles.Add(f);
                string label = Path.GetFileNameWithoutExtension(f);
                clbAnimations.Items.Add(label, true);
            }

            UpdateAnimCountLabel();
        }

        private void LoadSelectedModel(string path)
        {
            _currentModelPath = path;

            // Guess creature name (strip _Rig, _Mesh, etc.)
            string rawName = Path.GetFileNameWithoutExtension(path);
            string clean = rawName;
            string[] tags = { "_Rig", "_mesh", "_Mesh", "_Model", "_Tpose", "_TPose", "_Bind" };
            foreach (var t in tags)
            {
                int idx = clean.IndexOf(t, StringComparison.OrdinalIgnoreCase);
                if (idx > 0) clean = clean.Substring(0, idx);
            }
            txtCreatureName.Text = clean.TrimEnd('_', '-', ' ');

            Cursor = Cursors.WaitCursor;
            try
            {
                using var ctx = new AssimpContext();
                _rawModelScene = ctx.ImportFile(path,
                    PostProcessSteps.Triangulate |
                    PostProcessSteps.JoinIdenticalVertices |
                    PostProcessSteps.LimitBoneWeights |
                    PostProcessSteps.GenerateNormals);

                if (_rawModelScene == null || _rawModelScene.MeshCount == 0)
                {
                    MessageBox.Show(this, "Impossible de charger le modèle 3D ou aucun mesh trouvé.", "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Auto-detect unit scale: if bounding height > 50 -> cm (0.01), if > 500 -> mm (0.001)
                float maxY = float.MinValue, minY = float.MaxValue;
                foreach (var m in _rawModelScene.Meshes)
                {
                    foreach (var v in m.Vertices)
                    {
                        if (v.Y > maxY) maxY = v.Y;
                        if (v.Y < minY) minY = v.Y;
                    }
                }
                float h = maxY - minY;
                _updatingTransform = true;
                if (h > 500f) nudScale.Value = 0.001m;
                else if (h > 40f) nudScale.Value = 0.01m;
                else if (h > 5f) nudScale.Value = 0.1m;
                else nudScale.Value = 1.0m;
                _updatingTransform = false;

                BuildPreviewMeshes();
                AlignToGround();
                ResetCamera();
                PopulatePreviewAnimCombo();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Erreur de lecture du modèle : {ex.Message}", "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private void PopulatePreviewAnimCombo()
        {
            cmbPreviewAnim.Items.Clear();
            cmbPreviewAnim.Items.Add("[Pose de repos]");

            // Check if model scene has embedded animations
            if (_rawModelScene != null && _rawModelScene.AnimationCount > 0)
            {
                for (int i = 0; i < _rawModelScene.AnimationCount; i++)
                {
                    string aName = string.IsNullOrEmpty(_rawModelScene.Animations[i].Name) ? $"Anim_{i}" : _rawModelScene.Animations[i].Name;
                    cmbPreviewAnim.Items.Add($"[Intégré] {aName}");
                }
            }

            foreach (var f in _detectedAnimFiles)
            {
                cmbPreviewAnim.Items.Add(Path.GetFileNameWithoutExtension(f));
            }

            if (cmbPreviewAnim.Items.Count > 1)
                cmbPreviewAnim.SelectedIndex = 1; // Auto-select first real animation clip!
            else
                cmbPreviewAnim.SelectedIndex = 0;
        }

        private void OnPreviewAnimSelected()
        {
            int idx = cmbPreviewAnim.SelectedIndex;
            if (idx <= 0)
            {
                _activePreviewAnim = null;
                lblTime.Text = "Pose de repos";
                trackScrubber.Value = 0;
                glControl?.Invalidate();
                return;
            }

            string item = (string)cmbPreviewAnim.SelectedItem;
            if (_loadedAnims.TryGetValue(item, out var cached))
            {
                _activePreviewAnim = cached;
                _previewTime = 0f;
                return;
            }

            // Find matching animation file or embedded clip
            string? matchFile = _detectedAnimFiles.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(item, StringComparison.OrdinalIgnoreCase));
            if (matchFile != null && File.Exists(matchFile))
            {
                var anim = LoadFbxAnimation(matchFile, 0);
                if (anim != null)
                {
                    _loadedAnims[item] = anim;
                    _activePreviewAnim = anim;
                    _previewTime = 0f;
                }
            }
            else if (_rawModelScene != null && _currentModelPath != null && item.StartsWith("[Intégré]"))
            {
                int embedIdx = Math.Max(0, idx - 1);
                var anim = LoadFbxAnimation(_currentModelPath, embedIdx);
                if (anim != null)
                {
                    _loadedAnims[item] = anim;
                    _activePreviewAnim = anim;
                    _previewTime = 0f;
                }
            }

            glControl?.Invalidate();
        }

        // ==================== GL RESOURCE & PREVIEW MESHES ====================

        private void BuildPreviewMeshes()
        {
            if (_rawModelScene == null) return;

            CleanGlMeshes();

            string srcDir = Path.GetDirectoryName(_currentModelPath) ?? string.Empty;

            for (int m = 0; m < _rawModelScene.MeshCount; m++)
            {
                var mesh = _rawModelScene.Meshes[m];
                if (mesh.VertexCount == 0 || mesh.FaceCount == 0) continue;

                var pm = new PreviewMesh
                {
                    Name = string.IsNullOrEmpty(mesh.Name) ? $"Mesh_{m}" : mesh.Name
                };

                int nv = mesh.VertexCount;
                pm.BaseVertices = new Vector3[nv];
                pm.BaseNormals = new Vector3[nv];
                pm.UVs = new Vector2[nv];

                for (int v = 0; v < nv; v++)
                {
                    pm.BaseVertices[v] = new Vector3(mesh.Vertices[v].X, mesh.Vertices[v].Y, mesh.Vertices[v].Z);
                    if (mesh.HasNormals)
                        pm.BaseNormals[v] = new Vector3(mesh.Normals[v].X, mesh.Normals[v].Y, mesh.Normals[v].Z);
                    else
                        pm.BaseNormals[v] = Vector3.UnitZ;

                    if (mesh.HasTextureCoords(0))
                        pm.UVs[v] = new Vector2(mesh.TextureCoordinateChannels[0][v].X, mesh.TextureCoordinateChannels[0][v].Y);
                }

                // Indices
                var idxList = new List<int>();
                foreach (var f in mesh.Faces)
                {
                    if (f.IndexCount == 3)
                    {
                        idxList.Add(f.Indices[0]);
                        idxList.Add(f.Indices[1]);
                        idxList.Add(f.Indices[2]);
                    }
                }
                pm.Indices = idxList.ToArray();

                // Material / Texture
                if (mesh.MaterialIndex >= 0 && mesh.MaterialIndex < _rawModelScene.MaterialCount)
                {
                    var mat = _rawModelScene.Materials[mesh.MaterialIndex];
                    var diff = mat.ColorDiffuse;
                    if (diff.R > 0.01f || diff.G > 0.01f || diff.B > 0.01f)
                        pm.Color = new Vector3(diff.R, diff.G, diff.B);

                    // Try to find diffuse texture
                    string? texFile = ResolveTextureFile(mat, srcDir);
                    if (texFile != null && File.Exists(texFile))
                    {
                        pm.TextureId = LoadGlTexture(texFile);
                    }
                }

                // Skin influences
                if (mesh.BoneCount > 0)
                {
                    var vertInfs = new List<VertexInfluence>[nv];
                    for (int v = 0; v < nv; v++) vertInfs[v] = new List<VertexInfluence>();

                    for (int b = 0; b < mesh.BoneCount; b++)
                    {
                        var bone = mesh.Bones[b];
                        foreach (var vw in bone.VertexWeights)
                        {
                            if (vw.VertexID >= 0 && vw.VertexID < nv && vw.Weight > 0.001f)
                                vertInfs[vw.VertexID].Add(new VertexInfluence { BoneId = b, Weight = vw.Weight });
                        }
                    }

                    pm.Skin = new VertexInfluence[nv][];
                    for (int v = 0; v < nv; v++)
                    {
                        var infs = vertInfs[v];
                        float sum = infs.Sum(x => x.Weight);
                        if (sum > 0.0001f)
                        {
                            for (int k = 0; k < infs.Count; k++)
                                infs[k] = new VertexInfluence { BoneId = infs[k].BoneId, Weight = infs[k].Weight / sum };
                        }
                        pm.Skin[v] = infs.ToArray();
                    }
                }

                pm.DynamicBuffer = new float[nv * 8]; // pos(3) + normal(3) + uv(2)
                _previewMeshes.Add(pm);
            }

            UploadGlBuffers();
            glControl?.Invalidate();
        }

        private static string? ResolveTextureFile(Material mat, string srcDir)
        {
            if (mat == null || string.IsNullOrEmpty(srcDir) || !Directory.Exists(srcDir)) return null;

            // Direct texture diffuse path
            if (mat.HasTextureDiffuse && !string.IsNullOrEmpty(mat.TextureDiffuse.FilePath))
            {
                string fp = mat.TextureDiffuse.FilePath;
                string fn = Path.GetFileName(fp);
                if (File.Exists(Path.Combine(srcDir, fn))) return Path.Combine(srcDir, fn);
                if (File.Exists(Path.Combine(srcDir, fp))) return Path.Combine(srcDir, fp);
            }

            // Fuzzy lookup based on material name
            string matName = mat.Name ?? "";
            string[] imgs = Directory.GetFiles(srcDir, "*.*")
                .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (matName.IndexOf("mouth", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                var hit = imgs.FirstOrDefault(f => f.IndexOf("mouth", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                                   f.IndexOf("basecolor", StringComparison.OrdinalIgnoreCase) >= 0);
                if (hit != null) return hit;
            }

            var hitBase = imgs.FirstOrDefault(f => f.IndexOf("basecolor", StringComparison.OrdinalIgnoreCase) >= 0);
            if (hitBase != null) return hitBase;

            return imgs.FirstOrDefault();
        }

        private int LoadGlTexture(string path)
        {
            if (!_glReady || !File.Exists(path)) return 0;
            try
            {
                glControl.MakeCurrent();
                using var bmp = new Bitmap(path);
                var bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                int tex = GL.GenTexture();
                GL.BindTexture(TextureTarget.Texture2D, tex);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, bmp.Width, bmp.Height, 0,
                    OpenTK.Graphics.OpenGL.PixelFormat.Bgra, PixelType.UnsignedByte, bd.Scan0);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
                bmp.UnlockBits(bd);
                _glTextures.Add(tex);
                return tex;
            }
            catch { return 0; }
        }

        private void CleanGlMeshes()
        {
            if (!_glReady) return;
            try
            {
                glControl.MakeCurrent();
                foreach (var pm in _previewMeshes)
                {
                    if (pm.Vao != 0) GL.DeleteVertexArray(pm.Vao);
                    if (pm.Vbo != 0) GL.DeleteBuffer(pm.Vbo);
                    if (pm.Ebo != 0) GL.DeleteBuffer(pm.Ebo);
                }
                foreach (var t in _glTextures) GL.DeleteTexture(t);
                _glTextures.Clear();
                if (_viewCubeVao != 0) { GL.DeleteVertexArray(_viewCubeVao); _viewCubeVao = 0; }
                if (_viewCubeVbo != 0) { GL.DeleteBuffer(_viewCubeVbo); _viewCubeVbo = 0; }
                if (_viewCubeTex != 0) { GL.DeleteTexture(_viewCubeTex); _viewCubeTex = 0; }
                _previewMeshes.Clear();
            }
            catch { }
        }

        private void UploadGlBuffers()
        {
            if (!_glReady) return;
            glControl.MakeCurrent();

            foreach (var pm in _previewMeshes)
            {
                if (pm.Vao == 0) pm.Vao = GL.GenVertexArray();
                if (pm.Vbo == 0) pm.Vbo = GL.GenBuffer();
                if (pm.Ebo == 0) pm.Ebo = GL.GenBuffer();

                GL.BindVertexArray(pm.Vao);

                GL.BindBuffer(BufferTarget.ArrayBuffer, pm.Vbo);
                GL.BufferData(BufferTarget.ArrayBuffer, pm.DynamicBuffer.Length * sizeof(float), IntPtr.Zero, BufferUsageHint.DynamicDraw);

                GL.BindBuffer(BufferTarget.ElementArrayBuffer, pm.Ebo);
                GL.BufferData(BufferTarget.ElementArrayBuffer, pm.Indices.Length * sizeof(int), pm.Indices, BufferUsageHint.StaticDraw);

                // layout 0: pos (3 floats), layout 1: normal (3 floats), layout 2: uv (2 floats)
                int stride = 8 * sizeof(float);
                GL.EnableVertexAttribArray(0);
                GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);

                GL.EnableVertexAttribArray(1);
                GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));

                GL.EnableVertexAttribArray(2);
                GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));

                GL.BindVertexArray(0);
            }
        }

        // ==================== ANIMATION LOADING & SAMPLING ====================

        private GrannyAnimation? LoadFbxAnimation(string fbxPath, int animIndex)
        {
            if (!File.Exists(fbxPath)) return null;
            try
            {
                using var ctx = new AssimpContext();
                var scene = ctx.ImportFile(fbxPath, PostProcessSteps.None);
                if (scene == null || scene.AnimationCount == 0) return null;

                if (animIndex < 0 || animIndex >= scene.AnimationCount) animIndex = 0;
                var anim = scene.Animations[animIndex];

                float fps = anim.TicksPerSecond > 0 ? (float)anim.TicksPerSecond : 30f;
                int frameCount = (int)Math.Round(anim.DurationInTicks) + 1;
                if (frameCount < 2) frameCount = 2;

                var grnAnim = new GrannyAnimation
                {
                    Name = Path.GetFileNameWithoutExtension(fbxPath),
                    Duration = (frameCount - 1) / fps
                };

                // Map channels by NodeName
                foreach (var ch in anim.NodeAnimationChannels)
                {
                    var tr = new GrannyTrack
                    {
                        Name = ch.NodeName,
                        TimesP = new float[frameCount],
                        Pos = new Vector3[frameCount],
                        TimesR = new float[frameCount],
                        Rot = new Quaternion[frameCount]
                    };

                    for (int f = 0; f < frameCount; f++)
                    {
                        float t = f / fps;
                        float tick = f;
                        tr.TimesP[f] = t;
                        tr.TimesR[f] = t;

                        Vector3 p = Vector3.Zero;
                        Quaternion q = Quaternion.Identity;

                        if (ch.PositionKeyCount > 0)
                        {
                            var pk = ch.PositionKeys.OrderBy(k => Math.Abs(k.Time - tick)).First();
                            p = new Vector3(pk.Value.X, pk.Value.Y, pk.Value.Z);
                        }
                        if (ch.RotationKeyCount > 0)
                        {
                            var rk = ch.RotationKeys.OrderBy(k => Math.Abs(k.Time - tick)).First();
                            q = new Quaternion(rk.Value.X, rk.Value.Y, rk.Value.Z, rk.Value.W);
                        }

                        tr.Pos[f] = p;
                        tr.Rot[f] = q;
                    }

                    grnAnim.Tracks.Add(tr);
                    grnAnim.TracksByName[tr.Name] = tr;
                }

                return grnAnim;
            }
            catch { return null; }
        }

        // ==================== TRANSFORMS & ALIGNMENT ====================

        private Matrix4 GetUserTransform()
        {
            float yawRad = MathHelper.DegreesToRadians((float)nudYaw.Value);
            float pitchRad = MathHelper.DegreesToRadians((float)nudPitch.Value);
            float rollRad = MathHelper.DegreesToRadians((float)nudRoll.Value);
            float scale = (float)nudScale.Value;
            Vector3 off = new((float)nudOffX.Value, (float)nudOffY.Value, (float)nudOffZ.Value);

            Matrix4 rot = Matrix4.CreateRotationX(pitchRad) *
                          Matrix4.CreateRotationY(rollRad) *
                          Matrix4.CreateRotationZ(yawRad);

            return Matrix4.CreateScale(scale) * rot * Matrix4.CreateTranslation(off);
        }

        private void OnTransformChanged()
        {
            if (_updatingTransform) return;
            glControl?.Invalidate();
        }

        private void AutoFitScale()
        {
            if (_previewMeshes.Count == 0) return;
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (var pm in _previewMeshes)
            {
                foreach (var v in pm.BaseVertices)
                {
                    if (v.Y < minY) minY = v.Y;
                    if (v.Y > maxY) maxY = v.Y;
                }
            }
            float rawH = maxY - minY;
            if (rawH > 0.001f)
            {
                // Target creature height: ~1.2 meters
                decimal targetH = 1.2m;
                nudScale.Value = Math.Round(targetH / (decimal)rawH, 4);
                AlignToGround();
            }
        }

        private void AlignToGround()
        {
            if (_previewMeshes.Count == 0) return;
            Matrix4 cur = GetUserTransform();
            float minZ = float.MaxValue;
            foreach (var pm in _previewMeshes)
            {
                foreach (var v in pm.BaseVertices)
                {
                    var wv = new Vector4(v.X, v.Y, v.Z, 1.0f) * cur;
                    if (wv.Z < minZ) minZ = wv.Z;
                }
            }

            if (minZ != float.MaxValue)
            {
                _updatingTransform = true;
                nudOffZ.Value -= (decimal)minZ;
                _updatingTransform = false;
                glControl?.Invalidate();
            }
        }

        private void CenterXY()
        {
            if (_previewMeshes.Count == 0) return;
            Matrix4 cur = GetUserTransform();
            float minX = float.MaxValue, maxX = float.MinValue;
            float minY = float.MaxValue, maxY = float.MinValue;
            foreach (var pm in _previewMeshes)
            {
                foreach (var v in pm.BaseVertices)
                {
                    var wv = new Vector4(v.X, v.Y, v.Z, 1.0f) * cur;
                    if (wv.X < minX) minX = wv.X; if (wv.X > maxX) maxX = wv.X;
                    if (wv.Y < minY) minY = wv.Y; if (wv.Y > maxY) maxY = wv.Y;
                }
            }

            if (minX != float.MaxValue)
            {
                float midX = (minX + maxX) * 0.5f;
                float midY = (minY + maxY) * 0.5f;
                _updatingTransform = true;
                nudOffX.Value -= (decimal)midX;
                nudOffY.Value -= (decimal)midY;
                _updatingTransform = false;
                glControl?.Invalidate();
            }
        }

        private void SetAllAnimChecked(bool check)
        {
            for (int i = 0; i < clbAnimations.Items.Count; i++)
                clbAnimations.SetItemChecked(i, check);
            UpdateAnimCountLabel();
        }

        private void UpdateAnimCountLabel()
        {
            lblAnimCount.Text = $"{clbAnimations.CheckedItems.Count} / {clbAnimations.Items.Count} sélectionnées";
        }

        // ==================== GL RENDERING & TIMING ====================

        private void OnAnimTick()
        {
            if (!_isPlaying || _activePreviewAnim == null || _activePreviewAnim.Duration <= 0f) return;

            _previewTime += 0.033f;
            if (_previewTime > _activePreviewAnim.Duration)
            {
                if (chkLoop.Checked) _previewTime = 0f;
                else { _previewTime = _activePreviewAnim.Duration; _isPlaying = false; btnPlayPause.Text = "▶ Play"; }
            }

            int trackVal = (int)Math.Clamp((_previewTime / _activePreviewAnim.Duration) * 1000f, 0, 1000);
            trackScrubber.Value = trackVal;
            lblTime.Text = $"{_previewTime:F2}s / {_activePreviewAnim.Duration:F2}s";
            glControl?.Invalidate();
        }

        private void TogglePlayPause()
        {
            _isPlaying = !_isPlaying;
            btnPlayPause.Text = _isPlaying ? "⏸ Pause" : "▶ Play";
        }

        private void OnScrubberScroll()
        {
            if (_activePreviewAnim == null || _activePreviewAnim.Duration <= 0f) return;
            _previewTime = (trackScrubber.Value / 1000f) * _activePreviewAnim.Duration;
            lblTime.Text = $"{_previewTime:F2}s / {_activePreviewAnim.Duration:F2}s";
            glControl?.Invalidate();
        }

        private void SetCameraAngles(float yawDeg, float pitchDeg)
        {
            _camYaw = yawDeg;
            _camPitch = Math.Clamp(pitchDeg, -89.5f, 89.5f);
            glControl?.Invalidate();
        }

        public void ViewFront()  { SetCameraAngles(180f, 0f); }
        public void ViewBack()   { SetCameraAngles(0f, 0f); }
        public void ViewLeft()   { SetCameraAngles(-90f, 0f); }
        public void ViewRight()  { SetCameraAngles(90f, 0f); }
        public void ViewTop()    { SetCameraAngles(180f, 89.5f); }
        public void ViewIso()    { SetCameraAngles(135f, 35f); }

        private Vector3 GetCameraEye()
        {
            float radYaw = MathHelper.DegreesToRadians(_camYaw);
            float radPitch = MathHelper.DegreesToRadians(_camPitch);
            Vector3 offset = new Vector3(
                (float)(_camDist * Math.Cos(radPitch) * Math.Sin(radYaw)),
                (float)(-_camDist * Math.Cos(radPitch) * Math.Cos(radYaw)),
                (float)(_camDist * Math.Sin(radPitch)));
            return _camTarget + offset;
        }

        private Matrix4 GetViewMatrix()
        {
            return Matrix4.LookAt(GetCameraEye(), _camTarget, Vector3.UnitZ);
        }

        private void ResetCamera()
        {
            _camDist = 3.5f;
            _camTarget = new Vector3(0, 0, 0.6f);
            ViewFront(); // Default to front view so user sees facing immediately!
        }

        // ==================== VIEW CUBE (3DS MAX STYLE) ====================

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
                float vTop = row * 0.5f, vBot = (row + 1) * 0.5f;
                var ps = new[] { n - u - v, n + u - v, n + u + v, n - u + v };
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
                    Color.FromArgb(60, 130, 180),   // DEVANT (+Y)
                    Color.FromArgb(50, 70, 110),    // DERR (-Y)
                    Color.FromArgb(70, 130, 80),    // GAUCHE (-X)
                    Color.FromArgb(160, 90, 60),    // DROIT (+X)
                    Color.FromArgb(180, 160, 60),   // HAUT (+Z)
                    Color.FromArgb(120, 80, 150)    // BAS (-Z)
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

            var bd = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            int tex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, tex);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, bmp.Width, bmp.Height, 0,
                OpenTK.Graphics.OpenGL.PixelFormat.Bgra, PixelType.UnsignedByte, bd.Scan0);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            bmp.UnlockBits(bd);
            return tex;
        }

        private void DrawViewCube()
        {
            if (_shaderMesh == 0) return;
            int vw = Math.Max(1, glControl.Width);
            int vh = Math.Max(1, glControl.Height);
            if (vw < ViewCubePx + 24 || vh < ViewCubePx + 24) return;
            EnsureViewCube();
            if (_viewCubeVao == 0 || _viewCubeTex == 0) return;

            Matrix4 mvp = ViewCubeMvp(0, 0, vw, vh);

            int cx = ViewCubeMargin;
            int cy = vh - ViewCubePx - ViewCubeMargin;
            GL.Enable(EnableCap.ScissorTest);
            GL.Scissor(cx, cy, ViewCubePx, ViewCubePx);
            GL.Clear(ClearBufferMask.DepthBufferBit);
            GL.Scissor(0, 0, vw, vh);
            GL.Disable(EnableCap.ScissorTest);
            GL.Enable(EnableCap.DepthTest);
            GL.UseProgram(_shaderMesh);

            int uMvp = GL.GetUniformLocation(_shaderMesh, "uMvp");
            int uHasTex = GL.GetUniformLocation(_shaderMesh, "uHasTex");
            int uColor = GL.GetUniformLocation(_shaderMesh, "uColor");

            GL.UniformMatrix4(uMvp, false, ref mvp);
            GL.Uniform1(uHasTex, 1);
            GL.Uniform3(uColor, 1f, 1f, 1f);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, _viewCubeTex);
            GL.BindVertexArray(_viewCubeVao);
            GL.DrawArrays(PrimitiveType.Triangles, 0, 36);
            GL.BindVertexArray(0);
            GL.UseProgram(0);
        }

        private Matrix4 ViewCubeMvp(int vx, int vy, int vw, int vh)
        {
            int cornerX = vx + ViewCubeMargin;
            int cornerY = vy + vh - ViewCubePx - ViewCubeMargin;
            float half = ViewCubePx / 2f;
            float cubeHalf = half * 0.62f;

            Matrix4 rot = GetViewMatrix();
            rot.Row3 = new Vector4(0, 0, 0, 1);
            Matrix4 s = Matrix4.CreateScale(cubeHalf, cubeHalf, cubeHalf);
            Matrix4 t = Matrix4.CreateTranslation(cornerX + half, cornerY + half, 0f);
            Matrix4 pixelToNdc = Matrix4.CreateScale(2f / vw, 2f / vh, 1f / (2f * cubeHalf))
                               * Matrix4.CreateTranslation(-1f, -1f, 0f);
            return rot * s * t * pixelToNdc;
        }

        private bool TryViewCubeClick(MouseEventArgs e)
        {
            int vw = Math.Max(1, glControl.Width);
            int vh = Math.Max(1, glControl.Height);
            if (vw < ViewCubePx + 24 || vh < ViewCubePx + 24) return false;

            float mx = e.X;
            float my = vh - e.Y;
            int cornerX = ViewCubeMargin;
            int cornerY = vh - ViewCubePx - ViewCubeMargin;
            if (mx < cornerX || mx > cornerX + ViewCubePx || my < cornerY || my > cornerY + ViewCubePx)
                return false;

            Matrix4 rot = GetViewMatrix();
            rot.Row3 = new Vector4(0, 0, 0, 1);
            float half = ViewCubePx / 2f;
            float cubeHalf = half * 0.62f;
            Vector3 eye = GetCameraEye();

            for (int f = 0; f < 6; f++)
            {
                var n = ViewCubeFaceNormals[f];
                if (Vector3.Dot(n, eye - n) <= 0f) continue;
                ViewCubeFaceTangents(f, out var u, out var v);
                var pts = new Vector3[4];
                var signs = new[] { (-1, -1), (1, -1), (1, 1), (-1, 1) };
                for (int k = 0; k < 4; k++)
                {
                    Vector3 p = Vector3.TransformPosition(n + signs[k].Item1 * u + signs[k].Item2 * v, rot);
                    pts[k] = new Vector3(cornerX + half + p.X * cubeHalf, cornerY + half + p.Y * cubeHalf, 0f);
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
                var p1 = q[i];
                var p2 = q[(i + 1) % 4];
                float cross = (p2.X - p1.X) * (y - p1.Y) - (p2.Y - p1.Y) * (x - p1.X);
                if (Math.Abs(cross) < 1e-4f) continue;
                bool s = cross > 0;
                if (!sign.HasValue) sign = s;
                else if (sign.Value != s) return false;
            }
            return true;
        }

        private void ApplyViewCubeFace(int f)
        {
            switch (f)
            {
                case 0: ViewFront(); break;
                case 1: ViewBack(); break;
                case 2: ViewLeft(); break;
                case 3: ViewRight(); break;
                case 4: ViewTop(); break;
                case 5: SetCameraAngles(180f, -89.5f); break;
            }
        }

        private void EnsureShaders()
        {
            if (_shaderMesh != 0) return;

            const string vs = @"#version 330 core
layout(location=0) in vec3 aPos;
layout(location=1) in vec3 aNormal;
layout(location=2) in vec2 aUv;
uniform mat4 uMvp;
out vec3 vNormal;
out vec2 vUv;
void main() {
    gl_Position = uMvp * vec4(aPos, 1.0);
    vNormal = aNormal;
    vUv = aUv;
}";

            const string fs = @"#version 330 core
in vec3 vNormal;
in vec2 vUv;
uniform sampler2D uTex;
uniform int uHasTex;
uniform vec3 uColor;
out vec4 FragColor;
void main() {
    vec3 norm = normalize(vNormal);
    vec3 light1 = normalize(vec3(0.5, 0.7, 1.0));
    vec3 light2 = normalize(vec3(-0.5, -0.7, 0.4));
    float diff = max(dot(norm, light1), 0.0) + max(dot(norm, light2), 0.0) * 0.4;
    vec3 ambient = vec3(0.3, 0.32, 0.35);

    vec4 texColor = (uHasTex != 0) ? texture(uTex, vUv) : vec4(uColor, 1.0);
    vec3 finalRgb = (ambient + diff * 0.75) * texColor.rgb;
    FragColor = vec4(finalRgb, 1.0);
}";

            const string vsL = @"#version 330 core
layout(location=0) in vec3 aPos;
uniform mat4 uMvp;
void main() {
    gl_Position = uMvp * vec4(aPos, 1.0);
}";

            const string fsL = @"#version 330 core
uniform vec4 uColor;
out vec4 FragColor;
void main() {
    FragColor = uColor;
}";

            _shaderMesh = CompileProgram(vs, fs);
            _shaderLine = CompileProgram(vsL, fsL);
        }

        private static int CompileProgram(string vs, string fs)
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

        private static int CompileShader(ShaderType type, string src)
        {
            int s = GL.CreateShader(type);
            GL.ShaderSource(s, src);
            GL.CompileShader(s);
            return s;
        }

        private void GlControl_Paint(object? sender, PaintEventArgs e)
        {
            if (!_glReady && glControl.IsHandleCreated)
            {
                glControl.MakeCurrent();
                EnsureShaders();
                _glReady = true;
                UploadGlBuffers();
            }

            if (!_glReady) return;

            glControl.MakeCurrent();
            GL.Viewport(0, 0, glControl.Width, glControl.Height);
            GL.ClearColor(0.11f, 0.11f, 0.13f, 1.0f);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            GL.Enable(EnableCap.DepthTest);

            // Compute View / Proj
            float aspect = Math.Max(0.1f, (float)glControl.Width / Math.Max(1, glControl.Height));
            Matrix4 proj = Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(45f), aspect, 0.05f, 100f);

            Matrix4 view = GetViewMatrix();
            Matrix4 mvp = view * proj;

            // 1. Draw Ground Grid, Facing Arrow & Coordinate Axes
            DrawGridAndAxes(mvp);

            // 2. Compute Skinned Vertices with UserTransform
            Matrix4 userTransform = GetUserTransform();
            UpdateAndRenderModel(mvp, userTransform);

            // 3. Draw 3D ViewCube in corner
            DrawViewCube();

            glControl.SwapBuffers();
        }

        private void DrawGridAndAxes(Matrix4 mvp)
        {
            GL.UseProgram(_shaderLine);
            int uMvp = GL.GetUniformLocation(_shaderLine, "uMvp");
            int uCol = GL.GetUniformLocation(_shaderLine, "uColor");
            GL.UniformMatrix4(uMvp, false, ref mvp);

            // Grid lines on Z = 0
            GL.Uniform4(uCol, 0.22f, 0.22f, 0.25f, 1.0f);
            var gridPts = new List<float>();
            float extent = 3.0f;
            float step = 0.25f;
            for (float i = -extent; i <= extent + 1e-4f; i += step)
            {
                gridPts.Add(i); gridPts.Add(-extent); gridPts.Add(0f);
                gridPts.Add(i); gridPts.Add(extent); gridPts.Add(0f);

                gridPts.Add(-extent); gridPts.Add(i); gridPts.Add(0f);
                gridPts.Add(extent); gridPts.Add(i); gridPts.Add(0f);
            }
            DrawLines(gridPts.ToArray());

            // Coordinate Axes from origin
            // X (Red) = Droite (+X)
            GL.Uniform4(uCol, 0.9f, 0.2f, 0.2f, 1.0f);
            DrawLines(new float[] { 0, 0, 0, 1.0f, 0, 0 });

            // Z (Blue) = Haut (+Z)
            GL.Uniform4(uCol, 0.3f, 0.6f, 1.0f, 1.0f);
            DrawLines(new float[] { 0, 0, 0, 0, 0, 1.0f });

            // Big Facing Arrow on the Ground along +Y (Avant / Direction de regard en UO)
            GL.Uniform4(uCol, 0.15f, 0.95f, 0.35f, 1.0f);
            var facingArrow = new float[]
            {
                // Main stem from (0, -0.4) to (0, 2.2)
                0f, -0.4f, 0.005f,   0f, 2.2f, 0.005f,
                // Arrow tip
                0f, 2.2f, 0.005f,   -0.25f, 1.85f, 0.005f,
                0f, 2.2f, 0.005f,    0.25f, 1.85f, 0.005f,
                -0.25f, 1.85f, 0.005f, 0.25f, 1.85f, 0.005f,
                // Chevrons
                0f, 1.4f, 0.005f,   -0.20f, 1.15f, 0.005f,
                0f, 1.4f, 0.005f,    0.20f, 1.15f, 0.005f,
                0f, 0.8f, 0.005f,   -0.15f, 0.60f, 0.005f,
                0f, 0.8f, 0.005f,    0.15f, 0.60f, 0.005f
            };
            DrawLines(facingArrow);

            GL.UseProgram(0);
        }

        private static void DrawLines(float[] pts)
        {
            int vao = GL.GenVertexArray();
            int vbo = GL.GenBuffer();
            GL.BindVertexArray(vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            GL.BufferData(BufferTarget.ArrayBuffer, pts.Length * sizeof(float), pts, BufferUsageHint.StreamDraw);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 3 * sizeof(float), 0);
            GL.DrawArrays(PrimitiveType.Lines, 0, pts.Length / 3);
            GL.BindVertexArray(0);
            GL.DeleteBuffer(vbo);
            GL.DeleteVertexArray(vao);
        }

        private void UpdateAndRenderModel(Matrix4 mvp, Matrix4 userTransform)
        {
            if (_previewMeshes.Count == 0) return;

            GL.UseProgram(_shaderMesh);
            int uMvp = GL.GetUniformLocation(_shaderMesh, "uMvp");
            int uHasTex = GL.GetUniformLocation(_shaderMesh, "uHasTex");
            int uColor = GL.GetUniformLocation(_shaderMesh, "uColor");
            GL.UniformMatrix4(uMvp, false, ref mvp);

            // Compute bone skinning matrices if animation is playing
            Matrix4[]? boneMatrices = null;
            if (_activePreviewAnim != null && _rawModelScene != null)
            {
                boneMatrices = ComputeAnimBoneMatrices(_activePreviewAnim, _previewTime);
            }

            foreach (var pm in _previewMeshes)
            {
                if (pm.Vao == 0 || pm.Indices.Length == 0) continue;

                // Skin vertices into DynamicBuffer
                int nv = pm.BaseVertices.Length;
                bool hasSkin = boneMatrices != null && pm.Skin != null && pm.Skin.Length == nv;

                for (int v = 0; v < nv; v++)
                {
                    Vector3 origPos = pm.BaseVertices[v];
                    Vector3 origNorm = pm.BaseNormals[v];
                    Vector3 p = origPos;
                    Vector3 n = origNorm;

                    if (hasSkin)
                    {
                        var infs = pm.Skin[v];
                        if (infs.Length > 0)
                        {
                            p = Vector3.Zero;
                            n = Vector3.Zero;
                            foreach (var inf in infs)
                            {
                                if (inf.BoneId >= 0 && inf.BoneId < boneMatrices!.Length)
                                {
                                    var bm = boneMatrices[inf.BoneId];
                                    p += Vector3.TransformPosition(origPos, bm) * inf.Weight;
                                    n += Vector3.TransformVector(origNorm, bm) * inf.Weight;
                                }
                            }
                        }
                    }

                    // Apply user world transform (Orientation, Scale, Offset)
                    var wv = new Vector4(p.X, p.Y, p.Z, 1.0f) * userTransform;
                    var wn = new Vector4(n.X, n.Y, n.Z, 0.0f) * userTransform;
                    Vector3 finNorm = wn.Xyz.LengthSquared > 1e-4f ? Vector3.Normalize(wn.Xyz) : Vector3.UnitZ;

                    int offset = v * 8;
                    pm.DynamicBuffer[offset + 0] = wv.X;
                    pm.DynamicBuffer[offset + 1] = wv.Y;
                    pm.DynamicBuffer[offset + 2] = wv.Z;
                    pm.DynamicBuffer[offset + 3] = finNorm.X;
                    pm.DynamicBuffer[offset + 4] = finNorm.Y;
                    pm.DynamicBuffer[offset + 5] = finNorm.Z;
                    pm.DynamicBuffer[offset + 6] = pm.UVs[v].X;
                    pm.DynamicBuffer[offset + 7] = pm.UVs[v].Y;
                }

                // Upload to VBO
                GL.BindBuffer(BufferTarget.ArrayBuffer, pm.Vbo);
                GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, pm.DynamicBuffer.Length * sizeof(float), pm.DynamicBuffer);

                // Set Texture / Material
                bool useTex = pm.TextureId > 0;
                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, useTex ? pm.TextureId : 0);
                GL.Uniform1(uHasTex, useTex ? 1 : 0);
                GL.Uniform3(uColor, pm.Color.X, pm.Color.Y, pm.Color.Z);

                // Draw Mesh
                GL.BindVertexArray(pm.Vao);
                GL.DrawElements(PrimitiveType.Triangles, pm.Indices.Length, DrawElementsType.UnsignedInt, 0);
            }

            GL.BindVertexArray(0);
            GL.UseProgram(0);
        }

        private Matrix4[]? ComputeAnimBoneMatrices(GrannyAnimation anim, float time)
        {
            if (_rawModelScene == null || _rawModelScene.MeshCount == 0) return null;
            var mesh0 = _rawModelScene.Meshes[0];
            int nb = mesh0.BoneCount;
            if (nb == 0) return null;

            var result = new Matrix4[nb];

            // Reconstruct node world matrices under animation
            var nodes = new List<Node>();
            var nodeParent = new List<int>();
            var nameToNode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            void Collect(Node n, int par)
            {
                int idx = nodes.Count;
                nodes.Add(n);
                nodeParent.Add(par);
                if (!nameToNode.ContainsKey(n.Name)) nameToNode[n.Name] = idx;
                foreach (var c in n.Children) Collect(c, idx);
            }
            Collect(_rawModelScene.RootNode, -1);

            int nn = nodes.Count;
            var localWorld = new Matrix4[nn];

            for (int i = 0; i < nn; i++)
            {
                string nName = nodes[i].Name;
                Vector3 p;
                Quaternion q;

                nodes[i].Transform.Decompose(out var sc, out var q0, out var p0);
                p = new Vector3(p0.X, p0.Y, p0.Z);
                q = new Quaternion(q0.X, q0.Y, q0.Z, q0.W);

                if (anim.TracksByName.TryGetValue(nName, out var tr))
                {
                    if (tr.Pos != null && tr.Pos.Length > 0) p = tr.EvaluatePos(time);
                    if (tr.Rot != null && tr.Rot.Length > 0) q = tr.EvaluateRot(time);
                }

                Matrix4 local = Matrix4.CreateScale(sc.X, sc.Y, sc.Z) *
                              Matrix4.CreateFromQuaternion(q) *
                              Matrix4.CreateTranslation(p);

                int par = nodeParent[i];
                localWorld[i] = (par >= 0 && par < nn && par != i) ? local * localWorld[par] : local;
            }

            for (int b = 0; b < nb; b++)
            {
                var bone = mesh0.Bones[b];
                var offset = bone.OffsetMatrix;
                var invBind = new Matrix4(
                    offset.A1, offset.B1, offset.C1, offset.D1,
                    offset.A2, offset.B2, offset.C2, offset.D2,
                    offset.A3, offset.B3, offset.C3, offset.D3,
                    offset.A4, offset.B4, offset.C4, offset.D4);

                Matrix4 wAnim = Matrix4.Identity;
                if (nameToNode.TryGetValue(bone.Name, out int ni))
                {
                    wAnim = localWorld[ni];
                }

                result[b] = invBind * wAnim;
            }

            return result;
        }

        // ==================== MOUSE CONTROLS ====================

        private void GlControl_MouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && TryViewCubeClick(e))
                return;

            _lastMouse = e.Location;
            if (e.Button == MouseButtons.Left) _isRotating = true;
            else if (e.Button == MouseButtons.Right) _isPanning = true;
        }

        private void GlControl_MouseUp(object? sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) _isRotating = false;
            else if (e.Button == MouseButtons.Right) _isPanning = false;
        }

        private void GlControl_MouseMove(object? sender, MouseEventArgs e)
        {
            int dx = e.X - _lastMouse.X;
            int dy = e.Y - _lastMouse.Y;
            _lastMouse = e.Location;

            if (_isRotating)
            {
                _camYaw += dx * 0.5f;
                _camPitch = Math.Clamp(_camPitch - dy * 0.5f, -89f, 89f);
                glControl.Invalidate();
            }
            else if (_isPanning)
            {
                float factor = _camDist * 0.0015f;
                float radYaw = MathHelper.DegreesToRadians(_camYaw);
                Vector3 right = new((float)Math.Cos(radYaw), (float)Math.Sin(radYaw), 0);
                Vector3 up = Vector3.UnitZ;
                _camTarget += right * (-dx * factor) + up * (dy * factor);
                glControl.Invalidate();
            }
        }

        private void GlControl_MouseWheel(object? sender, MouseEventArgs e)
        {
            float zoom = (e.Delta > 0) ? 0.88f : 1.14f;
            _camDist = Math.Clamp(_camDist * zoom, 0.1f, 100f);
            glControl.Invalidate();
        }

        // ==================== EXECUTE IMPORT ====================

        private void ExecuteImport()
        {
            if (string.IsNullOrEmpty(_currentModelPath) || !File.Exists(_currentModelPath) || _rawModelScene == null)
            {
                MessageBox.Show(this, "Veuillez d'abord sélectionner un fichier modèle 3D valide.", "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string creatureName = txtCreatureName.Text.Trim();
            if (string.IsNullOrEmpty(creatureName))
            {
                MessageBox.Show(this, "Veuillez saisir un nom pour la créature (ex: Labrador).", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string targetOthers = Path.Combine(_basePath, "Others");
            if (!Directory.Exists(targetOthers)) Directory.CreateDirectory(targetOthers);

            string targetAnimDir = Path.Combine(targetOthers, creatureName);
            if (!Directory.Exists(targetAnimDir)) Directory.CreateDirectory(targetAnimDir);

            string outCharPath = Path.Combine(targetOthers, $"{creatureName}.char");
            Matrix4 userTransform = GetUserTransform();

            Cursor = Cursors.WaitCursor;
            try
            {
                // 1. Build and export GrannyModelFile (.char)
                var model = BuildExportModel(userTransform, targetOthers, creatureName);
                if (model == null || model.Parts.Count == 0)
                {
                    MessageBox.Show(this, "Échec de création du modèle .char.", "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                bool charSaved = GrannyModelFile.Save(model, outCharPath);
                if (!charSaved)
                {
                    MessageBox.Show(this, $"Impossible de sauvegarder le fichier :\n{outCharPath}", "Erreur d'écriture", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // 2. Export checked animations (.anim)
                int animsExported = 0;
                for (int i = 0; i < clbAnimations.Items.Count; i++)
                {
                    if (!clbAnimations.GetItemChecked(i)) continue;

                    string animName = (string)clbAnimations.Items[i];
                    string? matchFbx = _detectedAnimFiles.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(animName, StringComparison.OrdinalIgnoreCase));
                    if (matchFbx != null && File.Exists(matchFbx))
                    {
                        var anim = ConvertAnimation(matchFbx, 0, model.Bones, userTransform, animName);
                        if (anim != null)
                        {
                            string outAnimPath = Path.Combine(targetAnimDir, $"{animName}.anim");
                            if (GrannyAnimFile.Save(anim, outAnimPath)) animsExported++;
                        }
                    }
                }

                ImportedModelPath = outCharPath;

                MessageBox.Show(this,
                    $"✅ Full Set '{creatureName}' importé avec succès !\n\n" +
                    $"  • Modèle (.char) : {Path.GetFileName(outCharPath)}\n" +
                    $"  • Animations (.anim) : {animsExported} clips dans 'Others\\{creatureName}\\'\n\n" +
                    $"Le modèle va être automatiquement sélectionné et chargé dans le visualiseur.",
                    "Import Réussi", MessageBoxButtons.OK, MessageBoxIcon.Information);

                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Erreur lors de l'exportation : {ex.Message}\n{ex}", "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Cursor = Cursors.Default;
            }
        }

        private GrannyModelFile? BuildExportModel(Matrix4 userTransform, string outFolder, string creatureName)
        {
            if (_rawModelScene == null || _currentModelPath == null) return null;

            var nodes = new List<Node>();
            var nodeParent = new List<int>();
            var nameToNode = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            void Collect(Node n, int par)
            {
                int idx = nodes.Count;
                nodes.Add(n);
                nodeParent.Add(par);
                if (!nameToNode.ContainsKey(n.Name)) nameToNode[n.Name] = idx;
                foreach (var c in n.Children) Collect(c, idx);
            }
            Collect(_rawModelScene.RootNode, -1);

            int nn = nodes.Count;
            var localMat = new Matrix4[nn];
            for (int i = 0; i < nn; i++)
            {
                nodes[i].Transform.Decompose(out var sc, out var q, out var p);
                localMat[i] = Matrix4.CreateScale(sc.X, sc.Y, sc.Z) *
                              Matrix4.CreateFromQuaternion(new Quaternion(q.X, q.Y, q.Z, q.W)) *
                              Matrix4.CreateTranslation(p.X, p.Y, p.Z);
            }
            var wMat = new Matrix4[nn];
            for (int i = 0; i < nn; i++)
            {
                int p = nodeParent[i];
                wMat[i] = (p >= 0 && p < nn && p != i) ? localMat[i] * wMat[p] : localMat[i];
            }

            // Collect all unique bones used in meshes
            var boneNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in _rawModelScene.Meshes)
                foreach (var b in m.Bones)
                    boneNames.Add(b.Name);

            var boneNodes = new HashSet<int>();
            foreach (var bname in boneNames)
                if (nameToNode.TryGetValue(bname, out int ni)) boneNodes.Add(ni);

            int skelRoot = -1;
            for (int i = 0; i < nn; i++)
            {
                if (HasBoneDescendant(i, nodes, boneNodes) && (nodeParent[i] < 0 || !HasBoneDescendant(nodeParent[i], nodes, boneNodes)))
                {
                    skelRoot = i;
                    break;
                }
            }
            if (skelRoot < 0) skelRoot = 0;

            var skelNodes = new List<int>();
            var nodeToBone = new Dictionary<int, int>();
            void CollectSkel(int ni)
            {
                int bi = skelNodes.Count;
                skelNodes.Add(ni);
                nodeToBone[ni] = bi;
                foreach (var c in nodes[ni].Children)
                {
                    int ci = nodes.IndexOf(c);
                    if (ci >= 0 && HasBoneDescendant(ci, nodes, boneNodes))
                        CollectSkel(ci);
                }
            }
            CollectSkel(skelRoot);

            var bones = new List<GrannyBone>();
            var worldBones = new Matrix4[skelNodes.Count];
            for (int bi = 0; bi < skelNodes.Count; bi++)
            {
                int ni = skelNodes[bi];
                worldBones[bi] = wMat[ni] * userTransform;
            }

            for (int bi = 0; bi < skelNodes.Count; bi++)
            {
                int ni = skelNodes[bi];
                int pNode = nodeParent[ni];
                int pBone = (pNode >= 0 && nodeToBone.TryGetValue(pNode, out int pb)) ? pb : -1;

                Matrix4 local = (pBone >= 0) ? (worldBones[bi] * Matrix4.Invert(worldBones[pBone])) : worldBones[bi];
                bones.Add(new GrannyBone
                {
                    Id = bi,
                    ParentId = pBone,
                    Name = nodes[ni].Name,
                    LocalPosition = local.ExtractTranslation(),
                    LocalRotation = local.ExtractRotation(),
                    InverseBindMatrix = Matrix4.Invert(worldBones[bi])
                });
            }

            var model = new GrannyModelFile
            {
                Name = creatureName,
                Bones = bones
            };

            string srcDir = Path.GetDirectoryName(_currentModelPath) ?? string.Empty;

            // Copy and process textures
            for (int m = 0; m < _rawModelScene.MeshCount; m++)
            {
                var mesh = _rawModelScene.Meshes[m];
                var part = new GrannyModelPart
                {
                    Name = string.IsNullOrEmpty(mesh.Name) ? $"Mesh_{m}" : mesh.Name
                };

                int nv = mesh.VertexCount;
                part.Vertices = new Vector3[nv];
                part.Normals = new Vector3[nv];
                part.UVs = new Vector2[nv];

                for (int v = 0; v < nv; v++)
                {
                    var pt = mesh.Vertices[v];
                    var wv = new Vector4(pt.X, pt.Y, pt.Z, 1.0f) * userTransform;
                    part.Vertices[v] = wv.Xyz;

                    if (mesh.HasNormals)
                    {
                        var n = mesh.Normals[v];
                        var wn = new Vector4(n.X, n.Y, n.Z, 0.0f) * userTransform;
                        part.Normals[v] = Vector3.Normalize(wn.Xyz);
                    }

                    if (mesh.HasTextureCoords(0))
                    {
                        var uv = mesh.TextureCoordinateChannels[0][v];
                        part.UVs[v] = new Vector2(uv.X, uv.Y);
                    }
                }

                // Indices
                var indices = new List<int>();
                foreach (var face in mesh.Faces)
                {
                    if (face.IndexCount == 3)
                    {
                        indices.Add(face.Indices[0]);
                        indices.Add(face.Indices[1]);
                        indices.Add(face.Indices[2]);
                    }
                }
                part.Indices = indices.ToArray();

                // Material & Texture
                if (mesh.MaterialIndex >= 0 && mesh.MaterialIndex < _rawModelScene.MaterialCount)
                {
                    var mat = _rawModelScene.Materials[mesh.MaterialIndex];
                    part.MaterialName = mat.Name ?? "";
                    var diff = mat.ColorDiffuse;
                    if (diff.R > 0.01f || diff.G > 0.01f || diff.B > 0.01f)
                        part.Color = new Vector3(diff.R, diff.G, diff.B);

                    string? texFile = ResolveTextureFile(mat, srcDir);
                    if (texFile != null && File.Exists(texFile))
                    {
                        string ext = Path.GetExtension(texFile);
                        string savedTexName = $"{creatureName}_{part.Name}{ext}";
                        string destTexPath = Path.Combine(outFolder, savedTexName);
                        try
                        {
                            File.Copy(texFile, destTexPath, true);
                            part.TextureName = savedTexName;
                        }
                        catch { }
                    }
                }

                // Skin weights
                var vertInfluences = new List<VertexInfluence>[nv];
                for (int v = 0; v < nv; v++) vertInfluences[v] = new List<VertexInfluence>();

                foreach (var b in mesh.Bones)
                {
                    if (!nameToNode.TryGetValue(b.Name, out int ni) || !nodeToBone.TryGetValue(ni, out int bIdx))
                        continue;

                    foreach (var vw in b.VertexWeights)
                    {
                        if (vw.VertexID >= 0 && vw.VertexID < nv && vw.Weight > 0.001f)
                            vertInfluences[vw.VertexID].Add(new VertexInfluence { BoneId = bIdx, Weight = vw.Weight });
                    }
                }

                part.Skin = new VertexInfluence[nv][];
                for (int v = 0; v < nv; v++)
                {
                    var infs = vertInfluences[v];
                    float sum = infs.Sum(x => x.Weight);
                    if (sum > 0.0001f)
                    {
                        for (int k = 0; k < infs.Count; k++)
                            infs[k] = new VertexInfluence { BoneId = infs[k].BoneId, Weight = infs[k].Weight / sum };
                    }
                    part.Skin[v] = infs.ToArray();
                }

                model.Parts.Add(part);
            }

            return model;
        }

        private static GrannyAnimation? ConvertAnimation(string fbxPath, int animIndex, IReadOnlyList<GrannyBone> bones, Matrix4 userTransform, string animName)
        {
            if (!File.Exists(fbxPath)) return null;
            try
            {
                using var ctx = new AssimpContext();
                var scene = ctx.ImportFile(fbxPath, PostProcessSteps.None);
                if (scene == null || scene.AnimationCount == 0) return null;

                if (animIndex < 0 || animIndex >= scene.AnimationCount) animIndex = 0;
                var anim = scene.Animations[animIndex];

                var chanByName = new Dictionary<string, NodeAnimationChannel>(StringComparer.OrdinalIgnoreCase);
                foreach (var ch in anim.NodeAnimationChannels)
                    chanByName[ch.NodeName] = ch;

                float fps = anim.TicksPerSecond > 0 ? (float)anim.TicksPerSecond : 30f;
                int frameCount = (int)Math.Round(anim.DurationInTicks) + 1;
                if (frameCount < 2) frameCount = 2;

                var grnAnim = new GrannyAnimation
                {
                    Name = animName,
                    Duration = (frameCount - 1) / fps
                };

                for (int bi = 0; bi < bones.Count; bi++)
                {
                    var b = bones[bi];
                    chanByName.TryGetValue(b.Name, out var ch);

                    var tr = new GrannyTrack
                    {
                        BoneId = b.Id,
                        Name = b.Name,
                        RestPosition = b.LocalPosition,
                        RestRotation = b.LocalRotation,
                        TimesP = new float[frameCount],
                        Pos = new Vector3[frameCount],
                        TimesR = new float[frameCount],
                        Rot = new Quaternion[frameCount]
                    };

                    for (int f = 0; f < frameCount; f++)
                    {
                        float t = f / fps;
                        float tick = f;
                        tr.TimesP[f] = t;
                        tr.TimesR[f] = t;

                        Vector3 p = b.LocalPosition;
                        Quaternion q = b.LocalRotation;

                        if (ch != null)
                        {
                            if (ch.PositionKeyCount > 0)
                            {
                                var pk = ch.PositionKeys.OrderBy(k => Math.Abs(k.Time - tick)).First();
                                p = new Vector3(pk.Value.X, pk.Value.Y, pk.Value.Z);
                            }
                            if (ch.RotationKeyCount > 0)
                            {
                                var rk = ch.RotationKeys.OrderBy(k => Math.Abs(k.Time - tick)).First();
                                q = new Quaternion(rk.Value.X, rk.Value.Y, rk.Value.Z, rk.Value.W);
                            }
                        }

                        // Root bone receives user world transform
                        if (b.ParentId < 0)
                        {
                            var mLocal = Matrix4.CreateFromQuaternion(q) * Matrix4.CreateTranslation(p) * userTransform;
                            p = mLocal.ExtractTranslation();
                            q = mLocal.ExtractRotation();
                        }

                        tr.Pos[f] = p;
                        tr.Rot[f] = q;
                    }

                    grnAnim.Tracks.Add(tr);
                    grnAnim.TracksByName[tr.Name] = tr;
                    grnAnim.Skeleton.Add(b);
                    grnAnim.SkeletonByName[b.Name] = b;
                }

                return grnAnim;
            }
            catch { return null; }
        }

        private static bool HasBoneDescendant(int ni, List<Node> nodes, HashSet<int> boneNodes)
        {
            if (boneNodes.Contains(ni)) return true;
            foreach (var c in nodes[ni].Children)
            {
                int ci = nodes.IndexOf(c);
                if (ci >= 0 && HasBoneDescendant(ci, nodes, boneNodes)) return true;
            }
            return false;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            animTimer?.Stop();
            animTimer?.Dispose();
            CleanGlMeshes();
            base.OnFormClosed(e);
        }
    }
}
