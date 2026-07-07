using LocalOpsBot.Infrastructure.Notifications;
using Xunit;

namespace LocalOpsBot.Tests.Infrastructure.Notifications;

public sealed class RegexTextMaskerTests
{
    private static readonly string[] Patterns =
    [
        "(?<!!)\\d{6}(?!\\d)",
        "(?i)(password|passwd|pwd)\\s*[:=]\\s*\\S+",
        "(?i)bearer\\s+[a-z0-9._\\-]+",
        "(?i)(token|secret|api[_-]?key)\\s*[:=]\\s*\\S+"
    ];

    private readonly RegexTextMasker _masker = new(Patterns);

    [Fact]
    public void Mask_masks_six_digit_otp()
    {
        var result = _masker.Mask("Your code is 123456");
        Assert.Contains("******", result);
        Assert.DoesNotContain("123456", result);
    }

    [Fact]
    public void Mask_masks_password_in_value_format()
    {
        var result = _masker.Mask("password=supersecret123");
        Assert.Contains("password=", result);
        Assert.Contains("****", result);
        Assert.DoesNotContain("supersecret123", result);
    }

    [Fact]
    public void Mask_masks_password_in_colon_format()
    {
        var result = _masker.Mask("pwd:mysecret");
        Assert.Contains("pwd:", result);
        Assert.DoesNotContain("mysecret", result);
    }

    [Fact]
    public void Mask_masks_bearer_token()
    {
        var result = _masker.Mask("Authorization: bearer eyJhbGciOiJIUzI1NiJ9.test");
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9.test", result);
        Assert.Contains("****", result);
    }

    [Fact]
    public void Mask_masks_api_key()
    {
        var result = _masker.Mask("api_key=sk-1234567890abcdef");
        Assert.Contains("api_key=", result);
        Assert.DoesNotContain("sk-1234567890abcdef", result);
    }

    [Fact]
    public void Mask_returns_empty_when_input_empty()
    {
        Assert.Empty(_masker.Mask(""));
    }

    [Fact]
    public void Mask_returns_null_when_input_null()
    {
        Assert.Null(_masker.Mask(null!));
    }

    [Fact]
    public void Mask_does_not_mask_normal_text()
    {
        var text = "Hello, this is a normal message without secrets.";
        var result = _masker.Mask(text);
        Assert.Equal(text, result);
    }

    [Fact]
    public void Mask_does_not_mask_5_digit_numbers()
    {
        var text = "Ref: 12345";
        var result = _masker.Mask(text);
        Assert.Contains("12345", result);
    }
}
