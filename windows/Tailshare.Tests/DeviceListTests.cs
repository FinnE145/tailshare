using System.Net;
using Tailshare.Core;

namespace Tailshare.Tests;

/// <summary>How a computer builds its device list: computers by hello, phones and iPads from the hub.</summary>
public class DeviceListTests
{
    private const string AppHello = """{"app":"tailshare","protocol":1}""";
    private const string HubHello = """{"app":"tailshare","protocol":1,"hub":true}""";

    private static string Mobiles(params (string Id, string Name)[] mobiles) =>
        """{"computers":[],"mobiles":[""" + string.Join(",", mobiles.Select(m => $$"""{"name":"{{m.Name}}","id":"{{m.Id}}"}""")) + "]}";

    [Fact] // spec hello: a device answering hub:true is never in the send list
    public async Task A_hub_is_not_a_computer()
    {
        using var net = new Tailnet();
        var hub = net.Add("h1", "home-hub", 1, HubHello);
        var computer = net.Add("c1", "desk", 2, AppHello);
        using var sender = net.Sender();

        var found = await sender.DiscoverAsync(Tailnet.Status(hub, computer), CancellationToken.None);

        Assert.Equal([computer], found);
    }

    [Fact] // spec Apps/Refresh: the hub is found from the same hello pass and its mobiles are added to the computers
    public async Task Mobiles_come_from_the_hub_found_by_hello()
    {
        using var net = new Tailnet();
        var computer = net.Add("c1", "desk", 1, AppHello);
        var hub = net.Add("h1", "home-hub", 2, HubHello, Mobiles(("m1", "pixel"), ("m2", "ipad")));
        using var sender = net.Sender();

        var list = await sender.DiscoverDevicesAsync(Tailnet.Status(computer, hub), null, CancellationToken.None);

        Assert.Equal([computer], list.Computers);
        Assert.Equal([new Identity("m1", "pixel"), new Identity("m2", "ipad")], list.Mobiles);
    }

    [Fact] // spec Apps/Refresh: several hubs and no setting, the first by name (status order is the opposite here)
    public async Task With_several_hubs_the_first_by_name_is_used()
    {
        using var net = new Tailnet();
        var b = net.Add("hb", "b-hub", 1, HubHello, Mobiles(("mb", "from-b")));
        var a = net.Add("ha", "a-hub", 2, HubHello, Mobiles(("ma", "from-a")));
        using var sender = net.Sender();

        var list = await sender.DiscoverDevicesAsync(Tailnet.Status(b, a), null, CancellationToken.None);

        Assert.Equal([new Identity("ma", "from-a")], list.Mobiles);
        Assert.Empty(list.Computers);
    }

    [Theory] // spec Apps/Refresh: with hub set, that device is used even when another hub answers (name, any case)
    [InlineData("b-hub")]
    [InlineData("B-HUB")]
    public async Task A_hub_set_by_name_is_used_even_when_another_hub_answers(string setting)
    {
        using var net = new Tailnet();
        var a = net.Add("ha", "a-hub", 1, HubHello, Mobiles(("ma", "from-a")));
        var b = net.Add("hb", "b-hub", 2, HubHello, Mobiles(("mb", "from-b")));
        using var sender = net.Sender();

        var list = await sender.DiscoverDevicesAsync(Tailnet.Status(a, b), setting, CancellationToken.None);

        Assert.Equal([new Identity("mb", "from-b")], list.Mobiles);
    }

    [Fact] // spec Apps/Refresh: hub may be a Tailscale IPv4
    public async Task A_hub_set_by_address_is_used()
    {
        using var net = new Tailnet();
        var a = net.Add("ha", "a-hub", 1, HubHello, Mobiles(("ma", "from-a")));
        var b = net.Add("hb", "b-hub", 2, HubHello, Mobiles(("mb", "from-b")));
        using var sender = net.Sender();

        var list = await sender.DiscoverDevicesAsync(Tailnet.Status(a, b), "127.0.0.2", CancellationToken.None);

        Assert.Equal([new Identity("mb", "from-b")], list.Mobiles);
    }

