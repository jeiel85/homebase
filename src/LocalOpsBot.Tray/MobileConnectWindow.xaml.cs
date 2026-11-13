using System.IO;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Tray.Services;
using LocalOpsBot.Tray.Services.Mobile;
using QRCoder;

namespace LocalOpsBot.Tray;

/// <summary>
/// "Connect phone": turns the mobile feature on (one UAC step), shows the one-time pairing QR
/// with its countdown, and lists/revokes paired phones.
/// </summary>
public partial class MobileConnectWindow : ThemedWindow
{
    private readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeOffset _expiresAt;

    private sealed record DeviceRow(string DeviceId, string Name, string Detail);

    public MobileConnectWindow()
    {
        InitializeComponent();
        _countdown.Tick += (_, _) => UpdateCountdown();
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => _countdown.Stop();
    }

    private async Task RefreshAsync()
    {
        if (!MobileHost.IsEnabled())
        {
            StatusText.Text = "휴대폰 연결이 꺼져 있습니다.";
            EnablePanel.Visibility = Visibility.Visible;
            PairPanel.Visibility = Visibility.Collapsed;
            return;
        }

        EnablePanel.Visibility = Visibility.Collapsed;
        var client = MobileHost.EnsureStarted();
        StatusText.Text = "Homebase Agent에 연결하는 중…";

        // The Agent may be restarting right after enabling; give it a moment.
        BridgeStatus? status = null;
        for (var i = 0; i < 20 && status is not { ApiListening: true }; i++)
        {
            try
            {
                if (client.IsConnected)
                {
                    var r = await client.RequestAsync(MobileBridge.OpStatus);
                    status = r["payload"]?.Deserialize<BridgeStatus>(MobileProtocol.Json);
                    if (status is { ApiListening: true }) break;
                }
            }
            catch (Exception ex) when (ex is IOException or TaskCanceledException or OperationCanceledException) { }
            await Task.Delay(1000);
        }

        if (status is null)
        {
            StatusText.Text = "Homebase Agent에 연결하지 못했습니다. 서비스가 실행 중인지 확인하세요.";
            return;
        }
        if (!status.ApiListening)
        {
            StatusText.Text = "Tailscale 주소를 찾지 못했습니다. 이 PC에서 Tailscale이 연결되어 있는지 확인하세요.";
            return;
        }

        StatusText.Text = "휴대폰의 Homebase 앱에서 '+ PC 추가'를 누르고 아래 QR 코드를 스캔하세요. 휴대폰도 Tailscale에 연결되어 있어야 합니다.";
        PairPanel.Visibility = Visibility.Visible;
        await NewOfferAsync();
        await LoadDevicesAsync();
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
            QrImage.Source = RenderQr(JsonSerializer.Serialize(offer, MobileProtocol.Json));
            HostsText.Text = $"{string.Join(" · ", offer.Hosts)} : {offer.Port}";
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
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var png = new PngByteQRCode(data).GetGraphic(8);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return image;
    }

    private async void Enable_Click(object sender, RoutedEventArgs e)
    {
        EnableButton.IsEnabled = false;
        try
        {
            var root = await ElevatedConfig.ReadAsync();
            var mobile = root["mobile"] as JsonObject ?? new JsonObject();
            mobile["enabled"] = true;
            mobile["userSid"] = WindowsIdentity.GetCurrent().User?.Value;
            mobile["port"] ??= MobileProtocol.DefaultPort;
            mobile["claudeSessions"] ??= true;
            root["mobile"] = mobile;
            StatusText.Text = "설정을 적용하고 Agent를 다시 시작하는 중…";
            if (!await ElevatedConfig.WriteAsync(root))
            {
                StatusText.Text = "관리자 승인이 취소되어 아무것도 바뀌지 않았습니다.";
                return;
            }
            await RefreshAsync();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or JsonException)
        {
            MessageDialog.Show("Homebase", "휴대폰 연결을 켜지 못했습니다: " + ex.Message);
        }
        finally
        {
            EnableButton.IsEnabled = true;
        }
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
