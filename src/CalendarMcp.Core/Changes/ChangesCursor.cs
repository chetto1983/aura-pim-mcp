using System.Buffers.Text;
using System.Text.Json;

namespace CalendarMcp.Core.Changes;

/// <summary>
/// A position in one account's mail or calendars, opaque to Aura: versioned, and a map per folder
/// or calendar from the start, so more folders later are a setting rather than a new format.
/// </summary>
/// <remarks>
/// Nothing signs a cursor, so its content is untrusted input on the way back in: decoding checks
/// the shape, and a source that follows a URL from one validates the host first. A cursor can
/// hold delta links: it is never logged.
/// </remarks>
public sealed class ChangesCursor
{
    private const int CurrentVersion = 1;

    /// <summary>
    /// A position read back with a field gone would carry a null its type rules out, and to a source
    /// a sync token of null is a full resync, not an error. So nothing is optional here, a nullable
    /// field included, and nulls are written out for that reason:
    /// https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/required-properties
    /// </summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };

    private readonly IReadOnlyDictionary<string, JsonElement> _folders;

    private ChangesCursor(string kind, IReadOnlyDictionary<string, JsonElement> folders)
    {
        Kind = kind;
        _folders = folders;
    }

    /// <summary>The source that wrote this cursor. A cursor is only ever read by that source.</summary>
    public string Kind { get; }

    public static string KindOf(string provider, string resource) => $"{provider}.{resource}";

    public static ChangesCursor Of<T>(string kind, IEnumerable<KeyValuePair<string, T>> folders) =>
        new(kind, folders.ToDictionary(f => f.Key, f => JsonSerializer.SerializeToElement(f.Value, Json)));

    public static ChangesCursor Of<T>(string kind, string folder, T state) =>
        Of(kind, [new KeyValuePair<string, T>(folder, state)]);

    /// <summary>One folder's state. A cursor that lacks it was not written by this PIM.</summary>
    public T Folder<T>(string folder) =>
        _folders.TryGetValue(folder, out var state) ? Read<T>(state) : throw ChangesException.InvalidCursor();

    public IReadOnlyDictionary<string, T> Folders<T>() => _folders.ToDictionary(f => f.Key, f => Read<T>(f.Value));

    public string Encode()
    {
        var wire = new Wire(CurrentVersion, Kind, _folders);
        return Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(wire, Json));
    }

    /// <summary>
    /// Reads a cursor back. Text that is not a cursor is the caller's error. A cursor from another
    /// version is not: it was valid once and this PIM can no longer resume it, so it expires.
    /// </summary>
    public static ChangesCursor Decode(string encoded, string expectedKind)
    {
        Wire wire;
        try
        {
            wire = JsonSerializer.Deserialize<Wire>(Base64Url.DecodeFromChars(encoded), Json)
                   ?? throw ChangesException.InvalidCursor();
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            throw ChangesException.InvalidCursor(e);
        }

        if (wire.V != CurrentVersion)
            throw new CursorExpiredException();
        if (wire.K != expectedKind)
            throw ChangesException.InvalidCursor();

        return new ChangesCursor(wire.K, wire.F);
    }

    private static T Read<T>(JsonElement state)
    {
        try
        {
            return state.Deserialize<T>(Json) ?? throw ChangesException.InvalidCursor();
        }
        catch (JsonException e)
        {
            throw ChangesException.InvalidCursor(e);
        }
    }

    private sealed record Wire(int V, string K, IReadOnlyDictionary<string, JsonElement> F);
}
