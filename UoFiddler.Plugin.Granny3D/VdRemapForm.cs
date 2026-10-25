using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using OpenTK.Mathematics;

namespace UoFiddler.Plugin.Granny3D
{
    // Dialog replicating UOFiddler's "Export -> To vd.." remapper: pick the target
    // category, then bind each target action slot to a source Granny animation.
    // Includes per-action 3D offset controls (X, Y, Z) to position the character,
    // e.g. Z = +0.38 for mounted actions.
    public sealed class VdRemapForm : Form
    {
        private ComboBox _cmbTarget;
        private NumericUpDown _nudFps;
        private NumericUpDown _nudSize;
        private NumericUpDown _nudOffY;
        private Panel _rows;
        private readonly List<ComboBox> _combos = new List<ComboBox>();
        private readonly List<NumericUpDown> _nudX = new List<NumericUpDown>();
        private readonly List<NumericUpDown> _nudY = new List<NumericUpDown>();
        private readonly List<NumericUpDown> _nudZ = new List<NumericUpDown>();

        private readonly List<VdTargetDef> _targets;
        private readonly List<string> _sources;      // full .grn paths
        private readonly List<string> _sourceNames;  // display names
        private string _stripPrefix;                 // creature prefix to ignore when matching
        private readonly string _basePath;

        // Every animation across all folders (the "ALL anim" set): an optional superset
        // of _sources so clips from other creatures / imported .anim can be picked too.
        // _allDisplay holds the same entries grouped by category (e.g. "Monsters ▸ X").
        private readonly List<string> _allSources;
        private readonly List<string> _allNames;
        private readonly List<string> _allDisplay;
        private bool _useAll;
        private Button _btnChooseMap;
        private Button _btnAll;

        private readonly Func<string, int, float, Vector3, int, Bitmap> _previewRenderer;
        private Panel _pnlPreview;
        private PictureBox _picPreview;
        private ComboBox _cmbPreviewAction;
        private Button[] _btnDirs;
        private Button _btnPlayPause;
        private TrackBar _tbTime;
        private Label _lblTime;
        private Label _lblActionDetail;
        private Label _lblRoleSummary;
        private Label _lblWarning;
        private System.Windows.Forms.Timer _tmrAnim;
        private Bitmap _previewBmp;
        private int _previewDir = 0;
        private float _previewTimeNorm = 0f;
        private int _activeActionIdx = 0;
        private bool _isPlaying = false;

        public VdTargetDef Target { get; private set; }
        public int Fps => (int)_nudFps.Value;
        public int FrameSize => (int)_nudSize.Value;
        public int OffsetY => (int)_nudOffY.Value;

