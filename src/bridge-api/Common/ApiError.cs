namespace CortexBridge.Api.Common;

public record ApiError(ApiErrorBody Error)
{
    public static ApiError Of(string code, string message) => new(new ApiErrorBody(code, message));
}

public record ApiErrorBody(string Code, string Message);
