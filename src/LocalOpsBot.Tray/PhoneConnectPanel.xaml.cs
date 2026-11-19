using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Tray.Services;
using LocalOpsBot.Tray.Services.Mobile;
using QRCoder;

namespace LocalOpsBot.Tray;

/// <summary>
/// Settings › Phone (the Settings page of the Homebase window): picks the paths (LAN / Tailscale / Cloudflare Tunnel) and
/// turns the feature on (one UAC step), shows the one-time pairing QR that carries every path,
/// shows each path's state with an install button for a missing connector, and lists/revokes
/// paired phones. Nothing runs until the tab is first shown; status is polled only while the tab
/// is in view (<see cref="Show"/> / <see cref="Hide"/>) and stops for good on <see cref="Stop"/>.
/// </summary>
public partial class PhoneConnectPanel : UserControl
{
    private static readonly TimeSpan StatusPoll = TimeSpan.FromSeconds(5);

    private readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = StatusPoll };

    // The copied pairing link or 24-word code, wiped from the clipboard when its offer expires (#39).
    // A timer of its own, so it still fires while the Phone area is out of view.
    private readonly DispatcherTimer _clipboardWipe = new();
    private string? _copiedSecret;
    private DateTimeOffset _expiresAt;
    private string _offerEndpointsKey = "";
    private bool _hasSavedToken;
    private bool _busy;
    private bool _started;
    private bool _inView;

    private sealed record DeviceRow(string DeviceId, string Name, string Detail);

    private sealed record PcRow(LinkedPc Pc, string Name, string Detail, Brush Dot);

    private PairingOffer? _offer;

    private sealed record PathRow(string Title, string Detail, Brush Dot, string? ActionLabel = null, string? ActionKey = null)
    {
        public Visibility ActionVisibility => ActionLabel is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public PhoneConnectPanel()
    {
        InitializeComponent();
        _countdown.Tick += (_, _) => UpdateCountdown();
        _statusTimer.Tick += async (_, _) => await PollStatusAsync();
        _clipboardWipe.Tick += (_, _) => WipeCopiedSecret();
        // A device renamed from the dashboard, a phone or another PC (M-51) shows here at once.
        Action onAliases = () => Dispatcher.BeginInvoke(async () => { if (_inView) await LoadDevicesAsync(); });
        Loaded += (_, _) => { DeviceNames.Aliases.Changed -= onAliases; DeviceNames.Aliases.Changed += onAliases; };
        Unloaded += (_, _) => DeviceNames.Aliases.Changed -= onAliases;
    }

    /// <summary>The tab came into view: load everything the first time, then resume polling.</summary>
    public async void Show()
    {
        if (_inView) return;
        _inView = true;
        if (!_started)
        {
            _started = true;
            await RefreshAsync();
            return;
        }
        if (!MobileHost.IsEnabled()) return;
        // Restart the timer even while an install/apply runs: PollStatusAsync skips ticks while
        // busy, and the timer must still be running when it finishes (the Agent can take up to a
        // minute to notice a freshly installed connector).
        _statusTimer.Start();
        if (_busy) return;
        await PollStatusAsync();
        // The code ran out (or nearly) while the area was out of view: show a new one, as opening the
        // old Settings window always did, rather than an expired QR.
        if (PairPanel.Visibility == Visibility.Visible && _expiresAt - DateTimeOffset.UtcNow < TimeSpan.FromMinutes(1))
            await NewOfferAsync();
        await LoadDevicesAsync();
    }

    /// <summary>The tab left view: stop polling (the QR countdown keeps its own cheap timer).</summary>
    public void Hide()
    {
        _inView = false;
        _statusTimer.Stop();
    }

    /// <summary>
    /// The window was hidden (what closing the Settings window used to be): put away a shown 24-word
    /// code, which stays valid until the offer expires, drop unsaved edits (paths, tunnel token and
    /// hostname) and load everything afresh — settings and a new QR — the next time the area is shown.
    /// While an APPLY or INSTALL runs it reloads by itself when it finishes.
    /// </summary>
    public void Forget()
    {
        CodeBox.Visibility = Visibility.Collapsed;
        CodeBox.Text = "";
        TunnelTokenBox.Clear();
        if (!_busy) _started = false;
    }

    /// <summary>The tray is shutting down.</summary>
    public void Stop()
    {
        Hide();
        _countdown.Stop();
    }

    private async Task RefreshAsync()
    {
        await LoadSettingsAsync();
        if (!MobileHost.IsEnabled())
        {
            StatusText.Text = "휴대폰 연결이 꺼져 있습니다. 쓸 경로를 고른 뒤 ENABLE을 누르세요.";
            IntroCard.Visibility = Visibility.Visible;
            PairPanel.Visibility = PathsPanel.Visibility = DevicesPanel.Visibility = LinkPanel.Visibility = ApprovalPanel.Visibility = Visibility.Collapsed;
            ApplyButtonText.Text = "ENABLE";
            return;
        }

        IntroCard.Visibility = Visibility.Collapsed;
        ApplyButtonText.Text = "APPLY";
        var client = MobileHost.EnsureStarted();
        StatusText.Text = "Homebase Agent에 연결하는 중…";

        // The Agent may be restarting right after enabling; give it a moment.
        BridgeStatus? status = null;
        for (var i = 0; i < 20 && status is not { ApiListening: true }; i++)
        {
            if (client.IsConnected) status = await TryStatusAsync(client);
            if (status is { ApiListening: true }) break;
            await Task.Delay(1000);
        }

        if (status is null)
        {
            StatusText.Text = "Homebase Agent에 연결하지 못했습니다. 서비스가 실행 중인지 확인하세요.";
            return;
        }

        PathsPanel.Visibility = Visibility.Visible;
        DevicesPanel.Visibility = Visibility.Visible;
        LinkPanel.Visibility = Visibility.Visible;
        ShowApprovals();
        ShowPaths(status);
        if (_inView) _statusTimer.Start();
        await LoadDevicesAsync();
        if (status.Endpoints.Count == 0) { ShowNoPath(status); return; }

        ShowReady();
        await NewOfferAsync();
    }

    private void ShowReady()
    {
        StatusText.Text = "휴대폰의 Homebase 앱에서 '기기 추가'를 누르고 아래 QR 코드를 스캔하세요. " +
                          "앱이 같은 Wi-Fi → Tailscale → Cloudflare 순으로 되는 경로를 자동으로 고릅니다.";
        PairPanel.Visibility = Visibility.Visible;
    }

    private void ShowNoPath(BridgeStatus status)
    {
        PairPanel.Visibility = Visibility.Collapsed;
        // Forget the shown offer so the QR comes back even when the very same paths return.
        _offerEndpointsKey = "";
        StatusText.Text = status.Cloudflare is { State: "starting" }
            ? "Cloudflare Tunnel을 여는 중입니다. 잠시 후 QR 코드가 나타납니다."
            : "지금 열린 연결 경로가 없습니다. 아래 CONNECTION PATHS에서 원인을 확인하세요.";
    }

    private static async Task<BridgeStatus?> TryStatusAsync(MobileBridgeClient client)
    {
        try
        {
            var r = await client.RequestAsync(MobileBridge.OpStatus);
            if ((bool?)r["ok"] != true) return null;
            return r["payload"]?.Deserialize<BridgeStatus>(MobileProtocol.Json);
        }
        catch (Exception ex) when (ex is IOException or TaskCanceledException or OperationCanceledException or JsonException)
        {
            return null;
        }
    }

    private async Task PollStatusAsync()
    {
        if (_busy || MobileHost.Client is not { IsConnected: true } client) return;
        var status = await TryStatusAsync(client);
        if (status is null) return;
        ShowPaths(status);
        if (status.Endpoints.Count == 0)
        {
            ShowNoPath(status);
            return;
        }
        // A path came up or changed (Quick Tunnel address, DHCP, Tailscale login): reissue the QR so it carries it.
        if (EndpointsKey(status.Endpoints) != _offerEndpointsKey)
        {
            ShowReady();
            await NewOfferAsync();
        }
    }

    private static string EndpointsKey(IEnumerable<MobileEndpoint> endpoints) =>
        string.Join("|", endpoints.Select(e => $"{e.Transport}:{e.Host}:{e.Port}"));

    private void ShowPaths(BridgeStatus status)
    {
        var ok = (Brush)FindResource("Brush.StatusOk");
        var warn = (Brush)FindResource("Brush.StatusWarn");
        var off = (Brush)FindResource("Brush.StatusOffline");
        var rows = new List<PathRow>();

        // LAN
        var lan = status.Endpoints.Where(e => e.Transport == MobileTransport.Lan).ToList();
        if (!status.Options.Lan)
            rows.Add(new PathRow("LAN · 같은 Wi-Fi", "꺼짐", off));
        else if (lan.Count == 0)
            rows.Add(new PathRow("LAN · 같은 Wi-Fi", "사설 LAN 주소를 찾지 못했습니다. Wi-Fi·유선 연결을 확인하세요.", warn));
        else
        {
            var publicNets = MobileComponents.PublicNetworksFor(lan.Select(e => e.Host));
            rows.Add(publicNets is { Count: > 0 }
                ? new PathRow("LAN · 같은 Wi-Fi",
                    $"{string.Join(", ", lan.Select(e => e.Host))} — Windows가 '{string.Join(", ", publicNets)}'을(를) 공용 네트워크로 보고 있어 방화벽이 막습니다. 신뢰하는 네트워크면 '개인'으로 바꾸세요.",
                    warn, "NETWORK", "network-settings")
                : new PathRow("LAN · 같은 Wi-Fi", $"{string.Join(", ", lan.Select(e => e.Host))} : {status.Port} · TLS(인증서 핀)", ok));
        }

        // Tailscale
        var ts = status.Endpoints.Where(e => e.Transport == MobileTransport.Tailscale).ToList();
        if (!status.Options.Tailscale)
            rows.Add(new PathRow("Tailscale", "꺼짐", off));
        else if (ts.Count > 0)
            rows.Add(new PathRow("Tailscale", $"{string.Join(" · ", ts.Select(e => e.Host))} : {status.Port}", ok));
        else if (!MobileComponents.IsTailscaleInstalled())
            rows.Add(new PathRow("Tailscale", "설치되어 있지 않습니다. (선택 사항 — LAN·Cloudflare만으로도 됩니다)", off, "INSTALL", "install-tailscale"));
        else
            rows.Add(new PathRow("Tailscale", "설치되어 있지만 tailnet에 연결되어 있지 않습니다.", warn, "OPEN", "open-tailscale"));

        // Cloudflare
        var cf = status.Cloudflare;
        if (!status.Options.Cloudflare || cf is null)
            rows.Add(new PathRow("Cloudflare Tunnel", "꺼짐", off));
        else
        {
            var mode = cf.Mode == "named" ? "고정 주소" : "Quick Tunnel · 임시 주소(재시작마다 바뀜, 앱이 다른 경로로 붙을 때 자동 갱신)";
            rows.Add(cf.State switch
            {
                "running" => new PathRow("Cloudflare Tunnel", $"https://{cf.Hostname} · {mode}", ok),
                "starting" => new PathRow("Cloudflare Tunnel", "터널을 여는 중…", warn),
                "not_installed" => new PathRow("Cloudflare Tunnel", "cloudflared가 설치되어 있지 않습니다.", warn, "INSTALL", "install-cloudflared"),
                _ => new PathRow("Cloudflare Tunnel", "오류: " + (cf.Detail ?? "알 수 없음"), warn),
            });
        }

        PathList.ItemsSource = rows;
    }

    private async Task NewOfferAsync()
    {
        var client = MobileHost.Client;
        if (client is null) return;
        try
        {
            var r = await client.RequestAsync(MobileBridge.OpPairingBegin);
            var offer = r["payload"]?.Deserialize<PairingOffer>(MobileProtocol.Json);
            if ((bool?)r["ok"] != true || offer is null)
            {
                StatusText.Text = "페어링 코드를 만들지 못했습니다: " + ((string?)r["error"] ?? "unknown");
                return;
            }
            // An https link (M-20): the phone's own camera opens the app, or the install guide when it's missing.
            QrImage.Source = RenderQr(PairingLink.Build(offer));
            _offer = offer;
            // A shown code belongs to the previous offer; it can't pair any more — nor can a copied one.
            CodeBox.Visibility = Visibility.Collapsed;
            CodeBox.Text = "";
            WipeCopiedSecret();
            _offerEndpointsKey = EndpointsKey(offer.Endpoints);
            _expiresAt = offer.ExpiresAt;
            UpdateCountdown();
            _countdown.Start();
        }
        catch (Exception ex) when (ex is IOException or TaskCanceledException or OperationCanceledException or JsonException)
        {
            StatusText.Text = "Agent 응답이 없습니다: " + ex.Message;
        }
    }

    private async Task LoadDevicesAsync()
    {
        var client = MobileHost.Client;
        if (client is null) return;
        try
        {
            var r = await client.RequestAsync(MobileBridge.OpDevicesList);
            // An error reply (an Agent that doesn't know the op, a failed read) keeps what is shown
            // instead of claiming that no phone is paired.
            if ((bool?)r["ok"] == true)
            {
                // Phones only: a PC paired here is on the PC list below, together with this PC's link to it —
                // a group links PCs both ways (M-47), so it would otherwise show twice.
                var phones = (r["payload"]?.Deserialize<List<MobileDeviceInfo>>(MobileProtocol.Json) ?? new())
                    .Where(d => d.Kind != DeviceKind.Pc).ToList();
                // The group's alias for a device (M-51) in place of its name, which then leads the detail line.
                DeviceList.ItemsSource = phones.Select(d => new DeviceRow(
                    d.DeviceId,
                    DeviceNames.Show(d.Kind, d.Name, PhoneKey(d)),
                    AliasedFrom(d.Kind, d.Name, PhoneKey(d)) +
                    (d.Model ?? "Android") + $" · 연결 {d.PairedAt.ToLocalTime():yyyy-MM-dd HH:mm}" +
                    (d.LastSeenAt is { } seen ? $" · 최근 {seen.ToLocalTime():MM-dd HH:mm}" : ""))).ToList();
                NoDevicesText.Visibility = phones.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        catch (Exception ex) when (ex is IOException or TaskCanceledException or OperationCanceledException or JsonException) { }
        await LoadPcsAsync();
    }

    /// <summary>The PCs linked either way (M-29, M-47), one row each.</summary>
    private async Task LoadPcsAsync()
    {
        var client = MobileHost.Client;
        if (client is null) return;
        try
        {
            var r = await client.RequestAsync(MobileBridge.OpDevicesOverview);
            if ((bool?)r["ok"] != true) return;
            var overview = r["payload"]?.Deserialize<DeviceOverview>(MobileProtocol.Json);
            if (overview is null) return;
            HubList.ItemsSource = LinkedPcs.Merge(overview.Hubs, overview.Here.Devices).Select(p =>
            {
                var key = p.PcId is { } pcId ? GroupKeys.Pc(pcId) : null;
                return new PcRow(p,
                    DeviceNames.Show(DeviceKind.Pc, p.Name, key),
                    AliasedFrom(DeviceKind.Pc, p.Name, key) + DeviceText.Pc(p),
                    (Brush)FindResource(p.Online ? "Brush.StatusOk" : "Brush.StatusWarn"));
            }).ToList();
        }
        catch (Exception ex) when (ex is IOException or TaskCanceledException or OperationCanceledException or JsonException) { }
    }

    /// <summary>"DESKTOP-ABC · " when the device shows under an alias, else nothing.</summary>
    private static string AliasedFrom(string kind, string name, string? key) =>
        DeviceNames.AliasOf(key, kind, name) is null ? "" : name + " · ";

    /// <summary>A phone's group key.</summary>
    private static string PhoneKey(MobileDeviceInfo d) => GroupKeys.PhoneByName(d.Name, d.Model);

    private void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (_offer is null || _offer.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            CountdownText.Text = L.PhoneCopyLinkExpired;
            return;
        }
        try
        {
            CopySecret(PairingLink.Build(_offer), _offer.ExpiresAt);
            CountdownText.Text = L.PhoneCopyLinkDone;
        }
        catch (System.Runtime.InteropServices.COMException) { CountdownText.Text = L.PhoneCopyLinkFailed; }
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        if (_offer is null || _offer.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            CountdownText.Text = L.PhoneCopyLinkExpired;
            return;
        }
        var code = ConnectionCode.Encode(_offer);
        if (code is null) return;
        // Four lines of six numbered words: easy to read aloud or check against the other screen.
        var words = code.Split(' ');
        CodeBox.Text = string.Join(Environment.NewLine, Enumerable.Range(0, 4).Select(row =>
            string.Join("  ", Enumerable.Range(row * 6, 6).Select(i => $"{i + 1,2}. {words[i]}"))));
        CodeBox.Visibility = Visibility.Visible;
        try
        {
            CopySecret(code, _offer.ExpiresAt);
            CountdownText.Text = L.PhoneCopyCodeDone;
        }
        catch (System.Runtime.InteropServices.COMException) { CountdownText.Text = L.PhoneCopyLinkFailed; }
    }

    /// <summary>
    /// Copies a pairing secret (link or 24-word code). Windows is asked to keep it out of clipboard
    /// history (Win+V) and cloud clipboard sync, and the clipboard is wiped when the offer expires —
    /// only if it still holds exactly this text (#39). Throws <see cref="System.Runtime.InteropServices.COMException"/>
    /// when the clipboard is busy, like <see cref="Clipboard.SetText(string)"/>.
    /// </summary>
    private void CopySecret(string text, DateTimeOffset expiresAt)
    {
        try
        {
            var data = new DataObject();
            data.SetText(text);
            data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
            data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
            Clipboard.SetDataObject(data, true);
        }
        catch (Exception)
        {
            // The exclusion hints are optional; never let them cost the copy itself. Falls back to the
            // plain copy, which throws COMException again if the clipboard really is busy.
            Clipboard.SetText(text);
        }

        _copiedSecret = text;
        // Offers live 5 minutes; clamp anyway so a skewed expiry can't produce an invalid interval.
        var left = expiresAt - DateTimeOffset.UtcNow;
        if (left < TimeSpan.FromSeconds(1)) left = TimeSpan.FromSeconds(1);
        if (left > TimeSpan.FromHours(1)) left = TimeSpan.FromHours(1);
        _clipboardWipe.Stop();
        _clipboardWipe.Interval = left;
        _clipboardWipe.Start();
    }

    private void WipeCopiedSecret()
    {
        _clipboardWipe.Stop();
        var secret = _copiedSecret;
        _copiedSecret = null;
        if (secret is null) return;
        try
        {
            // Whatever the user copied since stays untouched.
            if (Clipboard.ContainsText() && Clipboard.GetText() == secret) Clipboard.Clear();
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Clipboard busy: leave it — the code has expired and can't pair any more anyway.
        }
    }

    private bool _approvalLoading;

    /// <summary>The phone-approvals toggle shows whether our hook is in ~/.claude/settings.json (M-48).</summary>
    private void ShowApprovals()
    {
        _approvalLoading = true;
        ApprovalCheck.IsChecked = ClaudeHookSetup.IsInstalled();
        _approvalLoading = false;
        ApprovalPanel.Visibility = Visibility.Visible;
    }

    private void ApprovalCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_approvalLoading) return;
        var on = ApprovalCheck.IsChecked == true;
        var error = on ? ClaudeHookSetup.Enable() : ClaudeHookSetup.Disable();
        ApprovalResultText.Visibility = Visibility.Visible;
        if (error is null)
        {
            ApprovalResultText.Text = on
                ? "켰습니다. 새로 시작하는 Claude Code 세션부터 적용됩니다(열려 있던 세션은 다시 열어야 할 수 있습니다)."
                : "껐습니다. Claude Code가 PC에서만 묻습니다.";
            return;
        }
        ApprovalResultText.Text = (on ? "켜지 못했습니다: " : "끄지 못했습니다: ") + error;
        _approvalLoading = true;
        ApprovalCheck.IsChecked = ClaudeHookSetup.IsInstalled();
        _approvalLoading = false;
    }

    private async void Link_Click(object sender, RoutedEventArgs e)
    {
        if (MobileHost.Client is not { } client) return;
        var link = LinkBox.Text.Trim();
        if (link.Length == 0) return;
        LinkButton.IsEnabled = false;
        LinkResultText.Visibility = Visibility.Visible;
        LinkResultText.Text = L.PhoneLinking;
        try
        {
            // Pairing tries each of the other PC's paths in turn (up to ~15 s each), so allow longer than the usual 10 s.
            var r = await client.RequestAsync(MobileBridge.OpHubsLink, new { link }, timeout: TimeSpan.FromSeconds(75));
            var result = r["payload"]?.Deserialize<HubLinkResult>(MobileProtocol.Json);
            if (result is { Linked: true })
            {
                LinkResultText.Text = L.PhoneLinked(result.PcName ?? "");
                LinkBox.Text = "";
            }
            else LinkResultText.Text = result?.Message ?? L.PhoneLinkFailed;
        }
        catch (Exception ex) when (ex is IOException or TaskCanceledException or OperationCanceledException or JsonException)
        {
            LinkResultText.Text = L.PhoneLinkFailed + " " + ex.Message;
        }
        finally { LinkButton.IsEnabled = true; }
        await LoadDevicesAsync();
    }

    private async void Unlink_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not LinkedPc pc || MobileHost.Client is not { } client) return;
        if (!MessageDialog.Show("Homebase", L.PhoneUnlinkConfirm, L.PhoneUnlink, L.Cancel, owner: Window.GetWindow(this))) return;
        // By pcId the Agent takes the PC out of the group: its link there and its pairing here (M-47).
        if (pc.PcId is { } pcId)
            await TryRequestAsync(client, MobileBridge.OpHubsUnlink, new { pcId }, TimeSpan.FromSeconds(20));
        // A pairing whose pcId the Agent doesn't know yet is not reached that way; revoked even if the unlink failed.
        foreach (var deviceId in pc.RevokeIds)
            await TryRequestAsync(client, MobileBridge.OpDevicesRevoke, new { deviceId }, null);
        await LoadDevicesAsync();
    }

    private static async Task TryRequestAsync(MobileBridgeClient client, string op, object payload, TimeSpan? timeout)
    {
        try { await client.RequestAsync(op, payload, timeout: timeout); }
        catch (Exception ex) when (ex is IOException or TaskCanceledException or OperationCanceledException) { }
    }

    private void UpdateCountdown()
    {
        var left = _expiresAt - DateTimeOffset.UtcNow;
        if (left <= TimeSpan.Zero)
        {
            _countdown.Stop();
            CountdownText.Text = "코드가 만료되었습니다 — NEW CODE를 누르세요";
            QrImage.Opacity = 0.25;
            return;
        }
        QrImage.Opacity = 1;
        CountdownText.Text = $"남은 시간 {left:m\\:ss}";
    }

    private static BitmapImage RenderQr(string text)
    {
        using var generator = new QRCodeGenerator();
        // Shown on a screen, never printed or scuffed: level L keeps the ~750-char link at version 19.
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.L);
        var png = new PngByteQRCode(data).GetGraphic(6);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private async Task LoadSettingsAsync()
    {
        try
        {
            var mobile = (await ElevatedConfig.ReadAsync())["mobile"] as JsonObject;
            var enabled = (bool?)mobile?["enabled"] == true;
            // Feature off → everything pre-checked (QR picks automatically); on → what is configured.
            LanCheck.IsChecked = enabled ? (bool?)mobile?["lan"] == true : true;
            TailscaleCheck.IsChecked = enabled ? (bool?)mobile?["tailscale"] ?? true : true;
            var cf = mobile?["cloudflare"] as JsonObject;
            CloudflareCheck.IsChecked = enabled ? (bool?)cf?["enabled"] == true : true;
            TunnelHostBox.Text = (string?)cf?["hostname"] ?? "";
            _hasSavedToken = !string.IsNullOrWhiteSpace((string?)cf?["token"]);
            TokenSavedText.Visibility = _hasSavedToken ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException) { }
        CloudflareFields.IsEnabled = CloudflareCheck.IsChecked == true;
    }

    private void CloudflareCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (CloudflareFields is not null) CloudflareFields.IsEnabled = CloudflareCheck.IsChecked == true;
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var token = TunnelTokenBox.Password.Trim();
        var host = TunnelHostBox.Text.Trim();
        if (CloudflareCheck.IsChecked == true && token.Length > 0 && host.Length == 0)
        {
            MessageDialog.Show("Homebase", "터널 토큰을 쓰려면 Cloudflare 대시보드에서 이 터널에 연결한 공개 호스트명도 넣어야 합니다.", owner: Window.GetWindow(this));
            return;
        }
        if (LanCheck.IsChecked != true && TailscaleCheck.IsChecked != true && CloudflareCheck.IsChecked != true)
        {
            MessageDialog.Show("Homebase", "연결 경로를 하나 이상 고르세요.", owner: Window.GetWindow(this));
            return;
        }

        ApplyButton.IsEnabled = false;
        _busy = true;
        try
        {
            var root = await ElevatedConfig.ReadAsync();
            var mobile = root["mobile"] as JsonObject ?? new JsonObject();
            mobile["enabled"] = true;
            mobile["userSid"] = WindowsIdentity.GetCurrent().User?.Value;
            mobile["port"] ??= MobileProtocol.DefaultPort;
            mobile["claudeSessions"] ??= true;
            mobile["lan"] = LanCheck.IsChecked == true;
            mobile["tailscale"] = TailscaleCheck.IsChecked == true;
            var cf = mobile["cloudflare"] as JsonObject ?? new JsonObject();
            cf["enabled"] = CloudflareCheck.IsChecked == true;
            if (token.Length > 0) cf["token"] = token;
            else if (host.Length == 0) cf.Remove("token"); // both empty → Quick Tunnel
            if (host.Length > 0) cf["hostname"] = host; else cf.Remove("hostname");
            mobile["cloudflare"] = cf;
            root["mobile"] = mobile;

            StatusText.Text = "설정을 적용하고 Agent를 다시 시작하는 중…";
            if (!await ElevatedConfig.WriteAsync(root))
            {
                StatusText.Text = "관리자 승인이 취소되어 아무것도 바뀌지 않았습니다.";
                return;
            }
            TunnelTokenBox.Clear();
            _offerEndpointsKey = "";
            _statusTimer.Stop();
            await RefreshAsync(); // the bridge client reconnects to the restarted Agent
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException)
        {
            MessageDialog.Show("Homebase", "설정을 적용하지 못했습니다: " + ex.Message, owner: Window.GetWindow(this));
        }
        finally
        {
            _busy = false;
            ApplyButton.IsEnabled = true;
        }
    }

    private async void PathAction_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string key) return;
        switch (key)
        {
            case "install-tailscale":
                await InstallAsync("Tailscale", MobileComponents.TailscalePackage,
                    "Tailscale을 설치했습니다. 트레이의 Tailscale 아이콘에서 로그인하면 이 경로가 켜집니다(최대 30초).");
                break;
            case "install-cloudflared":
                await InstallAsync("cloudflared", MobileComponents.CloudflaredPackage,
                    "cloudflared를 설치했습니다. Agent가 1분 안에 터널을 엽니다.");
                break;
            case "open-tailscale":
                TryStart(MobileComponents.TailscaleGuiExe);
                break;
            case "network-settings":
                TryStart("ms-settings:network-status");
                break;
        }
    }

    private async Task InstallAsync(string name, string packageId, string done)
    {
        if (!MessageDialog.Show("Homebase",
                $"winget으로 {name}을(를) 설치할까요? 설치 관리자가 관리자 승인을 물을 수 있고, 패키지 사용 약관에 동의하게 됩니다.",
                "Install", "Cancel", owner: Window.GetWindow(this)))
            return;
        _busy = true;
        StatusText.Text = $"{name} 설치 중… (1~2분 걸릴 수 있습니다)";
        try
        {
            var error = await MobileComponents.InstallAsync(packageId);
            StatusText.Text = error is null ? done : $"{name} 설치에 실패했습니다: {error}";
        }
        finally
        {
            _busy = false;
        }
        await PollStatusAsync();
    }

    private static void TryStart(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
    }

    private async void NewCode_Click(object sender, RoutedEventArgs e) => await NewOfferAsync();

    private async void Revoke_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string id || MobileHost.Client is not { } client) return;
        if (!MessageDialog.Show("Homebase", L.PhoneRevokeConfirm, "Revoke", "Cancel", owner: Window.GetWindow(this)))
            return;
        try { await client.RequestAsync(MobileBridge.OpDevicesRevoke, new { deviceId = id }); }
        catch (Exception ex) when (ex is IOException or TaskCanceledException or OperationCanceledException) { }
        await LoadDevicesAsync();
    }
}
