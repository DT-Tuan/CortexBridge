using System.Text.Json;
using System.Text.Json.Serialization;

namespace CortexBridge.Api.Common;

public static class Json
{
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

public static class ResultsHelpers
{
    public static IResult Error(int status, string code, string message) =>
        Microsoft.AspNetCore.Http.Results.Json(ApiError.Of(code, message), Json.Default, statusCode: status);
}
