using System.Collections;
using System.Text.Json;
using YamlDotNet.Serialization;

namespace AIWorkStation.Services;

public static class YamlValueConverter
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder().Build();
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .DisableAliases()
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    public static object ReadObject(string yaml)
    {
        var raw = Deserializer.Deserialize<object?>(yaml) ?? new Dictionary<string, object?>();
        return Normalize(raw)!;
    }

    public static string ToJson(string yaml) => JsonSerializer.Serialize(ReadObject(yaml));

    public static string JsonToYaml(string json)
    {
        using var document = JsonDocument.Parse(json);
        return Serializer.Serialize(FromJson(document.RootElement));
    }

    private static object? Normalize(object? value)
    {
        if (value is IDictionary dictionary)
        {
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (DictionaryEntry entry in dictionary)
                result[Convert.ToString(entry.Key, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty] = Normalize(entry.Value);
            return result;
        }

        if (value is IEnumerable enumerable and not string)
            return enumerable.Cast<object?>().Select(Normalize).ToList();

        return value;
    }

    private static object? FromJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => FromJson(p.Value), StringComparer.Ordinal),
        JsonValueKind.Array => element.EnumerateArray().Select(FromJson).ToList(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };
}
