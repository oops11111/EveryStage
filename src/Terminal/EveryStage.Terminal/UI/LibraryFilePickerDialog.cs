using EveryStage.Terminal.Data;

namespace EveryStage.Terminal.UI;

/// <summary>Lets <c>ActivitiesPanel</c>'s "添加文件..." pick files out of the <see
/// cref="FileLibraryStore"/> to copy into an activity — see <c>ActivitiesPanel</c>'s class doc
/// comment on why this exists instead of a drag from the 文件 panel. Multi-select (AP-01):
/// stocking a whole section used to mean reopening this dialog once per file.</summary>
public sealed class LibraryFilePickerDialog : Form
{
    private readonly ListBox _listBox;
    private IReadOnlyList<MediaFile> _selectedFiles = Array.Empty<MediaFile>();

    /// <summary>The first picked file, or null — kept for callers that only ever cared about a
    /// single pick; new code should read <see cref="SelectedFiles"/>.</summary>
    public MediaFile? Selected => _selectedFiles.FirstOrDefault();

    /// <summary>Every picked file, in list (display) order rather than click order: the caller
    /// appends in this order, and the operator reads the resulting list top-to-bottom against
    /// the dialog they just saw.</summary>
    public IReadOnlyList<MediaFile> SelectedFiles => _selectedFiles;

    public LibraryFilePickerDialog(IReadOnlyList<MediaFile> libraryFiles)
    {
        Text = "从文件库添加";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(320, 320);

        _listBox = new ListBox
        {
            Bounds = new Rectangle(12, 12, 296, 260),
            FormattingEnabled = true,
            SelectionMode = SelectionMode.MultiExtended,
        };
        // Show just the filename, not the full SourcePath — MediaFile has no dedicated display
        // property, so this uses ListBox's Format event instead of DisplayMember.
        _listBox.Format += (_, e) => e.Value = Path.GetFileName(((MediaFile)e.ListItem!).SourcePath);
        foreach (var file in libraryFiles) _listBox.Items.Add(file);
        _listBox.DoubleClick += (_, _) =>
        {
            // A double-click still means "this one, now" (the single-select dialog's shortcut):
            // collapse any multi-selection down to the double-clicked item and accept at once,
            // rather than surprising the operator by adding everything they had Ctrl+clicked.
            if (_listBox.SelectedItem is not MediaFile) return;
            var item = _listBox.SelectedItem;
            _listBox.ClearSelected();
            _listBox.SelectedItem = item;
            AcceptSelection();
            DialogResult = DialogResult.OK;
        };

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
        // Snapshot by walking the item list (not SelectedItems' enumeration) so the result is
        // guaranteed display order even if the selection was built up by scattered Ctrl+clicks.
        var chosen = new List<MediaFile>();
        foreach (var item in _listBox.Items)
            if (item is MediaFile file && _listBox.SelectedItems.Contains(file)) chosen.Add(file);
        _selectedFiles = chosen;
    }
}
