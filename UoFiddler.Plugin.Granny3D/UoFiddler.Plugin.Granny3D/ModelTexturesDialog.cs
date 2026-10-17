using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using OpenTK.Graphics.OpenGL;

namespace UoFiddler.Plugin.Granny3D
{
    /// <summary>
    /// Dialogue pour assigner ou modifier les textures (BaseColor / Diffuse) de chaque mesh d'un character importé (.char).
    /// </summary>
    public class ModelTexturesDialog : Form
    {
        private readonly string _charPath;
        private readonly GrannyModelFile _model;
        private readonly List<LoadedMesh>? _liveMeshes;
        private readonly string _initialFbxDir;
        private readonly string _othersFolder;
        private readonly Func<string, string, int>? _tryLoadTextureFunc;
        private readonly Action? _invalidateGlFunc;

        private DataGridView _dgv = null!;
        private PictureBox _pbPreview = null!;
        private Label _lblPreviewInfo = null!;
        private Button _btnApply = null!;
        private Button _btnCancel = null!;

        // Stocke les chemins en attente de validation : index part -> chemin de fichier choisi (ou "" pour suppression, null si inchangé)
        private readonly Dictionary<int, string?> _pendingTexturePaths = new();

        public ModelTexturesDialog(
            string charPath,
            GrannyModelFile model,
            List<LoadedMesh>? liveMeshes = null,
            string? initialFbxDir = null,
            Func<string, string, int>? tryLoadTextureFunc = null,
            Action? invalidateGlFunc = null)
        {
            _charPath = charPath;
            _model = model;
            _liveMeshes = liveMeshes;
            _initialFbxDir = initialFbxDir ?? string.Empty;
            _othersFolder = Path.GetDirectoryName(charPath) ?? string.Empty;
            _tryLoadTextureFunc = tryLoadTextureFunc;
            _invalidateGlFunc = invalidateGlFunc;

            InitializeComponentCustom();
            PopulateTable();
        }

        private void InitializeComponentCustom()
        {
            string charName = Path.GetFileNameWithoutExtension(_charPath);
            Text = $"Textures du Character : {charName}";
            Size = new Size(880, 520);
            MinimumSize = new Size(720, 400);
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.FromArgb(30, 30, 30);
            ForeColor = Color.Gainsboro;
            Font = new Font("Segoe UI", 9f);

            var pnlTop = new Panel
            {
                Dock = DockStyle.Top,
                Height = 58,
                BackColor = Color.FromArgb(36, 36, 36),
                Padding = new Padding(12, 8, 12, 6)
            };

            var lblTitle = new Label
            {
                Text = $"Modèle : {charName}  ({_model.Parts.Count} mesh{(_model.Parts.Count > 1 ? "es" : "")})",
                Font = new Font("Segoe UI", 10.5f, FontStyle.Bold),
                ForeColor = Color.White,
                Dock = DockStyle.Top,
                Height = 22
            };

            var lblSubtitle = new Label
            {
                Text = "Assignez une texture BaseColor / Diffuse (PNG, TGA, JPG, BMP) à chaque mesh du modèle, ou extrayez-les d'un FBX.",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = Color.Silver,
                Dock = DockStyle.Top,
                Height = 18
            };

            pnlTop.Controls.Add(lblSubtitle);
            pnlTop.Controls.Add(lblTitle);

            // Panneau droit : aperçu de texture
            var pnlRight = new Panel
            {
                Dock = DockStyle.Right,
                Width = 200,
                BackColor = Color.FromArgb(26, 26, 26),
                Padding = new Padding(10)
            };

            var lblPreviewHeader = new Label
            {
                Text = "Aperçu de la texture :",
                ForeColor = Color.Gainsboro,
                Dock = DockStyle.Top,
                Height = 22,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };

            _pbPreview = new PictureBox
            {
                Dock = DockStyle.Top,
                Height = 180,
                BorderStyle = BorderStyle.FixedSingle,
                BackColor = Color.FromArgb(18, 18, 18),
                SizeMode = PictureBoxSizeMode.Zoom
            };

            _lblPreviewInfo = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Color.Silver,
                Font = new Font("Segoe UI", 8f),
                TextAlign = ContentAlignment.TopCenter,
                Padding = new Padding(0, 8, 0, 0)
            };

