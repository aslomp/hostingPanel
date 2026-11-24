using System.ComponentModel.DataAnnotations;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HostingPanel.Api;

/// <summary>Only public, non-secret configuration belongs in this contract.
/// Keep credentials in a separate secret provider. Public readable/writable properties
/// are exposed to authenticated settings administrators unless marked JsonIgnore.</summary>
public interface IBackendSettings
{
    [JsonIgnore]
    IBackendSettingsValidator Validator { get; }
}

public interface IBackendSettingsValidator
{
    Dictionary<string, string[]> Validate(IBackendSettings settings);
}

public interface IBackendSettingsAccess
{
    Task<IResult> ReadAsync(AuthenticationHeaderValue auth, CancellationToken ct);
    Task<IResult> WriteAsync(AuthenticationHeaderValue auth, BackendSettingsUpdate update, CancellationToken ct);
}

public sealed record BackendSettingsUpdate(string Revision, JsonElement Values);
public sealed record BackendSettingOption(string Value, string Label);
public sealed record BackendSettingField(string Name, string Label, string? Description, string Type,
    bool Nullable, IReadOnlyList<BackendSettingOption> Options);
public sealed record BackendSettingsView(string Backend, string Revision,
    IReadOnlyList<BackendSettingField> Fields, JsonElement Values);

public static class BackendSettingsSchema
{
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    private static PropertyInfo[] Properties(Type type) => type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
        .Where(p => p.GetMethod?.IsPublic == true && p.SetMethod?.IsPublic == true
            && p.GetIndexParameters().Length == 0 && p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
        .OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();

    private static string Name(PropertyInfo p) => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name ?? p.Name;
    private static Type Underlying(PropertyInfo p) => Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;

    public static string Serialize(IBackendSettings value) => JsonSerializer.Serialize(
        Properties(value.GetType()).ToDictionary(Name, p => p.GetValue(value)), Options);

    public static string Revision(IBackendSettings value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(value))));

    public static BackendSettingsView Describe(string backend, IBackendSettings value)
    {
        var fields = Properties(value.GetType()).Select(p =>
        {
            var t = Underlying(p);
            var display = p.GetCustomAttribute<DisplayAttribute>();
            var kind = t.IsEnum ? "enum" : t == typeof(bool) ? "boolean" :
                t == typeof(string) ? "string" :
                t == typeof(byte) || t == typeof(short) || t == typeof(int) || t == typeof(long) ||
                t == typeof(sbyte) || t == typeof(ushort) || t == typeof(uint) || t == typeof(ulong) ||
                t == typeof(float) || t == typeof(double) || t == typeof(decimal) ? "number" : "json";
            BackendSettingOption[] choices = t.IsEnum
                ? Enum.GetNames(t).Select(n => new BackendSettingOption(n,
                    t.GetField(n)?.GetCustomAttribute<DisplayAttribute>()?.GetName() ?? n)).ToArray()
                : [];
            return new BackendSettingField(Name(p), display?.GetName() ?? p.Name,
                display?.GetDescription(), kind,
                Nullable.GetUnderlyingType(p.PropertyType) is not null ||
                (!p.PropertyType.IsValueType && new NullabilityInfoContext().Create(p).WriteState != NullabilityState.NotNull),
                choices);
        }).ToArray();
        return new(backend, Revision(value), fields,
            JsonSerializer.Deserialize<JsonElement>(Serialize(value)));
    }

    public static bool TryRead<T>(JsonElement input, out T? value, out Dictionary<string, string[]> errors)
        where T : class, IBackendSettings, new()
    {
        value = null;
        errors = new();
        if (input.ValueKind != JsonValueKind.Object)
        {
            errors["Values"] = ["Expected an object."];
            return false;
        }
        var properties = Properties(typeof(T)).ToDictionary(Name, StringComparer.Ordinal);
        var candidate = new T();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in input.EnumerateObject())
        {
            if (!seen.Add(field.Name) || !properties.TryGetValue(field.Name, out var p))
            { errors[field.Name] = ["Unknown or duplicate setting."]; continue; }
            try
            {
                var t = Underlying(p);
                if (field.Value.ValueKind == JsonValueKind.Null &&
                    (p.PropertyType.IsValueType ? Nullable.GetUnderlyingType(p.PropertyType) is null :
                        new NullabilityInfoContext().Create(p).WriteState == NullabilityState.NotNull))
                    throw new JsonException();
                if (t.IsEnum && field.Value.ValueKind != JsonValueKind.Null &&
                    (field.Value.ValueKind != JsonValueKind.String ||
                     !Enum.GetNames(t).Contains(field.Value.GetString(), StringComparer.Ordinal)))
                    throw new JsonException();
                p.SetValue(candidate, field.Value.Deserialize(p.PropertyType, Options));
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or TargetInvocationException or NotSupportedException)
            { errors[field.Name] = ["Invalid setting value."]; }
        }
        foreach (var name in properties.Keys.Where(n => !seen.Contains(n)))
            errors[name] = ["Required setting is missing."];
        if (errors.Count != 0) return false;
        foreach (var pair in candidate.Validator.Validate(candidate)) errors[pair.Key] = pair.Value;
        if (errors.Count != 0) return false;
        value = candidate;
        return true;
    }
}
