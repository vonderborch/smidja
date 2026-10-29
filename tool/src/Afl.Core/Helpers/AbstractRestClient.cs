using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Afl.Core.Exceptions;
using Niddy.Core.Resilience;

namespace Afl.Core.Helpers;

/// <summary>
///     Base for the JSON REST clients afl talks to services with (Jira, GitHub, GitLab): authenticated requests, retries
///     of transient failures, and errors turned into <see cref="AflException" />s that name the service. Subclasses say
///     where requests go, how they're authenticated and how error responses read.
/// </summary>
/// <param name="http">The HTTP client to send requests with.</param>
public abstract class AbstractRestClient(HttpClient http)
{
    /// <summary>
    ///     How requests are retried: network failures and the statuses <see cref="IsTransient" /> picks out (which are
    ///     thrown as <see cref="HttpRequestException" />s), with jitter so a rate-limited burst doesn't retry in step.
    /// </summary>
    private static readonly RetryOptions TransientRequestRetry = new()
    {
        BaseDelay = TimeSpan.FromMilliseconds(250),
        MaxJitter = TimeSpan.FromMilliseconds(250),
        ShouldRetry = ex => ex is HttpRequestException,
    };

    /// <summary>
    ///     Gets the HTTP client.
    /// </summary>
    protected HttpClient Http { get; } = http;

    /// <summary>
    ///     Gets how the service is named in messages, e.g. <c>Jira 'work'</c>.
    /// </summary>
    protected abstract string ServiceName { get; }

    /// <summary>
    ///     Gets how requests are retried.
    /// </summary>
    protected virtual RetryOptions RequestRetryOptions => TransientRequestRetry;

    /// <summary>
    ///     Returns the absolute URL of a request.
    /// </summary>
    /// <param name="path">The path and query, relative to the API's base.</param>
    /// <returns>The URL.</returns>
    protected abstract string RequestUrl(string path);

    /// <summary>
    ///     Adds the service's authentication (and any other headers it wants) to a request.
    /// </summary>
    /// <param name="request">The request.</param>
    protected abstract void Authorize(HttpRequestMessage request);

    /// <summary>
    ///     Appends query parameters to a path.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <param name="query">Query parameters; null values are skipped.</param>
    /// <returns>The path with its query.</returns>
    protected static string WithQuery(string path, params (string Name, string? Value)[] query)
    {
        var parts = query.Where(q => q.Value is not null)
            .Select(q => $"{q.Name}={Uri.EscapeDataString(q.Value!)}")
            .ToList();
        return parts.Count == 0 ? path : $"{path}{(path.Contains('?') ? '&' : '?')}{string.Join('&', parts)}";
    }

    /// <summary>
    ///     Reads a string property, if present.
    /// </summary>
    /// <param name="element">The object.</param>
    /// <param name="name">The property.</param>
    /// <returns>The value, or null.</returns>
    protected static string? GetString(JsonElement element, string name)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    /// <summary>
    ///     Reads a string property of a child object, if present.
    /// </summary>
    /// <param name="element">The object.</param>
    /// <param name="child">The child object property.</param>
    /// <param name="name">The string property.</param>
    /// <returns>The value, or null.</returns>
    protected static string? GetNested(JsonElement element, string child, string name)
    {
        return element.ValueKind == JsonValueKind.Object && element.TryGetProperty(child, out var value)
            ? GetString(value, name)
            : null;
    }

    /// <summary>
    ///     GETs an endpoint and reads the JSON response, reporting a response of the wrong shape as an error from the
    ///     service rather than a crash.
    /// </summary>
    /// <typeparam name="T">What is read from the response.</typeparam>
    /// <param name="path">The path and query, relative to the API's base.</param>
    /// <param name="read">Reads the result from the response's root element.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The result.</returns>
    /// <exception cref="AflException">The request failed or the response couldn't be read.</exception>
    protected async Task<T> GetAsync<T>(string path, Func<JsonElement, T> read, CancellationToken ct)
    {
        using var document = await this.GetJsonAsync(path, ct);
        return this.Read(document, path, read);
    }

    /// <summary>
    ///     POSTs JSON and reads the JSON response. It isn't retried, since a request that timed out may still have created
    ///     something.
    /// </summary>
    /// <typeparam name="T">What is read from the response.</typeparam>
    /// <param name="path">The path relative to the API's base.</param>
    /// <param name="body">The request body.</param>
    /// <param name="read">Reads the result from the response's root element.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The result.</returns>
    /// <exception cref="AflException">The request failed or the response couldn't be read.</exception>
    protected async Task<T> PostAsync<T>(string path, JsonNode body, Func<JsonElement, T> read, CancellationToken ct)
    {
        using var document = await this.PostJsonAsync(path, body, ct);
        return this.Read(document, path, read);
    }