            pnlRight.Controls.Add(_lblPreviewInfo);
            pnlRight.Controls.Add(_pbPreview);
            pnlRight.Controls.Add(lblPreviewHeader);

            // Table des meshes
            _dgv = new DataGridView
            {
                Dock = DockStyle.Fill,
                BackgroundColor = Color.FromArgb(30, 30, 30),
                GridColor = Color.FromArgb(50, 50, 50),
                BorderStyle = BorderStyle.None,
                AutoGenerateColumns = false,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false,
                EnableHeadersVisualStyles = false
            };

            _dgv.DefaultCellStyle.BackColor = Color.FromArgb(35, 35, 35);
            _dgv.DefaultCellStyle.ForeColor = Color.Gainsboro;
            _dgv.DefaultCellStyle.SelectionBackColor = Color.FromArgb(55, 65, 80);
            _dgv.DefaultCellStyle.SelectionForeColor = Color.White;

            _dgv.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(45, 45, 45);
            _dgv.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            _dgv.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            _dgv.ColumnHeadersHeight = 28;

            var colMesh = new DataGridViewTextBoxColumn
            {
                Name = "colMesh",
                HeaderText = "Mesh",
                Width = 140,
                ReadOnly = true
            };

            var colMat = new DataGridViewTextBoxColumn
            {
                Name = "colMat",
                HeaderText = "Matériau FBX",
                Width = 120,
                ReadOnly = true
            };

            var colTex = new DataGridViewTextBoxColumn
            {
                Name = "colTex",
                HeaderText = "Fichier Texture",
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                ReadOnly = true
            };

            var colBrowse = new DataGridViewButtonColumn
            {
                Name = "colBrowse",
                HeaderText = "Action",
                Text = "Parcourir...",
                UseColumnTextForButtonValue = true,
                Width = 95,
                FlatStyle = FlatStyle.Flat
            };

            var colClear = new DataGridViewButtonColumn
            {
                Name = "colClear",
                HeaderText = "",
                Text = "Retirer",
                UseColumnTextForButtonValue = true,
                Width = 70,
                FlatStyle = FlatStyle.Flat
            };

            _dgv.Columns.AddRange(colMesh, colMat, colTex, colBrowse, colClear);

            _dgv.CellContentClick += Dgv_CellContentClick;
            _dgv.SelectionChanged += Dgv_SelectionChanged;

            // Panneau inférieur
            var pnlBottom = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 48,
                BackColor = Color.FromArgb(36, 36, 36),
                Padding = new Padding(12, 8, 12, 8)
            };

            _btnApply = new Button
            {
                Text = "Appliquer et Enregistrer",
                DialogResult = DialogResult.OK,
                Dock = DockStyle.Right,
                Width = 170,
                BackColor = Color.FromArgb(40, 110, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            _btnApply.Click += BtnApply_Click;

            _btnCancel = new Button
            {
                Text = "Annuler",
                DialogResult = DialogResult.Cancel,
                Dock = DockStyle.Right,
                Width = 90,
                BackColor = Color.FromArgb(50, 50, 50),
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat
            };

            var btnExtractFbx = new Button
            {
                Text = "Extraire textures d'un FBX...",
                Dock = DockStyle.Left,
                Width = 200,
                BackColor = Color.FromArgb(40, 75, 120),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            btnExtractFbx.Click += BtnExtractFbx_Click;

            var btnOpenFolder = new Button
            {
                Text = "Ouvrir dossier modèle...",
                Dock = DockStyle.Left,
                Width = 160,
                BackColor = Color.FromArgb(50, 50, 50),
                ForeColor = Color.Gainsboro,
                FlatStyle = FlatStyle.Flat
            };
            btnOpenFolder.Click += (s, e) =>
            {
                try
                {
                    if (Directory.Exists(_othersFolder))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = _othersFolder,
                            UseShellExecute = true
                        });
                    }
                }
                catch { }
            };

