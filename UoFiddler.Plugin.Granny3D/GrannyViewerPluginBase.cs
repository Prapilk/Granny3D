using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows.Forms;
using UoFiddler.Controls.Plugin;
using UoFiddler.Controls.Plugin.Interfaces;

namespace UoFiddler.Plugin.Granny3D
{
    public class GrannyViewerPluginBase : PluginBase
    {
        static GrannyViewerPluginBase()
        {
            // UoFiddler charge les plugins via Assembly.LoadFrom : les dépendances
            // (OpenTK, AssimpNet) déposées dans le dossier plugins/ ne sont pas
            // trouvées par le probing standard de .NET. Ce handler les charge
            // depuis le dossier du plugin.
            AssemblyLoadContext.Default.Resolving += OnResolving;
        }

        private static Assembly OnResolving(AssemblyLoadContext context, AssemblyName assemblyName)
        {
            if (assemblyName?.Name == null)
            {
                return null;
            }

            string dir = Path.GetDirectoryName(typeof(GrannyViewerPluginBase).Assembly.Location);
            if (dir == null)
            {
                return null;
            }

            string path = Path.Combine(dir, assemblyName.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        }

        public override string Name { get; } = "Granny 3D Viewer";
        public override string Description { get; } = "Visualise les modèles et animations 3D (.grn) d'UO3D.";
        public override string Author { get; } = "Antigravity";
        public override string Version { get; } = "1.0.0";
        public override IPluginHost Host { get; set; }

        public override void Initialize()
        {
            // Initialisation si nécessaire
        }

        public override void Unload()
        {
            // Nettoyage si nécessaire
        }

        public override void ModifyTabPages(TabControl tabControl)
        {
            TabPage page = new TabPage
            {
                Tag = tabControl.TabCount + 1,
                Text = "3D Viewer (.grn)"
            };
            page.Controls.Add(new Granny3DControl { Dock = DockStyle.Fill });
            tabControl.TabPages.Add(page);
        }

        public override void ModifyPluginToolStrip(ToolStripDropDownButton toolStrip)
        {
            ToolStripMenuItem item = new ToolStripMenuItem
            {
                Text = "3D Viewer Options"
            };
            item.Click += (sender, e) => { MessageBox.Show("Granny 3D Plugin Loaded!"); };
            toolStrip.DropDownItems.Add(item);
        }
    }
}
