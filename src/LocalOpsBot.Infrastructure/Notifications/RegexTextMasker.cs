using System.Text.RegularExpressions;
using LocalOpsBot.Core.Notifications;

namespace LocalOpsBot.Infrastructure.Notifications;

public sealed class RegexTextMasker : ITextMasker
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);
    private readonly IReadOnlyList<Regex> _patterns;

    public RegexTextMasker(IReadOnlyList<string> patterns)
    {
        _patterns = patterns.Select(p => new Regex(p, RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout)).ToList();
    }

    public string Mask(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        var result = input;
        foreach (var regex in _patterns)
        {
            try { result = regex.Replace(result, MatchEvaluator); }
            catch (RegexMatchTimeoutException) { /* skip pathological match rather than hang the pipeline */ }
        }
        return result;
    }

    private static string MatchEvaluator(Match match)
    {
        var val = match.Value;
        var sepIdx = val.IndexOfAny(new[] { ':', '=' });
        if (sepIdx >= 0)
        {
            var prefix = val[..(sepIdx + 1)];
            return prefix + new string('*', Math.Min(val.Length - sepIdx - 1, 20));
        }
        return new string('*', Math.Min(val.Length, 20));
    }
}