            pnlBottom.Controls.Add(btnOpenFolder);
            pnlBottom.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 8 });
            pnlBottom.Controls.Add(btnExtractFbx);
            pnlBottom.Controls.Add(_btnCancel);
            pnlBottom.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 10 });
            pnlBottom.Controls.Add(_btnApply);

            Controls.Add(_dgv);
            Controls.Add(pnlRight);
            Controls.Add(pnlTop);
            Controls.Add(pnlBottom);
        }

        private void PopulateTable()
        {
            _dgv.Rows.Clear();
            for (int i = 0; i < _model.Parts.Count; i++)
            {
                var part = _model.Parts[i];
                string texDisplay = !string.IsNullOrEmpty(part.TextureName) ? part.TextureName : "(Aucune texture)";
                int rowIndex = _dgv.Rows.Add(
                    string.IsNullOrEmpty(part.Name) ? $"Mesh_{i + 1}" : part.Name,
                    string.IsNullOrEmpty(part.MaterialName) ? "-" : part.MaterialName,
                    texDisplay);
                _dgv.Rows[rowIndex].Tag = i;
            }

            if (_dgv.Rows.Count > 0)
            {
                _dgv.Rows[0].Selected = true;
                UpdatePreview(0);
            }
        }

        private void Dgv_CellContentClick(object? sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.RowIndex >= _model.Parts.Count) return;
            int partIdx = (int)(_dgv.Rows[e.RowIndex].Tag ?? e.RowIndex);

            if (e.ColumnIndex == _dgv.Columns["colBrowse"].Index)
            {
                BrowseTextureForPart(e.RowIndex, partIdx);
            }
            else if (e.ColumnIndex == _dgv.Columns["colClear"].Index)
            {
                ClearTextureForPart(e.RowIndex, partIdx);
            }
        }

        private void BrowseTextureForPart(int rowIndex, int partIdx)
        {
            var part = _model.Parts[partIdx];
            string initDir = _othersFolder;
            if (!string.IsNullOrEmpty(_initialFbxDir) && Directory.Exists(_initialFbxDir))
            {
                string texSub = Path.Combine(_initialFbxDir, "textures");
                if (!Directory.Exists(texSub)) texSub = Path.Combine(_initialFbxDir, "Textures");
                initDir = Directory.Exists(texSub) ? texSub : _initialFbxDir;
            }

            using var ofd = new OpenFileDialog
            {
                Title = $"Choisir une texture BaseColor / Diffuse pour '{part.Name}'",
                Filter = "Textures & Modèles 3D (*.png;*.jpg;*.tga;*.bmp;*.dds;*.fbx)|*.png;*.jpg;*.jpeg;*.tga;*.bmp;*.dds;*.fbx|Images (*.png;*.jpg;*.tga;*.bmp)|*.png;*.jpg;*.jpeg;*.tga;*.bmp;*.dds|Fichiers FBX (*.fbx)|*.fbx|Tous les fichiers (*.*)|*.*",
                InitialDirectory = initDir
            };

            if (ofd.ShowDialog(this) != DialogResult.OK || !File.Exists(ofd.FileName)) return;

            string selectedFile = ofd.FileName;
            if (string.Equals(Path.GetExtension(selectedFile), ".fbx", StringComparison.OrdinalIgnoreCase))
            {
                var extracted = MixamoCharacterImport.ExtractFbxEmbeddedTextures(selectedFile);
                if (extracted.Count == 0)
                {
                    long sz = new FileInfo(selectedFile).Length;
                    MessageBox.Show(
                        $"Ce fichier FBX '{Path.GetFileName(selectedFile)}' ne contient aucune texture intégrée (taille : {sz / 1024.0:F1} Ko).\n\n" +
                        "Sur Mixamo, téléchargez le modèle avec l'option 'Skin: With Skin' pour inclure les textures dans le FBX.",
                        "Aucune texture trouvée",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                string charName = Path.GetFileNameWithoutExtension(_charPath);
                if (!Directory.Exists(_othersFolder)) Directory.CreateDirectory(_othersFolder);

                string? bestDiffuse = null;
                foreach (var (rawFn, data) in extracted)
                {
                    string cleanFn = Path.GetFileName(rawFn);
                    string destPath = Path.Combine(_othersFolder, $"{charName}_{cleanFn}");
                    File.WriteAllBytes(destPath, data);
                    string fnLower = cleanFn.ToLowerInvariant();
                    if (!fnLower.Contains("normal") && !fnLower.Contains("spec") && !fnLower.Contains("rough") && !fnLower.Contains("metal") && !fnLower.Contains("ao") && !fnLower.Contains("glow"))
                    {
                        if (bestDiffuse == null || fnLower.Contains("diffuse") || fnLower.Contains("basecolor") || fnLower.Contains("albedo") || fnLower.Contains("color"))
                            bestDiffuse = destPath;
                    }
                }

                selectedFile = bestDiffuse ?? Path.Combine(_othersFolder, $"{charName}_{Path.GetFileName(extracted[0].Filename)}");
            }

            _pendingTexturePaths[partIdx] = selectedFile;
            _dgv.Rows[rowIndex].Cells["colTex"].Value = Path.GetFileName(selectedFile) + " (En attente d'enregistrement)";
            _dgv.Rows[rowIndex].DefaultCellStyle.ForeColor = Color.LightGreen;
            UpdatePreviewForPath(selectedFile);
        }

        private void BtnExtractFbx_Click(object? sender, EventArgs e)
        {
            using var ofd = new OpenFileDialog
            {
                Title = "Sélectionner un fichier FBX pour extraire ses textures",
                Filter = "Fichiers FBX (*.fbx)|*.fbx|Tous les fichiers (*.*)|*.*",
                InitialDirectory = !string.IsNullOrEmpty(_initialFbxDir) && Directory.Exists(_initialFbxDir) ? _initialFbxDir : string.Empty
            };

            if (ofd.ShowDialog(this) != DialogResult.OK || !File.Exists(ofd.FileName)) return;

            string fbxPath = ofd.FileName;
            var textures = MixamoCharacterImport.ExtractFbxEmbeddedTextures(fbxPath);

            if (textures.Count == 0)
            {
                long sz = new FileInfo(fbxPath).Length;
                MessageBox.Show(
                    $"Aucune texture intégrée n'a été trouvée dans '{Path.GetFileName(fbxPath)}' (taille : {sz / 1024.0:F1} Ko).\n\n" +
                    "Explication :\n" +
                    "Certains modèles FBX sont exportés sans textures intégrées (sans skin ou textures externes).\n" +
                    "• Sur Mixamo : lors du téléchargement, choisissez le format FBX avec l'option 'Skin: With Skin' pour inclure les textures dans le fichier.\n" +
                    "• Si vos textures sont dans un dossier séparé ou un fichier .zip, décompressez-les puis utilisez le bouton 'Parcourir...' pour chaque mesh.",
                    "Aucune texture dans ce FBX",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            string charName = Path.GetFileNameWithoutExtension(_charPath);
            int savedCount = 0;
            var savedFiles = new List<string>();

            if (!Directory.Exists(_othersFolder)) Directory.CreateDirectory(_othersFolder);

            foreach (var (rawFn, data) in textures)
            {
                string cleanFn = Path.GetFileName(rawFn);
                string destName = $"{charName}_{cleanFn}";
                string destPath = Path.Combine(_othersFolder, destName);
                File.WriteAllBytes(destPath, data);
                savedCount++;
                savedFiles.Add(destPath);
            }

            // Auto-assign diffuse/basecolor textures to meshes:
            int assignedCount = 0;
            for (int i = 0; i < _model.Parts.Count; i++)
            {
                var part = _model.Parts[i];
                string mNameLower = (part.Name ?? string.Empty).ToLowerInvariant();
                string matLower = (part.MaterialName ?? string.Empty).ToLowerInvariant();

                string? bestMatch = null;
                int bestScore = 0;

                foreach (var sPath in savedFiles)
                {
                    string fn = Path.GetFileNameWithoutExtension(sPath).ToLowerInvariant();
                    if (fn.Contains("normal") || fn.Contains("spec") || fn.Contains("rough") || fn.Contains("metal") || fn.Contains("ao") || fn.Contains("glow"))
                        continue;

                    int score = 0;
                    if (fn.Contains("diffuse") || fn.Contains("basecolor") || fn.Contains("albedo") || fn.Contains("color")) score += 100;
                    if (!string.IsNullOrEmpty(mNameLower) && fn.Contains(mNameLower)) score += 200;
                    if (!string.IsNullOrEmpty(matLower) && fn.Contains(matLower)) score += 200;

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestMatch = sPath;
                    }
                }

                if (bestMatch == null && _model.Parts.Count == 1)
                {
                    foreach (var sPath in savedFiles)
                    {
                        string fn = Path.GetFileNameWithoutExtension(sPath).ToLowerInvariant();
                        if (fn.Contains("normal") || fn.Contains("spec") || fn.Contains("rough") || fn.Contains("metal") || fn.Contains("ao")) continue;
                        bestMatch = sPath;
                        break;
                    }
                }

                if (bestMatch != null)
                {
                    _pendingTexturePaths[i] = bestMatch;
                    for (int r = 0; r < _dgv.Rows.Count; r++)
                    {
                        if ((int)(_dgv.Rows[r].Tag ?? r) == i)
                        {
                            _dgv.Rows[r].Cells["colTex"].Value = Path.GetFileName(bestMatch) + " (Extrait du FBX)";
                            _dgv.Rows[r].DefaultCellStyle.ForeColor = Color.LightGreen;
                            break;
                        }
                    }
                    assignedCount++;
                }
            }

            UpdatePreview(_dgv.SelectedRows.Count > 0 ? (int)(_dgv.SelectedRows[0].Tag ?? _dgv.SelectedRows[0].Index) : 0);

            MessageBox.Show(
                $"{savedCount} texture(s) extraite(s) avec succès depuis le FBX !\n" +
                $"Elles ont été enregistrées dans le dossier du modèle.\n" +
                (assignedCount > 0 ? $"\n{assignedCount} mesh(es) ont été automatiquement associés à leur texture." : "") +
                "\nCliquez sur 'Appliquer et Enregistrer' pour valider.",
                "Extraction réussie",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private void ClearTextureForPart(int rowIndex, int partIdx)
        {
            _pendingTexturePaths[partIdx] = string.Empty; // Supprimé
            _dgv.Rows[rowIndex].Cells["colTex"].Value = "(Aucune texture)";
            _dgv.Rows[rowIndex].DefaultCellStyle.ForeColor = Color.OrangeRed;
            UpdatePreviewForPath(null);
        }

        private void Dgv_SelectionChanged(object? sender, EventArgs e)
        {
            if (_dgv.SelectedRows.Count == 0) return;
            int rowIndex = _dgv.SelectedRows[0].Index;
            if (rowIndex >= 0 && rowIndex < _model.Parts.Count)
            {
                int partIdx = (int)(_dgv.Rows[rowIndex].Tag ?? rowIndex);
                UpdatePreview(partIdx);
            }
        }

        private void UpdatePreview(int partIdx)
        {
            if (_pendingTexturePaths.TryGetValue(partIdx, out string? pending))
            {
                UpdatePreviewForPath(pending);
                return;
            }

            var part = _model.Parts[partIdx];
            if (!string.IsNullOrEmpty(part.TextureName))
            {
                string direct = Path.Combine(_othersFolder, part.TextureName);
                if (File.Exists(direct))
                {
                    UpdatePreviewForPath(direct);
                    return;
                }
            }

            UpdatePreviewForPath(null);
        }

        private void UpdatePreviewForPath(string? filePath)
        {
            if (_pbPreview.Image != null)
            {
                var oldImg = _pbPreview.Image;
                _pbPreview.Image = null;
                oldImg.Dispose();
            }

            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                _lblPreviewInfo.Text = "Aucune texture assignée";
                return;
            }

            try
            {
                var bmp = Granny3DControl.LoadImageBitmap(filePath);
                if (bmp != null)
                {
                    _pbPreview.Image = bmp;
                    _lblPreviewInfo.Text = $"{Path.GetFileName(filePath)}\n{bmp.Width} x {bmp.Height} px";
                }
                else
                {
                    _lblPreviewInfo.Text = $"{Path.GetFileName(filePath)}\n(Aperçu non supporté)";
                }
            }
            catch (Exception ex)
            {
                _lblPreviewInfo.Text = $"Erreur aperçu :\n{ex.Message}";
            }
        }

        private void BtnApply_Click(object? sender, EventArgs e)
        {
            try
            {
                string charName = Path.GetFileNameWithoutExtension(_charPath);
                bool modified = false;

                foreach (var kvp in _pendingTexturePaths)
                {
                    int partIdx = kvp.Key;
                    string? chosenPath = kvp.Value;
                    if (partIdx < 0 || partIdx >= _model.Parts.Count) continue;
                    var part = _model.Parts[partIdx];

                    if (string.IsNullOrEmpty(chosenPath))
                    {
                        // Retirer texture
                        if (!string.IsNullOrEmpty(part.TextureName))
                        {
                            part.TextureName = string.Empty;
                            modified = true;
                        }
                    }
                    else if (File.Exists(chosenPath))
                    {
                        // Nouvelle texture choisie
                        string ext = Path.GetExtension(chosenPath);
                        string cleanMesh = Granny3DControl.MakeSafeFilename(part.Name);
                        string destName = $"{charName}_{cleanMesh}{ext}";
                        string destPath = Path.Combine(_othersFolder, destName);

                        if (!string.Equals(chosenPath, destPath, StringComparison.OrdinalIgnoreCase))
                        {
                            File.Copy(chosenPath, destPath, true);
                        }

                        part.TextureName = destName;
                        modified = true;
                    }
                }

                if (modified)
                {
                    // Réenregistre le fichier .char
                    if (!GrannyModelFile.Save(_model, _charPath))
                    {
                        MessageBox.Show("Impossible d'enregistrer le fichier .char.", "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    // Met à jour les maillages live s'ils sont chargés dans la scène 3D
                    if (_liveMeshes != null && _tryLoadTextureFunc != null)
                    {
                        foreach (var lm in _liveMeshes)
                        {
                            if (string.Equals(lm.SourcePath, _charPath, StringComparison.OrdinalIgnoreCase))
                            {
                                var part = _model.Parts.Find(p => string.Equals(p.Name, lm.Name, StringComparison.OrdinalIgnoreCase));
                                if (part != null)
                                {
                                    if (!string.IsNullOrEmpty(part.TextureName))
                                    {
                                        int newTex = _tryLoadTextureFunc(_charPath, part.TextureName);
                                        if (newTex > 0)
                                        {
                                            if (lm.TextureId > 0 && lm.TextureId != newTex) GL.DeleteTexture(lm.TextureId);
                                            lm.TextureId = newTex;
                                            lm.HasTexture = true;
                                        }
                                    }
                                    else
                                    {
                                        if (lm.TextureId > 0) GL.DeleteTexture(lm.TextureId);
                                        lm.TextureId = -1;
                                        lm.HasTexture = false;
                                    }
                                }
                            }
                        }
                        _invalidateGlFunc?.Invoke();
                    }
                }

                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Erreur lors de l'application des textures : {ex.Message}", "Erreur", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }
}
