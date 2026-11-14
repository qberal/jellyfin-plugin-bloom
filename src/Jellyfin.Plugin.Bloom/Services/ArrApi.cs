using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Bloom.Services;

#pragma warning disable SA1402 // Small DTOs grouped on purpose.
#pragma warning disable CA1812 // DTOs are created by the JSON serializer.

/// <summary>Radarr or Sonarr refused a request, or could not be reached: the message is for the admin.</summary>
public sealed class ArrException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ArrException"/> class.
    /// </summary>
    /// <param name="message">Message, in the admin's language.</param>
    /// <param name="statusCode">HTTP status, when it answered.</param>
    public ArrException(string message, HttpStatusCode? statusCode = null)
        : base(message)
    {
        StatusCode = statusCode;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrException"/> class.
    /// </summary>
    public ArrException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrException"/> class.
    /// </summary>
    /// <param name="message">Message.</param>
    /// <param name="innerException">Cause.</param>
    public ArrException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Gets the HTTP status, when Radarr or Sonarr answered.</summary>
    public HttpStatusCode? StatusCode { get; }
}

/// <summary>Answer of <c>/api/v3/system/status</c>.</summary>
public sealed class ArrSystemStatus
{
    /// <summary>Gets or sets the application name (Radarr, Sonarr).</summary>
    public string? AppName { get; set; }

    /// <summary>Gets or sets the version.</summary>
    public string? Version { get; set; }
}

/// <summary>A tag.</summary>
public sealed class ArrTag
{
    /// <summary>Gets or sets the id.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the label.</summary>
    public string? Label { get; set; }
}

/// <summary>A root folder.</summary>
public sealed class ArrRootFolder
{
    /// <summary>Gets or sets the path.</summary>
    public string? Path { get; set; }
}

/// <summary>A quality.</summary>
public sealed class ArrQuality
{
    /// <summary>Gets or sets the id.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the vertical resolution (0: unknown).</summary>
    public int Resolution { get; set; }
}

/// <summary>A quality of a profile, or a group of them.</summary>
public sealed class ArrQualityItem
{
    /// <summary>Gets or sets the group id (groups only).</summary>
    public int? Id { get; set; }

    /// <summary>Gets or sets the quality (single qualities only).</summary>
    public ArrQuality? Quality { get; set; }

    /// <summary>Gets or sets the qualities of a group.</summary>
    public List<ArrQualityItem>? Items { get; set; }

    /// <summary>Gets or sets a value indicating whether the profile accepts it.</summary>
    public bool Allowed { get; set; }

    /// <summary>
    /// Gets the resolutions of this quality, or of every quality of the group.
    /// </summary>
    /// <returns>Resolutions.</returns>
    public IEnumerable<int> Resolutions()
        => Quality is not null && Items is not { Count: > 0 } ? [Quality.Resolution] : (Items ?? []).SelectMany(i => i.Resolutions());
}

/// <summary>A quality profile.</summary>
public sealed class ArrQualityProfile
{
    /// <summary>Gets or sets the id.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the quality (or group) id at which Radarr or Sonarr stops upgrading.</summary>
    public int Cutoff { get; set; }

    /// <summary>Gets or sets a value indicating whether upgrades are allowed.</summary>
    public bool UpgradeAllowed { get; set; }

    /// <summary>Gets or sets the qualities.</summary>
    public List<ArrQualityItem> Items { get; set; } = [];

    /// <summary>
    /// Gets the lowest resolution this profile is happy with: the cutoff when it upgrades, otherwise the lowest quality
    /// it accepts.
    /// </summary>
    /// <returns>A resolution, or 0 when unknown.</returns>
    public int Threshold()
    {
        if (!UpgradeAllowed)
        {
            var allowed = Items.Where(i => i.Allowed).SelectMany(i => i.Resolutions()).Where(r => r > 0).ToList();
            return allowed.Count == 0 ? 0 : allowed.Min();
        }

        var cutoff = Items.FirstOrDefault(i => i.Quality is not null && i.Items is not { Count: > 0 } && i.Quality.Id == Cutoff)
            ?? Items.FirstOrDefault(i => i.Quality is null && i.Id == Cutoff);
        return cutoff?.Resolutions().DefaultIfEmpty(0).Max() ?? 0;
    }
}

/// <summary>A movie in Radarr.</summary>
public sealed class RadarrMovie
{
    /// <summary>Gets or sets the id.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the TMDB id.</summary>
    public int TmdbId { get; set; }

    /// <summary>Gets or sets the IMDb id.</summary>
    public string? ImdbId { get; set; }

    /// <summary>Gets or sets a value indicating whether it is monitored.</summary>
    public bool Monitored { get; set; }

    /// <summary>Gets or sets a value indicating whether Radarr has a file for it.</summary>
    public bool HasFile { get; set; }

    /// <summary>Gets or sets the quality profile id.</summary>
    public int QualityProfileId { get; set; }

    /// <summary>Gets or sets the root folder.</summary>
    public string? RootFolderPath { get; set; }
}

/// <summary>Statistics of a series.</summary>
public sealed class SonarrStatistics
{
    /// <summary>Gets or sets the number of episode files.</summary>
    public int EpisodeFileCount { get; set; }
}

/// <summary>A series in Sonarr.</summary>
public sealed class SonarrSeries
{
    /// <summary>Gets or sets the id.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the TVDB id.</summary>
    public int TvdbId { get; set; }

    /// <summary>Gets or sets the TMDB id.</summary>
    public int? TmdbId { get; set; }

