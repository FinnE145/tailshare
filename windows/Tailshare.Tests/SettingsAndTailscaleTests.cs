using System.Net;
using Tailshare.Core;

namespace Tailshare.Tests;

public class SettingsTests
{
    [Fact] // spec Settings file: missing file = all defaults
    public void Missing_file_is_all_defaults()
    {
        using var dir = new TempDir();
        var loaded = new SettingsStore(dir.Combine("settings.json")).Load();
        Assert.False(loaded.Malformed);
        Assert.Null(loaded.Value.ReceiveFolder);
        Assert.False(loaded.Value.IsTrusted("x"));
    }

    [Fact] // spec Settings file: port is null or absent for the default 45611
    public void Port_defaults_to_45611()
    {
        Assert.Equal(45611, Protocol.DefaultPort);
        Assert.Equal(45611, new Settings().EffectivePort);
        Assert.Equal(45611, new Settings(Port: null).EffectivePort);
    }

    [Theory] // spec Settings file: a usable port (1024 to 65535) is used, anything else means the default
    [InlineData(50000, 50000)]
    [InlineData(1024, 1024)]
    [InlineData(65535, 65535)]
    [InlineData(1023, 45611)]
    [InlineData(65536, 45611)]
    [InlineData(0, 45611)]
    [InlineData(-5, 45611)]
    public void Port_is_used_only_when_usable(int configured, int expected) =>
        Assert.Equal(expected, new Settings(Port: configured).EffectivePort);

    [Fact] // spec Settings file: port is read from the hand-edited file, and adding a trusted device keeps it
    public void Port_is_read_from_the_file_and_kept_by_AddTrusted()
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        File.WriteAllText(path, """{ "port": 50123 }""");
        var store = new SettingsStore(path);
        Assert.Equal(50123, store.Load().Value.EffectivePort);

        store.AddTrusted(new Identity("n1", "a"));

        Assert.Equal(50123, store.Load().Value.EffectivePort);
    }

    [Fact] // spec Settings file: a port that is not a number makes the file malformed
    public void Non_numeric_port_is_malformed()
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        File.WriteAllText(path, """{ "port": "abc" }""");
        Assert.True(new SettingsStore(path).Load().Malformed);
    }

    [Fact] // spec Settings file: the documented shape is read
    public void Documented_shape_is_read()
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        File.WriteAllText(path, """
            { "receiveFolder": "D:\\Inbox",
              "trusted": [ { "id": "t1", "name": "one" } ],
              "blocked": [ { "id": "b1", "name": "two" } ] }
            """);
        var s = new SettingsStore(path).Load().Value;
        Assert.Equal("D:\\Inbox", s.ReceiveFolder);
        Assert.True(s.IsTrusted("t1"));
        Assert.False(s.IsTrusted("b1"));
        Assert.True(s.IsBlocked("b1"));
        Assert.False(s.IsBlocked("t1"));
    }

    [Fact] // spec Settings file: Accept adds the sender to trusted with its name
    public void AddTrusted_stores_id_and_name_and_keeps_the_rest()
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        File.WriteAllText(path, """
            { "receiveFolder": "D:\\Inbox", "blocked": [ { "id": "b1", "name": "two" } ] }
            """);
        var store = new SettingsStore(path);

        Assert.True(store.AddTrusted(new Identity("n1", "laptop-a")));

        var s = store.Load().Value;
        Assert.Equal([new Identity("n1", "laptop-a")], s.Trusted);
        Assert.Equal("D:\\Inbox", s.ReceiveFolder);
        Assert.Equal([new Identity("b1", "two")], s.Blocked);
    }

    [Fact] // spec Settings file: written atomically (temp file, then replace), no temp left behind
    public void AddTrusted_leaves_only_the_settings_file()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.Combine("settings.json"));
        store.AddTrusted(new Identity("n1", "a"));
        store.AddTrusted(new Identity("n2", "b"));
        Assert.Equal([dir.Combine("settings.json")], Directory.GetFileSystemEntries(dir.Path));
        Assert.Equal(2, store.Load().Value.Trusted!.Count);
    }

    [Fact] // spec Settings file: a device already trusted is not added twice
    public void AddTrusted_is_idempotent()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.Combine("settings.json"));
        store.AddTrusted(new Identity("n1", "a"));
        store.AddTrusted(new Identity("n1", "a"));
        Assert.Single(store.Load().Value.Trusted!);
    }

    [Theory] // spec Settings file: a malformed file is treated as defaults and never overwritten
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("""{"trusted": 5}""")]
    [InlineData("""{"receiveFolder": 5}""")]
    [InlineData("null")]
    public void Malformed_file_is_defaults_and_is_not_overwritten(string content)
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        File.WriteAllText(path, content);
        var store = new SettingsStore(path);

        var loaded = store.Load();
        Assert.True(loaded.Malformed);
        Assert.False(loaded.Value.IsTrusted("n1"));

        Assert.False(store.AddTrusted(new Identity("n1", "a")));
        Assert.Equal(content, File.ReadAllText(path));
    }

    [Fact] // spec Settings file: read fresh, so a hand edit applies at once
    public void Load_sees_a_hand_edit_without_a_new_store()
    {
        using var dir = new TempDir();
        var path = dir.Combine("settings.json");
        var store = new SettingsStore(path);
        Assert.False(store.Load().Value.IsBlocked("b1"));
        File.WriteAllText(path, """{"blocked":[{"id":"b1","name":"x"}]}""");
        Assert.True(store.Load().Value.IsBlocked("b1"));
    }
}