        public VdRemapForm(List<VdTargetDef> targets, List<string> sources, string stripPrefix = null,
            List<string> allSources = null, List<string> allDisplay = null, string basePath = null,
            Func<string, int, float, Vector3, int, Bitmap> previewRenderer = null,
            string exportModeTitle = null, string cutoutInfo = null,
            bool hasCutoutWarning = false, string cutoutWarningText = null)
        {
            _previewRenderer = previewRenderer;
            _basePath = basePath;
            _targets = targets;
            _sources = sources != null ? new List<string>(sources) : new List<string>();
            _sourceNames = _sources.Select(Path.GetFileNameWithoutExtension).ToList();
            _stripPrefix = Normalize(stripPrefix ?? string.Empty);   // same normalization as source names

            _allSources = (allSources != null && allSources.Count > 0) ? allSources : _sources;
            _allNames = _allSources.Select(Path.GetFileNameWithoutExtension).ToList();
            _allDisplay = (allDisplay != null && allDisplay.Count == _allSources.Count)
                ? allDisplay : new List<string>(_allNames);

            Text = "Export .vd — mapping des animations";
            ClientSize = new Size(1120, 680);
            MinimumSize = new Size(1000, 520);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(40, 40, 44);
            ForeColor = Color.Gainsboro;
            MinimizeBox = false;
            MaximizeBox = false;
            FormBorderStyle = FormBorderStyle.Sizable;

            var top = new Panel { Dock = DockStyle.Top, Height = 106, Padding = new Padding(8) };

            top.Controls.Add(new Label { Text = "Cible (type UO) :", Location = new Point(8, 10), Width = 110, ForeColor = Color.Gainsboro });
            _cmbTarget = new ComboBox
            {
                Location = new Point(120, 6),
                Width = 195,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.Gainsboro
            };
            _cmbTarget.Items.AddRange(_targets.ToArray());
            _cmbTarget.SelectedIndexChanged += (s, e) =>
            {
                var keep = CaptureSelection();
                RebuildRows(keep, preserveClips: true);
            };
            top.Controls.Add(_cmbTarget);

            // "Choose map": pick a reference creature (H_Male, Zombie...) from the full models tree
            // to pre-fill the action mapping slots using its animation set.
            _btnChooseMap = new Button
            {
                Text = "Choose map…",
                Location = new Point(322, 5),
                Width = 135,
                Height = 26,
                BackColor = Color.FromArgb(60, 60, 62),
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat
            };
            _btnChooseMap.Click += (s, e) => OnChooseMapClicked();
            top.Controls.Add(_btnChooseMap);

            // "ALL anim": swap the per-model source list for every clip found in all
            // folders (other creatures, imported .anim...), not just the model's folder.
            _btnAll = new Button
            {
                Text = "ALL anim (tout)",
                Location = new Point(462, 5),
                Width = 130,
                Height = 26,
                BackColor = Color.FromArgb(60, 60, 62),
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat
            };
            _btnAll.Click += (s, e) => ToggleAllSources();
            top.Controls.Add(_btnAll);

            top.Controls.Add(new Label { Text = "Images/s :", Location = new Point(8, 42), Width = 65, ForeColor = Color.Gray });
            _nudFps = new NumericUpDown { Location = new Point(74, 38), Width = 50, Minimum = 2, Maximum = 30, Value = 10, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            top.Controls.Add(_nudFps);

            top.Controls.Add(new Label { Text = "Perso px :", Location = new Point(132, 42), Width = 65, ForeColor = Color.Gray });
            _nudSize = new NumericUpDown { Location = new Point(198, 38), Width = 55, Minimum = 0, Maximum = 512, Increment = 16, Value = 0, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            _nudSize.ValueChanged += (s, e) => UpdatePreview();
            top.Controls.Add(_nudSize);

            top.Controls.Add(new Label { Text = "Décal. Y px :", Location = new Point(262, 42), Width = 80, ForeColor = Color.Gray });
            _nudOffY = new NumericUpDown { Location = new Point(344, 38), Width = 55, Minimum = -64, Maximum = 64, Increment = 1, Value = 0, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            _nudOffY.ValueChanged += (s, e) => UpdatePreview();
            top.Controls.Add(_nudOffY);

            var btnMountZ = new Button
            {
                Text = "Z montures =",
                Location = new Point(410, 37),
                Width = 92,
                Height = 26,
                BackColor = Color.FromArgb(50, 65, 80),
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat
            };

            var nudMountZ = new NumericUpDown
            {
                Location = new Point(505, 38),
                Width = 58,
                Minimum = -50m,
                Maximum = 50m,
                DecimalPlaces = 2,
                Increment = 0.05m,
                Value = 0.38m,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle
            };

            btnMountZ.Click += (s, e) => ApplyMountZ((float)nudMountZ.Value);
            nudMountZ.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    ApplyMountZ((float)nudMountZ.Value);
                    e.SuppressKeyPress = true;
                }
            };
            top.Controls.Add(btnMountZ);
            top.Controls.Add(nudMountZ);

            var btnResetOffsets = new Button
            {
                Text = "Reset X/Y/Z",
                Location = new Point(570, 37),
                Width = 90,
                Height = 26,
                BackColor = Color.FromArgb(60, 60, 62),
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat
            };
            btnResetOffsets.Click += (s, e) => ResetAllOffsets();
            top.Controls.Add(btnResetOffsets);

            // Row 3: Save / Load / Reset / Clear mapping
            var btnSaveMap = new Button
            {
                Text = "💾 Sauver mapping…",
                Location = new Point(8, 71),
                Width = 145,
                Height = 26,
                BackColor = Color.FromArgb(40, 70, 95),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnSaveMap.Click += (s, e) => SaveMappingDialog();
            top.Controls.Add(btnSaveMap);

            var btnLoadMap = new Button
            {
                Text = "📂 Charger mapping…",
                Location = new Point(159, 71),
                Width = 145,
                Height = 26,
                BackColor = Color.FromArgb(45, 75, 55),
                ForeColor = Color.LightGreen,
                FlatStyle = FlatStyle.Flat
            };
            btnLoadMap.Click += (s, e) => LoadMappingDialog();
            top.Controls.Add(btnLoadMap);

            var btnResetMap = new Button
            {
                Text = "↺ Réinit. mapping",
                Location = new Point(310, 71),
                Width = 140,
                Height = 26,
                BackColor = Color.FromArgb(60, 60, 62),
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat
            };
            btnResetMap.Click += (s, e) => ResetMapping();
            top.Controls.Add(btnResetMap);

            var btnClearAll = new Button
            {
                Text = "🗑 Vider tout",
                Location = new Point(456, 71),
                Width = 95,
                Height = 26,
                BackColor = Color.FromArgb(70, 45, 45),
                ForeColor = Color.FromArgb(255, 170, 170),
                FlatStyle = FlatStyle.Flat
            };
            btnClearAll.Click += (s, e) => ClearAllMapping();
            top.Controls.Add(btnClearAll);

            var hdr = new Panel { Dock = DockStyle.Top, Height = 22, BackColor = Color.FromArgb(32, 32, 35), Padding = new Padding(8, 2, 8, 2) };
            hdr.Controls.Add(new Label { Text = "Action", Location = new Point(14, 4), Width = 130, ForeColor = Color.DarkGray, Font = new Font(Font.FontFamily, 8.25f, FontStyle.Bold) });
            hdr.Controls.Add(new Label { Text = "Animation source", Location = new Point(156, 4), Width = 250, ForeColor = Color.DarkGray, Font = new Font(Font.FontFamily, 8.25f, FontStyle.Bold) });
            hdr.Controls.Add(new Label { Text = "Décalage 3D X", Location = new Point(426, 4), Width = 74, ForeColor = Color.DarkGray, Font = new Font(Font.FontFamily, 8f, FontStyle.Bold) });
            hdr.Controls.Add(new Label { Text = "Décalage 3D Y", Location = new Point(502, 4), Width = 74, ForeColor = Color.DarkGray, Font = new Font(Font.FontFamily, 8f, FontStyle.Bold) });
            hdr.Controls.Add(new Label { Text = "Décalage 3D Z", Location = new Point(578, 4), Width = 74, ForeColor = Color.DarkGray, Font = new Font(Font.FontFamily, 8f, FontStyle.Bold) });

            _rows = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(8) };

            // Panel d'aperçu du rendu final et de validation cutout (à droite)
            _pnlPreview = new Panel
            {
                Dock = DockStyle.Right,
                Width = 360,
                BackColor = Color.FromArgb(32, 32, 35),
                Padding = new Padding(8),
                AutoScroll = true
            };
            BuildPreviewControls(_pnlPreview, exportModeTitle, cutoutInfo, hasCutoutWarning, cutoutWarningText);

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 46, Padding = new Padding(8) };
            var ok = new Button { Text = "Exporter", Width = 100, Height = 28, BackColor = Color.FromArgb(70, 90, 70), ForeColor = Color.Gainsboro, FlatStyle = FlatStyle.Flat };
            var cancel = new Button { Text = "Annuler", Width = 100, Height = 28, BackColor = Color.FromArgb(60, 60, 62), ForeColor = Color.Gainsboro, FlatStyle = FlatStyle.Flat };
            ok.Click += (s, e) => { Target = _cmbTarget.SelectedItem as VdTargetDef; DialogResult = DialogResult.OK; };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; };
            bottom.Controls.Add(ok);
            bottom.Controls.Add(cancel);

