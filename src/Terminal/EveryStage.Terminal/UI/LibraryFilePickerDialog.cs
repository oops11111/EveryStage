using EveryStage.Terminal.Data;

namespace EveryStage.Terminal.UI;

/// <summary>Lets <c>ActivitiesPanel</c>'s "添加文件..." pick one file out of the <see
/// cref="FileLibraryStore"/> to copy into an activity — see <c>ActivitiesPanel</c>'s class doc
/// comment on why this exists instead of a drag from the 文件 panel.</summary>
public sealed class LibraryFilePickerDialog : Form
{
    private readonly ListBox _listBox;

    public MediaFile? Selected { get; private set; }

    public LibraryFilePickerDialog(IReadOnlyList<MediaFile> libraryFiles)
    {
        Text = "从文件库添加";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(320, 320);

        _listBox = new ListBox { Bounds = new Rectangle(12, 12, 296, 260), FormattingEnabled = true };
        // Show just the filename, not the full SourcePath — MediaFile has no dedicated display
        // property, so this uses ListBox's Format event instead of DisplayMember.
        _listBox.Format += (_, e) => e.Value = Path.GetFileName(((MediaFile)e.ListItem!).SourcePath);
        foreach (var file in libraryFiles) _listBox.Items.Add(file);
        _listBox.DoubleClick += (_, _) => { if (_listBox.SelectedItem != null) AcceptSelection(); };

        var okButton = new Button { Text = "添加", Bounds = new Rectangle(140, 280, 80, 28), DialogResult = DialogResult.OK };
        okButton.Click += (_, _) => AcceptSelection();
        var cancelButton = new Button { Text = "取消", Bounds = new Rectangle(228, 280, 80, 28), DialogResult = DialogResult.Cancel };

        AcceptButton = okButton;
        CancelButton = cancelButton;

        Controls.AddRange(new Control[] { _listBox, okButton, cancelButton });
        ModernUi.StyleDialog(this);
    }

    private void AcceptSelection()
    {
        Selected = _listBox.SelectedItem as MediaFile;
    }
}
