using System.Security.Cryptography;
using System.Text;
using LocalOpsBot.Core.Push;
using Xunit;

namespace LocalOpsBot.Tests.Push;

/// <summary>RFC 8291 Appendix A ("Intermediate Values for Encryption"). The Kotlin side checks the same vector.</summary>
public class WebPushEncryptionTests
{
    internal const string Plaintext = "V2hlbiBJIGdyb3cgdXAsIEkgd2FudCB0byBiZSBhIHdhdGVybWVsb24";
    internal const string AsPublic = "BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8";
    internal const string AsPrivate = "yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw";
    internal const string UaPublic = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4";
    internal const string UaPrivate = "q1dXpw3UpT5VOmu_cf_v6ih07Aems3njxI-JWgLcM94";
    internal const string Salt = "DGv6ra1nlYgDCS1FRnbzlw";
    internal const string AuthSecret = "BTBZMqHH6r4Tts7J_aSIgg";
    internal const string Header = "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8";
    internal const string Ciphertext = "8pfeW0KbunFT06SuDKoJH9Ql87S1QUrdirN6GcG7sFz1y1sqLgVi1VhjVkHsUoEsbI_0LpXMuGvnzQ";

    internal static ECDiffieHellman Key(string privateB64, string publicB64)
    {
        var pub = Base64Url.Decode(publicB64);
        var key = ECDiffieHellman.Create();
        key.ImportParameters(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = Base64Url.Decode(privateB64),
            Q = new ECPoint { X = pub[1..33], Y = pub[33..65] },
        });
        return key;
    }

    [Fact]
    public void Encrypt_reproduces_the_rfc_8291_appendix_a_message()
    {
        using var asKey = Key(AsPrivate, AsPublic);
        var body = WebPushEncryption.Encrypt(
            Base64Url.Decode(Plaintext), Base64Url.Decode(UaPublic), Base64Url.Decode(AuthSecret),
            asKey, Base64Url.Decode(Salt));

        Assert.Equal(Header + Ciphertext, Base64Url.Encode(body[..86]) + Base64Url.Encode(body[86..]));
        Assert.Equal(Base64Url.Decode(Header), body[..86]);
        Assert.Equal(Base64Url.Decode(Ciphertext), body[86..]);
    }

    [Fact]
    public void Decrypt_recovers_the_appendix_a_plaintext()
    {
        using var uaKey = Key(UaPrivate, UaPublic);
        var body = Base64Url.Decode(Header).Concat(Base64Url.Decode(Ciphertext)).ToArray();

        var plain = WebPushEncryption.Decrypt(body, uaKey, Base64Url.Decode(AuthSecret));

        Assert.Equal("When I grow up, I want to be a watermelon", Encoding.UTF8.GetString(plain));
    }

    [Fact]
    public void Random_keys_round_trip_and_each_message_uses_a_new_key_and_salt()
    {
        using var ua = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var auth = RandomNumberGenerator.GetBytes(16);
        var uaPublic = WebPushEncryption.ExportUncompressed(ua);
        var text = Encoding.UTF8.GetBytes("{\"title\":\"디스크 부족\"}");

        var a = WebPushEncryption.Encrypt(text, uaPublic, auth);
        var b = WebPushEncryption.Encrypt(text, uaPublic, auth);

        Assert.Equal(text, WebPushEncryption.Decrypt(a, ua, auth));
        Assert.NotEqual(a[..86], b[..86]);
    }

    [Fact]
    public void Tampered_ciphertext_or_wrong_auth_fails()
    {
        using var uaKey = Key(UaPrivate, UaPublic);
        var body = Base64Url.Decode(Header).Concat(Base64Url.Decode(Ciphertext)).ToArray();
        var tampered = (byte[])body.Clone();
        tampered[^1] ^= 1;

        Assert.ThrowsAny<CryptographicException>(() => WebPushEncryption.Decrypt(tampered, uaKey, Base64Url.Decode(AuthSecret)));
        Assert.ThrowsAny<CryptographicException>(() => WebPushEncryption.Decrypt(body, uaKey, new byte[16]));
    }

    [Fact]
    public void Largest_plaintext_fills_exactly_4096_bytes_and_one_more_is_refused()
    {
        using var ua = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var uaPublic = WebPushEncryption.ExportUncompressed(ua);
        var auth = RandomNumberGenerator.GetBytes(16);

        Assert.Equal(3993, WebPushEncryption.MaxPlaintextBytes);
        Assert.Equal(4096, WebPushEncryption.Encrypt(new byte[3993], uaPublic, auth).Length);
        Assert.Throws<ArgumentException>(() => WebPushEncryption.Encrypt(new byte[3994], uaPublic, auth));
    }

    [Theory]
    [InlineData(64, 16)]
    [InlineData(65, 15)]
    public void Malformed_subscription_keys_are_rejected(int keyLength, int authLength)
    {
        var key = new byte[keyLength];
        if (keyLength > 0) key[0] = 0x04;
        Assert.Throws<ArgumentException>(() => WebPushEncryption.ValidateSubscriptionKeys(key, new byte[authLength]));
    }

    [Fact]
    public void A_point_off_the_curve_is_rejected()
    {
        var key = Base64Url.Decode(UaPublic);
        key[^1] ^= 1;
        Assert.Throws<ArgumentException>(() => WebPushEncryption.ValidateSubscriptionKeys(key, new byte[16]));
    }
}
