using System.IO;
using LocalOpsBot.Tray.Services;
using Xunit;

namespace LocalOpsBot.Tray.Tests;

/// <summary>
/// An error nothing caught still leaves a line in tray.log when the whole entry can't be written (#106): the last
/// resort writes from buffers set aside at start-up.
/// </summary>
public sealed class TrayLogTests
{
    private static string LogFile => Path.Combine(Wpf.DataDir, "Homebase", "logs", "tray.log");

    [Fact]
    public Task The_last_resort_writes_one_line_with_the_exceptions_type_and_message() => Wpf.Run(() =>
    {
        TrayLog.Prepare();
        var marker = "emergency-" + Guid.NewGuid().ToString("N");

        Assert.True(TrayLog.WriteEmergency("App", marker, new InvalidOperationException("out of commit")));

        var line = File.ReadAllLines(LogFile).Single(l => l.Contains(marker));
        Assert.Contains($"[Error] App: {marker} — System.InvalidOperationException: out of commit", line);
        return Task.CompletedTask;
    });

    [Fact]
    public Task A_message_longer_than_the_buffer_is_cut_not_dropped() => Wpf.Run(() =>
    {
        TrayLog.Prepare();
        var marker = "long-" + Guid.NewGuid().ToString("N");

        Assert.True(TrayLog.WriteEmergency("App", marker + new string('x', 5000), null));

        var line = File.ReadAllLines(LogFile).Single(l => l.Contains(marker));
        Assert.True(line.Length <= 1024);
        // Cut at the buffer, the line still ends with its own line break: nothing written after it joins this line.
        var text = File.ReadAllText(LogFile);
        var start = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = text.IndexOf("\r\n", start, StringComparison.Ordinal);
        Assert.True(end > start);
        Assert.All(text[start..end], c => Assert.Equal('x', c));
        return Task.CompletedTask;
    });

    [Fact]
    public Task An_error_is_written_whole_when_it_can_be() => Wpf.Run(() =>
    {
        var marker = "whole-" + Guid.NewGuid().ToString("N");

        TrayLog.Error("App", marker, new InvalidOperationException("boom"));

        var text = File.ReadAllText(LogFile);
        Assert.Contains($"[Error] App: {marker}", text);
        Assert.Contains("System.InvalidOperationException: boom", text);
        Assert.DoesNotContain(File.ReadAllLines(LogFile).Where(l => l.Contains(marker)), l => l.Contains("written without its stack trace"));
        return Task.CompletedTask;
    });
}
