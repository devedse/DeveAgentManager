using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeveAgentManager.Contracts;

/// <summary>How the Manager's API writes JSON, on both ends: web defaults (camelCase) with enums as strings.</summary>
public static class ApiJson
{
    public static readonly JsonSerializerOptions Options = Configure(new JsonSerializerOptions(JsonSerializerDefaults.Web));

    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
