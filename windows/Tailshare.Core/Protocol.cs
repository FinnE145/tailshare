using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tailshare.Core;

public enum OfferKind { Text, File, Bundle }

/// <summary>The body of <c>POST /v1/offer</c>.</summary>
public sealed record Offer(
    OfferKind? Kind,
    string? Name,
    int Items,
    bool IsFolder,
    long Bytes,
    long TotalSize);

public sealed record HelloResponse(string App, int Protocol);

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
    public static readonly TimeSpan OfferTimeout = TimeSpan.FromSeconds(130);
    public static readonly TimeSpan PromptTimeout = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromSeconds(60);

    public const long TextLimitBytes = 1024 * 1024;

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
        if (string.IsNullOrEmpty(o.Name)) return false;
        if (o.Kind == OfferKind.File && o.Items != 1) return false;
        return true;
    }
}
