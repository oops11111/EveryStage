using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net;
using EveryStage.Rendering;
using EveryStage.Discovery;
using EveryStage.Terminal.Data;
using EveryStage.Terminal.Devices;
using EveryStage.Terminal.Display;
using EveryStage.Terminal.Logging;
using EveryStage.Terminal.Playback;
using EveryStage.Terminal.Receiving;
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
        // Sections 7-9 play several 8 s clips in real time (audio output loss, slow decode, decode stall).
        if (!thread.Join(TimeSpan.FromMinutes(6))) throw new TimeoutException("Preview/Program tests did not finish.");
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
            TestOperatorScreenPlacement();
            var fixtures = GenerateFixtures(media);

            using var form = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ClientSize = new Size(400, 300) };
            var previewSurface = new PreviewSurface { Dock = DockStyle.Fill };
            form.Controls.Add(previewSurface);
            _ = form.Handle;
            form.PerformLayout();
            Require(previewSurface.Viewport == previewSurface.ClientRectangle, "Preview content does not use the whole preview area.");

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

            // ── 2. Preview fills its width, keeps the aspect ratio, crops vertically when needed ──────
            preview.RequestPlay(Media(Path.Combine(media, "wide.png")));
            Require(WaitFor(() => preview.State == PlaybackChannelState.Playing, 5000), "Wide PNG did not load.");
            Pump(200);
            using (var shot = new Bitmap(previewSurface.ContentSurface.Width, previewSurface.ContentSurface.Height))
            {
                previewSurface.ContentSurface.DrawToBitmap(shot, new Rectangle(Point.Empty, shot.Size));
                var top = shot.GetPixel(shot.Width / 2, 2);
                var middle = shot.GetPixel(shot.Width / 2, shot.Height / 2);
                var leftEdge = shot.GetPixel(1, shot.Height / 2);
                var rightEdge = shot.GetPixel(shot.Width - 2, shot.Height / 2);
                Require(top.R < 30 && top.G < 30 && top.B < 30, "Letterbox band missing above a wide image (image was stretched).");
                Require(middle.R > 200 && middle.G < 60, "Wide image content not drawn in the middle of the preview.");
                Require(leftEdge.R > 200 && rightEdge.R > 200, "Wide image does not fill the preview width.");
            }
            preview.RequestPlay(Media(Path.Combine(media, "tall.png")));
            Require(WaitFor(() => preview.State == PlaybackChannelState.Playing, 5000), "Tall PNG did not load.");
            Pump(200);
            using (var shot = new Bitmap(previewSurface.ContentSurface.Width, previewSurface.ContentSurface.Height))
            {
                previewSurface.ContentSurface.DrawToBitmap(shot, new Rectangle(Point.Empty, shot.Size));
                foreach (var p in new[] { new Point(1, 1), new Point(shot.Width - 2, 1), new Point(1, shot.Height - 2), new Point(shot.Width / 2, shot.Height / 2) })
                {
                    var c = shot.GetPixel(p.X, p.Y);
                    Require(c.B > 200 && c.R < 60, $"Tall image not scaled to full width (pixel {p} is {c}): it was fitted instead of filling the width.");
                }
            }
            Console.WriteLine("PASS: preview fills its width with the aspect ratio kept (wide content letterboxed, tall content cropped top/bottom)");

            // ── 2b. Phone photos: EXIF Orientation is applied (audit B-23) ─────────────────────────
            string rotated = Path.Combine(media, "portrait-exif6.jpg");
            WriteOrientedJpeg(rotated, orientation: 6);
            preview.RequestPlay(Media(rotated));
            Require(WaitFor(() => preview.State == PlaybackChannelState.Playing && preview.CurrentThumbnail != null, 5000), "EXIF-rotated JPEG did not load.");
            var upright = preview.CurrentThumbnail!;
            Require(upright.Width == 100 && upright.Height == 200, $"Orientation=6 photo shown {upright.Width}x{upright.Height}, expected upright 100x200.");
            var topPixel = upright.GetPixel(50, 20);
            var bottomPixel = upright.GetPixel(50, 180);
            Require(topPixel.R > 180 && topPixel.B < 80 && bottomPixel.B > 180 && bottomPixel.R < 80,
                $"Orientation=6 photo rotated the wrong way (top {topPixel}, bottom {bottomPixel}).");
            Console.WriteLine("PASS: EXIF orientation is applied (a phone portrait photo shows upright)");

            // ── 2c. Very large photos: bounded memory, no full-size rescale per repaint (audit C-29) ──
            string huge = Path.Combine(media, "huge-48mp.png");
            using (var big = new Bitmap(8000, 6000))
            {
                using (var g = Graphics.FromImage(big)) g.Clear(Color.DarkCyan);
                big.Save(huge, ImageFormat.Png);
            }
            preview.RequestPlay(Media(huge));
            Require(WaitFor(() => preview.State == PlaybackChannelState.Playing && preview.CurrentThumbnail != null, 15000), "48 MP image did not load.");
            var kept = preview.CurrentThumbnail!;
            Require(Math.Max(kept.Width, kept.Height) <= 3840 && kept.Width * 3 == kept.Height * 4,
                $"48 MP image kept at {kept.Width}x{kept.Height} (expected at most 3840 px, same aspect ratio).");
            var surface = previewSurface.ContentSurface;
            using (var shot = new Bitmap(Math.Max(1, surface.Width), Math.Max(1, surface.Height)))
            {
                surface.DrawToBitmap(shot, new Rectangle(Point.Empty, shot.Size)); // first paint scales once
                var repaint = Stopwatch.StartNew();
                for (int i = 0; i < 5; i++) surface.DrawToBitmap(shot, new Rectangle(Point.Empty, shot.Size));
                double perRepaint = repaint.Elapsed.TotalMilliseconds / 5;
                Require(perRepaint < 60, $"Repainting a large still image takes {perRepaint:F0} ms (rescaled on every paint).");
                Console.WriteLine($"PASS: a 48 MP photo is kept at {kept.Width}x{kept.Height} and repaints from a cached scale ({perRepaint:F1} ms)");
            }

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
            Require(machine.State == OutputState.Active && machine.Source == ProgramSource.DeviceCast,
                "The raw state machine should not reject an admitted device cast; Program retracts it at the controller layer.");
            machine.SetCastSwitch(true);
            int before = localStarting;
            Require(program.RequestPlay(preview.CaptureSnapshot()!.Value), "Local Take over a device cast failed.");
            Require(localStarting == before + 1 && machine.Source == ProgramSource.LocalMedia, "Local Take did not preempt the device cast.");
            machine.Disconnect();
            Require(WaitFor(() => program.State == PlaybackChannelState.Idle, 3000), "Disconnect did not stop Program.");
            Console.WriteLine("PASS: device cast and local Program output are mutually exclusive; Preview is never cleared");

            TestDeviceCastControllerFlow(directory, media, settings, scenarios, hasVideo);
            TestCastReceiverAttachDetachSmoke(hasVideo);

            preview.Stop();

            // ── 6. External open only on explicit request ─────────────────────────────────────────
            Require(externalOpens == 0, "Some playback path invoked the system default application.");
            ExternalOpener.OpenWithDefaultApp(null, av.SourcePath);
            Require(externalOpens == 1, "Explicit '使用系统默认程序打开' was not routed through ExternalOpener.");
            Console.WriteLine("PASS: system default application is only used on explicit request");

            // ── 7. Audio output failures (audit A-24 / B-20 / B-21) ─────────────────────────────────
            TestAudioOutputFailures(preview, media, logs, hasVideo, hasAudio);

            // ── 8. Decoding slower than real time (audit B-24) ──────────────────────────────────────
            TestSlowDecode(preview, media, logs, hasVideo);

            // ── 9. Audio timestamps vs the rendered-bytes clock (audit B-22) ───────────────────────
            TestAudioTimeline(preview, media, hasAudio);
        }
        finally
        {
            ExternalOpener.TestHook = null;
            EveryStage.Rendering.Audio.AudioPlaybackClock.SimulateNoOutputDevice = false;
            EveryStage.Terminal.ContentEngine.VideoContentController.SimulatedDecodeDelay = TimeSpan.Zero;
            EveryStage.Terminal.ContentEngine.AudioContentController.SimulatedStall = TimeSpan.Zero;
        }
    }

    private static void TestOperatorScreenPlacement()
    {
        Screen primary = Screen.PrimaryScreen ?? Screen.AllScreens.First();
        using var owner = new Form
        {
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = primary.WorkingArea.Location + new Size(24, 24),
            ClientSize = new Size(220, 120),
            Opacity = 0,
        };
        owner.Show();
        Pump(100);

        using var visibleOwnerDialog = new Form { ShowInTaskbar = false, ClientSize = new Size(180, 90) };
        OperatorScreen.PlaceOnOperatorScreen(visibleOwnerDialog, owner, null);
        Point expected = CenterOf(primary.WorkingArea, visibleOwnerDialog.Size);
        Require(visibleOwnerDialog.StartPosition == FormStartPosition.Manual
                && visibleOwnerDialog.Location == expected,
            $"Visible-owner confirmation dialog was not centered on the operator screen: {visibleOwnerDialog.Location} vs {expected}.");

        owner.Hide();
        using var hiddenOwnerDialog = new Form { ShowInTaskbar = false, ClientSize = new Size(180, 90) };
        OperatorScreen.PlaceOnOperatorScreen(
            hiddenOwnerDialog,
            owner,
            new MonitorInfo(primary.DeviceName, primary.Bounds, true));
        expected = CenterOf(primary.WorkingArea, hiddenOwnerDialog.Size);
        Require(hiddenOwnerDialog.StartPosition == FormStartPosition.Manual
                && hiddenOwnerDialog.Location == expected,
            $"Hidden-owner confirmation dialog was not centered on the fallback operator screen: {hiddenOwnerDialog.Location} vs {expected}.");

        Console.WriteLine("PASS: operator confirmation dialogs use the owner screen when visible and the primary fallback when hidden");
    }

    private static Point CenterOf(Rectangle area, Size size) => new(
        area.Left + Math.Max(0, (area.Width - size.Width) / 2),
        area.Top + Math.Max(0, (area.Height - size.Height) / 2));

    /// <summary>The clock counts rendered bytes, silence included. Gaps in a track must be filled and
    /// overlaps trimmed (so a video's sound stays on its picture), and silence rendered during a decode
    /// stall must not count as played content (so an audio file's position and fade-out stay right and
    /// nothing after the stall is skipped).</summary>
    private static void TestDeviceCastControllerFlow(string directory, string media, SettingsStore settings, ScenarioStore scenarios, bool hasVideo)
    {
        if (!hasVideo)
        {
            Console.WriteLine("SKIP: DeviceCastController preview/program/revert flow (needs GPU video processing)");
            return;
        }

        string dir = Path.Combine(directory, "device-controller");
        Directory.CreateDirectory(dir);
        var machine = new OutputStateMachine();
        var identity = DeviceIdentity.LoadOrCreate("device-controller", Path.Combine(dir, "identity.json"));
        using var overlay = new OverlayWindow(new MonitorInfo("DEVICE-SELFTEST", new Rectangle(0, 0, 320, 240), false));
        _ = overlay.Handle;
        using var programVideo = TryCreateVideoSurface(overlay.VideoHost.Handle, 320, 240);
        if (programVideo == null)
        {
            Console.WriteLine("SKIP: DeviceCastController preview/program/revert flow (VideoSurface unavailable)");
            return;
        }

        using var program = new PlaybackEngine(PlaybackChannel.Program, overlay, programVideo, settings, scenarios, machine, dir)
        {
            AudioVolume = 0f,
            Muted = true,
        };
        using var window = new MainWindow(machine, program, new FileLibraryStore(Path.Combine(dir, "library.json")),
            new PairedDeviceStore(Path.Combine(dir, "paired.json")), scenarios, new ScenarioRepository(Path.Combine(dir, "scenarios.json")),
            settings, identity, new DeviceConnectionLogger(dir))
        { Opacity = 0, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000) };
        window.Show();
        Pump(300);

        var transport = new FakeDeviceCastTransport();
        var logger = new DeviceConnectionLogger(dir);
        FakeDeviceCastStream? currentStream = null;
        var streams = new List<FakeDeviceCastStream>();
        var replacementDecisions = new Queue<bool>();
        int replacementPrompts = 0;
        string? lastReplacementNewDevice = null;
        string? lastReplacementCurrentDevice = null;
        bool lastReplacementCurrentOnProgram = false;
        DeviceCastController? controller = null;
        controller = new DeviceCastController(window, identity.DeviceId, transport, logger, machine,
            () => overlay, () => programVideo, () => program,
            (surface, width, height, videoPort, hasAudio, audioSampleRate, audioChannels, audioPort,
                audioIsAac, payloadType, audioPayloadType, mediaSessionId, videoKey, audioKey, allowedSender) =>
            {
                currentStream = new FakeDeviceCastStream();
                streams.Add(currentStream);
                return currentStream;
            },
            (info, currentDeviceName, currentOnProgram, _) =>
            {
                replacementPrompts++;
                lastReplacementNewDevice = info.DeviceName;
                lastReplacementCurrentDevice = currentDeviceName;
                lastReplacementCurrentOnProgram = currentOnProgram;
                return replacementDecisions.Dequeue();
            });
        void DropDeviceForLocalTake() => controller?.DropFromProgram(discardRestorePoint: true);
        void DropDeviceForIdle(OutputState state)
        {
            if (state == OutputState.Idle) controller?.DropFromProgram(discardRestorePoint: true);
        }
        void RetractDeviceForCastSwitchOff(bool on)
        {
            if (!on) controller?.RetractForCastSwitchOff();
        }
        program.LocalPlaybackStarting += DropDeviceForLocalTake;
        machine.StateChanged += DropDeviceForIdle;
        machine.CastSwitchChanged += RetractDeviceForCastSwitchOff;
        try
        {
        machine.SetCastSwitch(false);
            var switchOffId = Guid.NewGuid();
            var switchOff = CastInfo(switchOffId, "开关关闭设备", (ready, error, pending) =>
            {
                Require(ready && !pending && error == null,
                    $"Switch-off device request should still enter preview: ready={ready}, pending={pending}, error={error}");
            });
            controller.Connect(switchOff);
            Require(controller.HasReceiver && currentStream != null && currentStream.AudioMuted,
                "Switch-off device request should create a muted preview receiver.");
            controller.StopForDevice(switchOffId);
            Require(!controller.HasReceiver, "Switch-off preview setup did not stop cleanly.");
            machine.SetCastSwitch(true);

            var photo = Media(Path.Combine(media, "photo.jpg"));
            program.RequestPlay(photo);
            Require(WaitFor(() => program.State == PlaybackChannelState.Playing, 5000), "Program restore fixture did not start.");
            Require(machine.Source == ProgramSource.LocalMedia, "Program fixture is not local media.");

            var first = CastInfo(Guid.NewGuid(), "会议室笔记本", (ready, error, pending) =>
            {
                Require(ready && !pending && error == null, $"First device was not accepted: ready={ready}, pending={pending}, error={error}");
            });
            controller.Connect(first);
            Require(controller.HasReceiver && currentStream != null && currentStream.StartCount == 1, "Device did not connect to the preview stream.");
            Require(currentStream!.AudioMuted, "Preview stream should start muted.");
            Require(machine.Source == ProgramSource.LocalMedia && program.State == PlaybackChannelState.Playing,
                "Connecting a device changed Program before 投屏.");
            controller.SendStatus();
            Require(transport.LastStatus?.DeviceId == identity.DeviceId && transport.LastStatus.OnProgram == false,
                "Preview status should report the Terminal device id and OnProgram=false.");

            controller.TakeToProgram();
            Require(machine.Source == ProgramSource.DeviceCast && program.State == PlaybackChannelState.Idle,
                "投屏 did not move the device to Program and stop local playback.");
            Require(currentStream.AttachCount == 1 && !currentStream.AudioMuted, "投屏 did not attach/unmute the stream.");
            controller.SendStatus();
            Require(transport.LastStatus?.DeviceId == identity.DeviceId && transport.LastStatus.OnProgram == true,
                "Program status should report the Terminal device id and OnProgram=true.");

            controller.RevertProgram();
            Require(WaitFor(() => machine.Source == ProgramSource.LocalMedia && program.State == PlaybackChannelState.Playing, 5000),
                "回退 did not restore local Program content.");
            Require(currentStream.DetachCount == 1 && currentStream.AudioMuted && controller.HasReceiver,
                "回退 should detach/mute the stream but keep the device connected.");

            controller.TakeToProgram();
            machine.SetCastSwitch(false);
            Require(WaitFor(() => machine.Source == ProgramSource.LocalMedia && program.State == PlaybackChannelState.Playing, 5000)
                    && controller.HasReceiver && !controller.IsOnProgram && currentStream.DetachCount >= 2 && currentStream.AudioMuted,
                "Cast switch off should retract an active device cast to muted preview and restore the previous Program content.");
            machine.SetCastSwitch(true);

            controller.TakeToProgram();
            program.RequestPlay(photo);
            Require(WaitFor(() => machine.Source == ProgramSource.LocalMedia && program.State == PlaybackChannelState.Playing, 5000),
                "Local Take did not preempt the device cast.");
            Require(controller.HasReceiver && !controller.IsOnProgram && currentStream.DetachCount >= 2 && currentStream.AudioMuted,
                "Local Take should return the device to muted preview without disconnecting it.");
            controller.RevertProgram();
            Require(machine.Source == ProgramSource.LocalMedia && program.State == PlaybackChannelState.Playing,
                "A local takeover should discard the device restore point, so 回退 must not replace local Program output.");

            controller.TakeToProgram();
            int promptsBeforeSameDevice = replacementPrompts;
            var oldSameDeviceStream = currentStream!;
            var sameDeviceResponses = new List<(bool Ready, string? Error, bool Pending)>();
            var sameDeviceReconnect = CastInfo(first.DeviceId, "会议室笔记本", (ready, error, pending) =>
                sameDeviceResponses.Add((ready, error, pending)));
            controller.Connect(sameDeviceReconnect);
            Require(replacementPrompts == promptsBeforeSameDevice, "Same-device reconnect should not ask for second-device confirmation.");
            Require(sameDeviceResponses.Count == 1 && sameDeviceResponses[0].Ready && !sameDeviceResponses[0].Pending && sameDeviceResponses[0].Error == null,
                "Same-device reconnect did not complete immediately.");
            Require(oldSameDeviceStream.Disposed && currentStream != null && !ReferenceEquals(currentStream, oldSameDeviceStream)
                    && controller.IsOnProgram && machine.Source == ProgramSource.DeviceCast && !currentStream.AudioMuted
                    && currentStream.AttachCount == 1,
                "Same-device reconnect while on Program did not replace the stream directly on Program.");
            controller.RevertProgram();
            Require(WaitFor(() => machine.Source == ProgramSource.LocalMedia && program.State == PlaybackChannelState.Playing, 5000),
                "Same-device reconnect did not retain the original restore point.");

            var firstStream = currentStream;
            var rejectedResponses = new List<(bool Ready, string? Error, bool Pending)>();
            replacementDecisions.Enqueue(false);
            var rejected = CastInfo(Guid.NewGuid(), "拒绝的设备", (ready, error, pending) =>
                rejectedResponses.Add((ready, error, pending)));
            controller.Connect(rejected);
            Require(replacementPrompts == 1 && lastReplacementNewDevice == "拒绝的设备"
                    && lastReplacementCurrentDevice == "会议室笔记本" && !lastReplacementCurrentOnProgram,
                "Second-device rejection did not show the expected preview-state confirmation.");
            Require(rejectedResponses.Count == 2
                    && !rejectedResponses[0].Ready && rejectedResponses[0].Pending && rejectedResponses[0].Error == null
                    && !rejectedResponses[1].Ready && !rejectedResponses[1].Pending && rejectedResponses[1].Error != null,
                "Second-device rejection did not send Pending then final rejection.");
            Require(controller.DeviceId == first.DeviceId && ReferenceEquals(currentStream, firstStream)
                    && firstStream != null && !firstStream.Disposed && controller.HasReceiver,
                "Rejecting a second device disturbed the current preview session.");

            controller.TakeToProgram();
            int promptsBeforeAccept = replacementPrompts;
            var firstStreamBeforeAccept = currentStream!;
            var acceptedResponses = new List<(bool Ready, string? Error, bool Pending)>();
            replacementDecisions.Enqueue(true);
            var accepted = CastInfo(Guid.NewGuid(), "访客电脑", (ready, error, pending) =>
                acceptedResponses.Add((ready, error, pending)));
            controller.Connect(accepted);
            Require(replacementPrompts == promptsBeforeAccept + 1 && lastReplacementNewDevice == "访客电脑"
                    && lastReplacementCurrentDevice == "会议室笔记本" && lastReplacementCurrentOnProgram,
                "Second-device acceptance did not show the expected on-Program confirmation.");
            Require(acceptedResponses.Count == 2
                    && !acceptedResponses[0].Ready && acceptedResponses[0].Pending && acceptedResponses[0].Error == null
                    && acceptedResponses[1].Ready && !acceptedResponses[1].Pending && acceptedResponses[1].Error == null,
                "Second-device acceptance did not send Pending then final ready.");
            Require(firstStreamBeforeAccept.Disposed && controller.DeviceId == accepted.DeviceId
                    && currentStream != null && !ReferenceEquals(currentStream, firstStreamBeforeAccept)
                    && currentStream.AudioMuted && !controller.IsOnProgram,
                "Accepting a second device did not dispose the old stream and leave the new one muted in preview.");
            Require(machine.Source == ProgramSource.LocalMedia && program.State == PlaybackChannelState.Playing,
                "Accepting a second device while the first was on Program did not restore local Program output.");

            controller.TakeToProgram();
            controller.StopForDevice(accepted.DeviceId);
            Require(WaitFor(() => machine.Source == ProgramSource.LocalMedia && program.State == PlaybackChannelState.Playing, 5000),
                "Device disconnect while on Program did not restore local Program content.");
            Require(!controller.HasReceiver, "Device disconnect did not release the receiver.");

            var timeoutDevice = CastInfo(Guid.NewGuid(), "超时电脑", (ready, error, pending) =>
            {
                Require(ready && !pending && error == null, $"Timeout device was not accepted: ready={ready}, pending={pending}, error={error}");
            });
            controller.Connect(timeoutDevice);
            controller.TakeToProgram();
            currentStream!.LastPacketReceivedAt = DateTime.UtcNow - TimeSpan.FromSeconds(30);
            controller.CheckLiveness();
            Require(WaitFor(() => machine.Source == ProgramSource.LocalMedia && program.State == PlaybackChannelState.Playing, 5000),
                "Liveness timeout while on Program did not restore local Program content.");
            Require(!controller.HasReceiver && currentStream.Disposed, "Liveness timeout did not release the receiver.");

            var failedDevice = CastInfo(Guid.NewGuid(), "解码失败电脑", (ready, error, pending) =>
            {
                Require(ready && !pending && error == null, $"Failed device was not accepted: ready={ready}, pending={pending}, error={error}");
            });
            controller.Connect(failedDevice);
            controller.TakeToProgram();
            currentStream!.PresentLoopFailed = true;
            controller.CheckDecodeHealth();
            Require(WaitFor(() => machine.Source == ProgramSource.LocalMedia && program.State == PlaybackChannelState.Playing, 5000),
                "Decode/present failure while on Program did not restore local Program content.");
            Require(!controller.HasReceiver && currentStream.Disposed, "Decode/present failure did not release the receiver.");

            program.Stop();
            machine.Disconnect();
            var second = CastInfo(Guid.NewGuid(), "访客电脑", (ready, error, pending) =>
            {
                Require(ready && !pending && error == null, $"Second device was not accepted: ready={ready}, pending={pending}, error={error}");
            });
            controller.Connect(second);
            Require(currentStream != null && currentStream.AudioMuted, "Second preview stream should start muted.");
            controller.TakeToProgram();
            Require(machine.Source == ProgramSource.DeviceCast, "Idle restore case did not put device on Program.");
            controller.RevertProgram();
            Require(machine.State == OutputState.Idle && !controller.IsOnProgram && controller.HasReceiver,
                "回退 from an originally idle Program should return Program to Idle and keep preview connected.");
            Console.WriteLine("PASS: DeviceCastController keeps device preview local until 投屏, restores Program on 回退/disconnect/timeout/decode failure, same-device reconnect stays on Program, local Take keeps device connected in preview, and idle restore stays idle");
        }
        finally
        {
            program.LocalPlaybackStarting -= DropDeviceForLocalTake;
            machine.StateChanged -= DropDeviceForIdle;
            machine.CastSwitchChanged -= RetractDeviceForCastSwitchOff;
            controller.Dispose();
            window.Close();
        }
    }

    private static DiscoveryService.CastStartInfo CastInfo(Guid deviceId, string name, Action<bool, string?, bool> complete) =>
        new(deviceId, name, 1280, 720, 96,
            true, 48000, 2, 97, false,
            new IPEndPoint(IPAddress.Loopback, DiscoveryProtocol.Port), Guid.NewGuid(),
            new byte[32], new byte[32], complete);

    private static void TestCastReceiverAttachDetachSmoke(bool hasVideo)
    {
        if (!hasVideo)
        {
            Console.WriteLine("SKIP: CastReceiver shared-GPU Attach/Detach smoke (needs GPU video processing)");
            return;
        }

        using var form = new Form { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-32000, -32000), ClientSize = new Size(640, 240) };
        var previewHost = new Panel { Bounds = new Rectangle(0, 0, 320, 240) };
        var programHost = new Panel { Bounds = new Rectangle(320, 0, 320, 240) };
        form.Controls.Add(previewHost);
        form.Controls.Add(programHost);
        form.Show();
        Pump(100);

        using var previewSurface = TryCreateVideoSurface(previewHost.Handle, 320, 240);
        if (previewSurface == null)
        {
            Console.WriteLine("SKIP: CastReceiver shared-GPU Attach/Detach smoke (VideoSurface unavailable)");
            return;
        }
        using var programSurface = new VideoSurface(previewSurface.Gpu, programHost.Handle, 320, 240);
        CastReceiver? receiver = null;
        try
        {
            receiver = new CastReceiver(previewSurface, 1280, 720, listenPort: 0,
                hasAudio: false, payloadType: 96, mediaSessionId: Guid.NewGuid(),
                videoKey: new byte[32], audioKey: new byte[32], expectedAddress: IPAddress.Loopback);

            var sw = Stopwatch.StartNew();
            receiver.AttachProgram(programSurface);
            receiver.DetachProgram();
            sw.Stop();
            Require(sw.ElapsedMilliseconds < 500, $"Attach/Detach without frames took {sw.ElapsedMilliseconds} ms.");
            Console.WriteLine("PASS: CastReceiver Attach/Detach with two shared-GPU surfaces returns immediately without frames");
        }
        finally
        {
            receiver?.Dispose();
        }
    }

    private static void TestAudioTimeline(PlaybackEngine preview, string media, bool hasAudio)
    {
        static long Ticks(double ms) => (long)(ms * TimeSpan.TicksPerMillisecond);
        var chunk = new byte[480 * 4]; // 10 ms, 48 kHz stereo
        var gap = EveryStage.Terminal.ContentEngine.PcmTrim.AlignTo(chunk, Ticks(1100), Ticks(1000), 48000, 2);
        var overlap = EveryStage.Terminal.ContentEngine.PcmTrim.AlignTo(chunk, Ticks(995), Ticks(1000), 48000, 2);
        var late = EveryStage.Terminal.ContentEngine.PcmTrim.AlignTo(chunk, Ticks(900), Ticks(1000), 48000, 2);
        var jitter = EveryStage.Terminal.ContentEngine.PcmTrim.AlignTo(chunk, Ticks(1010), Ticks(1000), 48000, 2);
        Require(gap != null && gap.Length == (4800 + 480) * 4, $"A 100 ms gap was not filled with 100 ms of silence ({gap?.Length / 4} frames).");
        Require(ReferenceEquals(overlap, chunk) && ReferenceEquals(jitter, chunk), "Timestamp jitter within tolerance was altered.");
        Require(late == null, "Audio entirely due before the play head was not dropped.");
        Console.WriteLine("PASS: audio chunks are lined up with their play time (gaps filled, late/overlapping audio trimmed, jitter left alone)");

        if (!hasAudio)
        {
            Console.WriteLine("SKIP: decode-stall position accounting (needs an audio output device)");
            return;
        }
        string tone = Path.Combine(media, "long-tone.wav");
        if (!File.Exists(tone)) MediaFixtures.WriteWav(tone, seconds: 8);
        try
        {
            EveryStage.Terminal.ContentEngine.AudioContentController.SimulatedStall = TimeSpan.FromMilliseconds(2500);
            var sw = Stopwatch.StartNew();
            preview.RequestPlay(Media(tone));
            Pump(5000);
            double wall = sw.Elapsed.TotalSeconds, position = preview.Position.TotalSeconds;
            // ~1 s was buffered when decoding stalled for 2.5 s, so ~1.5 s of padding was rendered.
            Require(position < wall - 1.0 && position > wall - 2.2,
                $"Position counts silence rendered during the stall: {position:F2} s of content at {wall:F2} s wall time.");
            double lastPosition = 0;
            Require(WaitFor(() => { if (!preview.IsCompleted) lastPosition = preview.Position.TotalSeconds; return preview.IsCompleted; }, 9000),
                "Tone never completed after the stall.");
            Require(sw.Elapsed.TotalSeconds > 9.0, $"8 s tone with a stall completed after {sw.Elapsed.TotalSeconds:F2} s: content was skipped.");
            Require(lastPosition > 7.5, $"Position before completion was {lastPosition:F2} s of 8: content was discarded.");
            Console.WriteLine($"PASS: a decode stall does not count as played content ({position:F2} s at {wall:F2} s wall) and nothing after it is skipped");
        }
        finally
        {
            EveryStage.Terminal.ContentEngine.AudioContentController.SimulatedStall = TimeSpan.Zero;
        }
    }

    /// <summary>A machine too slow for the file used to drop every late frame — and every frame is late
    /// once decoding is slower than real time — freezing the picture until the end. The picture must keep
    /// updating (at least every 250 ms), with the reason shown and logged.</summary>
    private static void TestSlowDecode(PlaybackEngine preview, string media, string logs, bool hasVideo)
    {
        if (!hasVideo)
        {
            Console.WriteLine("SKIP: slow-decode handling (needs GPU video processing)");
            return;
        }
        string clip = Path.Combine(media, "long-silent.mp4");
        if (!File.Exists(clip)) MediaFixtures.WriteVideo(clip, TranscodeContainerTypeGuids.Mpeg4, VideoFormatGuids.H264, false, seconds: 8);
        try
        {
            EveryStage.Terminal.ContentEngine.VideoContentController.SimulatedDecodeDelay = TimeSpan.FromMilliseconds(60); // ~16 fps for a 30 fps clip
            preview.RequestPlay(Media(clip));
            Require(WaitFor(() => preview.VideoFrameStats.Presented >= 3, 5000), "Slow-decoded clip did not start.");
            Pump(1000); // well behind real time by now
            long before = preview.VideoFrameStats.Presented;
            Pump(2000);
            long presented = preview.VideoFrameStats.Presented - before;
            // The picture is refreshed on the first frame decoded after a 250 ms gap: ~3/s at this decode speed.
            Require(presented >= 5, $"Picture froze while decoding was too slow: {presented} frames presented in 2 s.");
            Require(preview.VideoFrameStats.Dropped > 0, "No frames were dropped although decoding ran at half speed.");
            Require(WaitFor(() => (preview.CurrentContentNote ?? "").Contains("解码跟不上"), 3000), $"No 'decoding too slow' note: {preview.CurrentContentNote}");
            Require(Directory.GetFiles(Path.Combine(logs, "playback")).Any(f => File.ReadAllText(f).Contains("video_decode_too_slow")),
                "Slow decoding was not logged.");
            preview.Stop();
            Console.WriteLine($"PASS: decoding slower than real time keeps the picture moving ({presented} frames in 2 s), noted and logged");
        }
        finally
        {
            EveryStage.Terminal.ContentEngine.VideoContentController.SimulatedDecodeDelay = TimeSpan.Zero;
        }
    }

    /// <summary>The sound output going away must never freeze or kill a picture, and must never escape to
    /// the UI: a video continues silently (with a reason and a log line), standalone audio fails with an
    /// error card, and multichannel sources are folded to stereo instead of refusing to play.</summary>
    private static void TestAudioOutputFailures(PlaybackEngine preview, string media, string logs, bool hasVideo, bool hasAudio)
    {
        if (!hasAudio)
        {
            Console.WriteLine("SKIP: audio output failure handling (needs an audio output device)");
            return;
        }
        string longClip = Path.Combine(media, "long-av.mp4");
        string longTone = Path.Combine(media, "long-tone.wav");
        string surround = Path.Combine(media, "surround-5.1.wav");
        if (!File.Exists(longClip) && hasVideo)
            MediaFixtures.WriteVideo(longClip, TranscodeContainerTypeGuids.Mpeg4, VideoFormatGuids.H264, true, AudioFormatGuids.Aac, seconds: 8);
        if (!File.Exists(longTone)) MediaFixtures.WriteWav(longTone, seconds: 8);
        if (!File.Exists(surround)) MediaFixtures.WriteWav(surround, seconds: 3, channels: 6);

        // B-20: a 5.1 source used to throw "Value does not fall within the expected range" on a stereo output.
        preview.RequestPlay(Media(surround));
        Require(WaitFor(() => preview.Position > TimeSpan.FromMilliseconds(400), 5000) && preview.State != PlaybackChannelState.Failed,
            $"6-channel WAV did not play: {preview.LastError?.Message}");
        preview.Stop();
        Console.WriteLine("PASS: a 6-channel (5.1) source is folded to stereo and plays");

        if (hasVideo)
        {
            // A-24: output lost mid-video → silent continuation from the same position, not a frozen frame.
            preview.RequestPlay(Media(longClip));
            Require(WaitFor(() => preview.VideoFrameStats.Presented >= 15 && preview.CurrentVideoHasAudio, 6000), "Long clip did not start with sound.");
            EveryStage.Rendering.Audio.AudioPlaybackClock.SimulateOutputLossForTests();
            Pump(300);
            var before = (Frames: preview.VideoFrameStats.Presented, Position: preview.Position);
            Pump(1500);
            long framesGained = preview.VideoFrameStats.Presented - before.Frames;
            double secondsGained = (preview.Position - before.Position).TotalSeconds;
            Require(preview.State == PlaybackChannelState.Playing, $"Video stopped after losing the audio output: {preview.State} {preview.LastError?.Message}");
            Require(framesGained >= 25, $"Video froze after losing the audio output: {framesGained} frames in 1.5 s.");
            Require(secondsGained is > 1.0 and < 2.2, $"Position did not keep advancing in real time after the loss: +{secondsGained:F2} s in 1.5 s.");
            Require(!preview.CurrentVideoHasAudio && (preview.CurrentContentNote ?? "").Contains("声音输出已断开"),
                $"No 'audio lost' note after the loss: {preview.CurrentContentNote}");
            Require(WaitFor(() => preview.IsCompleted, 12000), "Video never completed after losing the audio output.");
            Require(Directory.GetFiles(Path.Combine(logs, "playback")).Any(f => File.ReadAllText(f).Contains("audio_output_lost")),
                "Losing the audio output was not logged.");
            Console.WriteLine($"PASS: losing the audio output mid-video continues silently ({framesGained} frames / +{secondsGained:F2} s in 1.5 s), noted and logged");

            // B-20 / B-21: no output device at all — the video plays silently; a seek never escapes.
            EveryStage.Rendering.Audio.AudioPlaybackClock.SimulateNoOutputDevice = true;
            preview.RequestPlay(Media(longClip));
            Require(WaitFor(() => preview.VideoFrameStats.Presented >= 15, 6000) && preview.State == PlaybackChannelState.Playing,
                $"Video with sound refused to play without an output device: {preview.LastError?.Message}");
            Require((preview.CurrentContentNote ?? "").Contains("无法输出声音"), $"No 'cannot output sound' note: {preview.CurrentContentNote}");
            Require(preview.SeekTo(TimeSpan.FromSeconds(4)) && WaitFor(() => preview.Position > TimeSpan.FromSeconds(4.2), 4000),
                "Seeking a silently playing video failed.");
            preview.Stop();
            EveryStage.Rendering.Audio.AudioPlaybackClock.SimulateNoOutputDevice = false;
            Console.WriteLine("PASS: without an output device a video plays silently with a reason, and seeking works");

            // B-21: the output vanishes, then the operator seeks — the rebuilt clock fails; video keeps going.
            preview.RequestPlay(Media(longClip));
            Require(WaitFor(() => preview.VideoFrameStats.Presented >= 15, 6000), "Long clip did not start.");
            EveryStage.Rendering.Audio.AudioPlaybackClock.SimulateNoOutputDevice = true;
            Require(preview.SeekTo(TimeSpan.FromSeconds(3)), "Seek after the output vanished was refused.");
            Require(WaitFor(() => preview.Position > TimeSpan.FromSeconds(3.5), 4000) && preview.State == PlaybackChannelState.Playing,
                $"Video did not continue after a seek with no output device: {preview.State} {preview.LastError?.Message}");
            preview.Stop();
            EveryStage.Rendering.Audio.AudioPlaybackClock.SimulateNoOutputDevice = false;
            Console.WriteLine("PASS: a seek after the output vanished keeps the video playing (no exception to the UI)");
        }
        else Console.WriteLine("SKIP: video output-loss handling (needs GPU video processing)");

        // Standalone audio cannot continue without an output: error card, not a hang or an escaping exception.
        preview.RequestPlay(Media(longTone));
        Require(WaitFor(() => preview.Position > TimeSpan.FromMilliseconds(500), 5000), "Long tone did not start.");
        EveryStage.Rendering.Audio.AudioPlaybackClock.SimulateOutputLossForTests();
        Require(WaitFor(() => preview.State == PlaybackChannelState.Failed, 3000),
            $"Audio did not fail after losing its output (it would hang forever): {preview.State}");
        Require((preview.LastError?.Message ?? "").Contains("声音输出已断开"), $"Unclear error after losing the output: {preview.LastError?.Message}");

        preview.RequestPlay(Media(longTone));
        Require(WaitFor(() => preview.Position > TimeSpan.FromMilliseconds(500), 5000), "Long tone did not restart.");
        EveryStage.Rendering.Audio.AudioPlaybackClock.SimulateNoOutputDevice = true;
        preview.SeekTo(TimeSpan.FromSeconds(4)); // must not throw
        Require(WaitFor(() => preview.State == PlaybackChannelState.Failed, 3000)
                && (preview.LastError?.Message ?? "").Contains("无法打开声音输出设备"),
            $"Seek with no output device did not end in a clear error card: {preview.State} {preview.LastError?.Message}");

        preview.RequestPlay(Media(longTone));
        Require(WaitFor(() => preview.State == PlaybackChannelState.Failed, 3000)
                && (preview.LastError?.Message ?? "").Contains("无法打开声音输出设备"),
            $"Audio with no output device did not show a clear error card: {preview.State} {preview.LastError?.Message}");
        EveryStage.Rendering.Audio.AudioPlaybackClock.SimulateNoOutputDevice = false;
        preview.Stop();
        Console.WriteLine("PASS: standalone audio losing / lacking its output shows a clear error card (also after a seek)");
    }

    // Let the CI degradation path be exercised on a machine that does have a GPU and a sound card.
    private static bool SimulateNoVideo => Environment.GetEnvironmentVariable("ES_SELFTEST_NO_VIDEO") == "1";

    /// <summary>A 200x100 JPEG (left half red, right half blue) carrying an EXIF Orientation tag — how a
    /// phone stores a portrait photo. Orientation 6 means "rotate 90° clockwise to display".</summary>
    private static void WriteOrientedJpeg(string path, int orientation)
    {
        using var bitmap = new Bitmap(200, 100);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.FillRectangle(Brushes.Red, 0, 0, 100, 100);
            g.FillRectangle(Brushes.Blue, 100, 0, 100, 100);
        }
        // PropertyItem has no public constructor; GDI+ only needs the fields set.
        var item = (PropertyItem)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(PropertyItem));
        item.Id = 0x0112;
        item.Type = 3; // SHORT
        item.Len = 2;
        item.Value = new[] { (byte)orientation, (byte)0 };
        bitmap.SetPropertyItem(item);
        bitmap.Save(path, ImageFormat.Jpeg);
    }
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
        (src, dst) = SwapChainPresenter.ComputeRects(VideoScaleMode.FitWidth, 1920, 1080, 400, 300);
        Require(src.Width == 1920 && src.Height == 1080 && dst.Left == 0 && dst.Width == 400 && dst.Height == 225 && dst.Top == 37, "FitWidth wrong for a wide frame (should span the width, bars top/bottom).");
        (src, dst) = SwapChainPresenter.ComputeRects(VideoScaleMode.FitWidth, 1080, 1920, 400, 300);
        Require(dst.Width == 400 && dst.Height == 300 && src.Width == 1080 && src.Height == 810 && src.Top == 555, "FitWidth wrong for a tall frame (should span the width, crop top/bottom).");
        (src, dst) = SwapChainPresenter.ComputeRects(VideoScaleMode.Stretch, 1920, 1080, 400, 300);
        Require(src.Width == 1920 && dst.Width == 400 && dst.Height == 300, "Stretch rectangle wrong.");
        Console.WriteLine("PASS: video fit-width / fit / fill / stretch rectangles");
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
        SaveSolid(Path.Combine(dir, "tall.png"), 100, 200, Color.Blue, ImageFormat.Png);
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
        MediaKind.Image => $"图片 {engine.CurrentThumbnail?.Width}x{engine.CurrentThumbnail?.Height}{(engine.CurrentContentNote is { } n ? "，" + n : "")}",
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

    private sealed class FakeDeviceCastTransport : IDeviceCastTransport
    {
        public DiscoveryProtocol.CastStatusMessage? LastStatus { get; private set; }

        public Task SendCastStatusAsync(IPEndPoint casterEndPoint, DiscoveryProtocol.CastStatusMessage status)
        {
            LastStatus = status;
            return Task.CompletedTask;
        }

        public Task SendCastNackAsync(IPEndPoint casterEndPoint, Guid mediaSessionId, IReadOnlyList<ushort> missingSequences) =>
            Task.CompletedTask;

        public Task<TimeSpan?> PingAsync(IPEndPoint casterEndPoint, TimeSpan? timeout = null) =>
            Task.FromResult<TimeSpan?>(TimeSpan.FromMilliseconds(1));
    }

    private sealed class FakeDeviceCastStream : IDeviceCastStream
    {
        public event Action<IReadOnlyList<ushort>>? VideoPacketsMissing;
        public event Action<string>? AudioDegraded;

        public bool AudioMuted { get; set; }
        public DateTime LastPacketReceivedAt { get; set; } = DateTime.UtcNow;
        public int ConsecutiveVideoDecodeErrors { get; set; }
        public bool PresentLoopFailed { get; set; }
        public bool DecodeLoopFailed { get; set; }
        public long FramesDecoded { get; set; } = 42;
        public long BytesReceived { get; set; } = 4096;
        public string? LastError { get; set; }
        public bool HasAudio { get; set; } = true;
        public long AudioBytesReceived { get; set; } = 2048;
        public string? AudioError { get; set; }
        public long PayloadTypeMismatches { get; set; }
        public double? EstimatedPacketLossPercent { get; set; }
        public long VideoPacketsLost { get; set; }
        public int StartCount { get; private set; }
        public int AttachCount { get; private set; }
        public int DetachCount { get; private set; }
        public bool Disposed { get; private set; }

        public void Start() => StartCount++;
        public void AttachProgram(VideoSurface surface) => AttachCount++;
        public void DetachProgram() => DetachCount++;
        public void Dispose() => Disposed = true;

        public void RaiseMissing(params ushort[] missing) => VideoPacketsMissing?.Invoke(missing);
        public void RaiseAudioDegraded(string reason) => AudioDegraded?.Invoke(reason);
    }
}
