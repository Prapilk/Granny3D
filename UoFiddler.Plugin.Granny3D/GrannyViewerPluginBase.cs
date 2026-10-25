using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Windows.Forms;
using UoFiddler.Controls.Plugin;
using UoFiddler.Controls.Plugin.Interfaces;

namespace UoFiddler.Plugin.Granny3D
{
    public class GrannyViewerPluginBase : PluginBase
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern bool SetDllDirectory(string lpPathName);

        static GrannyViewerPluginBase()
        {
            // UoFiddler charge les plugins via Assembly.LoadFrom : les dépendances
            // gérées (OpenTK, AssimpNet) déposées dans le dossier plugins/ ne sont pas
            // trouvées par le probing standard de .NET. Ce handler les charge
            // depuis le dossier du plugin.
            AssemblyLoadContext.Default.Resolving += OnResolving;

            // Résolution des dépendances natives C/C++ (assimp.dll, glfw3.dll) dans
            // le dossier plugins/ ou sous runtimes/win-x64/native/ pour que le plugin
            // soit 100% autonome et copiable directement dans n'importe quel UOFiddler.
            AssemblyLoadContext.Default.ResolvingUnmanagedDll += OnResolvingUnmanagedDll;

            try
            {
                string dir = Path.GetDirectoryName(typeof(GrannyViewerPluginBase).Assembly.Location);
                if (!string.IsNullOrEmpty(dir))
                {
                    SetDllDirectory(dir);
                }
            }
            catch { }
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

        private static IntPtr OnResolvingUnmanagedDll(Assembly assembly, string unmanagedDllName)
        {
            string dir = Path.GetDirectoryName(typeof(GrannyViewerPluginBase).Assembly.Location);
            if (dir == null) return IntPtr.Zero;

            string[] candidates = new[]
            {
                Path.Combine(dir, unmanagedDllName + ".dll"),
                Path.Combine(dir, unmanagedDllName),
                Path.Combine(dir, "runtimes", "win-x64", "native", unmanagedDllName + ".dll"),
                Path.Combine(dir, "runtimes", "win-x64", "native", unmanagedDllName)
            };

            foreach (var path in candidates)
            {
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out IntPtr handle))
                    return handle;
            }

            return IntPtr.Zero;
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
