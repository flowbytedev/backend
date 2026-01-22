using Application.Shared.Enums;
using Application.Shared.Models;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Application.Shared.Services;

public class ClickHouseService : IClickHouseService
{
    private readonly IHttpClientFactory _httpClientFactory;

    public ClickHouseService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<List<Dictionary<string, object?>>> ExecuteQueryAsync(MetricDataSource dataSource, string query)
    {
        if (dataSource.Type != DataSourceType.ClickHouse)
        {
            throw new ArgumentException("Data source type must be ClickHouse");
        }

        var client = _httpClientFactory.CreateClient();
        var baseUrl = BuildConnectionUrl(dataSource);

        var request = new HttpRequestMessage(HttpMethod.Post, baseUrl);

        // Add authentication
        var authString = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{dataSource.Username}:{dataSource.Password}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authString);

        // Send query with FORMAT JSONEachRow for easier parsing
        var queryWithFormat = query.TrimEnd(';') + " FORMAT JSONEachRow";
        request.Content = new StringContent(queryWithFormat, Encoding.UTF8, "text/plain");

        var response = await client.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new Exception($"ClickHouse query failed: {errorContent}");
        }

        var content = await response.Content.ReadAsStringAsync();
        return ParseJsonEachRow(content);
    }

    public async Task<bool> TestConnectionAsync(MetricDataSource dataSource)
    {
        if (dataSource.Type != DataSourceType.ClickHouse)
        {
            throw new ArgumentException("Data source type must be ClickHouse");
        }

        try
        {
            var client = _httpClientFactory.CreateClient();
            var baseUrl = BuildConnectionUrl(dataSource);

            var request = new HttpRequestMessage(HttpMethod.Post, baseUrl);

            // Add authentication
            var authString = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{dataSource.Username}:{dataSource.Password}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authString);

            // Simple test query
            request.Content = new StringContent("SELECT 1", Encoding.UTF8, "text/plain");

            var response = await client.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private string BuildConnectionUrl(MetricDataSource dataSource)
    {
        var protocol = dataSource.UseSSL ? "https" : "http";
        return $"{protocol}://{dataSource.Host}:{dataSource.Port}/?database={Uri.EscapeDataString(dataSource.Database)}";
    }

    private List<Dictionary<string, object?>> ParseJsonEachRow(string content)
    {
        var result = new List<Dictionary<string, object?>>();

        if (string.IsNullOrWhiteSpace(content))
        {
            return result;
        }

        // JSONEachRow format: each line is a separate JSON object
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            try
            {
                var row = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line);
                if (row != null)
                {
                    var convertedRow = new Dictionary<string, object?>();
                    foreach (var kvp in row)
                    {
                        convertedRow[kvp.Key] = ConvertJsonElement(kvp.Value);
                    }
                    result.Add(convertedRow);
                }
            }
            catch
            {
                // Skip malformed lines
            }
        }

        return result;
    }

    private object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToList(),
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(p => p.Name, p => ConvertJsonElement(p.Value)),
            _ => element.ToString()
        };
    }
}
