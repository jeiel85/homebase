using System.Text;
using LocalOpsBot.Tray.Services;
using Xunit;

namespace LocalOpsBot.Tray.Tests;

/// <summary>
/// The elevated config write: the Agent (SYSTEM) runs what the config names, and the config is staged
/// in the user's %TEMP% — so the elevated step must apply only the bytes the Tray wrote.
/// </summary>
public sealed class ElevatedConfigTests
{
    private static readonly string Digest = ElevatedConfig.Sha256Hex(Encoding.UTF8.GetBytes("{}"));

    [Fact]
    public void Applies_only_the_bytes_whose_hash_it_was_given()
    {
        var script = ElevatedConfig.BuildApplyScript(@"C:\Users\u\AppData\Local\Temp\homebase_cfg_1.json", Digest);

        Assert.Contains($"$expected = '{Digest}'", script);
        var read = script.IndexOf("ReadAllBytes($src)", StringComparison.Ordinal);
        var check = script.IndexOf("if ($actual -ne $expected)", StringComparison.Ordinal);
        var write = script.IndexOf("$out.Write($bytes", StringComparison.Ordinal);
        Assert.True(read > 0 && read < check && check < write);
        // The staged file is read once; nothing after the check reads it again.
        Assert.Equal(2, CountOf(script, "$src")); // its assignment and the one read
        Assert.DoesNotContain("Copy-Item", script);
        // Written as a new file, never into one that already exists (and may belong to someone else).
        Assert.Contains("[System.IO.FileMode]::CreateNew", script);
    }

    [Fact]
    public void Quotes_the_staged_path_as_a_literal()
    {
        var script = ElevatedConfig.BuildApplyScript(@"C:\Users\O'Brien\cfg.json", Digest);
        Assert.Contains(@"$src = 'C:\Users\O''Brien\cfg.json'", script);
    }

    [Theory]
    [InlineData(@"C:\cfg.json", "abc")]
    [InlineData("C:\\cfg\n.json", null)]
    public void Rejects_bad_input(string path, string? sha256) =>
        Assert.Throws<ArgumentException>(() => ElevatedConfig.BuildApplyScript(path, sha256 ?? Digest));

    private static int CountOf(string s, string part)
    {
        var n = 0;
        for (var i = s.IndexOf(part, StringComparison.Ordinal); i >= 0; i = s.IndexOf(part, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }
}
