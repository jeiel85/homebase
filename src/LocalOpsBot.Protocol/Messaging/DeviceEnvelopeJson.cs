using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalOpsBot.Protocol.Messaging;

public static class DeviceEnvelopeJson
{
    public static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false,
            MaxDepth = 32
        };

        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
