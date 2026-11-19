using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tailshare.Core;

public enum OfferKind { Text, File, Bundle }

/// <summary>The body of <c>POST /v1/offer</c>.</summary>
/// <param name="Bytes">The upload's exact length; absent from a phone's offers, which may not know it up front.</param>
/// <param name="TotalSize">Required for a file or bundle; absent for text.</param>
/// <param name="SizeIsEstimate">The sender's <c>TotalSize</c> is a guess, which loosens a bundle's extraction cap.</param>
public sealed record Offer(
    OfferKind? Kind,
    string? Name,
    int Items,
    bool IsFolder,
    long? Bytes,
    long? TotalSize,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool SizeIsEstimate = false)
{
    [JsonIgnore] public long TotalOrZero => TotalSize ?? 0;

    /// <summary>What progress is measured against: the upload's length, or the best guess when it has none.</summary>
    [JsonIgnore] public long ExpectedBytes => Bytes ?? TotalSize ?? 0;
}

/// <param name="Hub">True only from the hub; an app leaves it out.</param>
public sealed record HelloResponse(string App, int Protocol, bool? Hub = null);

/// <summary>An item a computer is holding for a phone or iPad, as <c>GET /v1/pickup</c> lists it.</summary>
public sealed record PickupListing(string Token, OfferKind Kind, string? Name, long Bytes);

public sealed record HubComputer(string Name, string Address);

/// <summary>The hub's answer to <c>GET /v1/devices</c> and <c>POST /v1/register</c>.</summary>
public sealed record HubDevices(IReadOnlyList<HubComputer>? Computers, IReadOnlyList<Identity>? Mobiles);

public sealed record TokenResponse(string Token);

public sealed record ErrorResponse(string Error);

/// <summary>Someone on the tailnet: the stable node ID and the Tailscale machine name.</summary>
public sealed record Identity(string Id, string Name);

public static class Protocol
{
    /// <summary>Every device must use the same port; settings.json may override this (see Settings.EffectivePort).</summary>
    public const int DefaultPort = 45611;
    public const int Version = 1;
    public const string AppName = "tailshare";

    public static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(2);
    // The hub used to answer /v1/devices only after probing every peer, and one that didn't answer cost
    // it a whole HelloTimeout, so an app that waited just HelloTimeout lost every phone whenever a single
    // computer was off (found 2026-11-19: the Mac app was quit, the Windows list had no phones). The hub
    // answers from a cache now; this stays well above HelloTimeout as margin.
    public static readonly TimeSpan DevicesTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan OfferTimeout = TimeSpan.FromSeconds(130);
    public static readonly TimeSpan PromptTimeout = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan HoldLifetime = TimeSpan.FromSeconds(120);

    public const long TextLimitBytes = 1024 * 1024;

    /// <summary>The most a bundle may write when extracted. An estimated size gets headroom; an exact one gets none.</summary>
    public static long ExtractionCap(Offer o)
    {
        var total = o.TotalOrZero;
        return o.SizeIsEstimate ? total + total / 4 + 1024 * 1024 : total;
    }

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    /// <summary>Whether the offer is well formed; the receiver answers 400 when it is not.</summary>
    public static bool IsValid(Offer o)
    {
        if (o.Kind is null) return false;
        if (o.Items < 1 || o.Bytes < 0 || o.TotalSize < 0) return false;
        if (o.Kind == OfferKind.Text) return o.Items == 1;
        if (string.IsNullOrEmpty(o.Name) || o.TotalSize is null) return false;
        if (o.Kind == OfferKind.File && o.Items != 1) return false;
        return true;
    }
}
