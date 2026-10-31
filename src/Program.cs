using System;
using System.IO;
using System.Windows.Forms;

namespace ComfyModelManager
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args != null && args.Length > 0 && string.Equals(args[0], "--selftest", StringComparison.OrdinalIgnoreCase))
                return SelfTest.Run();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                Lang.Load("en");

                string iniPath = Path.Combine(Application.StartupPath, "ComfyUI Model Manager.ini");
                IniFile ini = new IniFile(iniPath);
                string language = ini.Get("General", "Language", "");
                bool missing = !string.IsNullOrEmpty(language) && !Lang.LanguageExists(language);

                if (string.IsNullOrEmpty(language) || missing)
                {
                    string chosen = LanguageDialog.ShowFirstRun(missing);
                    ini.Set("General", "Language", chosen);
                    ini.Save();
                    language = chosen;
                }

                Lang.Load(language);
                Application.Run(new MainForm());
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), Lang.T("error.unexpectedTitle"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }
}