public class TailscaleJsonTests
{
    private const string Status = """
        { "BackendState": "Running",
          "Self": { "ID": "self1", "DNSName": "this-box.example.ts.net.", "TailscaleIPs": ["fd7a:115c::1", "100.64.0.1"], "Online": true },
          "Peer": {
            "nodekey:aaa": { "ID": "p1", "DNSName": "peer-one.example.ts.net.", "TailscaleIPs": ["100.64.0.2", "fd7a:115c::2"], "Online": true },
            "nodekey:bbb": { "ID": "p2", "DNSName": "peer-two.example.ts.net.", "TailscaleIPs": ["100.64.0.3"], "Online": false },
            "nodekey:ccc": { "ID": "p3", "DNSName": "v6-only.example.ts.net.", "TailscaleIPs": ["fd7a:115c::4"], "Online": true }
          } }
        """;

    [Fact] // spec Tailscale integration: Self.TailscaleIPs IPv4 and Peer.* ID, DNSName first label, Online
    public void Status_is_parsed()
    {
        var s = TailscaleJson.ParseStatus(Status);
        Assert.True(s.Connected);
        Assert.Equal(IPAddress.Parse("100.64.0.1"), s.SelfIp);
        Assert.Equal(2, s.Peers.Count);
        Assert.Equal(new PeerInfo(new Identity("p1", "peer-one"), IPAddress.Parse("100.64.0.2"), true), s.Peers[0]);
        Assert.Equal(new PeerInfo(new Identity("p2", "peer-two"), IPAddress.Parse("100.64.0.3"), false), s.Peers[1]);
    }

    [Theory] // spec Tailscale integration: down or logged out is "not connected"
    [InlineData("""{"BackendState":"NeedsLogin","Self":{"TailscaleIPs":["100.64.0.1"]}}""")]
    [InlineData("""{"BackendState":"Stopped"}""")]
    [InlineData("""{"BackendState":"Running","Self":{"TailscaleIPs":["fd7a:115c::1"]}}""")]
    [InlineData("not json")]
    public void Down_or_unusable_status_is_disconnected(string json)
    {
        var s = TailscaleJson.ParseStatus(json);
        Assert.False(s.Connected);
        Assert.Null(s.SelfIp);
        Assert.Empty(s.Peers);
    }

    [Fact] // spec Tailscale integration: whois gives Node.StableID and Node.ComputedName
    public void Whois_is_parsed() =>
        Assert.Equal(new Identity("stable1", "peer-one"),
            TailscaleJson.ParseWhois("""{"Node":{"StableID":"stable1","ComputedName":"peer-one","Name":"other.ts.net."}}"""));

    [Theory] // spec Tailscale integration: no answer is no identity
    [InlineData("""{"Node":{"ComputedName":"x"}}""")]
    [InlineData("""{"UserProfile":{}}""")]
    [InlineData("garbage")]
    public void Unusable_whois_is_null(string json) => Assert.Null(TailscaleJson.ParseWhois(json));

    [Theory] // spec Tailscale integration: the address in 100.64.0.0/10
    [InlineData("100.64.0.0", true)]
    [InlineData("100.127.255.255", true)]
    [InlineData("100.63.255.255", false)]
    [InlineData("100.128.0.0", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("fd7a:115c::1", false)]
    public void Tailnet_range(string ip, bool expected) =>
        Assert.Equal(expected, TailscaleJson.IsTailnetIPv4(IPAddress.Parse(ip)));
}