            Controls.Add(_rows);
            Controls.Add(hdr);
            Controls.Add(_pnlPreview);
            Controls.Add(bottom);
            Controls.Add(top);

            if (_cmbTarget.Items.Count > 0) _cmbTarget.SelectedIndex = 0;
        }

        private void BuildPreviewControls(Panel pnl, string modeTitle, string cutoutInfo, bool hasWarning, string warningText)
        {
            var lblTitle = new Label
            {
                Text = "Aperçu du rendu final",
                Location = new Point(8, 6),
                AutoSize = true,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                ForeColor = Color.White
            };
            pnl.Controls.Add(lblTitle);

            var lblBadge = new Label
            {
                Text = modeTitle ?? "Rendu complet (.vd)",
                Location = new Point(8, 28),
                AutoSize = true,
                Font = new Font("Segoe UI", 8.25f, FontStyle.Bold),
                ForeColor = Color.FromArgb(100, 190, 255)
            };
            pnl.Controls.Add(lblBadge);

            var lblActionPick = new Label
            {
                Text = "Action à prévisualiser :",
                Location = new Point(8, 50),
                AutoSize = true,
                ForeColor = Color.DarkGray
            };
            pnl.Controls.Add(lblActionPick);

            _cmbPreviewAction = new ComboBox
            {
                Location = new Point(8, 68),
                Width = 340,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(24, 24, 26),
                ForeColor = Color.Gainsboro
            };
            _cmbPreviewAction.SelectedIndexChanged += (s, e) =>
            {
                if (_cmbPreviewAction.SelectedIndex >= 0)
                {
                    _activeActionIdx = _cmbPreviewAction.SelectedIndex;
                    UpdatePreview();
                }
            };
            pnl.Controls.Add(_cmbPreviewAction);

            _picPreview = new PictureBox
            {
                Location = new Point(8, 98),
                Size = new Size(340, 260),
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(20, 20, 22)
            };
            _picPreview.Paint += PicPreview_Paint;
            pnl.Controls.Add(_picPreview);

            // Direction selector: 5 buttons [0: Sud] [1: S-O] [2: Ouest] [3: N-O] [4: Nord]
            var lblDir = new Label
            {
                Text = "Angle UO (direction) :",
                Location = new Point(8, 364),
                AutoSize = true,
                ForeColor = Color.DarkGray
            };
            pnl.Controls.Add(lblDir);

            string[] dirNames = { "0: Sud", "1: S-O", "2: Ouest", "3: N-O", "4: Nord" };
            _btnDirs = new Button[5];
            int btnW = 63;
            int btnH = 24;
            for (int d = 0; d < 5; d++)
            {
                int dirIdx = d;
                var b = new Button
                {
                    Text = dirNames[d],
                    Location = new Point(8 + d * 69, 384),
                    Width = btnW,
                    Height = btnH,
                    FlatStyle = FlatStyle.Flat,
                    ForeColor = Color.Gainsboro,
                    BackColor = (d == _previewDir) ? Color.FromArgb(40, 100, 150) : Color.FromArgb(50, 50, 55),
                    Font = new Font("Segoe UI", 7.5f)
                };
                b.Click += (s, e) =>
                {
                    _previewDir = dirIdx;
                    for (int i = 0; i < 5; i++)
                        _btnDirs[i].BackColor = (i == _previewDir) ? Color.FromArgb(40, 100, 150) : Color.FromArgb(50, 50, 55);
                    UpdatePreview();
                };
                _btnDirs[d] = b;
                pnl.Controls.Add(b);
            }

            // Timeline / scrubber
            _btnPlayPause = new Button
            {
                Text = "▶ Play",
                Location = new Point(8, 414),
                Width = 60,
                Height = 24,
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(50, 70, 55),
                ForeColor = Color.LightGreen,
                Font = new Font("Segoe UI", 8f)
            };
            _btnPlayPause.Click += (s, e) => TogglePlay();
            pnl.Controls.Add(_btnPlayPause);

            _tbTime = new TrackBar
            {
                Location = new Point(72, 414),
                Width = 224,
                Height = 26,
                Minimum = 0,
                Maximum = 100,
                TickFrequency = 10,
                Value = 0
            };
            _tbTime.Scroll += (s, e) =>
            {
                _previewTimeNorm = _tbTime.Value / 100f;
                _lblTime.Text = $"{_tbTime.Value}%";
                UpdatePreview();
            };
            pnl.Controls.Add(_tbTime);

            _lblTime = new Label
            {
                Text = "0%",
                Location = new Point(300, 418),
                Width = 48,
                ForeColor = Color.Gainsboro,
                Font = new Font("Segoe UI", 8f)
            };
            pnl.Controls.Add(_lblTime);

            _tmrAnim = new System.Windows.Forms.Timer { Interval = 100 };
            _tmrAnim.Tick += (s, e) =>
            {
                _previewTimeNorm = (_previewTimeNorm + 0.08f) % 1.0f;
                int val = (int)(_previewTimeNorm * 100);
                if (val >= 0 && val <= 100) _tbTime.Value = val;
                _lblTime.Text = $"{val}%";
                UpdatePreview();
            };

            // Action details label
            _lblActionDetail = new Label
            {
                Location = new Point(8, 444),
                Size = new Size(340, 30),
                ForeColor = Color.LightSkyBlue,
                Font = new Font("Segoe UI", 8f),
                Text = "Action: 00"
            };
            pnl.Controls.Add(_lblActionDetail);

            // Validation & Role Info Card
            var grpInfo = new GroupBox
            {
                Text = "Rôles & Découpage Cutout",
                Location = new Point(8, 478),
                Size = new Size(340, 140),
                ForeColor = Color.Gainsboro,
                Font = new Font("Segoe UI", 8f)
            };

            _lblRoleSummary = new Label
            {
                Location = new Point(8, 18),
                Size = new Size(324, 76),
                ForeColor = Color.LightGray,
                Font = new Font("Segoe UI", 7.5f),
                Text = cutoutInfo ?? "Rendu normal de tous les meshes visibles."
            };
            grpInfo.Controls.Add(_lblRoleSummary);

            _lblWarning = new Label
            {
                Location = new Point(8, 96),
                Size = new Size(324, 38),
                ForeColor = hasWarning ? Color.FromArgb(255, 120, 100) : Color.FromArgb(160, 220, 140),
                Font = new Font("Segoe UI", 7.5f, FontStyle.Bold),
                Text = warningText ?? (hasWarning ? "⚠️ Attention aux rôles !" : "✓ Rôles configurés pour l'export")
            };
            grpInfo.Controls.Add(_lblWarning);

            pnl.Controls.Add(grpInfo);
        }

