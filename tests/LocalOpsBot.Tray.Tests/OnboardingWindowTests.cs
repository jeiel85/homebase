using System.Windows;
using System.Windows.Controls;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Tray.Services;
using Xunit;

namespace LocalOpsBot.Tray.Tests;

/// <summary>
/// The first-run checklist treats the phone (Homebase app push) as the main channel and Telegram as
/// the optional fallback: the phone row sits under REQUIRED, Telegram under OPTIONAL, and the phone
/// row's state comes from the Agent's device list.
/// </summary>
public sealed class OnboardingWindowTests
{
    [Fact]
    public Task Phone_is_required_and_Telegram_is_optional() => Wpf.Run(() =>
    {
        // Built but never shown, so no probe runs: only the layout is under test.
        var window = new OnboardingWindow();
        try
        {
            var required = CardAfter(window, L.SetupRequired);
            var optional = CardAfter(window, L.SetupOptional);

            Assert.True(Contains(required, window.ServiceChip));
            Assert.True(Contains(required, window.PhoneChip));
            Assert.False(Contains(required, window.TelegramChip));

            Assert.True(Contains(optional, window.TelegramChip));
            Assert.True(Contains(optional, window.OllamaChip));
            Assert.False(Contains(optional, window.PhoneChip));
        }
        finally
        {
            window.Close();
        }
        return Task.CompletedTask;
    });

    [Fact]
    public void Phone_off_or_unreachable_needs_a_step()
    {
        Assert.Equal(new PhoneProbe(PhoneReadiness.Off, 0), ConnectionReadiness.ClassifyPhone(false, true, [Phone("a")]));
        Assert.Equal(new PhoneProbe(PhoneReadiness.NoBridge, 0), ConnectionReadiness.ClassifyPhone(true, false, null));
        Assert.Equal(new PhoneProbe(PhoneReadiness.Unknown, 0), ConnectionReadiness.ClassifyPhone(true, true, null));
    }

    [Fact]
    public void Only_phones_count_as_paired()
    {
        // A linked PC (device group) in the list is not a phone to send alerts to.
        Assert.Equal(new PhoneProbe(PhoneReadiness.NoPhone, 0),
            ConnectionReadiness.ClassifyPhone(true, true, []));
        Assert.Equal(new PhoneProbe(PhoneReadiness.NoPhone, 0),
            ConnectionReadiness.ClassifyPhone(true, true, [Pc("hub")]));
        Assert.Equal(new PhoneProbe(PhoneReadiness.Ready, 2),
            ConnectionReadiness.ClassifyPhone(true, true, [Phone("a"), Pc("hub"), Phone("b")]));
    }

    [Fact]
    public void A_device_from_an_older_app_without_a_kind_is_a_phone()
    {
        var old = Phone("old") with { Kind = null! };
        Assert.Equal(new PhoneProbe(PhoneReadiness.Ready, 1), ConnectionReadiness.ClassifyPhone(true, true, [old]));
    }

    private static MobileDeviceInfo Phone(string id) =>
        new(id, "Pixel", "Pixel 9", DateTimeOffset.UtcNow, null, DeviceKind.Phone);

    private static MobileDeviceInfo Pc(string id) =>
        new(id, "DESKTOP", null, DateTimeOffset.UtcNow, null, DeviceKind.Pc);

    /// <summary>The card right after the section header labelled <paramref name="label"/>.</summary>
    private static DependencyObject CardAfter(Window window, string label)
    {
        var list = Descendants(window).OfType<ScrollViewer>().Single().Content as StackPanel;
        Assert.NotNull(list);
        var items = list.Children.Cast<UIElement>().ToList();
        var header = items.FindIndex(e => Descendants(e).OfType<TextBlock>().Any(t => t.Text == label));
        Assert.True(header >= 0 && header + 1 < items.Count, $"No card after the '{label}' header.");
        return items[header + 1];
    }

    private static bool Contains(DependencyObject root, DependencyObject target) =>
        Descendants(root).Contains(target);

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var d in Descendants(child))
                yield return d;
    }
}
