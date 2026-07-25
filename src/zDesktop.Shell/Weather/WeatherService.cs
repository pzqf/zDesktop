using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace zDesktop.Shell.Weather;

/// <summary>
/// 和风天气服务 — 通过和风天气 API 获取实时天气和预报
///
/// API 文档：https://dev.qweather.com/
/// 需要：用户自行注册免费 API Key
/// 流程：
/// 1. GeoAPI — 根据城市名查询 LocationId（或用 IP 定位）
/// 2. Weather Now API — 实时天气
/// 3. Weather 3d API — 3 天预报
/// </summary>
public sealed class WeatherService : IDisposable
{
    private readonly HttpClient _http;
    private const string BaseUrl = "https://devapi.qweather.com/v7";
    private const string GeoUrl = "https://geoapi.qweather.com/v2";

    /// <summary>和风天气 API Key（由组件配置注入）</summary>
    public string ApiKey { get; set; } = string.Empty;

    public WeatherService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>
    /// 根据城市名查询 LocationId
    /// </summary>
    public async Task<GeoLocation?> LookupCityAsync(string cityName)
    {
        if (string.IsNullOrEmpty(ApiKey) || string.IsNullOrEmpty(cityName)) return null;

        try
        {
            var url = $"{GeoUrl}/city/lookup?location={Uri.EscapeDataString(cityName)}&key={ApiKey}&number=1";
            var json = await _http.GetStringAsync(url);
            var resp = JsonSerializer.Deserialize<GeoResponse>(json);
            return resp?.Locations?.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Weather] 城市查询失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 获取实时天气
    /// </summary>
    public async Task<WeatherNow?> GetWeatherNowAsync(string locationId)
    {
        if (string.IsNullOrEmpty(ApiKey) || string.IsNullOrEmpty(locationId)) return null;

        try
        {
            var url = $"{BaseUrl}/weather/now?location={locationId}&key={ApiKey}";
            var json = await _http.GetStringAsync(url);
            var resp = JsonSerializer.Deserialize<WeatherNowResponse>(json);
            return resp?.Now;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Weather] 实时天气获取失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 获取 3 天预报
    /// </summary>
    public async Task<List<DailyForecast>?> GetForecastAsync(string locationId)
    {
        if (string.IsNullOrEmpty(ApiKey) || string.IsNullOrEmpty(locationId)) return null;

        try
        {
            var url = $"{BaseUrl}/weather/3d?location={locationId}&key={ApiKey}";
            var json = await _http.GetStringAsync(url);
            var resp = JsonSerializer.Deserialize<ForecastResponse>(json);
            return resp?.Daily;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Weather] 预报获取失败: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}

// ===== 响应模型 =====

public sealed class GeoResponse
{
    [JsonPropertyName("location")]
    public List<GeoLocation>? Locations { get; set; }
}

public sealed class GeoLocation
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("adm2")]
    public string Adm2 { get; set; } = string.Empty;

    [JsonPropertyName("adm1")]
    public string Adm1 { get; set; } = string.Empty;
}

public sealed class WeatherNowResponse
{
    [JsonPropertyName("now")]
    public WeatherNow? Now { get; set; }
}

public sealed class WeatherNow
{
    [JsonPropertyName("temp")]
    public string Temp { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("icon")]
    public string Icon { get; set; } = string.Empty;

    [JsonPropertyName("humidity")]
    public string Humidity { get; set; } = string.Empty;

    [JsonPropertyName("windDir")]
    public string WindDir { get; set; } = string.Empty;

    [JsonPropertyName("windScale")]
    public string WindScale { get; set; } = string.Empty;

    [JsonPropertyName("feelsLike")]
    public string FeelsLike { get; set; } = string.Empty;
}

public sealed class ForecastResponse
{
    [JsonPropertyName("daily")]
    public List<DailyForecast>? Daily { get; set; }
}

public sealed class DailyForecast
{
    [JsonPropertyName("fxDate")]
    public string FxDate { get; set; } = string.Empty;

    [JsonPropertyName("tempMax")]
    public string TempMax { get; set; } = string.Empty;

    [JsonPropertyName("tempMin")]
    public string TempMin { get; set; } = string.Empty;

    [JsonPropertyName("textDay")]
    public string TextDay { get; set; } = string.Empty;

    [JsonPropertyName("iconDay")]
    public string IconDay { get; set; } = string.Empty;

    [JsonPropertyName("textNight")]
    public string TextNight { get; set; } = string.Empty;
}
