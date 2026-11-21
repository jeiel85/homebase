using LocalOpsBot.Core.Mobile;
using Xunit;

namespace LocalOpsBot.Tests.Mobile;

/// <summary>Old entries left by reinstalling the phone app and pairing again (DECISIONS M-71).</summary>
public class PhoneReinstallsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 8, 36, 0, TimeSpan.FromHours(9));

    private static MobileDeviceInfo Phone(string id, string name, string? model, DateTimeOffset paired, DateTimeOffset? seen) =>
        new(id, name, model, paired, seen);

    [Fact]
    public void The_entry_quiet_since_the_same_phone_paired_again_is_marked()
    {
        // The case from 2026-11-21: the GitHub build's S24 went quiet on 11-20, the Play build paired on 11-21.
        var old = Phone("34dd104ea994", "용은의 S24", "SM-S921N", T0, T0.AddDays(5.65));
        var fresh = Phone("f622da43b992", "용은의 S24", "SM-S921N", T0.AddDays(6.7), T0.AddDays(6.8));

        Assert.Equal(new[] { "34dd104ea994" }, PhoneReinstalls.Superseded(new[] { old, fresh }));
    }

    [Fact]
    public void Name_and_model_match_like_the_group_key_trimmed_and_case_insensitive()
    {
        var old = Phone("a", "Galaxy S24 ", "sm-s921n", T0, null);
        var fresh = Phone("b", "galaxy s24", "SM-S921N", T0.AddHours(1), null);

        Assert.Equal(new[] { "a" }, PhoneReinstalls.Superseded(new[] { fresh, old }));
    }

    [Fact]
    public void A_second_phone_of_the_same_name_that_still_reports_is_left_alone()
    {
        var other = Phone("a", "Galaxy S24", "SM-S921N", T0, T0.AddDays(2));
        var fresh = Phone("b", "Galaxy S24", "SM-S921N", T0.AddDays(1), T0.AddDays(2));

        Assert.Empty(PhoneReinstalls.Superseded(new[] { other, fresh }));
    }

    [Fact]
    public void Other_models_other_names_pcs_and_single_phones_are_never_marked()
    {
        var devices = new[]
        {
            Phone("a", "S24", "SM-S921N", T0, null),
            Phone("b", "S24", "SM-S928N", T0.AddDays(1), null),
            Phone("c", "Tab", "SM-X710", T0, null),
            new MobileDeviceInfo("d", "DESKTOP", null, T0, null, DeviceKind.Pc),
            new MobileDeviceInfo("e", "DESKTOP", null, T0.AddDays(1), null, DeviceKind.Pc),
        };

        Assert.Empty(PhoneReinstalls.Superseded(devices));
    }

    [Fact]
    public void Several_reinstalls_leave_only_the_newest()
    {
        var devices = new[]
        {
            Phone("a", "S24", "SM-S921N", T0, T0.AddHours(1)),
            Phone("b", "S24", "SM-S921N", T0.AddDays(1), T0.AddDays(1).AddHours(1)),
            Phone("c", "S24", "SM-S921N", T0.AddDays(2), null),
        };

        Assert.Equal(new[] { "a", "b" }, PhoneReinstalls.Superseded(devices).Order());
    }
}
