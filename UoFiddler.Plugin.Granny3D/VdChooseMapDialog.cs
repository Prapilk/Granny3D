using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace UoFiddler.Plugin.Granny3D
{
    // Model / creature definition for prefilling the animation export mapping
    public sealed class ModelEntry
    {
        public string DisplayName { get; set; }   // e.g. "H_Male", "Walking_Dead_Zombie"
        public string Category { get; set; }      // e.g. "Humans", "Monsters"
        public string Folder { get; set; }        // Directory containing the model and animations
        public string MeshPath { get; set; }      // Full path to the reference LOD2 mesh
        public string Prefix { get; set; }        // Animation filename prefix, e.g. "H_Male_"

        public override string ToString() => DisplayName;
    }

    // Modal dialog to choose a reference model/creature (H_Male, H_Female, Zombie, etc.)
    // preserving the full category hierarchy (Humans, Monsters, Animals, Sea Creatures, Others).
    public sealed class VdChooseMapDialog : Form
    {
        private TextBox _txtSearch;
        private TreeView _tvModels;
        private Button _btnOk;
        private Button _btnCancel;

        private readonly string _basePath;
        private readonly List<(string Category, List<ModelEntry> Entries)> _data = new List<(string, List<ModelEntry>)>();

        public ModelEntry SelectedModel { get; private set; }

        public VdChooseMapDialog(string basePath = null)
        {
            _basePath = !string.IsNullOrEmpty(basePath) ? basePath : Granny3DControl.BasePath;

            Text = Localization.Tr("map_dlg_title", "Sélectionner un modèle ou créature");
            ClientSize = new Size(480, 560);
            MinimumSize = new Size(380, 420);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(40, 40, 44);
            ForeColor = Color.Gainsboro;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = false;

            BuildUi();
            LoadModelsData();
            ApplyFilter(null);
        }

        private void BuildUi()
        {
            var pnlTop = new Panel { Dock = DockStyle.Top, Height = 64, Padding = new Padding(10, 8, 10, 6) };

            var lblPrompt = new Label
            {
                Text = Localization.Tr("map_dlg_prompt", "Modèle ou créature de référence pour le mapping :"),
                Dock = DockStyle.Top,
                Height = 20,
                ForeColor = Color.Gainsboro
            };

            _txtSearch = new TextBox
            {
                Dock = DockStyle.Bottom,
                Height = 24,
                BackColor = Color.FromArgb(28, 28, 30),
                ForeColor = Color.Gainsboro,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font(Font.FontFamily, 9.5f)
            };
            _txtSearch.TextChanged += (s, e) => ApplyFilter(_txtSearch.Text);
            _txtSearch.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter && _btnOk.Enabled)
                {
                    e.SuppressKeyPress = true;
                    AcceptChoice();
                }
                else if (e.KeyCode == Keys.Down)
                {
                    _tvModels.Focus();
                }
            };

            pnlTop.Controls.Add(lblPrompt);
            pnlTop.Controls.Add(_txtSearch);

            _tvModels = new TreeView
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(28, 28, 30),
                ForeColor = Color.Gainsboro,
                LineColor = Color.FromArgb(80, 80, 85),
                BorderStyle = BorderStyle.FixedSingle,
                FullRowSelect = true,
                ShowLines = true,
                HideSelection = false,
                Font = new Font(Font.FontFamily, 9.5f)
            };
            _tvModels.AfterSelect += (s, e) =>
            {
                _btnOk.Enabled = (e.Node?.Tag is ModelEntry);
            };
            _tvModels.NodeMouseDoubleClick += (s, e) =>
            {
                if (e.Node?.Tag is ModelEntry)
                {
                    AcceptChoice();
                }
            };
            _tvModels.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter && _btnOk.Enabled)
                {
                    e.SuppressKeyPress = true;
                    AcceptChoice();
                }
            };

            var pnlCenter = new Panel { Dock = DockStyle.Fill, Padding = new Padding(10, 0, 10, 0) };
            pnlCenter.Controls.Add(_tvModels);

            var pnlBottom = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                Height = 48,
                FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(10, 8, 10, 8)
            };

            _btnCancel = new Button
            {
                Text = Localization.Tr("cancel", "Annuler"),
                Width = 90,
                Height = 28,
                BackColor = Color.FromArgb(60, 60, 62),
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat
            };
            _btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            _btnOk = new Button
            {
                Text = Localization.Tr("map_dlg_select", "Sélectionner"),
                Width = 105,
                Height = 28,
                BackColor = Color.FromArgb(70, 95, 75),
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat,
                Enabled = false
            };
            _btnOk.Click += (s, e) => AcceptChoice();

            var btnBrowseFile = new Button
            {
                Text = Localization.Tr("map_dlg_browse", "📁 Parcourir fichier .grn / .char..."),
                AutoSize = true,
                Height = 28,
                BackColor = Color.FromArgb(50, 70, 95),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btnBrowseFile.Click += (s, e) =>
            {
                using var ofd = new OpenFileDialog
                {
                    Title = Localization.Tr("map_dlg_browse", "Sélectionner un modèle ou squelette .grn / .char"),
                    Filter = "Fichiers Meshs / Squelettes (*.grn;*.char)|*.grn;*.char|Tous les fichiers (*.*)|*.*",
                    InitialDirectory = Directory.Exists(_basePath) ? _basePath : AppDomain.CurrentDomain.BaseDirectory
                };
                if (ofd.ShowDialog(this) == DialogResult.OK)
                {
                    string fn = Path.GetFileNameWithoutExtension(ofd.FileName);
                    SelectedModel = new ModelEntry
                    {
                        DisplayName = fn,
                        Category = "Custom",
                        Folder = Path.GetDirectoryName(ofd.FileName) ?? string.Empty,
                        MeshPath = ofd.FileName,
                        Prefix = fn + "_"
                    };
                    DialogResult = DialogResult.OK;
                    Close();
                }
            };

            pnlBottom.Controls.Add(_btnCancel);
            pnlBottom.Controls.Add(_btnOk);
            pnlBottom.Controls.Add(btnBrowseFile);

            Controls.Add(pnlCenter);
            Controls.Add(pnlBottom);
            Controls.Add(pnlTop);
        }

        private void AcceptChoice()
        {
            if (_tvModels.SelectedNode?.Tag is ModelEntry entry)
            {
                SelectedModel = entry;
                DialogResult = DialogResult.OK;
                Close();
            }
        }

        private void LoadModelsData()
        {
            _data.Clear();
            if (string.IsNullOrEmpty(_basePath) || !Directory.Exists(_basePath))
                return;

            var categories = new (string FolderName, string Display)[]
            {
                ("Humans", "Humans"),
                ("Monsters", "Monsters"),
                ("Animals", "Animals"),
                ("SeaCreatures", "Sea Creatures"),
                ("Others", "Others")
            };

            foreach (var (folderName, display) in categories)
            {
                string folder = Path.Combine(_basePath, folderName);
                if (!Directory.Exists(folder)) continue;

                var list = new List<ModelEntry>();

                if (string.Equals(folderName, "Others", StringComparison.OrdinalIgnoreCase))
                {
                    // For Others: look for imported characters (.char, .fbx) and custom .grn LOD meshes
                    try
                    {
                        var files = new List<string>();
                        files.AddRange(Directory.GetFiles(folder, "*.char", SearchOption.AllDirectories));
                        files.AddRange(Directory.GetFiles(folder, "*.fbx", SearchOption.AllDirectories));
                        foreach (var f in Directory.GetFiles(folder, "*.grn", SearchOption.AllDirectories))
                        {
                            string fn = Path.GetFileNameWithoutExtension(f);
                            if (fn.IndexOf("lod", StringComparison.OrdinalIgnoreCase) >= 0)
                                files.Add(f);
                        }

                        files.Sort(StringComparer.OrdinalIgnoreCase);
                        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var f in files)
                        {
                            string name = Path.GetFileNameWithoutExtension(f);
                            if (seen.Add(name))
                            {
                                list.Add(new ModelEntry
                                {
                                    DisplayName = name,
                                    Category = display,
                                    Folder = Path.GetDirectoryName(f),
                                    MeshPath = f,
                                    Prefix = name + "_"
                                });
                            }
                        }
                    }
                    catch { }
                }
                else
                {
                    // Scan standard creature LOD meshes
                    try
                    {
                        var map = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var f in Directory.GetFiles(folder, "*.grn", SearchOption.AllDirectories))
                        {
                            string fn = Path.GetFileNameWithoutExtension(f);
                            if (fn.IndexOf("lod", StringComparison.OrdinalIgnoreCase) < 0) continue;
                            string creature = Granny3DControl.DeriveCreature(fn);
                            if (string.IsNullOrEmpty(creature)) creature = fn;

                            // Prefer lod2
                            if (!map.ContainsKey(creature) || fn.IndexOf("lod2", StringComparison.OrdinalIgnoreCase) >= 0)
                                map[creature] = f;
                        }

                        foreach (var kvp in map)
                        {
                            string c = kvp.Key;
                            string pfx = c + "_";
                            if (c.StartsWith("H_Male", StringComparison.OrdinalIgnoreCase)) pfx = "H_Male_";
                            else if (c.StartsWith("H_Female", StringComparison.OrdinalIgnoreCase)) pfx = "H_Female_";

                            list.Add(new ModelEntry
                            {
                                DisplayName = c,
                                Category = display,
                                Folder = folder,
                                MeshPath = kvp.Value,
                                Prefix = pfx
                            });
                        }
                    }
                    catch { }
                }

                if (list.Count > 0)
                    _data.Add((display, list));
            }
        }

        private void ApplyFilter(string query)
        {
            string q = query?.Trim();
            bool isFilter = !string.IsNullOrEmpty(q);

            _tvModels.BeginUpdate();
            _tvModels.Nodes.Clear();

            TreeNode firstSelectable = null;

            foreach (var (category, entries) in _data)
            {
                var catNode = new TreeNode(category);
                bool catMatches = isFilter && category.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0;

                foreach (var entry in entries)
                {
                    if (!isFilter || catMatches || entry.DisplayName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var node = new TreeNode(entry.DisplayName) { Tag = entry };
                        catNode.Nodes.Add(node);
                        if (firstSelectable == null) firstSelectable = node;
                    }
                }

                if (catNode.Nodes.Count > 0)
                {
                    _tvModels.Nodes.Add(catNode);
                    if (isFilter || string.Equals(category, "Humans", StringComparison.OrdinalIgnoreCase))
                    {
                        catNode.Expand();
                    }
                }
            }

            if (firstSelectable != null)
            {
                _tvModels.SelectedNode = firstSelectable;
                _btnOk.Enabled = true;
            }
            else
            {
                _btnOk.Enabled = false;
            }

            _tvModels.EndUpdate();
        }
    }
}
