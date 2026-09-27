using System.Diagnostics;
using System.Drawing.Imaging;
using EveryStage.Rendering;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Display;
using EveryStage.Terminal.Playback;
using EveryStage.Terminal.StateMachine;
using EveryStage.Terminal.UI;
using Vortice.MediaFoundation;

/// <summary>
/// End-to-end tests of the Preview / Program playback architecture against real media decoded by the
/// real pipeline (Media Foundation, D3D11 video processor, WASAPI, GDI+). All sample files are generated
/// here with Windows' own encoders. Audio runs at volume 0 and the Program overlay is never shown, so the
/// run neither makes sound nor covers the desktop.
/// </summary>
internal static class PreviewProgramTests
{
    public static void Run(string directory)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { RunCore(directory); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromMinutes(4))) throw new TimeoutException("Preview/Program tests did not finish.");
        if (failure != null) throw new InvalidOperationException("Preview/Program tests failed: " + failure.Message, failure);
    }

    private static void RunCore(string directory)
    {
        string media = Path.Combine(directory, "media");
        string logs = Path.Combine(directory, "logs");
        Directory.CreateDirectory(media);

        int externalOpens = 0;
        ExternalOpener.TestHook = _ => { externalOpens++; return true; };
        try
        {
            TestScaleMath();
            var fixtures = GenerateFixtures(media);

            using var form = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ClientSize = new Size(400, 300) };
            var previewSurface = new PreviewSurface { Dock = DockStyle.Fill };
            form.Controls.Add(previewSurface);
            _ = form.Handle;
            form.PerformLayout();
            Require(Math.Abs(previewSurface.Viewport.Width / (double)previewSurface.Viewport.Height - 4.0 / 3.0) < 0.02, "Preview viewport is not 4:3.");

            // Hosted CI runners have no GPU video processor and no audio endpoint. Probe both, run what the
            // machine supports and report the rest as SKIP (the app itself degrades the same way: no video
            // surface → video shows an error card; images/documents/audio still preview).
            using var previewVideo = SimulateNoVideo ? null : TryCreateVideoSurface(previewSurface.VideoHost.Handle, Math.Max(1, previewSurface.VideoHost.Width), Math.Max(1, previewSurface.VideoHost.Height));
            bool hasVideo = previewVideo != null;
            bool hasAudio = !SimulateNoAudio && CanOpenAudioOutput();
            if (previewVideo != null) previewSurface.VideoHostResized += (w, h) => previewVideo.Resize(w, h);
            Console.WriteLine($"INFO: GPU video processing {(hasVideo ? "available" : "UNAVAILABLE")}, audio output device {(hasAudio ? "available" : "UNAVAILABLE")}");
            var settings = new SettingsStore(Path.Combine(directory, "settings.json"));
            var scenarios = new ScenarioStore();
            using var preview = new PlaybackEngine(PlaybackChannel.Preview, previewSurface, previewVideo, settings, scenarios, null, logs)
            {
                AudioVolume = 0f,
                Muted = true,
            };

            // ── 1. No extended display at all: everything previews internally ───────────────────────
            var matrix = new List<string>();
            foreach (var (name, note) in fixtures)
            {
                if (note != null) { matrix.Add($"{Path.GetExtension(name),-6} 未验证：{note}"); continue; }
                var file = Media(Path.Combine(media, name));
                if (MissingCapability(name, file.Kind, hasVideo, hasAudio) is { } missing)
                {
                    matrix.Add($"{Path.GetExtension(name),-6} SKIP：{missing}");
                    continue;
                }
                preview.RequestPlay(file);
                bool ok = WaitFor(() => preview.State is PlaybackChannelState.Playing or PlaybackChannelState.Paused or PlaybackChannelState.Failed, 8000)
                          && preview.State != PlaybackChannelState.Failed;
                string detail = ok ? DescribePlaying(preview, file) : $"失败：{preview.LastError?.Stage} {preview.LastError?.Message}";
                if (ok && file.Kind == MediaKind.Video)
                {
                    ok = WaitFor(() => preview.VideoFrameStats.Presented >= 5, 5000);
                    if (!ok) detail = "视频未呈现任何帧";
                }
                if (ok && file.Kind == MediaKind.Audio)
                {
                    ok = WaitFor(() => preview.Position > TimeSpan.FromMilliseconds(300), 5000);
                    if (!ok) detail = "音频位置不前进";
                }
                matrix.Add($"{Path.GetExtension(name),-6} {(ok ? "PASS" : "FAIL")}：{detail}");
                Require(ok, $"Internal preview failed for {name}: {detail}");
            }
            preview.Stop();
            Require(preview.State == PlaybackChannelState.Idle && preview.CurrentFile == null, "Stop did not return Preview to Idle.");
            Console.WriteLine("PASS: internal preview without an extended display (images, video with/without audio, audio)");
            foreach (var line in matrix) Console.WriteLine("  FORMAT " + line);

            // ── 2. Image aspect ratio: 2:1 image in the 4:3 viewport is letterboxed, not stretched ──
            preview.RequestPlay(Media(Path.Combine(media, "wide.png")));
            Require(WaitFor(() => preview.State == PlaybackChannelState.Playing, 5000), "Wide PNG did not load.");
            Pump(200);
            using (var shot = new Bitmap(previewSurface.ContentSurface.Width, previewSurface.ContentSurface.Height))
            {
                previewSurface.ContentSurface.DrawToBitmap(shot, new Rectangle(Point.Empty, shot.Size));
                var top = shot.GetPixel(shot.Width / 2, 2);
                var middle = shot.GetPixel(shot.Width / 2, shot.Height / 2);
                Require(top.R < 30 && top.G < 30 && top.B < 30, "Letterbox band missing above a wide image (image was stretched).");
                Require(middle.R > 200 && middle.G < 60, "Wide image content not drawn in the middle of the viewport.");
            }
            Console.WriteLine("PASS: images keep their aspect ratio inside the 4:3 preview viewport");

            // ── 3. Video: A/V sync, silent video, pause / seek / loop ─────────────────────────────
            var av = Media(Path.Combine(media, "av.mp4"));
            var sw = Stopwatch.StartNew();
            if (hasVideo && hasAudio)
            {
            preview.RequestPlay(av);
            Require(WaitFor(() => preview.IsCompleted, 12000), "Video with audio never completed.");
            double wall = sw.Elapsed.TotalSeconds;
            var (presented, dropped) = preview.VideoFrameStats;
            Require(preview.CurrentVideoHasAudio, "Video's audio track was not used.");
            Require(presented >= 75 && dropped <= 8, $"A/V pacing poor: presented {presented}, dropped {dropped} of 90.");
            Require(wall is > 2.6 and < 5.0, $"3 s video with audio took {wall:F2} s (clock not driving playback).");
            Console.WriteLine($"PASS: MP4 with audio stays in sync (presented {presented}/90, dropped {dropped}, {wall:F2}s for 3.0s)");
            TestTransport(preview, av, "video");
            }
            else Console.WriteLine("SKIP: A/V sync and video transport (needs GPU video processing and an audio device)");

            if (hasVideo)
            {
            var silent = Media(Path.Combine(media, "silent.mp4"));
            sw.Restart();
            preview.RequestPlay(silent);
            Require(WaitFor(() => preview.IsCompleted, 12000), "Video without audio never completed.");
            Require(!preview.CurrentVideoHasAudio && preview.VideoFrameStats.Presented >= 75, "Silent MP4 did not play all frames.");
            Require(sw.Elapsed.TotalSeconds is > 2.6 and < 5.0, $"Silent 3 s video took {sw.Elapsed.TotalSeconds:F2} s.");
            Console.WriteLine("PASS: MP4 without an audio track plays (wall-clock paced)");
            }
            else Console.WriteLine("SKIP: silent video playback (needs GPU video processing)");

            if (hasAudio)
            {
            TestTransport(preview, Media(Path.Combine(media, "tone.mp3")), "audio");

            // Audio completion must mean "finished playing", not "finished decoding" (decode runs ~1 s ahead;
            // reporting at end-of-decode cut the last second of every track in sequential playback).
            sw.Restart();
            preview.RequestPlay(Media(Path.Combine(media, "tone.wav")));
            Require(WaitFor(() => preview.IsCompleted, 10000), "3 s WAV never completed.");
            Require(sw.Elapsed.TotalSeconds >= 2.8, $"Audio reported completion after {sw.Elapsed.TotalSeconds:F2} s of a 3.0 s file (tail cut off).");
            Console.WriteLine($"PASS: audio completes only after the tail has played ({sw.Elapsed.TotalSeconds:F2}s for 3.0s)");
            }
            else Console.WriteLine("SKIP: audio transport and completion timing (needs an audio output device)");

            // ── 4. Broken / missing file: internal error details, no external app, next file plays ─
            preview.RequestPlay(Media(Path.Combine(media, "broken.mp4")));
            Require(WaitFor(() => preview.State == PlaybackChannelState.Failed, 8000), "Corrupt MP4 did not fail.");
            var error = preview.LastError!;
            Require(error.Extension == "MP4" && error.Stage.Length > 0 && error.Suggestion.Length > 0 && error.FileName == "broken.mp4",
                "Error card data incomplete for a corrupt file.");
            preview.RequestPlay(Media(Path.Combine(media, "does-not-exist.mp4")));
            Require(WaitFor(() => preview.State == PlaybackChannelState.Failed, 8000) && preview.LastError!.Message.Contains("不存在"),
                "Missing file did not produce a 'not found' error.");
            preview.RequestPlay(Media(Path.Combine(media, "photo.jpg")));
            Require(WaitFor(() => preview.State == PlaybackChannelState.Playing, 5000), "Playback did not recover after a failed file.");
            Require(externalOpens == 0, "A playback path launched an external application.");
            Console.WriteLine("PASS: corrupt/missing files show internal error details and the next file still plays; no external app launched");

            // ── 5. Program channel: cast switch, Take, independent stop, device-cast exclusivity ───
            var machine = new OutputStateMachine();
            using var overlay = new OverlayWindow(new MonitorInfo("SELFTEST", new Rectangle(0, 0, 320, 240), false));
            _ = overlay.Handle; // never shown
            using var programVideo = SimulateNoVideo ? null : TryCreateVideoSurface(overlay.VideoHost.Handle, 320, 240);
            // What goes on air: the A/V clip when the machine can play it, otherwise a still image (the
            // Program/Take/cast-switch/device-cast logic under test is the same for both).
            var onAir = hasVideo && hasAudio ? av : Media(Path.Combine(media, "photo.jpg"));
            using var program = new PlaybackEngine(PlaybackChannel.Program, overlay, programVideo, settings, scenarios, machine, logs)
            {
                AudioVolume = 0f,
                Muted = true,
            };
            int localStarting = 0;
            program.LocalPlaybackStarting += () => localStarting++;

            preview.RequestPlay(onAir);
            Require(WaitFor(() => preview.State == PlaybackChannelState.Playing, 5000), "Preview content did not start.");

            machine.SetCastSwitch(false);
            bool declined = false;
            program.PlaybackDeclinedByCastSwitch += _ => declined = true;
            program.RequestPlay(onAir);
            Require(declined && program.State == PlaybackChannelState.Idle && machine.State == OutputState.Idle, "Cast switch off did not block Program.");
            Require(preview.State == PlaybackChannelState.Playing, "Cast switch off affected Preview.");
            Console.WriteLine("PASS: cast switch off blocks Program only; Preview keeps playing");

            machine.SetCastSwitch(true);
            Pump(700);
            var snapshot = preview.CaptureSnapshot()!.Value;
            Require(program.RequestPlay(snapshot), "Take was refused with the cast switch on.");
            Require(WaitFor(() => program.State == PlaybackChannelState.Playing, 5000), "Program did not start after Take.");
            Require(machine.State == OutputState.Active && machine.Source == ProgramSource.LocalMedia, "Take did not put local media on air.");
            Require(program.Position >= snapshot.Position - TimeSpan.FromMilliseconds(400), $"Take restarted instead of continuing ({program.Position} < {snapshot.Position}).");
            Console.WriteLine($"PASS: Take continues Preview content on Program at {snapshot.Position.TotalSeconds:F2}s");

            preview.Pause(); // pauses video; a still image without a stay timer simply stays up
            var heldAt = preview.Position;
            var heldState = preview.State;
            machine.SetCastSwitch(false); // switch off while live cuts local Program output
            Require(WaitFor(() => program.State == PlaybackChannelState.Idle, 3000) && machine.State == OutputState.Idle, "Switching off did not stop Program.");
            Require(ReferenceEquals(preview.CurrentFile, onAir) && preview.State == heldState
                    && Math.Abs((preview.Position - heldAt).TotalMilliseconds) < 150, "Stopping Program disturbed Preview's content or position.");
            Console.WriteLine("PASS: stopping Program (cast switch off) keeps Preview content and position");

            machine.SetCastSwitch(true);
            Require(program.RequestPlay(preview.CaptureSnapshot()!.Value), "Re-take failed.");
            Require(WaitFor(() => program.State == PlaybackChannelState.Playing, 5000), "Program did not restart on re-take.");
            program.StopForDeviceCast();          // what Program.cs does before accepting a device cast
            machine.AcceptDeviceCastRequest();
            Require(machine.Source == ProgramSource.DeviceCast && program.State == PlaybackChannelState.Idle, "Device cast did not replace local Program output.");
            Require(ReferenceEquals(preview.CurrentFile, onAir), "Device cast cleared the Preview.");
            machine.SetCastSwitch(false);
            Require(machine.State == OutputState.Active, "Cast switch cut a device cast (it only governs local output).");
            machine.SetCastSwitch(true);
            int before = localStarting;
            Require(program.RequestPlay(preview.CaptureSnapshot()!.Value), "Local Take over a device cast failed.");
            Require(localStarting == before + 1 && machine.Source == ProgramSource.LocalMedia, "Local Take did not preempt the device cast.");
            machine.Disconnect();
            Require(WaitFor(() => program.State == PlaybackChannelState.Idle, 3000), "Disconnect did not stop Program.");
            Console.WriteLine("PASS: device cast and local Program output are mutually exclusive; Preview is never cleared");

            preview.Stop();

            // ── 6. External open only on explicit request ─────────────────────────────────────────
            Require(externalOpens == 0, "Some playback path invoked the system default application.");
            ExternalOpener.OpenWithDefaultApp(null, av.SourcePath);
            Require(externalOpens == 1, "Explicit '使用系统默认程序打开' was not routed through ExternalOpener.");
            Console.WriteLine("PASS: system default application is only used on explicit request");
        }
        finally
        {
            ExternalOpener.TestHook = null;
        }
    }

    // Let the CI degradation path be exercised on a machine that does have a GPU and a sound card.
    private static bool SimulateNoVideo => Environment.GetEnvironmentVariable("ES_SELFTEST_NO_VIDEO") == "1";
    private static bool SimulateNoAudio => Environment.GetEnvironmentVariable("ES_SELFTEST_NO_AUDIO") == "1";

    private static VideoSurface? TryCreateVideoSurface(IntPtr hwnd, int width, int height)
    {
        try { return new VideoSurface(hwnd, width, height); }
        catch (Exception) { return null; } // no D3D11 video processing (e.g. hosted CI runner)
    }

    private static bool CanOpenAudioOutput()
    {
        try
        {
            using var clock = new EveryStage.Rendering.Audio.AudioPlaybackClock(48000, 2);
            return true;
        }
        catch (Exception)
        {
            return false; // no audio endpoint
        }
    }

    private static string? MissingCapability(string name, MediaKind kind, bool hasVideo, bool hasAudio) => kind switch
    {
        MediaKind.Video when !hasVideo => "本机无 GPU 视频处理能力（如托管 CI 机器），需在有显卡的机器上验证",
        MediaKind.Video when !hasAudio && !name.StartsWith("silent", StringComparison.OrdinalIgnoreCase) => "本机无音频输出设备，带音轨视频需在有声卡的机器上验证",
        MediaKind.Audio when !hasAudio => "本机无音频输出设备，需在有声卡的机器上验证",
        _ => null,
    };

    private static void TestTransport(PlaybackEngine preview, MediaFile file, string label)
    {
        preview.LoopCurrent = false;
        preview.RequestPlay(file);
        Require(WaitFor(() => preview.State == PlaybackChannelState.Playing && preview.Position > TimeSpan.FromMilliseconds(300), 6000), $"{label}: did not start.");

        Require(preview.CanPause, $"{label}: pause not available while playing.");
        preview.Pause();
        // WASAPI settles the reported position ~50 ms (the output latency) after Stop; measure from there.
        Pump(200);
        var p1 = preview.Position;
        Pump(600);
        var p2 = preview.Position;
        Require(preview.State == PlaybackChannelState.Paused && Math.Abs((p2 - p1).TotalMilliseconds) < 60, $"{label}: position moved while paused ({p1} -> {p2}).");
        preview.Resume();
        Require(WaitFor(() => preview.Position > p2 + TimeSpan.FromMilliseconds(250), 3000), $"{label}: resume did not continue.");

        Require(preview.SeekTo(TimeSpan.FromSeconds(2)), $"{label}: seek refused.");
        Pump(150);
        var afterSeek = preview.Position;
        Require(afterSeek >= TimeSpan.FromSeconds(1.8) && afterSeek <= TimeSpan.FromSeconds(2.8), $"{label}: seek to 2 s landed at {afterSeek}.");

        preview.Pause();
        Require(preview.SeekTo(TimeSpan.FromSeconds(1)), $"{label}: seek while paused refused.");
        Pump(300);
        Require(preview.IsPaused && Math.Abs((preview.Position - TimeSpan.FromSeconds(1)).TotalMilliseconds) < 250, $"{label}: seek while paused did not stay paused at 1 s ({preview.Position}).");
        preview.Resume();

        int starts = 0;
        void OnStarted(MediaFile _) => starts++;
        preview.FileStarted += OnStarted;
        preview.LoopCurrent = true;
        preview.SeekTo(TimeSpan.FromSeconds(2.5));
        Require(WaitFor(() => starts >= 1 && preview.State == PlaybackChannelState.Playing && preview.Position < TimeSpan.FromSeconds(1.5), 6000), $"{label}: loop did not restart the file.");
        preview.FileStarted -= OnStarted;
        preview.LoopCurrent = false;
        Console.WriteLine($"PASS: {label} pause / resume / seek (playing and paused) / loop");
    }

    private static void TestScaleMath()
    {
        var (src, dst) = SwapChainPresenter.ComputeRects(VideoScaleMode.Fit, 1920, 1080, 400, 300);
        Require(src.Width == 1920 && dst.Width == 400 && dst.Height == 225 && dst.Top == 37, "Fit rectangle wrong for 16:9 in 4:3.");
        (src, dst) = SwapChainPresenter.ComputeRects(VideoScaleMode.Fill, 1920, 1080, 400, 300);
        Require(dst.Width == 400 && dst.Height == 300 && src.Height == 1080 && src.Width == 1440 && src.Left == 240, "Fill rectangle wrong for 16:9 in 4:3.");
        (src, dst) = SwapChainPresenter.ComputeRects(VideoScaleMode.Stretch, 1920, 1080, 400, 300);
        Require(src.Width == 1920 && dst.Width == 400 && dst.Height == 300, "Stretch rectangle wrong.");
        Console.WriteLine("PASS: video fit / fill / stretch rectangles keep the aspect ratio");
    }

    /// <summary>Returns (file name, null) for generated fixtures and (extension, reason) for formats in
    /// the import whitelist that this machine cannot produce a sample of.</summary>
    private static List<(string Name, string? UnverifiedReason)> GenerateFixtures(string dir)
    {
        var list = new List<(string, string?)>();
        void Add(string name, Action<string> make)
        {
            string path = Path.Combine(dir, name);
            try { make(path); list.Add((name, null)); }
            catch (Exception ex) { list.Add((name, "本机无法生成样本：" + ex.Message.Split('\n')[0])); }
        }

        Add("photo.jpg", p => SaveSolid(p, 320, 240, Color.SteelBlue, ImageFormat.Jpeg));
        Add("photo.jpeg", p => SaveSolid(p, 320, 240, Color.SeaGreen, ImageFormat.Jpeg));
        Add("image.png", p => SaveSolid(p, 320, 240, Color.Orange, ImageFormat.Png));
        Add("image.bmp", p => SaveSolid(p, 320, 240, Color.Purple, ImageFormat.Bmp));
        Add("still.gif", p => SaveSolid(p, 320, 240, Color.Teal, ImageFormat.Gif));
        Add("anim.gif", WriteAnimatedGif);
        Add("pages.tif", WriteTwoPageTiff);
        Add("pages.tiff", WriteTwoPageTiff);
        Add("av.mp4", p => MediaFixtures.WriteVideo(p, TranscodeContainerTypeGuids.Mpeg4, VideoFormatGuids.H264, true, AudioFormatGuids.Aac));
        Add("silent.mp4", p => MediaFixtures.WriteVideo(p, TranscodeContainerTypeGuids.Mpeg4, VideoFormatGuids.H264, false));
        Add("clip.m4v", p => MediaFixtures.WriteVideo(p, TranscodeContainerTypeGuids.Mpeg4, VideoFormatGuids.H264, true, AudioFormatGuids.Aac));
        // QuickTime and MP4 share the ISO base media format; MF's MPEG-4 source is what opens .mov.
        Add("clip.mov", p => MediaFixtures.WriteVideo(p, TranscodeContainerTypeGuids.Mpeg4, VideoFormatGuids.H264, true, AudioFormatGuids.Aac));
        Add("clip.wmv", p => MediaFixtures.WriteVideo(p, TranscodeContainerTypeGuids.Asf, VideoFormatGuids.Wmv3, true, AudioFormatGuids.WMAudioV8));
        Add("tone.mp3", p => MediaFixtures.WriteAudio(p, TranscodeContainerTypeGuids.Mp3, AudioFormatGuids.Mp3));
        Add("tone.m4a", p => MediaFixtures.WriteAudio(p, TranscodeContainerTypeGuids.Mpeg4, AudioFormatGuids.Aac));
        Add("tone.aac", p => MediaFixtures.WriteAudio(p, TranscodeContainerTypeGuids.Adts, AudioFormatGuids.Aac));
        Add("tone.wma", p => MediaFixtures.WriteAudio(p, TranscodeContainerTypeGuids.Asf, AudioFormatGuids.WMAudioV8));
        Add("tone.flac", p => MediaFixtures.WriteAudio(p, TranscodeContainerTypeGuids.Flac, AudioFormatGuids.Flac));
        Add("tone.wav", p => MediaFixtures.WriteWav(p));

        // Whitelisted, but Windows ships no encoder/muxer to produce a sample without third-party tools.
        list.Add((".mkv", "Windows 无 MKV 封装器，需用真实 MKV 文件真机验证"));
        list.Add((".avi", "MF Sink Writer 无法写 H.264 AVI，需用真实 AVI 文件真机验证"));

        // Fixtures used by specific tests (not part of the format matrix loop).
        SaveSolid(Path.Combine(dir, "wide.png"), 200, 100, Color.Red, ImageFormat.Png);
        File.WriteAllText(Path.Combine(dir, "broken.mp4"), "this is not a video file");

        // Every whitelisted extension must be covered by the matrix (verified or explicitly unverified).
        foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".mp4", ".mkv", ".mov", ".avi", ".wmv", ".m4v", ".mp3", ".wav", ".flac", ".aac", ".m4a", ".wma" })
        {
            Require(FileLibraryStore.InferKind("x" + ext) != null, $"{ext} unexpectedly not importable.");
            Require(list.Any(e => Path.GetExtension(e.Item1).Equals(ext, StringComparison.OrdinalIgnoreCase) || e.Item1 == ext), $"{ext} has no test result.");
        }
        Require(FileLibraryStore.InferKind("x.webp") == null, "WebP is importable but GDI+ cannot decode it.");
        return list;
    }

    private static string DescribePlaying(PlaybackEngine engine, MediaFile file) => file.Kind switch
    {
        MediaKind.Image => $"图片 {engine.CurrentThumbnail?.Width}x{engine.CurrentThumbnail?.Height}{(engine.CurrentImageNote is { } n ? "，" + n : "")}",
        MediaKind.Video => $"视频 {engine.CurrentVideoCodec}{(engine.CurrentVideoHasAudio ? " + 音轨" : "，无音轨")}，时长 {engine.Duration?.TotalSeconds:F2}s",
        _ => $"音频，时长 {engine.Duration?.TotalSeconds:F2}s",
    };

    private static MediaFile Media(string path) => new()
    {
        SourcePath = path,
        Kind = FileLibraryStore.InferKind(path) ?? MediaKind.Video,
        OnCompletion = CompletionAction.HoldOnLastFrame,
    };

    private static void SaveSolid(string path, int w, int h, Color color, ImageFormat format)
    {
        using var bitmap = new Bitmap(w, h);
        using (var g = Graphics.FromImage(bitmap)) g.Clear(color);
        bitmap.Save(path, format);
    }

    private static void WriteTwoPageTiff(string path)
    {
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/tiff");
        using var page1 = new Bitmap(160, 120);
        using var page2 = new Bitmap(160, 120);
        using (var g = Graphics.FromImage(page1)) g.Clear(Color.Red);
        using (var g = Graphics.FromImage(page2)) g.Clear(Color.Blue);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.SaveFlag, (long)EncoderValue.MultiFrame);
        page1.Save(path, codec, parameters);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.SaveFlag, (long)EncoderValue.FrameDimensionPage);
        page1.SaveAdd(page2, parameters);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.SaveFlag, (long)EncoderValue.Flush);
        page1.SaveAdd(parameters);
    }

    /// <summary>A 1x1 two-frame GIF89a, written byte by byte (GDI+ cannot encode animation).</summary>
    private static void WriteAnimatedGif(string path)
    {
        byte[] frame = { 0x21, 0xF9, 0x04, 0x00, 0x0A, 0x00, 0x00, 0x00, 0x2C, 0, 0, 0, 0, 1, 0, 1, 0, 0, 0x02, 0x02, 0x44, 0x01, 0x00 };
        var bytes = new List<byte>();
        bytes.AddRange("GIF89a"u8.ToArray());
        bytes.AddRange(new byte[] { 1, 0, 1, 0, 0x80, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF });
        bytes.AddRange(frame);
        bytes.AddRange(frame);
        bytes.Add(0x3B);
        File.WriteAllBytes(path, bytes.ToArray());
    }

    private static bool WaitFor(Func<bool> condition, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            Application.DoEvents();
            if (condition()) return true;
            Thread.Sleep(10);
        }
        Application.DoEvents();
        return condition();
    }

    private static void Pump(int ms) => WaitFor(() => false, ms);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
