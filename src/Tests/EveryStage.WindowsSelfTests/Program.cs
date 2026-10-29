using System.Collections.Concurrent;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using EveryStage.Caster.Discovery;
using EveryStage.Discovery;
using EveryStage.Terminal.ContentEngine;
using EveryStage.Terminal.Devices;
using EveryStage.Terminal.Logging;

// Same visual-styles setup as the Terminal (comctl32 v6): without it the file grid's tile view and
// other themed controls fall back to their classic look, and the UI screenshots would not match the app.
Application.EnableVisualStyles();

// Run continuations on a single owning thread, as the WinForms renderer callers do.
using var context = new TestContext();
SynchronizationContext.SetSynchronizationContext(context);
Task tests = RunTests();
while (!tests.IsCompleted) context.RunOne();
tests.GetAwaiter().GetResult();
Console.WriteLine("Windows content self-tests passed.");

static async Task RunTests()
{
    TestBoundedQueue();
    TestAspectRatioSizing();
    string directory = Path.Combine(Path.GetTempPath(), "EveryStage-WindowsTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try
    {
        string first = Path.Combine(directory, "first.png");
        string second = Path.Combine(directory, "second.png");
        using (var bitmap = new Bitmap(64, 32)) bitmap.Save(first, ImageFormat.Png);
        using (var bitmap = new Bitmap(32, 64)) bitmap.Save(second, ImageFormat.Png);
        string officePath = Path.Combine(directory, "preview.docx");
        using (var archive = System.IO.Compression.ZipFile.Open(officePath, System.IO.Compression.ZipArchiveMode.Create))
        using (var entry = archive.CreateEntry("docProps/thumbnail.png").Open())
        using (var source = File.OpenRead(first)) source.CopyTo(entry);
        using (var officePreview = OfficeThumbnailReader.Read(officePath, new Size(240, 150)))
            Require(officePreview?.Size == new Size(240, 120), "Office embedded preview missing or distorted.");
        Require(ShellThumbnailReader.Read(Path.Combine(directory, "missing.mp4"), new Size(240, 150)) == null,
            "Missing video unexpectedly produced a thumbnail.");
        using (File.Open(officePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        using var renderer = new ImageContentRenderer();
        await renderer.LoadAsync(first);
        Require(renderer.CurrentFrame?.Size == new Size(64, 32), "Image size changed.");
        using (File.Open(first, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        Task old = renderer.LoadAsync(first);
        Task latest = renderer.LoadAsync(second);
        await Task.WhenAll(old, latest);
        Require(renderer.CurrentFrame?.Size == new Size(32, 64), "Older load replaced latest frame.");
        string invalid = Path.Combine(directory, "invalid.png");
        File.WriteAllText(invalid, "not an image");
        bool failed = false;
        try { await renderer.LoadAsync(invalid); }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException) { failed = true; }
        Require(failed && renderer.CurrentFrame == null, "Bad image retained stale frame.");
        Task pending = renderer.LoadAsync(first);
        renderer.Dispose();
        await pending;
        Require(renderer.CurrentFrame == null, "Frame published after disposal.");
        Console.WriteLine("PASS: image decode, file release, latest-load wins, corrupt image, disposal during load");
        string pdfPath = Path.Combine(directory, "pages.pdf");
        WritePdfFixture(pdfPath);
        using (var thumbnail = PdfContentRenderer.CreateThumbnail(pdfPath, new Size(240, 150)))
            Require(thumbnail.Size == new Size(200, 150), "PDF thumbnail did not preserve page proportions.");
        using (File.Open(pdfPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        using var pdf = new PdfContentRenderer();
        pdf.SetTargetSize(new Size(320, 240));
        await pdf.LoadAsync(pdfPath);
        Require(pdf.PageCount == 2 && pdf.CurrentFrame?.Size == new Size(320, 240), "PDF initial rendering failed.");
        Require(await pdf.TurnPageAsync(1) && pdf.CurrentPageIndex == 1 && !await pdf.TurnPageAsync(1), "PDF next-page boundary failed.");
        Require(pdf.CurrentFrame?.Size == new Size(180, 240), "Portrait PDF was stretched to the landscape output.");
        Require(await pdf.TurnPageAsync(-1) && pdf.CurrentPageIndex == 0 && !await pdf.TurnPageAsync(-1), "PDF previous-page boundary failed.");
        Require(pdf.CurrentFrame?.Size == new Size(320, 240), "Landscape PDF aspect ratio changed.");
        Task<bool> oldPage = pdf.TurnPageAsync(1);
        Task replacement = pdf.LoadAsync(pdfPath);
        await Task.WhenAll(oldPage, replacement);
        Require(pdf.CurrentPageIndex == 0 && pdf.CurrentFrame?.Size == new Size(320, 240),
            "Old page render replaced a newly loaded PDF.");
        using (var closing = new PdfContentRenderer())
        {
            await closing.LoadAsync(pdfPath);
            Task<bool> turning = closing.TurnPageAsync(1);
            closing.Dispose();
            await turning;
            Require(closing.CurrentFrame == null, "Page render published after disposal.");
        }
        Task pendingPdf = pdf.LoadAsync(pdfPath);
        pdf.Dispose();
        await pendingPdf;
        Require(pdf.CurrentFrame == null, "PDF frame published after disposal.");
        using (File.Open(pdfPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        Console.WriteLine("PASS: native PDF rendering, async page boundaries, replacement during paging, disposal during paging/load, file release");
        TestAudioLayout(directory);
        PreviewProgramTests.Run(directory);
        await TestPairingV2DiscoveryServiceUdpAsync(directory);
        DocumentTests.Run(directory);
        CastAudioTests.Run();
    }
    finally { Directory.Delete(directory, true); }
}

static async Task TestPairingV2DiscoveryServiceUdpAsync(string directory)
{
    string identityPath = Path.Combine(directory, "terminal-identity.json");
    string pairedPath = Path.Combine(directory, "paired-devices.json");
    string logRoot = Path.Combine(directory, "logs");
    var identity = DeviceIdentity.LoadOrCreate("terminal-test", identityPath);
    identity.DeviceName = "B15 Terminal";
    identity.Save();
    var paired = new PairedDeviceStore(pairedPath);
    var logger = new DeviceConnectionLogger(logRoot);

    DiscoveryService service;
    try { service = new DiscoveryService(identity, paired, logger, portOverride: 0); }
    catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse || ex.SocketErrorCode == SocketError.AccessDenied)
    {
        Console.WriteLine($"SKIP: DiscoveryService UDP pairing v2 smoke ({DiscoveryProtocol.Port} unavailable: {ex.SocketErrorCode})");
        return;
    }
    using (service)
    using (var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12)))
    {
        client.Connect(IPAddress.Loopback, service.LocalPort);
        service.Start();

        string oldRequestId = Guid.NewGuid().ToString("N");
        var oldRequest = new DiscoveryProtocol.PairRequestMessage
        {
            RequestId = oldRequestId,
            DeviceId = Guid.NewGuid(),
            DeviceName = "Old Caster",
        };
        var oldResponse = await SendUntilPairResponseAsync(client, oldRequest, oldRequestId, timeout.Token);
        Require(!oldResponse.Accepted && oldResponse.Reason?.Contains("版本过旧") == true,
            "Legacy pairing request was not rejected with the update reason.");

        string requestId = Guid.NewGuid().ToString("N");
        Guid casterId = Guid.NewGuid();
        using var caster = new CasterPairingHandshake(requestId, casterId, identity.DeviceId);
        var pairingShown = new TaskCompletionSource<PairingRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.PairingRequested += request => pairingShown.TrySetResult(request);
        var pairRequest = new DiscoveryProtocol.PairRequestMessage
        {
            RequestId = requestId,
            DeviceId = casterId,
            DeviceName = "Caster With A Very Long Name That Still Reaches The Pairing UI",
            PairingProtocolVersion = PairingHandshake.ProtocolVersion,
            CasterPublicKey = caster.CasterPublicKey,
            CasterNonceCommitment = caster.CasterNonceCommitment,
        };

        var challenge = await SendUntilPairChallengeAsync(client, pairRequest, requestId, timeout.Token);
        Require(challenge.TerminalDeviceId == identity.DeviceId, "Pair challenge used the wrong Terminal identity.");
        var casterResult = caster.ProcessChallenge(challenge.TerminalPublicKey, challenge.TerminalNonce);
        var reveal = new DiscoveryProtocol.PairRevealMessage
        {
            RequestId = requestId,
            CasterNonce = caster.CasterNonce,
        };
        while (!pairingShown.Task.IsCompleted)
        {
            await SendDiscoveryAsync(client, reveal, timeout.Token);
            var finished = await Task.WhenAny(pairingShown.Task, Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token));
            if (finished == pairingShown.Task) break;
        }

        var request = await pairingShown.Task.WaitAsync(timeout.Token);
        Require(request.RequestId == requestId && request.DeviceId == casterId, "Pairing UI request did not preserve the request/device id.");
        Require(request.VerificationCode == casterResult.VerificationCode, "Terminal pairing code did not match Caster pairing code.");
        service.RespondToPairing(requestId, accept: true, TrustMode.Trusted, allowCast: true, allowMonitor: false);
        var response = await ReceivePairResponseAsync(client, requestId, timeout.Token);
        Require(response.Accepted && response.PairingKey == null, "Pair response accepted with a wire PairingKey.");
        Require(caster.VerifyKeyConfirmation(response.KeyConfirmation), "Pair response key confirmation did not verify.");
        var stored = paired.Find(casterId);
        Require(stored != null
            && stored.AllowCast
            && !stored.AllowMonitor
            && stored.PairingKeyFormatVersion == PairingSecurity.CurrentKeyFormatVersion
            && stored.PairingKey == casterResult.PairingKey,
            "Terminal did not persist the derived v2 pairing key and permissions.");

        var cached = await SendUntilPairResponseAsync(client, reveal, requestId, timeout.Token);
        Require(cached.Accepted
            && cached.PairingKey == null
            && cached.KeyConfirmation == response.KeyConfirmation,
            "Duplicate pair_reveal did not receive the cached key confirmation response.");

        string casterIdentityPath = Path.Combine(directory, "caster-identity.json");
        string casterStorePath = Path.Combine(directory, "caster-paired-terminals.json");
        var casterIdentity = DeviceIdentity.LoadOrCreate("caster-test", casterIdentityPath);
        casterIdentity.DeviceName = "B15 Caster";
        casterIdentity.Save();
        var casterStore = new PairedTerminalStore(casterStorePath);
        using var casterClient = new TerminalDiscoveryClient(casterIdentity, casterStore, listenPortOverride: 0, terminalPortOverride: service.LocalPort);
        casterClient.Start();
        bool oldTerminalCodeShown = false;
        var oldTerminalResponse = await casterClient.RequestPairingAsync(
            new DiscoveredTerminal(Guid.NewGuid(), "Old Terminal", IPAddress.Loopback, DateTimeOffset.UtcNow,
                MediaAuthenticationVersion: 1, PairingProtocolVersion: 0),
            casterIdentity, _ => oldTerminalCodeShown = true,
            timeout: TimeSpan.FromMilliseconds(200), cancellationToken: timeout.Token);
        Require(oldTerminalResponse?.Accepted == false
            && oldTerminalResponse.Reason == "需要更新程序"
            && !oldTerminalCodeShown,
            "Caster client did not reject an old Terminal before starting pairing.");
        await TestCasterRejectsMismatchedPairChallengeAsync(casterIdentity, casterStore, timeout.Token);
        string? casterCode = null;
        PairingRequest? terminalRequest = null;
        service.PairingRequested += request =>
        {
            if (request.DeviceId != casterIdentity.DeviceId) return;
            terminalRequest = request;
            service.RespondToPairing(request.RequestId, accept: true, TrustMode.Trusted, allowCast: true, allowMonitor: false);
        };
        var discovered = new DiscoveredTerminal(identity.DeviceId, identity.DeviceName, IPAddress.Loopback, DateTimeOffset.UtcNow,
            MediaAuthenticationVersion: 1, PairingProtocolVersion: PairingHandshake.ProtocolVersion);
        var casterResponse = await casterClient.RequestPairingAsync(
            discovered, casterIdentity, code => casterCode = code,
            timeout: TimeSpan.FromSeconds(12), cancellationToken: timeout.Token);
        if (casterResponse == null || !casterResponse.Accepted)
            throw new InvalidOperationException($"Caster client pairing failed: {casterResponse?.Reason ?? "no response"}");
        Require(PairingSecurity.IsSupportedKey(casterResponse.PairingKey, casterResponse.PairingKeyFormatVersion),
            "Caster client did not return a supported local v2 pairing key.");
        Require(terminalRequest != null && terminalRequest.VerificationCode == casterCode,
            "Caster client and Terminal service did not show the same verification code.");
        var terminalStoredForClient = paired.Find(casterIdentity.DeviceId);
        Require(terminalStoredForClient?.PairingKey == casterResponse.PairingKey,
            "Terminal service and Caster client did not persist/return the same derived pairing key.");
        await TestCasterHandlesUnsignedCastStartRejectionsAsync(directory, casterIdentity, timeout.Token);
    }
    Console.WriteLine("PASS: DiscoveryService/Caster pairing v2 UDP handshake, old-version rejection, cached reveal response and Caster client pairing");
}

static async Task TestCasterRejectsMismatchedPairChallengeAsync(DeviceIdentity casterIdentity, PairedTerminalStore casterStore, CancellationToken token)
{
    Guid expectedTerminalId = Guid.NewGuid();
    using var fakeTerminal = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    int fakeTerminalPort = ((IPEndPoint)fakeTerminal.Client.LocalEndPoint!).Port;
    using var casterClient = new TerminalDiscoveryClient(casterIdentity, casterStore, listenPortOverride: 0, terminalPortOverride: fakeTerminalPort);
    casterClient.Start();
    bool codeShown = false;
    var discovered = new DiscoveredTerminal(expectedTerminalId, "Mismatched Terminal", IPAddress.Loopback, DateTimeOffset.UtcNow,
        MediaAuthenticationVersion: 1, PairingProtocolVersion: PairingHandshake.ProtocolVersion);
    var pairingTask = casterClient.RequestPairingAsync(
        discovered, casterIdentity, _ => codeShown = true,
        timeout: TimeSpan.FromSeconds(3), cancellationToken: token);
    var requestMessage = await ReceiveDiscoveryAsync(fakeTerminal, token);
    if (requestMessage is not DiscoveryProtocol.PairRequestMessage request)
        throw new InvalidOperationException($"Expected a pair_request, got {requestMessage.GetType().Name}.");
    await SendDiscoveryToAsync(fakeTerminal, new DiscoveryProtocol.PairChallengeMessage
    {
        RequestId = request.RequestId,
        TerminalDeviceId = Guid.NewGuid(),
        TerminalPublicKey = Convert.ToBase64String(new byte[] { 1, 2, 3 }),
        TerminalNonce = Convert.ToBase64String(new byte[PairingHandshake.NonceSizeBytes]),
    }, new IPEndPoint(IPAddress.Loopback, casterClient.LocalPort), token);
    var response = await pairingTask.WaitAsync(token);
    Require(response?.Accepted == false
        && response.Reason?.Contains("身份不一致") == true
        && !codeShown,
        "Caster client did not reject a pair_challenge from a different Terminal identity.");
}

static async Task TestCasterHandlesUnsignedCastStartRejectionsAsync(string directory, DeviceIdentity casterIdentity, CancellationToken token)
{
    await Run("not_paired", shouldRemovePairing: true, expectedError: "重新配对");
    await Run("not_allowed", shouldRemovePairing: false, expectedError: "未允许");

    async Task Run(string reason, bool shouldRemovePairing, string expectedError)
    {
        using var fakeTerminal = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        int fakeTerminalPort = ((IPEndPoint)fakeTerminal.Client.LocalEndPoint!).Port;
        string storePath = Path.Combine(directory, $"{reason}-paired-terminals.json");
        var store = new PairedTerminalStore(storePath);
        Guid terminalId = Guid.NewGuid();
        string staleKey = PairingSecurity.GenerateKey();
        store.Upsert(new PairedTerminal(terminalId, $"Terminal Rejecting {reason}", DateTimeOffset.UtcNow, staleKey, PairingSecurity.CurrentKeyFormatVersion));
        using var casterClient = new TerminalDiscoveryClient(casterIdentity, store, listenPortOverride: 0, terminalPortOverride: fakeTerminalPort);
        casterClient.Start();
        Guid sessionId = Guid.NewGuid();
        var terminal = new DiscoveredTerminal(terminalId, $"Terminal Rejecting {reason}", IPAddress.Loopback, DateTimeOffset.UtcNow,
            MediaAuthenticationVersion: 1, PairingProtocolVersion: PairingHandshake.ProtocolVersion);
        var startTask = casterClient.SendCastStartAsync(terminal, casterIdentity, 1280, 720, payloadType: 96, audio: null, sessionId, token);
        var request = await ReceiveDiscoveryAsync(fakeTerminal, token);
        Require(request is DiscoveryProtocol.CastStartMessage start
                && start.MediaSessionId == sessionId
                && PairingSecurity.Verify(start, staleKey),
            $"Caster did not send an authenticated cast_start before {reason}.");
        await SendDiscoveryToAsync(fakeTerminal, new DiscoveryProtocol.CastStartRejectedMessage
        {
            TerminalDeviceId = terminalId,
            CasterDeviceId = casterIdentity.DeviceId,
            MediaSessionId = sessionId,
            Reason = reason,
        }, new IPEndPoint(IPAddress.Loopback, casterClient.LocalPort), token);
        bool rejected = false;
        try { await startTask.WaitAsync(token); }
        catch (InvalidOperationException ex) when (ex.Message.Contains(expectedError, StringComparison.Ordinal))
        {
            rejected = true;
        }
        Require(rejected, $"Unsigned {reason} rejection did not surface the expected error.");
        bool stillPaired = store.Find(terminalId) != null;
        Require(stillPaired == !shouldRemovePairing, $"Caster pairing removal after {reason} was wrong.");
    }
}

static async Task<DiscoveryProtocol.PairChallengeMessage> SendUntilPairChallengeAsync(
    UdpClient client, DiscoveryProtocol.Message message, string requestId, CancellationToken token)
{
    while (true)
    {
        await SendDiscoveryAsync(client, message, token);
        var received = await ReceiveDiscoveryOrNullAsync(client, TimeSpan.FromMilliseconds(250), token);
        if (received is DiscoveryProtocol.PairChallengeMessage challenge && challenge.RequestId == requestId) return challenge;
    }
}

static async Task<DiscoveryProtocol.PairResponseMessage> SendUntilPairResponseAsync(
    UdpClient client, DiscoveryProtocol.Message message, string requestId, CancellationToken token)
{
    while (true)
    {
        await SendDiscoveryAsync(client, message, token);
        var received = await ReceiveDiscoveryOrNullAsync(client, TimeSpan.FromMilliseconds(250), token);
        if (received is DiscoveryProtocol.PairResponseMessage response && response.RequestId == requestId) return response;
    }
}

static async Task SendDiscoveryAsync(UdpClient client, DiscoveryProtocol.Message message, CancellationToken token)
{
    byte[] payload = DiscoveryProtocol.Encode(message);
    token.ThrowIfCancellationRequested();
    await client.SendAsync(payload, payload.Length);
}

static async Task SendDiscoveryToAsync(UdpClient client, DiscoveryProtocol.Message message, IPEndPoint destination, CancellationToken token)
{
    byte[] payload = DiscoveryProtocol.Encode(message);
    token.ThrowIfCancellationRequested();
    await client.SendAsync(payload, payload.Length, destination);
}

static async Task<DiscoveryProtocol.PairResponseMessage> ReceivePairResponseAsync(UdpClient client, string requestId, CancellationToken token)
{
    while (true)
    {
        var message = await ReceiveDiscoveryAsync(client, token);
        if (message is DiscoveryProtocol.PairResponseMessage response && response.RequestId == requestId) return response;
    }
}

static async Task<DiscoveryProtocol.Message?> ReceiveDiscoveryOrNullAsync(UdpClient client, TimeSpan timeout, CancellationToken token)
{
    using var attemptTimeout = new CancellationTokenSource(timeout);
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, attemptTimeout.Token);
    try { return await ReceiveDiscoveryAsync(client, linked.Token); }
    catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
}

static async Task<DiscoveryProtocol.Message> ReceiveDiscoveryAsync(UdpClient client, CancellationToken token)
{
    while (true)
    {
        var result = await client.ReceiveAsync(token);
        var message = DiscoveryProtocol.Decode(result.Buffer);
        if (message != null) return message;
    }
}

static void TestBoundedQueue()
{
    var released = new List<int>();
    using var queue = new EveryStage.Transport.BoundedLatestQueue<int>(3, released.Add);
    for (int i = 0; i < 100; i++) queue.Enqueue(i);
    Require(released.SequenceEqual(Enumerable.Range(0, 97)), "Overflow did not release oldest frames exactly once.");
    Require(queue.TryDequeue(out int frame) && frame == 97, "Queue did not retain latest frames in order.");
    queue.Dispose();
    queue.Enqueue(100);
    queue.Dispose();
    Require(released.SequenceEqual(Enumerable.Range(0, 97).Concat(new[] { 98, 99, 100 })),
        "Shutdown leaked or double-released queued/late frames.");
    Require(!queue.TryDequeue(out _), "Disposed queue retained a frame.");
    Console.WriteLine("PASS: live frame queue capacity, latest-frame retention and exactly-once release");
}

static void TestAspectRatioSizing()
{
    var ratio = new Size(4, 3);
    foreach (int dpi in new[] { 96, 120, 144, 192 })
    foreach (var screen in new[] { new Size(1920, 1040), new Size(1366, 728), new Size(800, 560) })
    foreach (bool vertical in new[] { false, true })
    {
        var maximum = new Size(screen.Width - 24 * dpi / 96, screen.Height - 24 * dpi / 96);
        var result = EveryStage.Terminal.UI.AspectRatioSizing.Resize(new Size(1700, 900),
            new Size(800 * dpi / 96, 600 * dpi / 96), maximum, ratio, vertical);
        Require(result.Width * 3 == result.Height * 4, "Window ratio drifted.");
        Require(result.Width <= maximum.Width && result.Height <= maximum.Height, "Window exceeded working area.");
    }
    Console.WriteLine("PASS: 4:3 window sizing across DPI scales, small screens and drag directions");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void TestAudioLayout(string directory)
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            using (var window = new AspectTestWindow())
            {
                var proposed = Rectangle.FromLTRB(150, 120, 1257, 823);
                for (int edge = 1; edge <= 8; edge++)
                {
                    Rectangle resized = window.ApplySizing(edge, proposed);
                    Require(resized.Width * 3 == resized.Height * 4, $"WM_SIZING edge {edge} broke 4:3.");
                    if (edge is 1 or 4 or 7) Require(resized.Right == proposed.Right, "Left resize moved right anchor.");
                    else Require(resized.Left == proposed.Left, "Right resize moved left anchor.");
                    if (edge is 3 or 4 or 5) Require(resized.Bottom == proposed.Bottom, "Top resize moved bottom anchor.");
                    else Require(resized.Top == proposed.Top, "Bottom resize moved top anchor.");
                }
            }
            Console.WriteLine("PASS: window WM_SIZING handling for all eight edges/corners and fixed anchors");
            var library = new EveryStage.Terminal.Data.FileLibraryStore(Path.Combine(directory, "audio-library.json"));
            var audio = library.Import("layout-test.mp3")!;
            var type = typeof(ImageContentRenderer).Assembly.GetType("EveryStage.Terminal.UI.Panels.AudioPlayerBar", throwOnError: true)!;
            using var bar = (Control)Activator.CreateInstance(type, library,
                new EveryStage.Terminal.Logging.FileOperationLogger(directory), null)!;
            // No audio: the bar stays visible as an「导入音频」placeholder instead of hiding.
            type.GetMethod("SetAudioFiles")!.Invoke(bar, new object[] { Array.Empty<EveryStage.Terminal.Data.MediaFile>() });
            var actionButton = bar.Controls.OfType<Button>().Single();
            Require(bar.Visible && actionButton.Text.Contains("导入音频"), "Empty audio bar did not show its import placeholder.");
            int importRequests = 0;
            Action onImport = () => importRequests++;
            type.GetEvent("ImportRequested")!.AddEventHandler(bar, onImport);
            actionButton.PerformClick();
            Require(importRequests == 1, "Empty audio bar button did not request an audio import.");
            type.GetEvent("ImportRequested")!.RemoveEventHandler(bar, onImport);
            type.GetMethod("SetAudioFiles")!.Invoke(bar, new object[] { new[] { audio } });
            Require(actionButton.Text.Contains("加入活动音轨"), "Audio bar action is not「加入活动音轨」.");
            EveryStage.Terminal.Data.MediaFile? trackRequest = null;
            Action<EveryStage.Terminal.Data.MediaFile> onTrack = file => trackRequest = file;
            type.GetEvent("AddToActivityTrackRequested")!.AddEventHandler(bar, onTrack);
            actionButton.PerformClick();
            Require(trackRequest?.Id == audio.Id, "「加入活动音轨」did not request the bound audio file.");
            type.GetEvent("AddToActivityTrackRequested")!.RemoveEventHandler(bar, onTrack);
            foreach (int width in new[] { 900, 576, 400, 900 })
            {
                bar.Width = width;
                type.GetMethod("RefreshLiveState")!.Invoke(bar, null);
                var regions = new[] { "_playButton", "_progressTrack", "_volumeTrack", "_loopButton" }
                    .Select(name => (Rectangle)type.GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(bar)!).ToList();
                regions.Add(bar.Controls.OfType<Button>().Single().Bounds);
                foreach (var region in regions)
                    Require(bar.ClientRectangle.Contains(region), $"Audio control outside {width}px layout: {region}");
                for (int i = 0; i < regions.Count; i++)
                    for (int j = i + 1; j < regions.Count; j++)
                        Require(!regions[i].IntersectsWith(regions[j]), $"Audio controls overlap at {width}px.");
                using var bitmap = new Bitmap(bar.Width, bar.Height);
                bar.DrawToBitmap(bitmap, bar.ClientRectangle);
            }
            int playRequests = 0;
            Action<EveryStage.Terminal.Data.MediaFile> onPlay = file =>
            {
                Require(file.Id == audio.Id, "Keyboard played a different file.");
                playRequests++;
            };
            type.GetEvent("PlayRequested")!.AddEventHandler(bar, onPlay);
            var keyHandler = type.GetMethod("OnKeyDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            var space = new KeyEventArgs(Keys.Space);
            keyHandler.Invoke(bar, new object[] { space });
            Require(playRequests == 1 && space.Handled && space.SuppressKeyPress, "Space did not trigger audio playback.");
            keyHandler.Invoke(bar, new object[] { new KeyEventArgs(Keys.Control | Keys.Space) });
            Require(playRequests == 1, "Modified shortcut unexpectedly triggered playback.");
            keyHandler.Invoke(bar, new object[] { new KeyEventArgs(Keys.L) });
            var reloaded = new EveryStage.Terminal.Data.FileLibraryStore(Path.Combine(directory, "audio-library.json"));
            Require(reloaded.Files.Single().OnCompletion == EveryStage.Terminal.Data.CompletionAction.Loop,
                "Keyboard loop setting was not persisted.");
            keyHandler.Invoke(bar, new object[] { new KeyEventArgs(Keys.L) });
            Require(audio.OnCompletion == EveryStage.Terminal.Data.CompletionAction.NextItem, "Keyboard could not disable looping.");
            type.GetEvent("PlayRequested")!.RemoveEventHandler(bar, onPlay);

            // Several audio files: the bar shows the first until the operator picks another in the
            // file-name selector, and the pick survives the periodic live refresh.
            var second = library.Import("layout-test-2.mp3")!;
            type.GetMethod("SetAudioFiles")!.Invoke(bar, new object[] { new[] { audio, second } });
            EveryStage.Terminal.Data.MediaFile? Bound() => (EveryStage.Terminal.Data.MediaFile?)type.GetProperty("BoundFile")!.GetValue(bar);
            Require(Bound()?.Id == audio.Id, "Audio bar did not start on the first audio file.");
            int stateChanges = 0;
            Action onState = () => stateChanges++;
            type.GetEvent("StateChanged")!.AddEventHandler(bar, onState);
            type.GetMethod("SelectFile")!.Invoke(bar, new object[] { second });
            type.GetMethod("RefreshLiveState")!.Invoke(bar, null);
            Require(Bound()?.Id == second.Id, "Audio selector pick was not kept across a live refresh.");
            Require(stateChanges >= 2, "Audio bar did not announce its state changes.");
            type.GetEvent("StateChanged")!.RemoveEventHandler(bar, onState);

            // The sidebar「音频独立播放区」card is a second view of the same bar.
            var cardType = type.Assembly.GetType("EveryStage.Terminal.UI.Panels.AudioSidebarCard", throwOnError: true)!;
            using var card = (Control)Activator.CreateInstance(cardType, bar)!;
            foreach (bool compact in new[] { false, true })
            {
                cardType.GetProperty("Compact")!.SetValue(card, compact);
                card.Size = new Size(218, (int)cardType.GetProperty(compact ? "CompactHeight" : "FullHeight")!.GetValue(card)!);
                using var cardBitmap = new Bitmap(card.Width, card.Height);
                card.DrawToBitmap(cardBitmap, card.ClientRectangle);
            }
        }
        catch (Exception ex) { failure = ex; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    if (!thread.Join(TimeSpan.FromSeconds(20))) throw new TimeoutException("Audio layout test did not finish.");
    if (failure != null) throw new InvalidOperationException("Audio layout test failed.", failure);
    Console.WriteLine("PASS: audio bar layout, offscreen painting, keyboard playback, modifier isolation, loop persistence, track selection and sidebar card");
}

static void WritePdfFixture(string path)
{
    string[] objects =
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R 4 0 R] /Count 2 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 320 240] /Resources << >> >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 240 320] /Resources << >> >>",
    };
    var text = new System.Text.StringBuilder("%PDF-1.4\n");
    var offsets = new List<int>();
    for (int i = 0; i < objects.Length; i++)
    {
        offsets.Add(text.Length);
        text.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
    }
    int xref = text.Length;
    text.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
    foreach (int offset in offsets) text.Append($"{offset:D10} 00000 n \n");
    text.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
    File.WriteAllText(path, text.ToString(), System.Text.Encoding.ASCII);
}

sealed class TestContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    public override void Post(SendOrPostCallback callback, object? state) => _queue.Add((callback, state));
    public void RunOne()
    {
        if (_queue.TryTake(out var work, 100)) work.Callback(work.State);
    }
    public void Dispose() => _queue.Dispose();
}

sealed class AspectTestWindow : EveryStage.Terminal.UI.GradientForm
{
    public AspectTestWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        NormalWindowAspectRatio = new Size(4, 3);
        MinimumSize = new Size(800, 600);
    }

    public Rectangle ApplySizing(int edge, Rectangle proposed)
    {
        IntPtr memory = System.Runtime.InteropServices.Marshal.AllocHGlobal(16);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(new[] { proposed.Left, proposed.Top, proposed.Right, proposed.Bottom }, 0, memory, 4);
            Message message = Message.Create(Handle, 0x214, new IntPtr(edge), memory);
            base.WndProc(ref message);
            if (message.Result != new IntPtr(1)) throw new InvalidOperationException("Sizing message was not handled.");
            int[] result = new int[4];
            System.Runtime.InteropServices.Marshal.Copy(memory, result, 0, 4);
            return Rectangle.FromLTRB(result[0], result[1], result[2], result[3]);
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(memory); }
    }
}
