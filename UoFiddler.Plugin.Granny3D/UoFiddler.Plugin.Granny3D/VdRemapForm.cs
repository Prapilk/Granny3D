using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace UoFiddler.Plugin.Granny3D
{
    // Dialog replicating UOFiddler's "Export -> To vd.." remapper: pick the target
    // category, then bind each target action slot to a source Granny animation.
    // The slots are pre-filled by name (run -> run, walk -> walk, ...).
    public sealed class VdRemapForm : Form
    {
        private ComboBox _cmbTarget;
        private NumericUpDown _nudFps;
        private NumericUpDown _nudSize;
        private NumericUpDown _nudOffY;
        private Panel _rows;
        private readonly List<ComboBox> _combos = new List<ComboBox>();

        private readonly List<VdTargetDef> _targets;
        private readonly List<string> _sources;      // full .grn paths
        private readonly List<string> _sourceNames;  // display names
        private readonly string _stripPrefix;        // creature prefix to ignore when matching

        public VdTargetDef Target { get; private set; }
        public int Fps => (int)_nudFps.Value;
        public int FrameSize => (int)_nudSize.Value;
        public int OffsetY => (int)_nudOffY.Value;

        public VdRemapForm(List<VdTargetDef> targets, List<string> sources, string stripPrefix = null)
        {
            _targets = targets;
            _sources = sources;
            _sourceNames = sources.Select(Path.GetFileNameWithoutExtension).ToList();
            _stripPrefix = Normalize(stripPrefix ?? string.Empty);   // same normalization as source names

            Text = "Export .vd — mapping des animations";
            ClientSize = new Size(560, 620);
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
                Width = 250,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.Gainsboro
            };
            _cmbTarget.Items.AddRange(_targets.ToArray());
            _cmbTarget.SelectedIndexChanged += (s, e) => RebuildRows();
            top.Controls.Add(_cmbTarget);

            top.Controls.Add(new Label { Text = "Images/s :", Location = new Point(8, 42), Width = 70, ForeColor = Color.Gray });
            _nudFps = new NumericUpDown { Location = new Point(80, 38), Width = 60, Minimum = 2, Maximum = 30, Value = 10, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            top.Controls.Add(_nudFps);

            top.Controls.Add(new Label { Text = "Perso (px, 0=aperçu) :", Location = new Point(150, 42), Width = 150, ForeColor = Color.Gray });
            _nudSize = new NumericUpDown { Location = new Point(300, 38), Width = 60, Minimum = 0, Maximum = 512, Increment = 16, Value = 0, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            top.Controls.Add(_nudSize);

            top.Controls.Add(new Label { Text = "Décal. Y (px) :", Location = new Point(375, 42), Width = 95, ForeColor = Color.Gray });
            _nudOffY = new NumericUpDown { Location = new Point(470, 38), Width = 55, Minimum = -64, Maximum = 64, Increment = 1, Value = 0, BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.Gainsboro, BorderStyle = BorderStyle.FixedSingle };
            top.Controls.Add(_nudOffY);

            _rows = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(8) };

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, Height = 46, Padding = new Padding(8) };
            var ok = new Button { Text = "Exporter", Width = 100, Height = 28, BackColor = Color.FromArgb(70, 90, 70), ForeColor = Color.Gainsboro, FlatStyle = FlatStyle.Flat };
            var cancel = new Button { Text = "Annuler", Width = 100, Height = 28, BackColor = Color.FromArgb(60, 60, 62), ForeColor = Color.Gainsboro, FlatStyle = FlatStyle.Flat };
            ok.Click += (s, e) => { Target = _cmbTarget.SelectedItem as VdTargetDef; DialogResult = DialogResult.OK; };
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; };
            bottom.Controls.Add(ok);
            bottom.Controls.Add(cancel);

            Controls.Add(_rows);
            Controls.Add(bottom);
            Controls.Add(top);

            if (_cmbTarget.Items.Count > 0) _cmbTarget.SelectedIndex = 0;
        }

        private void RebuildRows()
        {
            _rows.SuspendLayout();
            _rows.Controls.Clear();
            _combos.Clear();

            var target = _cmbTarget.SelectedItem as VdTargetDef;
            if (target == null) { _rows.ResumeLayout(); return; }

            var options = new List<string> { "--- (aucune) ---" };
            options.AddRange(_sourceNames);

            int y = 6;
            for (int i = 0; i < target.ActionCount; i++)
            {
                string actionName = (i < target.ActionNames.Length) ? target.ActionNames[i] : "";
                string lbl = $"{i:D2} {actionName}";

                _rows.Controls.Add(new Label
                {
                    Text = lbl,
                    Location = new Point(6, y + 3),
                    Width = 190,
                    ForeColor = Color.Gainsboro
                });

                // Show duplicate-idle slots distinctly enough to still prefill.
                var cb = new ComboBox
                {
                    Location = new Point(200, y),
                    Width = 330,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    BackColor = Color.FromArgb(30, 30, 30),
                    ForeColor = Color.Gainsboro
                };
                cb.Items.AddRange(options.ToArray());
                cb.Tag = i;
                cb.SelectedIndex = PrefillIndex(i, actionName, target);
                _combos.Add(cb);
                _rows.Controls.Add(cb);

                y += 28;
            }

            _rows.ResumeLayout();
        }

        // Returns the combo index (0 = none) best matching the target action name.
        // Priority: per-target alias -> exact name (after stripping the creature
        // prefix) -> best prefix match.
        private int PrefillIndex(int actionIndex, string actionName, VdTargetDef target)
        {
            if (_sourceNames.Count == 0) return 0;

            // Normalized source names with the creature prefix removed.
            var norm = new string[_sourceNames.Count];
            for (int i = 0; i < _sourceNames.Count; i++)
            {
                string s = Normalize(_sourceNames[i]);
                if (_stripPrefix.Length > 0 && s.StartsWith(_stripPrefix))
                    s = s.Substring(_stripPrefix.Length);
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
            var result = new string[n];
            foreach (var cb in _combos)
            {
                int idx = (int)cb.Tag;
                if (idx < 0 || idx >= n) continue;
                int sel = cb.SelectedIndex - 1;
                result[idx] = (sel >= 0 && sel < _sources.Count) ? _sources[sel] : null;
            }
            return result;
        }
    }
}
