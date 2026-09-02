using System.Net;
using System.Text.Json;
using Capacitor.Web.Models;

namespace Capacitor.Web.Services;

/// <summary>Typed reader for the persisted-session API. It never manufactures dashboard data.</summary>
public sealed class CapacitorSessionsClient(HttpClient httpClient) : ICapacitorSessionsClient {
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ApiResult<SessionSearchResponse>> SearchAsync(SessionSearchRequest request, CancellationToken cancellationToken = default) {
        ArgumentOutOfRangeException.ThrowIfNegative(request.Offset);
        ArgumentOutOfRangeException.ThrowIfLessThan(request.Limit, 1);

        var query = new List<string> {
            $"limit={request.Limit}",
            $"offset={request.Offset}"
        };
        AddQuery(query, "query", request.Query);
        AddQuery(query, "repo", request.Repository);
        AddQuery(query, "vendor", request.Vendor);
        AddQuery(query, "status", request.Status);

        return await GetAsync<SessionSearchResponse>($"api/sessions/search?{string.Join('&', query)}", cancellationToken);
    }

    public Task<ApiResult<SessionDetailResponse>> GetSessionAsync(string sessionId, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return GetAsync<SessionDetailResponse>($"api/sessions/{Uri.EscapeDataString(sessionId)}", cancellationToken);
    }

    static void AddQuery(ICollection<string> query, string name, string? value) {
        if (!string.IsNullOrWhiteSpace(value)) query.Add($"{name}={Uri.EscapeDataString(value)}");
    }

    async Task<ApiResult<T>> GetAsync<T>(string requestUri, CancellationToken cancellationToken) {
        try {
            using var response = await httpClient.GetAsync(requestUri, cancellationToken);
            if (!response.IsSuccessStatusCode) return ApiResults.Failed<T>(ToFailure(response));

            var result = await response.Content.ReadFromJsonAsync<T>(JsonOptions, cancellationToken);
            return result is null
                ? ApiResults.Failed<T>(new ApiFailure((int)response.StatusCode, "The API returned an empty response."))
                : ApiResults.Success(result);
        } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
            return ApiResults.Failed<T>(new ApiFailure(null, "The API did not respond before the dashboard request timed out."));
        } catch (HttpRequestException) {
            return ApiResults.Failed<T>(new ApiFailure(null, "The Capacitor API is unavailable."));
        } catch (JsonException) {
            return ApiResults.Failed<T>(new ApiFailure(null, "The API response did not match the Sessions contract."));
        }
    }

    static ApiFailure ToFailure(HttpResponseMessage response) => response.StatusCode switch {
        HttpStatusCode.NotFound => new ApiFailure((int)response.StatusCode, "This Sessions API route is not available yet."),
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new ApiFailure((int)response.StatusCode, "The API did not authorize this dashboard request."),
        _ => new ApiFailure((int)response.StatusCode, $"The API request failed ({(int)response.StatusCode} {response.ReasonPhrase}).")
    };
}
