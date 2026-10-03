using System.Text.Json;
using Rayvia.Models;
using Rayvia.Services;

namespace Rayvia.Tests;

public sealed class ParserAndRoutingTests
{
    private const string Uuid = "11111111-1111-1111-1111-111111111111";

    [Fact]
    public void VlessRealityAndIpv6AreValidatedAndNormalized()
    {
        var node = SubscriptionParser.Parse($"vless://{Uuid}@[2001:db8::1]:443?security=reality&type=tcp&pbk=key&sni=example.com#Cosmetic");
        Assert.Equal("2001:db8::1", node.Host); Assert.Equal("[2001:db8::1]:443", node.Endpoint); Assert.Equal("reality", node.Security);
    }

    [Fact]
    public void InvalidPortAndUuidAreRejected() {
        Assert.Throws<SubscriptionParseException>(() => SubscriptionParser.Parse($"vless://bad@example.com:0"));
        Assert.Throws<SubscriptionParseException>(() => SubscriptionParser.Parse("vless://bad@example.com:443"));
    }

    [Fact]
    public void OtherProtocolsAndSsPluginAreValidated()
    {
        Assert.Equal("trojan", SubscriptionParser.Parse("trojan://password@example.com:443").Protocol);
        var vmess = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{{\"add\":\"example.com\",\"port\":443,\"id\":\"{Uuid}\",\"net\":\"ws\"}}"));
        Assert.Equal("vmess", SubscriptionParser.Parse("vmess://" + vmess).Protocol);
        Assert.Throws<SubscriptionParseException>(() => SubscriptionParser.Parse("ss://aes-128-gcm:pass@example.com:443?plugin=obfs-local"));
        Assert.Throws<SubscriptionParseException>(() => SubscriptionParser.Parse("ss://not-base64"));
    }

    [Fact]
    public void CosmeticNameDoesNotChangeCanonicalIdButTransportDoes()
    {
        var first = SubscriptionParser.Parse($"vless://{Uuid}@example.com:443?security=tls&type=ws&path=%2F#A");
        var second = SubscriptionParser.Parse($"vless://{Uuid}@EXAMPLE.com:443?security=tls&type=ws&path=%2F#B");
        var changed = SubscriptionParser.Parse($"vless://{Uuid}@example.com:443?security=tls&type=tcp#B");
        Assert.Equal(CanonicalNodeIdentity.CreateId(first), CanonicalNodeIdentity.CreateId(second));
        Assert.NotEqual(CanonicalNodeIdentity.CreateId(first), CanonicalNodeIdentity.CreateId(changed));
    }

    [Fact]
    public void RoutingHasExplicitFinalProxyAndPrivateDirect()
    {
        var json = JsonDocument.Parse(XrayConfigBuilder.Build(new ProxyNode { Protocol = "vless", Host = "example.com", Port = 443, UserId = Uuid }, new AppSettings(), "access", "error"));
        var rules = json.RootElement.GetProperty("routing").GetProperty("rules");
        Assert.Contains(rules.EnumerateArray(), rule => rule.GetProperty("outboundTag").GetString() == "direct" && rule.GetProperty("ip").EnumerateArray().Any(x => x.GetString() == "10.0.0.0/8"));
        Assert.Equal("proxy", rules[ rules.GetArrayLength() - 1 ].GetProperty("outboundTag").GetString());
    }
}
