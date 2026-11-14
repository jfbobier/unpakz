using System;
using System.Linq;
using System.Windows.Forms;
using Unpakz.Presentation;
using Unpakz.Windows;

namespace Unpakz.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (args.Contains("--register", StringComparer.OrdinalIgnoreCase))
        {
            ShellIntegration.Register(Application.ExecutablePath);
            MessageBox.Show("Unpakz now appears in \"Open with\" and in Settings > Default apps for .zip, .rar and .tgz.", "Unpakz", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        if (args.Contains("--unregister", StringComparer.OrdinalIgnoreCase))
        {
            ShellIntegration.Unregister();
            MessageBox.Show("Unpakz's file type registration was removed.", "Unpakz", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        using (var model = new MainViewModel(new FormsFolderPicker(), new FormsPrompt()))
        using (var form = new MainForm(model))
        {
            var path = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));
            if (path is not null) form.OpenArchive(path);
            Application.Run(form);
            return 0;
        }
    }
}
