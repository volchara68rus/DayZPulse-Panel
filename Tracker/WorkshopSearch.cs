using System.Net;
using System.Text.RegularExpressions;

namespace DayZPulse.Tracker;

/// <summary>Результат поиска мода в Steam Workshop.</summary>
public sealed record WorkshopItem(string Id, string Title, string PreviewUrl);

/// <summary>
/// Ищет моды в Steam Workshop по названию. Обращается к странице поиска
/// мастерской DayZ (app 221100) и разбирает карточки предметов.
/// </summary>
public static partial class WorkshopSearch
{
    private const string GameAppId = "221100";

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    /// <summary>Находит до 20 модов по названию.</summary>
    public static async Task<IReadOnlyList<WorkshopItem>> SearchAsync(string query, CancellationToken ct)
    {
        try
        {
            var url = "https://steamcommunity.com/workshop/browse/?" +
                $"appid={GameAppId}&searchtext={Uri.EscapeDataString(query)}&browsesort=textsearch&section=readytouseitems";

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible; DayZPulse/1.0)");

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                return [];
            }

            var html = await response.Content.ReadAsStringAsync(ct);
            return Parse(html);
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static List<WorkshopItem> Parse(string html)
    {
        var items = new List<WorkshopItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (Match match in ItemPattern().Matches(html))
        {
            var id = match.Groups["id"].Value;
            if (!seen.Add(id))
            {
                continue;
            }

            var title = WebUtility.HtmlDecode(match.Groups["title"].Value).Trim();
            var preview = match.Groups["preview"].Success ? match.Groups["preview"].Value : "";

            if (title.Length == 0)
            {
                continue;
            }

            items.Add(new WorkshopItem(id, title, preview));
            if (items.Count >= 20)
            {
                break;
            }
        }

        return items;
    }

    // Заголовок лежит в alt превью-картинки рядом со ссылкой на предмет.
    [GeneratedRegex(
        @"filedetails/\?id=(?<id>\d+)[^>]*>\s*<img[^>]*?(?:src=""(?<preview>[^""]+)"")?[^>]*?alt=""(?<title>[^""]*)""",
        RegexOptions.IgnoreCase)]
    private static partial Regex ItemPattern();
}
