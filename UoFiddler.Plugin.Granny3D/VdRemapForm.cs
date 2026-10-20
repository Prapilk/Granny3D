using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
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

        public VdTargetDef Target { get; private set; }
        public int Fps => (int)_nudFps.Value;
        public int FrameSize => (int)_nudSize.Value;
        public int OffsetY => (int)_nudOffY.Value;

        public VdRemapForm(List<VdTargetDef> targets, List<string> sources, string stripPrefix = null,
            List<string> allSources = null, List<string> allDisplay = null, string basePath = null)
        {
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
            ClientSize = new Size(740, 640);
            MinimumSize = new Size(700, 480);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(40, 40, 44);
            ForeColor = Color.Gainsboro;
            MinimizeBox = false;
            MaximizeBox = false;
            FormBorderStyle = FormBorderStyle.Sizable;

            var top = new Panel { Dock = DockStyle.Top, Height = 74, Padding = new Padding(8) };

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
            top.Controls.Add(_nudSize);

            top.Controls.Add(new Label { Text = "Décal. Y px :", Location = new Point(262, 42), Width = 80, ForeColor = Color.Gray });
            _nudOffY = new NumericUpDown { Location = new Point(344, 38), Width = 55, Minimum = -64, Maximum = 64, Increment = 1, Value = 0, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
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

            var hdr = new Panel { Dock = DockStyle.Top, Height = 22, BackColor = Color.FromArgb(32, 32, 35), Padding = new Padding(8, 2, 8, 2) };
            hdr.Controls.Add(new Label { Text = "Action", Location = new Point(14, 4), Width = 130, ForeColor = Color.DarkGray, Font = new Font(Font.FontFamily, 8.25f, FontStyle.Bold) });
            hdr.Controls.Add(new Label { Text = "Animation source", Location = new Point(156, 4), Width = 250, ForeColor = Color.DarkGray, Font = new Font(Font.FontFamily, 8.25f, FontStyle.Bold) });
            hdr.Controls.Add(new Label { Text = "Décalage 3D X", Location = new Point(426, 4), Width = 74, ForeColor = Color.DarkGray, Font = new Font(Font.FontFamily, 8f, FontStyle.Bold) });
            hdr.Controls.Add(new Label { Text = "Décalage 3D Y", Location = new Point(502, 4), Width = 74, ForeColor = Color.DarkGray, Font = new Font(Font.FontFamily, 8f, FontStyle.Bold) });
            hdr.Controls.Add(new Label { Text = "Décalage 3D Z", Location = new Point(578, 4), Width = 74, ForeColor = Color.DarkGray, Font = new Font(Font.FontFamily, 8f, FontStyle.Bold) });

            _rows = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(8) };

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 46, Padding = new Padding(8) };
            var ok = new Button { Text = "Exporter", Width = 100, Height = 28, BackColor = Color.FromArgb(70, 90, 70), ForeColor = Color.Gainsboro, FlatStyle = FlatStyle.Flat };
            var cancel = new Button { Text = "Annuler", Width = 100, Height = 28, BackColor = Color.FromArgb(60, 60, 62), ForeColor = Color.Gainsboro, FlatStyle = FlatStyle.Flat };
            ok.Click += (s, e) => { Target = _cmbTarget.SelectedItem as VdTargetDef; DialogResult = DialogResult.OK; };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; };
            bottom.Controls.Add(ok);
            bottom.Controls.Add(cancel);

            Controls.Add(_rows);
            Controls.Add(hdr);
            Controls.Add(bottom);
            Controls.Add(top);

            if (_cmbTarget.Items.Count > 0) _cmbTarget.SelectedIndex = 0;
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

            var sources = ActiveSources();
            var display = ActiveDisplay();
            var options = new List<string> { "--- (aucune) ---" };
            options.AddRange(display);

            int y = 6;
            for (int i = 0; i < target.ActionCount; i++)
            {
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
                _nudZ.Add(nz);
                _rows.Controls.Add(nz);

                y += 28;
            }

            _rows.ResumeLayout();
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
    }
}