    /// <summary>
    ///     Sends a GET and parses the JSON response, retrying transient failures and turning errors into
    ///     <see cref="AflException" />s.
    /// </summary>
    /// <param name="path">The path relative to the API's base.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The parsed response.</returns>
    protected async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
    {
        (HttpStatusCode Status, string Body) response;
        try
        {
            // Only transient failures throw inside the retry; anything else comes back to be reported once.
            response = await Retry.ExecuteAsync(async () =>
            {
                var attempt = await this.SendOnceAsync(HttpMethod.Get, path, null, ct);
                if (this.IsTransient(attempt.Status))
                {
                    throw new HttpRequestException($"{this.ServiceName} returned {(int)attempt.Status}.", null, attempt.Status);
                }

                return attempt;
            }, this.RequestRetryOptions, ct);
        }
        catch (RetryException ex)
        {
            throw new AflException(AflErrorKind.ExternalTool,
                $"Couldn't reach {this.ServiceName} after {ex.Attempts} attempts: {ex.InnerException?.Message}", ex);
        }

        return this.ParseResponse(response.Status, response.Body);
    }

    /// <summary>
    ///     POSTs JSON and parses the JSON response, without retrying.
    /// </summary>
    /// <param name="path">The path relative to the API's base.</param>
    /// <param name="body">The request body.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The parsed response.</returns>
    /// <exception cref="AflException">The request failed.</exception>
    protected async Task<JsonDocument> PostJsonAsync(string path, JsonNode body, CancellationToken ct)
    {
        (HttpStatusCode Status, string Body) response;
        try
        {
            response = await this.SendOnceAsync(HttpMethod.Post, path, body, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new AflException(AflErrorKind.ExternalTool, $"Couldn't reach {this.ServiceName}: {ex.Message}", ex);
        }

        return this.ParseResponse(response.Status, response.Body);
    }

    /// <summary>
    ///     Turns an error response into an exception with a useful message.
    /// </summary>
    /// <param name="status">The HTTP status.</param>
    /// <param name="body">The response body.</param>
    /// <returns>The exception to throw.</returns>
    protected virtual AflException MapError(HttpStatusCode status, string body)
    {
        var detail = this.ReadErrorDetail(body);
        var suffix = detail is null ? string.Empty : $": {detail}";
        return status switch
        {
            HttpStatusCode.Unauthorized => new AflException(AflErrorKind.InvalidState, $"{this.ServiceName} rejected the token (401){suffix}"),
            HttpStatusCode.Forbidden => new AflException(AflErrorKind.InvalidState, $"{this.ServiceName} refused access (403){suffix}"),
            HttpStatusCode.NotFound => new AflException(AflErrorKind.NotFound, $"{this.ServiceName} found nothing there (404){suffix}"),
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity =>
                new AflException(AflErrorKind.InvalidInput, $"{this.ServiceName} rejected the request{suffix}"),
            _ => new AflException(AflErrorKind.ExternalTool, $"{this.ServiceName} returned {(int)status}{suffix}"),
        };
    }

    /// <summary>
    ///     Reads the service's explanation out of an error response, if it gave one.
    /// </summary>
    /// <param name="body">The response body.</param>
    /// <returns>The explanation, or null.</returns>
    protected virtual string? ReadErrorDetail(string body)
    {
        return null;
    }

    /// <summary>
    ///     Returns whether a status is worth retrying.
    /// </summary>
    /// <param name="status">The status.</param>
    /// <returns>True for rate limiting and gateway errors.</returns>
    protected virtual bool IsTransient(HttpStatusCode status)
    {
        return status is HttpStatusCode.TooManyRequests or HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;
    }

    /// <summary>
    ///     Reads a result from a parsed response.
    /// </summary>
    /// <typeparam name="T">What is read.</typeparam>
    /// <param name="document">The response.</param>
    /// <param name="path">The path it came from, for the error message.</param>
    /// <param name="read">Reads the result.</param>
    /// <returns>The result.</returns>
    /// <exception cref="AflException">The response isn't the expected shape.</exception>
    private T Read<T>(JsonDocument document, string path, Func<JsonElement, T> read)
    {
        try
        {
            return read(document.RootElement);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new AflException(AflErrorKind.ExternalTool,
                $"{this.ServiceName} sent an unexpected response from {path.Split('?')[0]}: {ex.Message}", ex);
        }
    }

    /// <summary>
    ///     Sends one authenticated request.
    /// </summary>
    /// <param name="method">The method.</param>
    /// <param name="path">The path relative to the API's base.</param>
    /// <param name="body">The JSON body, if any.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <returns>The status and response body.</returns>
    private async Task<(HttpStatusCode Status, string Body)> SendOnceAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, this.RequestUrl(path));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        this.Authorize(request);
        if (body is not null)
        {
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        }

        using var message = await this.Http.SendAsync(request, ct);
        return (message.StatusCode, await message.Content.ReadAsStringAsync(ct));
    }

    /// <summary>
    ///     Parses a response, turning an error status or a body that isn't JSON into an <see cref="AflException" />.
    /// </summary>
    /// <param name="status">The status.</param>
    /// <param name="body">The body.</param>
    /// <returns>The parsed response.</returns>
    private JsonDocument ParseResponse(HttpStatusCode status, string body)
    {
        if ((int)status is < 200 or > 299)
        {
            throw this.MapError(status, body);
        }

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new AflException(AflErrorKind.ExternalTool, $"{this.ServiceName} returned something that isn't JSON.", ex);
        }
    }
}
