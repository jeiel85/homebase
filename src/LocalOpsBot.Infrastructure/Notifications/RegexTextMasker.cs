using System.Text.RegularExpressions;
using LocalOpsBot.Core.Notifications;

namespace LocalOpsBot.Infrastructure.Notifications;

public sealed class RegexTextMasker : ITextMasker
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// What a text becomes when a masking expression runs out of time on it: the expression could not say
    /// where its secrets are, so none of the text is passed on (fail closed, v0.60.2).
    /// </summary>
    public const string TimedOut = "[masked]";
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
            // Bounded so a pathological expression cannot hang the pipeline; on timeout the text may still
            // hold what that expression should have hidden, so it is withheld whole rather than forwarded.
            try { result = regex.Replace(result, MatchEvaluator); }
            catch (RegexMatchTimeoutException) { return TimedOut; }
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
