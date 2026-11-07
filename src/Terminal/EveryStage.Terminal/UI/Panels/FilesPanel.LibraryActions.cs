using System.Drawing.Drawing2D;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Logging;
using EveryStage.Terminal.Playback;

namespace EveryStage.Terminal.UI.Panels;

public sealed partial class FilesPanel
{
    private void ImportViaDialog()
    {
        using var dialog = new OpenFileDialog { Multiselect = true, Title = "导入文件到文件库" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        ImportPaths(dialog.FileNames);
    }

    /// <summary>音频区空状态的「导入音频」：同 <see cref="ImportViaDialog"/>，只是默认筛选音频格式。</summary>
    private void ImportAudioViaDialog()
    {
        using var dialog = new OpenFileDialog
        {
            Multiselect = true, Title = "导入音频到文件库",
            Filter = "音频文件|*.mp3;*.wav;*.flac;*.aac;*.m4a;*.wma|所有文件|*.*",
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        ImportPaths(dialog.FileNames);
    }

    // One file card inside its grid tile, per the design: 12px between cards, the thumbnail (16:10)
    // edge to edge at the top, then the name (with ⋯) and a muted meta line.
    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetData(DataFormats.FileDrop) is string[] paths)
            ImportPaths(paths);
    }

    /// <summary>PLANNING.md §11 "批量选择" — the "删除" half of it (see class doc comment for the
    /// other two actions §11 describes for the same selection). Handles any number of selected
    /// items, not just one, now that <see cref="_grid"/> allows multi-selection.</summary>
    private void OnRemoveClick(object? sender, EventArgs e)
    {
        RemoveFromLibrary(SelectedFiles());
    }