        private void TogglePlay()
        {
            _isPlaying = !_isPlaying;
            if (_isPlaying)
            {
                _btnPlayPause.Text = "⏸ Pause";
                _btnPlayPause.BackColor = Color.FromArgb(80, 50, 50);
                _btnPlayPause.ForeColor = Color.OrangeRed;
                _tmrAnim?.Start();
            }
            else
            {
                _btnPlayPause.Text = "▶ Play";
                _btnPlayPause.BackColor = Color.FromArgb(50, 70, 55);
                _btnPlayPause.ForeColor = Color.LightGreen;
                _tmrAnim?.Stop();
            }
        }

        private void PicPreview_Paint(object sender, PaintEventArgs e)
        {
            var g = e.Graphics;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;

            int box = 14;
            var c1 = Color.FromArgb(24, 24, 28);
            var c2 = Color.FromArgb(34, 34, 40);
            using (var b1 = new SolidBrush(c1))
            using (var b2 = new SolidBrush(c2))
            {
                for (int y = 0; y < _picPreview.Height; y += box)
                {
                    for (int x = 0; x < _picPreview.Width; x += box)
                    {
                        var b = ((x / box + y / box) % 2 == 0) ? b1 : b2;
                        g.FillRectangle(b, x, y, box, box);
                    }
                }
            }

            if (_previewBmp != null)
            {
                int drawW = _previewBmp.Width;
                int drawH = _previewBmp.Height;
                float scale = 1f;
                if (drawW > _picPreview.Width || drawH > _picPreview.Height)
                {
                    scale = Math.Min((float)_picPreview.Width / drawW, (float)_picPreview.Height / drawH);
                }
                else if (drawW * 2 <= _picPreview.Width && drawH * 2 <= _picPreview.Height)
                {
                    scale = 2f; // Pixel-perfect 2x zoom for smaller sprites
                }
                int w = (int)(drawW * scale);
                int h = (int)(drawH * scale);
                int cx = (_picPreview.Width - w) / 2;
                int cy = (_picPreview.Height - h) / 2;
                g.DrawImage(_previewBmp, cx, cy, w, h);
            }
            else
            {
                using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                using var brush = new SolidBrush(Color.DarkGray);
                g.DrawString("Sélectionnez une action\npour voir l'aperçu", Font, brush, _picPreview.ClientRectangle, sf);
            }
        }

