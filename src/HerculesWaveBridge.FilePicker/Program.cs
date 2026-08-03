using System;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace HerculesWaveBridge.FilePicker;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
        {
            return 2;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            using var dialog = new OpenFileDialog
            {
                Title = "Choose Hercules Background Image",
                Filter = "Image files|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|PNG images|*.png|JPEG images|*.jpg;*.jpeg|All files|*.*",
                CheckFileExists = true,
                Multiselect = false,
                RestoreDirectory = true,
                AutoUpgradeEnabled = true
            };

            if (dialog.ShowDialog() == DialogResult.OK)
            {
                File.WriteAllText(Path.GetFullPath(args[0]), dialog.FileName, new UTF8Encoding(false));
            }

            return 0;
        }
        catch (Exception ex)
        {
            try
            {
                File.WriteAllText($"{Path.GetFullPath(args[0])}.error", ex.ToString(), new UTF8Encoding(false));
            }
            catch
            {
                // The parent process will still observe the non-zero exit code.
            }

            return 1;
        }
    }
}