    /// <summary>Gets or sets the IMDb id.</summary>
    public string? ImdbId { get; set; }

    /// <summary>Gets or sets a value indicating whether it is monitored.</summary>
    public bool Monitored { get; set; }

    /// <summary>Gets or sets the quality profile id.</summary>
    public int QualityProfileId { get; set; }

    /// <summary>Gets or sets the root folder.</summary>
    public string? RootFolderPath { get; set; }

    /// <summary>Gets or sets the statistics.</summary>
    public SonarrStatistics? Statistics { get; set; }

    /// <summary>Gets or sets which seasons coming out later Sonarr monitors ("all" or "none").</summary>
    public string? MonitorNewItems { get; set; }
}

/// <summary>An episode in Sonarr.</summary>
public sealed class SonarrEpisode
{
    /// <summary>Gets or sets the id.</summary>
    public int Id { get; set; }

    /// <summary>Gets or sets the season number.</summary>
    public int SeasonNumber { get; set; }

    /// <summary>Gets or sets the episode number.</summary>
    public int EpisodeNumber { get; set; }

    /// <summary>Gets or sets a value indicating whether it is monitored.</summary>
    public bool Monitored { get; set; }

    /// <summary>Gets or sets a value indicating whether Sonarr has a file for it.</summary>
    public bool HasFile { get; set; }
}

/// <summary>An entry of an exclusion list (Radarr: TMDB id, Sonarr: TVDB id).</summary>
public sealed class ArrExclusion
{
    /// <summary>Gets or sets the TMDB id (Radarr).</summary>
    public int TmdbId { get; set; }

    /// <summary>Gets or sets the TVDB id (Sonarr).</summary>
    public int TvdbId { get; set; }
}

/// <summary>
/// Talks to Radarr or Sonarr (API v3, which both use since their version 4). Errors become
/// <see cref="ArrException"/>s worded for the admin.
/// </summary>
public sealed class ArrClient
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _apiKey;

    /// <summary>
    /// Initializes a new instance of the <see cref="ArrClient"/> class.
    /// </summary>
    /// <param name="http">HTTP client (no redirects).</param>
    /// <param name="app">"Radarr" or "Sonarr".</param>
    /// <param name="baseUrl">Normalized base URL.</param>
    /// <param name="apiKey">API key.</param>
    public ArrClient(HttpClient http, string app, string baseUrl, string apiKey)
    {
        _http = http;
        App = app;
        _baseUrl = baseUrl;
        _apiKey = apiKey;
    }

    /// <summary>Gets the application name.</summary>
    public string App { get; }

    /// <summary>
    /// Normalizes an address typed by the admin: http(s) only, without trailing slash.
    /// </summary>
    /// <param name="url">Address.</param>
    /// <returns>The address, or null if it is not one.</returns>
    public static string? NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    /// <summary>
    /// Gets a resource.
    /// </summary>
    /// <typeparam name="T">Its type.</typeparam>
    /// <param name="path">Path from the base URL ("/api/v3/movie").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The resource.</returns>
    public async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
        => await SendAsync<T>(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false)
            ?? throw new ArrException(Text.Get("ArrUnreadable", App));

    /// <summary>
    /// Sends a request and reads the answer.
    /// </summary>
    /// <typeparam name="T">Type of the answer.</typeparam>
    /// <param name="method">Method.</param>
    /// <param name="path">Path from the base URL.</param>
    /// <param name="body">JSON body.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The answer, or null when empty.</returns>
    public async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var response = await SendRawAsync(method, path, body, cancellationToken).ConfigureAwait(false);
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(text) ? default : JsonSerializer.Deserialize<T>(text, Options);
        }
        catch (JsonException ex)
        {
            throw new ArrException(Text.Get("ArrUnreadable", App), ex);
        }
    }

    /// <summary>
    /// Sends a request, ignoring the answer.
    /// </summary>
    /// <param name="method">Method.</param>
    /// <param name="path">Path from the base URL.</param>
    /// <param name="body">JSON body.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var response = await SendRawAsync(method, path, body, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, _baseUrl + path);
        request.Headers.TryAddWithoutValidation("X-Api-Key", _apiKey);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: Options);
        }

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ArrException(Text.Get("ArrTimeout", App), ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ArrException(Text.Get("ArrUnreachable", App, ex.Message), ex);
        }

        if (response.IsSuccessStatusCode)
        {
            return response;
        }

        using (response)
        {
            var status = (int)response.StatusCode;
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new ArrException(Text.Get("ArrBadKey", App), response.StatusCode);
            }

            if (status is >= 300 and < 400)
            {
                throw new ArrException(Text.Get("ArrRedirect", App, response.Headers.Location?.ToString() ?? "?"), response.StatusCode);
            }

            var detail = await ReadErrorAsync(response, cancellationToken).ConfigureAwait(false);
            throw new ArrException(
                detail is null ? Text.Get("ArrHttp", App, status.ToString(CultureInfo.InvariantCulture)) : Text.Get("ArrHttpWithMessage", App, status.ToString(CultureInfo.InvariantCulture), detail),
                response.StatusCode);
        }
    }

    /// <summary>
    /// Reads what Radarr and Sonarr say went wrong: a list of validation errors, or a message.
    /// </summary>
    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            string? message = null;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0 && root[0].ValueKind == JsonValueKind.Object
                && root[0].TryGetProperty("errorMessage", out var error))
            {
                message = error.GetString();
            }
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("message", out var msg))
            {
                message = msg.GetString();
            }

            return string.IsNullOrWhiteSpace(message) ? null : message.Length > 300 ? message[..300] : message;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
