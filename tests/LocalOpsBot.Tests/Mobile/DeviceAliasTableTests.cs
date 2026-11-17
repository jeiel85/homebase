using LocalOpsBot.Agent.Services.Mobile;
using LocalOpsBot.Core.Mobile;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LocalOpsBot.Tests.Mobile;

/// <summary>The group's aliases (DECISIONS M-50, M-51).</summary>
public sealed class DeviceAliasTableTests : IDisposable
{
    private const string Work = "pc:a1b2c3d4e5f60718";
    private static readonly string Phone = GroupKeys.PhoneByName("용은의 S24", "samsung SM-S921N");

    private readonly ManualTimeProvider _time = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hb-alias-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private DeviceAlias Entry(string key, string? alias, TimeSpan ago, string name = "WORK-PC") => new(key, name, alias, _time.Now - ago);

    [Theory]
    [InlineData("  작업실  ", "작업실")]
    [InlineData("거실\r\n  노트북", "거실 노트북")]
    [InlineData("a\t\tb", "a b")]
    public void Typed_text_is_trimmed_and_inner_whitespace_made_one_space(string input, string expected) =>
        Assert.Equal(expected, DeviceAliasTable.Normalize(input, "DESKTOP-5BP128L"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("DESKTOP-5BP128L")]
    [InlineData("  DESKTOP-5BP128L ")]
    public void Blank_or_the_devices_own_name_means_no_alias(string? input) =>
        Assert.Null(DeviceAliasTable.Normalize(input, "DESKTOP-5BP128L"));

    [Fact]
    public void Long_input_is_cut_at_40_code_points_without_splitting_an_emoji()
    {
        var alias = DeviceAliasTable.Normalize(new string('가', 39) + "😀😀", "PC")!;
        Assert.Equal(40, DeviceAliasTable.Length(alias));
        Assert.EndsWith("😀", alias);
    }

    [Fact]
    public void Setting_stamps_now_and_a_change_is_strictly_after_the_one_it_replaces()
    {
        var t = new DeviceAliasTable(_time);
        var raised = 0;
        t.Changed += () => raised++;

        var first = t.Set(Work, "WORK-PC", "회사")!;
        Assert.Equal(_time.Now, first.At);
        Assert.Null(t.Set(Work, "WORK-PC", "  회사 ")); // the same alias
        Assert.Null(t.Set("bogus", "WORK-PC", "x"));
        Assert.Null(t.Set(Work, " ", "x"));

        // Another member stamped a later time (its clock is ahead); a change made here still wins.
        t.Merge(new[] { Entry(Work, "사무실", TimeSpan.FromMinutes(-5)) });
        var cleared = t.Set(Work, "WORK-PC", "")!;
        Assert.Null(cleared.Alias);
        Assert.True(cleared.At > _time.Now);
        Assert.Null(t.Get(Work));
        Assert.Equal(3, raised);
    }

    [Fact]
    public void The_newest_entry_wins_and_a_tie_goes_to_the_larger_alias_on_every_member()
    {
        var a = new DeviceAliasTable(_time);
        var b = new DeviceAliasTable(_time);
        var older = Entry(Work, "회사", TimeSpan.FromMinutes(2));
        var newer = Entry(Work, "사무실", TimeSpan.FromMinutes(1));
        var tieLow = Entry(Phone, "A폰", TimeSpan.Zero, "용은의 S24");
        var tieHigh = Entry(Phone, "B폰", TimeSpan.Zero, "용은의 S24");

        Assert.Equal(2, a.Merge(new[] { older, tieLow }));
        Assert.Equal(2, a.Merge(new[] { newer, tieHigh }));
        Assert.Equal(2, b.Merge(new[] { newer, tieHigh }));
        Assert.Equal(0, b.Merge(new[] { older, tieLow })); // arriving late changes nothing

        Assert.Equal(a.List(), b.List());
        Assert.Equal("사무실", a.Get(Work));
        Assert.Equal("B폰", a.Get(Phone));
    }

    [Fact]
    public void A_cleared_alias_travels_and_is_forgotten_after_90_days()
    {
        var t = new DeviceAliasTable(_time);
        t.Merge(new[] { Entry(Work, "회사", TimeSpan.FromMinutes(2)) });
        t.Merge(new[] { Entry(Work, null, TimeSpan.FromMinutes(1)) });
        Assert.Null(t.Get(Work));
        Assert.Contains(t.List(), e => e.Key == Work && e.Alias is null); // goes out with the group answer

        // A member that was off still holds the old alias: it doesn't come back.
        Assert.Equal(0, t.Merge(new[] { Entry(Work, "회사", TimeSpan.FromMinutes(2)) }));

        _time.Advance(DeviceAliasTable.ClearedKeep + TimeSpan.FromDays(1));
        Assert.Empty(t.List());
    }

    [Fact]
    public void Entries_far_in_the_future_or_with_bad_keys_or_names_are_refused()
    {
        var t = new DeviceAliasTable(_time);
        Assert.Equal(0, t.Merge(new[]
        {
            Entry(Work, "미래", -DeviceAliasTable.FutureSlack - TimeSpan.FromHours(1)),
            Entry("tablet:x", "x", TimeSpan.Zero),
            Entry(Work, "이름 없음", TimeSpan.Zero, name: "  "),
            Entry(Work, null, DeviceAliasTable.ClearedKeep + TimeSpan.FromDays(1)),
        }));
        Assert.Equal(1, t.Merge(new[] { Entry(Work, "  " + new string('x', 300), TimeSpan.Zero) }));
        Assert.Equal(40, t.Get(Work)!.Length);
    }

    [Fact]
    public void A_device_is_found_by_key_or_else_by_kind_and_name()
    {
        var t = new DeviceAliasTable(_time);
        t.Merge(new[]
        {
            Entry(Work, "회사", TimeSpan.FromMinutes(1)),
            Entry(Phone, "내 폰", TimeSpan.FromMinutes(1), "용은의 S24"),
            Entry("pc:cleared", null, TimeSpan.FromMinutes(1), "HOME-PC"),
            Entry("pc:old", "집", TimeSpan.FromMinutes(5), "HOME-PC"),
        });

        Assert.Equal("회사", t.Resolve(Work, DeviceKind.Pc, "anything"));
        // Only a name (a relayed notification, a PC that has not said its id yet).
        Assert.Equal("회사", t.Resolve(null, DeviceKind.Pc, " work-pc "));
        Assert.Equal("내 폰", t.Resolve(null, null, "용은의 S24")); // no kind = phone
        Assert.Null(t.Resolve(null, DeviceKind.Pc, "용은의 S24"));
        // A cleared key means no alias, even when another entry carries the same name.
        Assert.Null(t.Resolve("pc:cleared", DeviceKind.Pc, "HOME-PC"));
        Assert.Equal("집", t.Resolve(null, DeviceKind.Pc, "HOME-PC"));
        Assert.Equal("LAPTOP", t.Show(null, DeviceKind.Pc, "LAPTOP"));
    }

    [Fact]
    public void Replace_takes_the_agents_table_and_says_so_only_when_it_differs()
    {
        var t = new DeviceAliasTable(_time);
        var raised = 0;
        t.Changed += () => raised++;
        var table = new[] { Entry(Work, "회사", TimeSpan.FromMinutes(1)) };

        t.Replace(table);
        t.Replace(table);
        Assert.Equal(1, raised);
        t.Replace(Array.Empty<DeviceAlias>());
        Assert.Equal(2, raised);
        Assert.Equal(0, t.Count);
    }

    [Fact]
    public void Past_256_entries_the_oldest_cleared_go_first()
    {
        var t = new DeviceAliasTable(_time);
        t.Merge(new[] { Entry("pc:cleared", null, TimeSpan.FromDays(1)) });
        t.Merge(Enumerable.Range(0, DeviceAliasTable.MaxEntries)
            .Select(i => Entry("pc:" + i, "n" + i, TimeSpan.FromMinutes(DeviceAliasTable.MaxEntries - i))).ToList());

        Assert.Equal(DeviceAliasTable.MaxEntries, t.Count);
        Assert.DoesNotContain(t.List(), e => e.Key == "pc:cleared");
        Assert.Equal("n0", t.Get("pc:0"));
    }

    [Fact]
    public void The_agents_file_keeps_the_table_readable_and_survives_a_broken_file()
    {
        var t = DeviceAliasFile.Open(_dir, _time, NullLogger.Instance);
        t.Set(Work, "WORK-PC", "회사 데스크톱 😀");
        t.Set(Phone, "용은의 S24", "내 폰");
        t.Set(Phone, "용은의 S24", null);

        var again = DeviceAliasFile.Open(_dir, _time, NullLogger.Instance);
        Assert.Equal("회사 데스크톱 😀", again.Get(Work));
        Assert.Contains(again.List(), e => e.Key == Phone && e.Alias is null);
        var path = Path.Combine(_dir, "aliases.json");
        Assert.Contains("회사 데스크톱", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));

        File.WriteAllText(path, "{ not json");
        var broken = DeviceAliasFile.Open(_dir, _time, NullLogger.Instance);
        Assert.Equal(0, broken.Count);
        broken.Set(Work, "WORK-PC", "회사");
        Assert.Equal("회사", DeviceAliasFile.Open(_dir, _time, NullLogger.Instance).Get(Work));
    }
}