    /// <summary>Confirm, remove, log, refresh — shared by the grid (selection) and the audio
    /// selector (one audio file).</summary>
    private void RemoveFromLibrary(IReadOnlyList<MediaFile> files)
    {
        if (files.Count == 0) return;

        string prompt = files.Count == 1
            ? $"确定要从文件库中移除 \"{Path.GetFileName(files[0].SourcePath)}\" 吗？"
            : $"确定要从文件库中移除选中的 {files.Count} 个文件吗？";
        const string copiesNote = "已经添加到流程里的副本不受影响——流程保存的是独立拷贝，不是对这个文件库条目的引用。";
        bool stopOutput = false;
        var onAir = IsProgramLive?.Invoke() == true && ProgramFile?.Invoke() is { } live
            ? files.FirstOrDefault(f => SameFile(f, live)) : null;
        if (onAir != null)
        {
            // The file on the big screen: say so by name. Removing it never stops the output by itself;
            // the operator may tick 「同时停止输出」 (off by default).
            var stop = new TaskDialogVerificationCheckBox("同时停止输出（大屏断开，没有撤销）", isChecked: false);
            var remove = new TaskDialogButton("移除");
            var page = new TaskDialogPage
            {
                Caption = "移除文件",
                Heading = $"「{Path.GetFileName(onAir.SourcePath)}」正在大屏上播放",
                Text = prompt + "\n移除后大屏仍继续显示当前画面，直到切换到下一项或停止输出。\n" + copiesNote,
                Icon = TaskDialogIcon.Warning,
                Verification = stop,
                Buttons = { remove, TaskDialogButton.Cancel },
                DefaultButton = TaskDialogButton.Cancel,
            };
            if (TaskDialog.ShowDialog(this, page) != remove) return;
            stopOutput = stop.Checked;
        }
        else if (MessageBox.Show(this, prompt + "\n" + copiesNote, "移除文件", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;

        foreach (var file in files)
        {
            try { _library.Remove(file.Id); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, $"移除未完成：{DataDirectory.Describe(ex)}", "文件库保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                break;
            }
            _fileOpLog.LogFileRemoved(file.Id, file.SourcePath);
        }
        Refresh_();
        if (stopOutput) StopProgramRequested?.Invoke();
    }

    /// <summary>PLANNING.md §11 "批量选择"'s "加入活动" half — see class doc comment for why this
    /// was previously left unimplemented, and <see cref="ActivityPickerDialog"/> for the picker this
    /// needed that didn't exist until now. Handles any number of selected files at once, same as
    /// <see cref="OnRemoveClick"/>. Deep-copies each selected file (<see cref="MediaFile.Clone"/>)
    /// into the chosen activity — same "always an independent copy, never a shared reference"
    /// behavior <c>ActivitiesPanel</c>'s own "添加文件..." already established (see this project's
    /// README "已知风险" #22). Doesn't refresh <c>ActivitiesPanel</c>'s own tree directly: this
    /// panel has no reference to it, and <c>MainWindow.ShowPanel</c> already calls
    /// <c>ActivitiesPanel.RefreshTree()</c> every time the operator navigates there, which is the
    /// same lazy-refresh-on-show convention every panel in this app already relies on (see e.g.
    /// <c>SettingsPanel.Refresh_</c>'s own doc comment on why only the 显示 tab needs it) rather than
    /// pushing a live update across panels that aren't currently visible anyway.</summary>
    private void OnAddToActivityClick(object? sender, EventArgs e)
    {
        var files = SelectedFiles();
        if (files.Count == 0 || _scenarioStore.Scenarios.Count == 0) return;

        using var dialog = new ActivityPickerDialog(_scenarioStore.Scenarios, _scenarioStore.CurrentScenarioId);
        if (dialog.ShowDialog(this) != DialogResult.OK
            || dialog.SelectedScenario == null || dialog.SelectedActivity == null)
            return;

        var additions = files.Select(file => file.Clone()).ToList();
        dialog.SelectedActivity.Files.AddRange(additions);
        try { _scenarioRepository.Save(_scenarioStore); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (var addition in additions) dialog.SelectedActivity.Files.Remove(addition);
            MessageBox.Show(this, $"未加入活动：{DataDirectory.Describe(ex)}", "方案保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _fileOpLog.LogActivityModified(dialog.SelectedScenario.Id, dialog.SelectedActivity.Id, dialog.SelectedActivity.Name);

        ShowSuccessNotice(
            files.Count == 1
                ? $"已将 \"{Path.GetFileName(files[0].SourcePath)}\" 加入活动 \"{dialog.SelectedActivity.Name}\""
                : $"已将 {files.Count} 个文件加入活动 \"{dialog.SelectedActivity.Name}\"",
            "加入活动");
    }

    /// <summary>Success results go to the toast stack when MainWindow wired <see cref="ShowToast"/>
    /// (审查 L-03 的迁移规则：纯结果通知走通知栏，不弹模态抢焦点；需要当场决策的确认框和需要
    /// 看明细的汇总框——导入被拒/需转码汇总、保存失败——仍保留 MessageBox，不走这里）。通知是
    /// 单行短文本，所以文案保持一行放得下；未接线时回退到原来的信息框，保证独立使用本面板时
    /// 成功反馈不丢。</summary>
    private void ShowSuccessNotice(string message, string title)
    {
        if (ShowToast is { } show)
        {
            show(message, ToastSeverity.Info, Array.Empty<ToastAction>());
            return;
        }
        MessageBox.Show(this, message, title, MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ImportPaths(IEnumerable<string> paths)
    {
        var rejected = new List<string>();
        // 本次成功导入、但编码可能不受支持的文件：导入照常成功（不阻断），结束后统一提示
        // 可能需要转码，见方法末尾的汇总框；逐个收集是为了在提示里带上各自的原因。
        var needsTranscode = new List<MediaFile>();
        var importedFiles = new List<MediaFile>();
        // Audio goes to the background music (the selected 歌单), not into the library.
        var all = paths.ToList();
        var audio = all.Where(p => FileLibraryStore.InferKind(p) == MediaKind.Audio).ToList();
        if (audio.Count > 0) AudioDropped?.Invoke(audio);
        var rest = all.Except(audio).ToList();
        if (rest.Count > 0)
        {
            // 性能审查 P-19: one batch import = one library save at the end, instead of a full
            // save per file. Results align with `rest` by index; a null slot is a rejected path,
            // counted and reported exactly as the old per-file loop counted it.
            IReadOnlyList<MediaFile?> results;
            try { results = _library.ImportMany(rest); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, $"导入未完成：{DataDirectory.Describe(ex)}\n此前成功导入的文件已保留。", "文件库保存失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                results = Array.Empty<MediaFile?>();
            }
            for (int i = 0; i < results.Count; i++)
            {
                var imported = results[i];
                if (imported == null) { rejected.Add(Path.GetFileName(rest[i])); continue; }
                importedFiles.Add(imported);
                _fileOpLog.LogFileImported(imported.Id, imported.SourcePath);
                if (imported.NeedsTranscode) needsTranscode.Add(imported);
            }
        }
        // 性能审查 P-17: an Office document's first play pays a cold-start conversion on the spot
        // (static slides / read-only pages always need it, and so does every presentation's
        // Preview) — kick that conversion off in the background now, at import time, so the wait
        // leaves the show moment. The conversion cache serialises through its own gate, so
        // several documents simply queue; failures are logged there and retried on demand at
        // play time, so nothing is surfaced in the UI here.
        foreach (var file in importedFiles)
            if (IsUnconvertedOfficeDocument(file))
                _ = PrewarmConversionAsync(file.SourcePath);
        if (audio.Count < all.Count) Refresh_();

        string message = "";
        if (rejected.Count > 0)
        {
            var sheets = rejected.Where(Documents.DocumentFormats.IsSpreadsheet).ToList();
            var other = rejected.Except(sheets).ToList();
            if (sheets.Count > 0)
                message += $"表格文件不能直接在大屏上播放（没有固定的页面版式）。请在表格软件中「另存为 PDF」后再导入：\n{string.Join("\n", sheets)}";
            if (other.Count > 0)
                message += (message.Length > 0 ? "\n\n" : "") + $"以下文件的类型不受支持，未导入：\n{string.Join("\n", other)}";
        }
        if (needsTranscode.Count > 0)
        {
            // 这些文件已经导入成功，只是编码不一定能被播放管线解码；只提示、不拦人，
            // 让操作员有空时离线转码，而不是在导入当口卡住。
            message += (message.Length > 0 ? "\n\n" : "")
                + "以下文件已导入，但可能需要转码为 MP4（H.264 + AAC）才能正常播放：\n"
                + string.Join("\n", needsTranscode.Select(file =>
                    $"{Path.GetFileName(file.SourcePath)}：{TranscodeReason(file)}"));
        }
        if (message.Length > 0)
            MessageBox.Show(this, message, "导入", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>True for a Word/presentation document with no cached playback conversion yet
    /// (性能审查 P-17) — the same test 开演前检查 reports as its 未预转换 finding, sharing
    /// <see cref="Documents.DocumentConversionCache.TryGet"/> as the one definition of "cached".</summary>
    private static bool IsUnconvertedOfficeDocument(MediaFile file) =>
        file.Kind == MediaKind.Document
        && Documents.DocumentFormats.Of(file.SourcePath) is Documents.DocumentFormat.Word or Documents.DocumentFormat.Presentation
        && Documents.DocumentConversionCache.Shared.TryGet(file.SourcePath) == null;

    /// <summary>Background pre-conversion kicked off at import time (性能审查 P-17). Never throws
    /// and never touches the UI: the conversion cache logs failures itself, and playback retries
    /// on demand with its normal error surface if this did not succeed.</summary>
    private static async Task PrewarmConversionAsync(string sourcePath)
    {
        try
        {
            await Documents.DocumentConversionCache.Shared
                .GetOrConvertAsync(sourcePath, Documents.DocumentFormats.Of(sourcePath))
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceError("Pre-conversion failed for {0}: {1}", sourcePath, ex);
        }
    }

    /// <summary>为什么这个文件可能需要转码：优先用导入时探测到的具体原因，没探测到时给一句通用说明。</summary>
    private static string TranscodeReason(MediaFile file) =>
        string.IsNullOrWhiteSpace(file.TranscodeReason) ? "编码格式可能不受支持" : file.TranscodeReason.Trim();

    /// <summary>Call after the library changes from outside this control (e.g. a file removed
    /// elsewhere) to re-pull the list. Named with a trailing underscore only to avoid colliding
    /// with <see cref="Control.Refresh"/>, which does something unrelated (repaint).</summary>
}
