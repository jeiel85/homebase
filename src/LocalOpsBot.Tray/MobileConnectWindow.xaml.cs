using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Tray.Services;
using LocalOpsBot.Tray.Services.Mobile;
using QRCoder;

namespace LocalOpsBot.Tray;

/// <summary>
/// "Connect phone": picks the paths (LAN / Tailscale / Cloudflare Tunnel) and turns the feature on
/// (one UAC step), shows the one-time pairing QR that carries every path, shows each path's state
/// with an install button for a missing connector, and lists/revokes paired phones.
/// Status is polled only while this window is open.
/// </summary>
public partial class MobileConnectWindow : ThemedWindow
{
    private static readonly TimeSpan StatusPoll = TimeSpan.FromSeconds(5);

    private readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = StatusPoll };
    private DateTimeOffset _expiresAt;
    private string _offerEndpointsKey = "";
    private bool _hasSavedToken;
    private bool _busy;

    private sealed record DeviceRow(string DeviceId, string Name, string Detail);

    private sealed record PathRow(string Title, string Detail, Brush Dot, string? ActionLabel = null, string? ActionKey = null)
    {
        public Visibility ActionVisibility => ActionLabel is null ? Visibility.Collapsed : Visibility.Visible;
    }

    public MobileConnectWindow()
    {
        InitializeComponent();
        _countdown.Tick += (_, _) => UpdateCountdown();
        _statusTimer.Tick += async (_, _) => await PollStatusAsync();
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => { _countdown.Stop(); _statusTimer.Stop(); };
    }

    private async Task RefreshAsync()
    {
        await LoadSettingsAsync();
        if (!MobileHost.IsEnabled())
        {
            StatusText.Text = "휴대폰 연결이 꺼져 있습니다. 쓸 경로를 고른 뒤 ENABLE을 누르세요.";
            IntroCard.Visibility = Visibility.Visible;
            PairPanel.Visibility = PathsPanel.Visibility = DevicesPanel.Visibility = Visibility.Collapsed;
            ApplyButton.Content = "ENABLE";
            return;
        }

        IntroCard.Visibility = Visibility.Collapsed;
        ApplyButton.Content = "APPLY";
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
        ShowPaths(status);
        _statusTimer.Start();
        await LoadDevicesAsync();
        if (status.Endpoints.Count == 0) { ShowNoPath(status); return; }

        ShowReady();
        await NewOfferAsync();
    }

    private void ShowReady()
    {
        StatusText.Text = "휴대폰의 Homebase 앱에서 '+ PC 추가'를 누르고 아래 QR 코드를 스캔하세요. " +
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
            _offerEndpointsKey = EndpointsKey(offer.Endpoints);
            _expiresAt = offer.ExpiresAt;
            UpdateCountdown();
            _countdown.Start();
        }
        catch (Exception ex) when (ex is IOException or TaskCanceledException or OperationCanceledException)
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
            var devices = r["payload"]?.Deserialize<List<MobileDeviceInfo>>(MobileProtocol.Json) ?? new();
            DeviceList.ItemsSource = devices.Select(d => new DeviceRow(
                d.DeviceId,
                d.Name,
                $"{d.Model ?? "Android"} · 연결 {d.PairedAt.ToLocalTime():yyyy-MM-dd HH:mm}" +
                (d.LastSeenAt is { } seen ? $" · 최근 {seen.ToLocalTime():MM-dd HH:mm}" : ""))).ToList();
            NoDevicesText.Visibility = devices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
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
            MessageDialog.Show("Homebase", "터널 토큰을 쓰려면 Cloudflare 대시보드에서 이 터널에 연결한 공개 호스트명도 넣어야 합니다.");
            return;
        }
        if (LanCheck.IsChecked != true && TailscaleCheck.IsChecked != true && CloudflareCheck.IsChecked != true)
        {
            MessageDialog.Show("Homebase", "연결 경로를 하나 이상 고르세요.");
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
            MessageDialog.Show("Homebase", "설정을 적용하지 못했습니다: " + ex.Message);
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
                "Install", "Cancel"))
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
        if (!MessageDialog.Show("Homebase", "이 휴대폰의 연결을 해제할까요? 다시 쓰려면 QR로 다시 연결해야 합니다.", "Revoke", "Cancel"))
            return;
        try { await client.RequestAsync(MobileBridge.OpDevicesRevoke, new { deviceId = id }); }
        catch (Exception ex) when (ex is IOException or TaskCanceledException or OperationCanceledException) { }
        await LoadDevicesAsync();
    }
}