    [Fact] // spec Apps/Refresh: an address needs no lookup, so a hub missing from Tailscale's peer list still works
    public async Task A_hub_set_by_an_address_tailscale_does_not_list_is_used()
    {
        using var net = new Tailnet();
        var computer = net.Add("c1", "desk", 1, AppHello);
        net.Add("hb", "unlisted", 2, HubHello, Mobiles(("mb", "from-b")));
        using var sender = net.Sender();

        var list = await sender.DiscoverDevicesAsync(Tailnet.Status(computer), "127.0.0.2", CancellationToken.None);

        Assert.Equal([new Identity("mb", "from-b")], list.Mobiles);
        Assert.Equal([computer], list.Computers);
    }

    [Fact] // spec Apps/Refresh: a hub setting that names nobody means no phones, even though a hub answers; no error
    public async Task A_hub_setting_naming_nobody_gives_no_mobiles()
    {
        using var net = new Tailnet();
        var computer = net.Add("c1", "desk", 1, AppHello);
        var hub = net.Add("h1", "home-hub", 2, HubHello, Mobiles(("m1", "pixel")));
        using var sender = net.Sender();

        var list = await sender.DiscoverDevicesAsync(Tailnet.Status(computer, hub), "nobody", CancellationToken.None);

        Assert.Empty(list.Mobiles);
        Assert.Equal([computer], list.Computers);
    }

    [Fact] // spec Apps/Refresh: an offline hub is not used
    public async Task An_offline_hub_gives_no_mobiles()
    {
        using var net = new Tailnet();
        var computer = net.Add("c1", "desk", 1, AppHello);
        var hub = net.Add("h1", "home-hub", 2, HubHello, Mobiles(("m1", "pixel")), online: false);
        using var sender = net.Sender();

        var byName = await sender.DiscoverDevicesAsync(Tailnet.Status(computer, hub), "home-hub", CancellationToken.None);
        var auto = await sender.DiscoverDevicesAsync(Tailnet.Status(computer, hub), null, CancellationToken.None);

        Assert.Empty(byName.Mobiles);
        Assert.Empty(auto.Mobiles);
    }

    [Fact] // spec Apps/Refresh: a hub that doesn't answer within 2 s (short here) costs only the phones
    public async Task A_hub_that_does_not_answer_costs_only_the_mobiles()
    {
        using var net = new Tailnet();
        var computer = net.Add("c1", "desk", 1, AppHello);
        var hub = net.Add("h1", "home-hub", 2, HubHello, devices: null);
        using var sender = net.Sender(TimeSpan.FromMilliseconds(400));

        var list = await sender.DiscoverDevicesAsync(Tailnet.Status(computer, hub), null, CancellationToken.None);

        Assert.Equal([computer], list.Computers);
        Assert.Empty(list.Mobiles);
    }

    [Theory] // spec Apps/Refresh: an answer that isn't the hub's JSON means no phones, not an exception
    [InlineData("not json")]
    [InlineData("""{"mobiles":null}""")]
    [InlineData("""{"computers":[]}""")]
    [InlineData("""{"mobiles":[{"id":"","name":"x"},{"id":"m1","name":""}]}""")]
    public async Task Garbage_from_the_hub_means_no_mobiles(string answer)
    {
        using var net = new Tailnet();
        var hub = net.Add("h1", "home-hub", 1, HubHello, answer);
        using var sender = net.Sender();

        var list = await sender.DiscoverDevicesAsync(Tailnet.Status(hub), null, CancellationToken.None);

        Assert.Empty(list.Mobiles);
    }

    [Fact] // spec Apps/Refresh: excluding any device already listed
    public async Task A_mobile_already_listed_as_a_computer_is_dropped()
    {
        using var net = new Tailnet();
        var computer = net.Add("c1", "desk", 1, AppHello);
        var hub = net.Add("h1", "home-hub", 2, HubHello, Mobiles(("c1", "desk"), ("m1", "pixel"), ("m2", "ipad")));
        using var sender = net.Sender();

        var list = await sender.DiscoverDevicesAsync(Tailnet.Status(computer, hub), null, CancellationToken.None);

        Assert.Equal([new Identity("m1", "pixel"), new Identity("m2", "ipad")], list.Mobiles);
    }

    [Fact] // spec Apps/Refresh: no hub on the tailnet is not an error
    public async Task No_hub_means_no_mobiles()
    {
        using var net = new Tailnet();
        var computer = net.Add("c1", "desk", 1, AppHello);
        using var sender = net.Sender();

        var list = await sender.DiscoverDevicesAsync(Tailnet.Status(computer), null, CancellationToken.None);

        Assert.Equal([computer], list.Computers);
        Assert.Empty(list.Mobiles);
    }
}
