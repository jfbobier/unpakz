using System.Windows.Forms;
using Unpakz.Presentation;

namespace Unpakz.App;

/// <summary>The native folder picker (the Vista-style dialog), opened next to the archive.</summary>
internal sealed class FormsFolderPicker : IFolderPicker
{
    public string? Pick(string title, string? startFolder)
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = title,
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            InitialDirectory = startFolder ?? string.Empty,
        };
        return dialog.ShowDialog(Form.ActiveForm) == DialogResult.OK ? dialog.SelectedPath : null;
    }
}

internal sealed class FormsPrompt : IPrompt
{
    public bool ConfirmReplace(string message) =>
        MessageBox.Show(Form.ActiveForm, message, "Unpakz", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
}
