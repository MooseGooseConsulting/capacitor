using Capacitor.Web.Models;

namespace Capacitor.Web.Services;

/// <summary>HTTP boundary between the Sessions web surface and the Capacitor API.</summary>
public interface ICapacitorSessionsClient {
    Task<ApiResult<SessionSearchResponse>> SearchAsync(SessionSearchRequest request, CancellationToken cancellationToken = default);

    Task<ApiResult<SessionDetailResponse>> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default);
}

/// <summary>A request result that keeps expected backend-unavailable and empty states distinguishable.</summary>
public sealed record ApiResult<T>(T? Value, ApiFailure? Failure) {
    public bool IsSuccess => Value is not null && Failure is null;
}

public static class ApiResults {
    public static ApiResult<T> Success<T>(T value) => new(value, null);

    public static ApiResult<T> Failed<T>(ApiFailure failure) => new(default, failure);
}

public sealed record ApiFailure(int? StatusCode, string Message) {
    public bool IsNotFound => StatusCode == StatusCodes.Status404NotFound;
}