        private void UpdatePreview()
        {
            if (_previewRenderer == null) return;
            int a = _activeActionIdx;
            if (a < 0 || a >= _combos.Count) return;

            var sources = ActiveSources();
            int sel = _combos[a].SelectedIndex - 1;
            string path = (sel >= 0 && sel < sources.Count) ? sources[sel] : null;

            Vector3 off = Vector3.Zero;
            if (a < _nudX.Count && a < _nudY.Count && a < _nudZ.Count)
            {
                off = new Vector3((float)_nudX[a].Value, (float)_nudY[a].Value, (float)_nudZ[a].Value);
            }

            float t = _previewTimeNorm;
            int dir = _previewDir;
            int size = (int)_nudSize.Value;

            try
            {
                var bmp = _previewRenderer(path, dir, t, off, size);
                var old = _previewBmp;
                _previewBmp = bmp;
                old?.Dispose();
                _picPreview?.Invalidate();

                if (_lblActionDetail != null)
                {
                    string clipName = !string.IsNullOrEmpty(path) ? Path.GetFileName(path) : "(aucune anim)";
                    _lblActionDetail.Text = $"Action {a:D2} : {clipName}\nOffset 3D : ({off.X:F2}, {off.Y:F2}, {off.Z:F2})";
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"UpdatePreview failed: {ex.Message}");
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _tmrAnim?.Stop();
            _tmrAnim?.Dispose();
            _tmrAnim = null;
            _previewBmp?.Dispose();
            _previewBmp = null;
            base.OnFormClosing(e);
        }

        // Currently offered sources: the per-model set, or every clip when "ALL" is on.
        private List<string> ActiveSources() => _useAll ? _allSources : _sources;
        private List<string> ActiveNames() => _useAll ? _allNames : _sourceNames;
        private List<string> ActiveDisplay() => _useAll ? _allDisplay : _sourceNames;
        private string ActivePrefix() => _useAll ? string.Empty : _stripPrefix;

        // Capture currently chosen clip paths and 3D offsets so target switches or map switches
        // preserve the user's manual adjustments.
        private (string Path, Vector3 Offset)[] CaptureSelection()
        {
            var from = ActiveSources();
            var keep = new (string, Vector3)[_combos.Count];
            for (int i = 0; i < _combos.Count; i++)
            {
                var cb = _combos[i];
                int idx = (int)cb.Tag;
                int sel = cb.SelectedIndex - 1;
                string path = (sel >= 0 && sel < from.Count) ? from[sel] : null;
                Vector3 off = Vector3.Zero;
                if (i < _nudX.Count && i < _nudY.Count && i < _nudZ.Count)
                {
                    off = new Vector3((float)_nudX[i].Value, (float)_nudY[i].Value, (float)_nudZ[i].Value);
                }
                if (idx >= 0 && idx < keep.Length)
                    keep[idx] = (path, off);
            }
            return keep;
        }

        private void OnChooseMapClicked()
        {
            using (var dlg = new VdChooseMapDialog(_basePath))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK || dlg.SelectedModel == null)
                    return;

                var entry = dlg.SelectedModel;
                var newSources = new List<string>();
                Granny3DControl.CollectAnims(entry.Folder, entry.Prefix, newSources, "*.grn");
                Granny3DControl.CollectAnims(entry.Folder, entry.Prefix, newSources, "*.anim");

                if (newSources.Count == 0)
                {
                    MessageBox.Show(this,
                        $"Aucune animation trouvée pour '{entry.DisplayName}' dans :\n{entry.Folder}",
                        "Choose map", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                var keep = CaptureSelection();

                _sources.Clear();
                _sources.AddRange(newSources);
                _sourceNames.Clear();
                _sourceNames.AddRange(_sources.Select(Path.GetFileNameWithoutExtension));
                _stripPrefix = Normalize(entry.Prefix ?? string.Empty);

                if (_useAll)
                {
                    _useAll = false;
                    _btnAll.Text = "ALL anim (tout)";
                    _btnAll.BackColor = Color.FromArgb(60, 60, 62);
                }

                _btnChooseMap.Text = $"Map: {entry.DisplayName}";
                _btnChooseMap.BackColor = Color.FromArgb(80, 96, 70);

                // Auto prefill target actions from the chosen model's animation set, but keep existing X, Y, Z offsets!
                RebuildRows(preserve: keep, preserveClips: false);
            }
        }

        // Flip between the current model's clips and every clip found in all folders.
        private void ToggleAllSources()
        {
            if (_allSources.Count == 0) return;
            var keep = CaptureSelection();          // capture against the CURRENT list
            _useAll = !_useAll;
            _btnAll.Text = _useAll ? "ALL anim (tout) *" : "ALL anim (tout)";
            _btnAll.BackColor = _useAll ? Color.FromArgb(80, 96, 70) : Color.FromArgb(60, 60, 62);
            RebuildRows(keep, preserveClips: true);
        }

        // preserve == null -> auto prefill from action names; otherwise keep the
        // given clip paths where they still exist in the offered set.
        private void RebuildRows((string Path, Vector3 Offset)[] preserve = null, bool preserveClips = true)
        {
            _rows.SuspendLayout();
            _rows.Controls.Clear();
            _combos.Clear();
            _nudX.Clear();
            _nudY.Clear();
            _nudZ.Clear();

            var target = _cmbTarget.SelectedItem as VdTargetDef;
            if (target == null) { _rows.ResumeLayout(); return; }

            // Sync preview action dropdown
            if (_cmbPreviewAction != null)
            {
                _cmbPreviewAction.BeginUpdate();
                _cmbPreviewAction.Items.Clear();
                for (int i = 0; i < target.ActionCount; i++)
                {
                    string actionName = (i < target.ActionNames.Length) ? target.ActionNames[i] : "";
                    _cmbPreviewAction.Items.Add($"{i:D2} {actionName}");
                }
                _cmbPreviewAction.EndUpdate();
                if (_cmbPreviewAction.Items.Count > 0)
                {
                    _cmbPreviewAction.SelectedIndex = Math.Min(_activeActionIdx, _cmbPreviewAction.Items.Count - 1);
                }
            }

            var sources = ActiveSources();
            var display = ActiveDisplay();
            var options = new List<string> { "--- (aucune) ---" };
            options.AddRange(display);

            int y = 6;
            for (int i = 0; i < target.ActionCount; i++)
            {
                int actIdx = i;
                string actionName = (i < target.ActionNames.Length) ? target.ActionNames[i] : "";
                string lbl = $"{i:D2} {actionName}";

                _rows.Controls.Add(new Label
                {
                    Text = lbl,
                    Location = new Point(6, y + 3),
                    Width = 140,
                    ForeColor = Color.Gainsboro
                });

                var cb = new ComboBox
                {
                    Location = new Point(148, y),
                    Width = 265,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    BackColor = Color.FromArgb(30, 30, 30),
                    ForeColor = Color.Gainsboro
                };
                cb.Items.AddRange(options.ToArray());
                cb.Tag = i;

                int keep = (preserveClips && preserve != null && i < preserve.Length && preserve[i].Path != null)
                    ? sources.FindIndex(p => string.Equals(p, preserve[i].Path, StringComparison.OrdinalIgnoreCase)) + 1
                    : 0;
                cb.SelectedIndex = keep > 0 ? keep : PrefillIndex(i, actionName, target);

                cb.SelectedIndexChanged += (s, e) =>
                {
                    if (_activeActionIdx == actIdx) UpdatePreview();
                };
                cb.Enter += (s, e) =>
                {
                    if (_cmbPreviewAction != null && _cmbPreviewAction.SelectedIndex != actIdx && actIdx < _cmbPreviewAction.Items.Count)
                    {
                        _cmbPreviewAction.SelectedIndex = actIdx;
                    }
                };

                _combos.Add(cb);
                _rows.Controls.Add(cb);

                // 3D Offset controls (X, Y, Z)
                var ctxMenu = CreateOffsetMenu(i);

                float initX = (preserve != null && i < preserve.Length) ? preserve[i].Offset.X : 0f;
                float initY = (preserve != null && i < preserve.Length) ? preserve[i].Offset.Y : 0f;
                float initZ = (preserve != null && i < preserve.Length) ? preserve[i].Offset.Z : 0f;

                // Label X
                _rows.Controls.Add(new Label { Text = "X:", Location = new Point(418, y + 3), Width = 16, ForeColor = Color.DarkGray });
                var nx = new NumericUpDown
                {
                    Location = new Point(434, y),
                    Width = 56,
                    Minimum = -50m,
                    Maximum = 50m,
                    DecimalPlaces = 2,
                    Increment = 0.05m,
                    Value = (decimal)initX,
                    BackColor = Color.FromArgb(30, 30, 30),
                    ForeColor = Color.Gainsboro,
                    BorderStyle = BorderStyle.FixedSingle,
                    ContextMenuStrip = ctxMenu
                };
                nx.ValueChanged += (s, e) => { if (_activeActionIdx == actIdx) UpdatePreview(); };
                _nudX.Add(nx);
                _rows.Controls.Add(nx);

                // Label Y
                _rows.Controls.Add(new Label { Text = "Y:", Location = new Point(494, y + 3), Width = 16, ForeColor = Color.DarkGray });
                var ny = new NumericUpDown
                {
                    Location = new Point(510, y),
                    Width = 56,
                    Minimum = -50m,
                    Maximum = 50m,
                    DecimalPlaces = 2,
                    Increment = 0.05m,
                    Value = (decimal)initY,
                    BackColor = Color.FromArgb(30, 30, 30),
                    ForeColor = Color.Gainsboro,
                    BorderStyle = BorderStyle.FixedSingle,
                    ContextMenuStrip = ctxMenu
                };
                ny.ValueChanged += (s, e) => { if (_activeActionIdx == actIdx) UpdatePreview(); };
                _nudY.Add(ny);
                _rows.Controls.Add(ny);

                // Label Z
                _rows.Controls.Add(new Label { Text = "Z:", Location = new Point(570, y + 3), Width = 16, ForeColor = Color.DarkGray });
                var nz = new NumericUpDown
                {
                    Location = new Point(586, y),
                    Width = 56,
                    Minimum = -50m,
                    Maximum = 50m,
                    DecimalPlaces = 2,
                    Increment = 0.05m,
                    Value = (decimal)initZ,
                    BackColor = Color.FromArgb(30, 30, 30),
                    ForeColor = Color.Gainsboro,
                    BorderStyle = BorderStyle.FixedSingle,
                    ContextMenuStrip = ctxMenu
                };
                nz.ValueChanged += (s, e) => { if (_activeActionIdx == actIdx) UpdatePreview(); };
                _nudZ.Add(nz);
                _rows.Controls.Add(nz);

                y += 28;
            }

            _rows.ResumeLayout();
            UpdatePreview();
        }

        private ContextMenuStrip CreateOffsetMenu(int actionIdx)
        {
            var menu = new ContextMenuStrip();
            var itemMount = menu.Items.Add("Appliquer ce (X, Y, Z) à toutes les actions montées");
            itemMount.Click += (s, e) =>
            {
                if (actionIdx < 0 || actionIdx >= _nudX.Count) return;
                decimal vx = _nudX[actionIdx].Value;
                decimal vy = _nudY[actionIdx].Value;
                decimal vz = _nudZ[actionIdx].Value;
                var target = _cmbTarget.SelectedItem as VdTargetDef;
                for (int i = 0; i < _combos.Count && i < _nudX.Count; i++)
                {
                    string name = (target != null && i < target.ActionNames.Length) ? target.ActionNames[i] : "";
                    if (IsMountAction(i, name, target))
                    {
                        _nudX[i].Value = vx;
                        _nudY[i].Value = vy;
                        _nudZ[i].Value = vz;
                    }
                }
            };

            var itemAll = menu.Items.Add("Appliquer ce (X, Y, Z) à TOUTES les actions");
            itemAll.Click += (s, e) =>
            {
                if (actionIdx < 0 || actionIdx >= _nudX.Count) return;
                decimal vx = _nudX[actionIdx].Value;
                decimal vy = _nudY[actionIdx].Value;
                decimal vz = _nudZ[actionIdx].Value;
                for (int i = 0; i < _combos.Count && i < _nudX.Count; i++)
                {
                    _nudX[i].Value = vx;
                    _nudY[i].Value = vy;
                    _nudZ[i].Value = vz;
                }
            };

            var itemReset = menu.Items.Add("Réinitialiser ce décalage à 0");
            itemReset.Click += (s, e) =>
            {
                if (actionIdx >= 0 && actionIdx < _nudX.Count)
                {
                    _nudX[actionIdx].Value = 0;
                    _nudY[actionIdx].Value = 0;
                    _nudZ[actionIdx].Value = 0;
                }
            };

            return menu;
        }

        public void ApplyMountZ(float val)
        {
            var target = _cmbTarget.SelectedItem as VdTargetDef;
            if (target == null) return;
            for (int i = 0; i < _combos.Count && i < _nudZ.Count; i++)
            {
                string actionName = (i < target.ActionNames.Length) ? target.ActionNames[i] : "";
                if (IsMountAction(i, actionName, target))
                {
                    _nudZ[i].Value = (decimal)val;
                }
            }
        }

        public void ResetAllOffsets()
        {
            for (int i = 0; i < _nudX.Count; i++) _nudX[i].Value = 0;
            for (int i = 0; i < _nudY.Count; i++) _nudY[i].Value = 0;
            for (int i = 0; i < _nudZ.Count; i++) _nudZ[i].Value = 0;
        }

        private static bool IsMountAction(int actionIndex, string actionName, VdTargetDef target)
        {
            if (actionName.IndexOf("Horse", StringComparison.OrdinalIgnoreCase) >= 0 ||
                actionName.IndexOf("Mount", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            if (target != null && target.ActionCount == 35 && actionIndex >= 23 && actionIndex <= 29)
                return true;

            if (target != null && target.Uop && actionIndex >= 29 && actionIndex <= 31)
                return true;

            return false;
        }

        // Returns the combo index (0 = none) best matching the target action name.
        // Priority: per-target alias -> exact name (after stripping the creature
        // prefix) -> best prefix match.
        private int PrefillIndex(int actionIndex, string actionName, VdTargetDef target)
        {
            var names = ActiveNames();
            if (names.Count == 0) return 0;
            string strip = ActivePrefix();

            // Normalized source names with the creature prefix removed.
            var norm = new string[names.Count];
            for (int i = 0; i < names.Count; i++)
            {
                string s = Normalize(names[i]);
                if (strip.Length > 0 && s.StartsWith(strip))
                    s = s.Substring(strip.Length);
                norm[i] = s;
            }

            // 1) Explicit alias for this slot.
            if (target?.Aliases != null && target.Aliases.TryGetValue(actionIndex, out string aliasKey))
            {
                string key = Normalize(aliasKey);
                for (int i = 0; i < norm.Length; i++)
                    if (norm[i] == key) return i + 1;
                for (int i = 0; i < norm.Length; i++)
                    if (norm[i].StartsWith(key)) return i + 1;
            }

            if (string.IsNullOrEmpty(actionName)) return 0;
            string tgt = Normalize(actionName);

            // 2) Exact match on the stripped name (disambiguates Run_01 vs Horse_Run_01).
            for (int i = 0; i < norm.Length; i++)
                if (norm[i] == tgt) return i + 1;

            // 2b) In "ALL" mode names keep their creature prefix (e.g. "h_male_idle"
            // for "Idle"), so also accept a trailing-token match.
            if (_useAll)
                for (int i = 0; i < norm.Length; i++)
                    if (norm[i].Length > tgt.Length && norm[i].EndsWith(tgt, StringComparison.Ordinal))
                        return i + 1;

            // 3) Closest by common prefix length (tie -> shortest extra).
            int bestIdx = 0, bestPref = 0, bestExtra = int.MaxValue;
            for (int i = 0; i < norm.Length; i++)
            {
                int p = CommonPrefixLen(norm[i], tgt);
                int extra = Math.Abs(norm[i].Length - tgt.Length);
                if (p > bestPref || (p == bestPref && p >= 5 && extra < bestExtra))
                {
                    bestPref = p; bestExtra = extra; bestIdx = i + 1;
                }
            }
            return bestPref >= 5 ? bestIdx : 0;
        }

        private static int CommonPrefixLen(string a, string b)
        {
            int n = Math.Min(a.Length, b.Length), i = 0;
            while (i < n && a[i] == b[i]) i++;
            return i;
        }

        private static string Normalize(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        // action index -> full source path, or null for none.
        public string[] GetMapping()
        {
            var target = _cmbTarget.SelectedItem as VdTargetDef;
            int n = target?.ActionCount ?? 0;
            var sources = ActiveSources();
            var result = new string[n];
            foreach (var cb in _combos)
            {
                int idx = (int)cb.Tag;
                if (idx < 0 || idx >= n) continue;
                int sel = cb.SelectedIndex - 1;
                result[idx] = (sel >= 0 && sel < sources.Count) ? sources[sel] : null;
            }
            return result;
        }

        // action index -> 3D translation offset (X, Y, Z) in model world coordinates.
        public Vector3[] GetOffsets()
        {
            var target = _cmbTarget.SelectedItem as VdTargetDef;
            int n = target?.ActionCount ?? 0;
            var result = new Vector3[n];
            for (int i = 0; i < n && i < _nudX.Count && i < _nudY.Count && i < _nudZ.Count; i++)
            {
                result[i] = new Vector3(
                    (float)_nudX[i].Value,
                    (float)_nudY[i].Value,
                    (float)_nudZ[i].Value);
            }
            return result;
        }

        private void SaveMappingDialog()
        {
            using var sfd = new SaveFileDialog
            {
                Title = "Sauvegarder le mapping d'animations",
                Filter = "Mapping VD (*.vdmap;*.json)|*.vdmap;*.json|Tous les fichiers (*.*)|*.*",
                DefaultExt = "vdmap",
                FileName = $"mapping_{Normalize(_cmbTarget.SelectedItem?.ToString() ?? "anim")}.vdmap"
            };

            if (sfd.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                var target = _cmbTarget.SelectedItem as VdTargetDef;
                var sources = ActiveSources();
                var preset = new VdMappingPreset
                {
                    TargetName = target?.Key ?? target?.Label,
                    TargetActionCount = target?.ActionCount ?? _combos.Count,
                    Fps = (int)_nudFps.Value,
                    Size = (int)_nudSize.Value,
                    OffsetY = (int)_nudOffY.Value
                };

                for (int i = 0; i < _combos.Count; i++)
                {
                    var cb = _combos[i];
                    int actIdx = (int)cb.Tag;
                    int sel = cb.SelectedIndex - 1;
                    string path = (sel >= 0 && sel < sources.Count) ? sources[sel] : null;
                    string actionName = (target != null && actIdx < target.ActionNames.Length) ? target.ActionNames[actIdx] : $"Action {actIdx:D2}";
                    float ox = (i < _nudX.Count) ? (float)_nudX[i].Value : 0f;
                    float oy = (i < _nudY.Count) ? (float)_nudY[i].Value : 0f;
                    float oz = (i < _nudZ.Count) ? (float)_nudZ[i].Value : 0f;

                    preset.Actions.Add(new VdActionMapItem
                    {
                        ActionIndex = actIdx,
                        ActionName = actionName,
                        AnimPath = path,
                        AnimFileName = !string.IsNullOrEmpty(path) ? Path.GetFileName(path) : null,
                        OffsetX = ox,
                        OffsetY = oy,
                        OffsetZ = oz
                    });
                }

                string json = JsonSerializer.Serialize(preset, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(sfd.FileName, json, Encoding.UTF8);
                MessageBox.Show(this, $"Mapping sauvegardé avec succès ({preset.Actions.Count} actions) :\n{Path.GetFileName(sfd.FileName)}",
                    "Sauvegarde mapping", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Erreur lors de la sauvegarde : {ex.Message}", "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void LoadMappingDialog()
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Charger un mapping d'animations",
                Filter = "Mapping VD (*.vdmap;*.json)|*.vdmap;*.json|Tous les fichiers (*.*)|*.*",
                DefaultExt = "vdmap"
            };

            if (ofd.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                string json = File.ReadAllText(ofd.FileName, Encoding.UTF8);
                var preset = JsonSerializer.Deserialize<VdMappingPreset>(json);
                if (preset == null) throw new InvalidOperationException("Fichier mapping invalide ou vide.");

                // 1) Match target type if needed
                if (!string.IsNullOrEmpty(preset.TargetName))
                {
                    foreach (var item in _cmbTarget.Items)
                    {
                        if (item is VdTargetDef td && (string.Equals(td.Key, preset.TargetName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(td.Label, preset.TargetName, StringComparison.OrdinalIgnoreCase) ||
                            (preset.TargetActionCount > 0 && td.ActionCount == preset.TargetActionCount)))
                        {
                            if (_cmbTarget.SelectedItem != td)
                            {
                                _cmbTarget.SelectedItem = td;
                            }
                            break;
                        }
                    }
                }

                if (preset.Fps >= _nudFps.Minimum && preset.Fps <= _nudFps.Maximum) _nudFps.Value = preset.Fps;
                if (preset.Size >= _nudSize.Minimum && preset.Size <= _nudSize.Maximum) _nudSize.Value = preset.Size;
                if (preset.OffsetY >= _nudOffY.Minimum && preset.OffsetY <= _nudOffY.Maximum) _nudOffY.Value = preset.OffsetY;

                var sources = ActiveSources();
                int matchedCount = 0;

                // Check if we need to switch to "ALL anim" mode if some files exist only in _allSources
                if (!_useAll && preset.Actions != null)
                {
                    bool needAll = false;
                    foreach (var a in preset.Actions)
                    {
                        if (!string.IsNullOrEmpty(a.AnimFileName) && !sources.Any(s => string.Equals(Path.GetFileName(s), a.AnimFileName, StringComparison.OrdinalIgnoreCase)))
                        {
                            if (_allSources.Any(s => string.Equals(Path.GetFileName(s), a.AnimFileName, StringComparison.OrdinalIgnoreCase)))
                            {
                                needAll = true;
                                break;
                            }
                        }
                    }
                    if (needAll)
                    {
                        ToggleAllSources();
                        sources = ActiveSources();
                    }
                }

                if (preset.Actions != null)
                {
                    foreach (var item in preset.Actions)
                    {
                        int actIdx = item.ActionIndex;
                        if (actIdx < 0 || actIdx >= _combos.Count) continue;

                        int foundIdx = -1;
                        if (!string.IsNullOrEmpty(item.AnimPath))
                            foundIdx = sources.FindIndex(s => string.Equals(s, item.AnimPath, StringComparison.OrdinalIgnoreCase));
                        if (foundIdx < 0 && !string.IsNullOrEmpty(item.AnimFileName))
                            foundIdx = sources.FindIndex(s => string.Equals(Path.GetFileName(s), item.AnimFileName, StringComparison.OrdinalIgnoreCase));
                        if (foundIdx < 0 && !string.IsNullOrEmpty(item.AnimFileName))
                        {
                            string norm = Normalize(Path.GetFileNameWithoutExtension(item.AnimFileName));
                            foundIdx = sources.FindIndex(s => Normalize(Path.GetFileNameWithoutExtension(s)) == norm);
                        }

                        if (foundIdx >= 0)
                        {
                            _combos[actIdx].SelectedIndex = foundIdx + 1;
                            matchedCount++;
                        }
                        else if (string.IsNullOrEmpty(item.AnimPath) && string.IsNullOrEmpty(item.AnimFileName))
                        {
                            _combos[actIdx].SelectedIndex = 0;
                        }

                        if (actIdx < _nudX.Count) _nudX[actIdx].Value = Math.Max(_nudX[actIdx].Minimum, Math.Min(_nudX[actIdx].Maximum, (decimal)item.OffsetX));
                        if (actIdx < _nudY.Count) _nudY[actIdx].Value = Math.Max(_nudY[actIdx].Minimum, Math.Min(_nudY[actIdx].Maximum, (decimal)item.OffsetY));
                        if (actIdx < _nudZ.Count) _nudZ[actIdx].Value = Math.Max(_nudZ[actIdx].Minimum, Math.Min(_nudZ[actIdx].Maximum, (decimal)item.OffsetZ));
                    }
                }

                UpdatePreview();
                MessageBox.Show(this, $"Mapping chargé avec succès !\n{matchedCount} animations associées sur {_combos.Count} actions.",
                    "Chargement mapping", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Erreur lors du chargement : {ex.Message}", "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ResetMapping()
        {
            var target = _cmbTarget.SelectedItem as VdTargetDef;
            if (target == null) return;

            for (int i = 0; i < _combos.Count; i++)
            {
                string actionName = (i < target.ActionNames.Length) ? target.ActionNames[i] : "";
                _combos[i].SelectedIndex = PrefillIndex(i, actionName, target);
            }
            ResetAllOffsets();
            UpdatePreview();
        }

        private void ClearAllMapping()
        {
            for (int i = 0; i < _combos.Count; i++)
            {
                _combos[i].SelectedIndex = 0;
            }
            ResetAllOffsets();
            UpdatePreview();
        }
    }

    public class VdActionMapItem
    {
        public int ActionIndex { get; set; }
        public string ActionName { get; set; }
        public string AnimPath { get; set; }
        public string AnimFileName { get; set; }
        public float OffsetX { get; set; }
        public float OffsetY { get; set; }
        public float OffsetZ { get; set; }
    }

    public class VdMappingPreset
    {
        public string TargetName { get; set; }
        public int TargetActionCount { get; set; }
        public int Fps { get; set; } = 10;
        public int Size { get; set; } = 0;
        public int OffsetY { get; set; } = 0;
        public List<VdActionMapItem> Actions { get; set; } = new List<VdActionMapItem>();
    }
}
