using System.Collections.Concurrent;
using System.Net.Http;
using GreenLuma_Manager.Models;
using Newtonsoft.Json.Linq;
using SteamKit2;

namespace GreenLuma_Manager.Services;

public class CacheEntry<T>
{
    public DateTime Expiry { get; set; }
    public T Data { get; set; } = default!;
}

public static class SteamApiCache
{
    private static readonly ConcurrentDictionary<string, CacheEntry<object>> Cache = new();
    private static readonly ConcurrentDictionary<string, Task<object>> TaskCache = new();
    private static readonly TimeSpan CacheDurationLocal = TimeSpan.FromMinutes(30);

    public static async Task<T> GetOrAddAsync<T>(string key, Func<Task<T>> fetchFunc)
    {
        if (Cache.TryGetValue(key, out var entry))
            if (DateTime.Now < entry.Expiry && entry.Data is T cachedVal)
                return cachedVal;

        var task = TaskCache.GetOrAdd(key, _ => FetchAndCacheAsync(key, fetchFunc));
        try
        {
            var result = await task;
            return (T)result;
        }
        finally
        {
            TaskCache.TryRemove(key, out _);
        }
    }

    private static async Task<object> FetchAndCacheAsync<T>(string key, Func<Task<T>> fetchFunc)
    {
        var data = await fetchFunc();
        Cache[key] = new CacheEntry<object>
        {
            Expiry = DateTime.Now.Add(CacheDurationLocal),
            Data = data!
        };

        return data!;
    }
}

public class SearchService
{
    private const string SteamStoreSearchUrl =
        "https://store.steampowered.com/api/storesearch/?term={0}&l=english&cc=US";

    private const string SteamStoreApiUrl = "https://api.steampowered.com/IStoreService/GetAppList/v1/";
    private const string SteamApiKey = "1DD0450A99F573693CD031EBB160907D";

    private const int MaxConcurrentRequests = 8;
    private static readonly HttpClient Client = new();
    private static List<SteamApp>? _appListCache;
    private static readonly Dictionary<string, GameDetails> DetailsCache = [];
    private static DateTime _cacheExpiry = DateTime.MinValue;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(24);

    private static readonly SteamManager Manager = new();

    static SearchService()
    {
        Client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
        Client.Timeout = TimeSpan.FromSeconds(30);
    }

    private static async Task<List<Game>> SearchStoreAsync(string query)
    {
        try
        {
            var url = string.Format(SteamStoreSearchUrl, Uri.EscapeDataString(query));
            var response = await Client.GetStringAsync(url);
            var json = JObject.Parse(response);

            var items = json["items"];
            if (items == null) return [];

            var results = new List<Game>();

            foreach (var item in items)
            {
                var appId = item["id"]?.ToString();
                var name = item["name"]?.ToString();
                var tinyImage = item["tiny_image"]?.ToString();

                if (!string.IsNullOrEmpty(appId) && !string.IsNullOrEmpty(name))
                    results.Add(new Game
                    {
                        AppId = appId,
                        Name = name,
                        Type = "Game",
                        IconUrl = tinyImage ?? string.Empty
                    });
            }

            return results;
        }
        catch
        {
            return [];
        }
    }

    private static async Task<List<SteamApp>> GetAppListAsync()
    {
        if (_appListCache != null && DateTime.Now < _cacheExpiry)
            return _appListCache;

        try
        {
            _appListCache = [];
            uint lastAppId = 0;
            const int maxResults = 50000;

            while (true)
            {
                var url =
                    $"{SteamStoreApiUrl}?key={SteamApiKey}&include_games=true&include_dlc=true&include_software=true&include_videos=true&include_hardware=true&max_results={maxResults}&last_appid={lastAppId}";

                var response = await Client.GetStringAsync(url);
                var json = JObject.Parse(response);
                var apps = json["response"]?["apps"];

                if (apps == null || !apps.Any())
                    break;

                foreach (var app in apps)
                {
                    var appId = app["appid"]?.ToString() ?? string.Empty;
                    var name = app["name"]?.ToString() ?? string.Empty;

                    if (!string.IsNullOrWhiteSpace(appId) && !string.IsNullOrWhiteSpace(name))
                        _appListCache.Add(new SteamApp(appId, name));
                }

                var haveMore = json["response"]?["have_more_results"]?.Value<bool>() ?? false;
                if (!haveMore)
                    break;

                var lastAppIdFromResponse = json["response"]?["last_appid"]?.Value<uint>();
                if (lastAppIdFromResponse.HasValue)
                    lastAppId = lastAppIdFromResponse.Value;
                else
                    break;
            }

            _cacheExpiry = DateTime.Now.Add(CacheDuration);
            return _appListCache;
        }
        catch
        {
            // ignored
        }

        return _appListCache ?? [];
    }

    public static async Task<List<Game>> SearchAsync(string query, int maxResults = 50)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return [];

        try
        {
            var queryLower = query.ToLower();
            var cacheKey = $"search:{queryLower}:{maxResults}";

            var cached = await SteamApiCache.GetOrAddAsync(cacheKey, async () =>
            {
                if (uint.TryParse(query, out _))
                {
                    var details = await FetchGameDetailsAsync(query);
                    if (!string.IsNullOrEmpty(details.Name) && details.Name != $"App {query}")
                        return
                        [
                            new Game
                            {
                                AppId = query,
                                Name = details.Name,
                                Type = details.Type,
                                IconUrl = details.IconUrl
                            }
                        ];
                }

                var storeTask = SearchStoreAsync(query);

                var localTask = Task.Run(async () =>
                {
                    var appList = await GetAppListAsync();
                    if (appList.Count == 0) return [];

                    return appList
                        .Select(app => (app, score: CalculateScore(app.Name, queryLower)))
                        .Where(x => x.score > 0)
                        .OrderByDescending(x => x.score)
                        .ThenBy(x => x.app.Name.Length)
                        .Take(maxResults)
                        .Select(x => new Game
                        {
                            AppId = x.app.AppId,
                            Name = x.app.Name,
                            Type = "Game"
                        })
                        .ToList();
                });

                await Task.WhenAll(storeTask, localTask);

                var smartResults = storeTask.Result;
                var localResults = localTask.Result;

                var finalResults = new List<Game>(smartResults);
                var existingIds = new HashSet<string>(smartResults.Select(g => g.AppId));

                foreach (var game in localResults)
                {
                    if (finalResults.Count >= maxResults) break;

                    if (!existingIds.Contains(game.AppId))
                    {
                        finalResults.Add(game);
                        existingIds.Add(game.AppId);
                    }
                }

                return finalResults;
            });

            return
            [
                .. cached.Select(g => new Game { AppId = g.AppId, Name = g.Name, Type = g.Type, IconUrl = g.IconUrl })
            ];
        }
        catch
        {
            return [];
        }
    }

    private static int CalculateScore(string appName, string query)
    {
        if (string.IsNullOrEmpty(appName))
            return 0;

        var nameLower = appName.ToLower();
        var score = 0;

        if (nameLower == query)
            return 10000;

        if (nameLower.StartsWith(query))
            score += 5000;

        var nameWords = nameLower.Split([' ', '-', ':', '_', '™', '®'], StringSplitOptions.RemoveEmptyEntries);
        var queryWords = query.Split([' '], StringSplitOptions.RemoveEmptyEntries);

        if (nameWords.Length > 0 && nameWords[0].StartsWith(query))
            score += 3000;

        var matchingWords = queryWords.Count(queryWord => nameWords.Any(w => w.StartsWith(queryWord)));

        if (queryWords.Length > 1 && matchingWords == queryWords.Length)
            score += 2000;

        if (nameLower.Contains(query))
            score += 1000;

        var lengthPenalty = Math.Max(0, (appName.Length - query.Length) * 50);
        score -= lengthPenalty;

        if (HasWordBoundaryMatch(nameLower, query))
            score += 500;

        if (ContainsAllCharsInOrder(nameLower, query))
            score += 100;

        return Math.Max(0, score);
    }

    private static bool HasWordBoundaryMatch(string name, string query)
    {
        var words = name.Split([' ', '-', ':', '_'], StringSplitOptions.RemoveEmptyEntries);
        return words.Any(w => w.StartsWith(query));
    }

    private static bool ContainsAllCharsInOrder(string text, string chars)
    {
        var charIndex = 0;
        foreach (var c in text)
            if (charIndex < chars.Length && c == chars[charIndex])
                charIndex++;
        return charIndex == chars.Length;
    }

    private static async Task FetchBatchDetailsAsync(List<Game> games)
    {
        var gamesNeedingDetails = games
            .Where(g => !DetailsCache.ContainsKey(g.AppId))
            .ToList();

        if (gamesNeedingDetails.Count == 0)
        {
            ApplyCachedDetails(games);
            return;
        }

        var syncContext = SynchronizationContext.Current;
        var semaphore = new SemaphoreSlim(MaxConcurrentRequests);

        try
        {
            var tasks = gamesNeedingDetails.Select(async game =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var details = await FetchGameDetailsAsync(game.AppId);
                    DetailsCache[game.AppId] = details;

                    if (syncContext != null)
                    {
                        var tcs = new TaskCompletionSource<bool>();
                        syncContext.Post(_ =>
                        {
                            try
                            {
                                if (details.Name != $"App {game.AppId}")
                                    game.Name = details.Name;

                                game.Type = details.Type;
                                if (string.IsNullOrEmpty(game.IconUrl))
                                    game.IconUrl = details.IconUrl;

                                tcs.SetResult(true);
                            }
                            catch (Exception ex)
                            {
                                tcs.SetException(ex);
                            }
                        }, null);
                        await tcs.Task;
                    }
                    else
                    {
                        if (details.Name != $"App {game.AppId}")
                            game.Name = details.Name;

                        game.Type = details.Type;
                        if (string.IsNullOrEmpty(game.IconUrl))
                            game.IconUrl = details.IconUrl;
                    }
                }
                catch
                {
                    DetailsCache[game.AppId] = new GameDetails("Game", string.Empty, $"App {game.AppId}");
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToList();

            await Task.WhenAll(tasks);
            ApplyCachedDetails(games);

            if (gamesNeedingDetails.Count > 0)
                _cacheExpiry = DateTime.Now.Add(CacheDuration);
        }
        finally
        {
            semaphore.Dispose();
        }
    }

    private static void ApplyCachedDetails(List<Game> games)
    {
        foreach (var game in games)
            if (DetailsCache.TryGetValue(game.AppId, out var details))
            {
                if (details.Name != $"App {game.AppId}")
                    game.Name = details.Name;

                game.Type = details.Type;
                if (string.IsNullOrEmpty(game.IconUrl) && !string.IsNullOrEmpty(details.IconUrl))
                    game.IconUrl = details.IconUrl;
            }
    }

    private static async Task<GameDetails> FetchGameDetailsAsync(string appId)
    {
        var key = $"details:{appId}";
        return await SteamApiCache.GetOrAddAsync(key, async () =>
        {
            if (!uint.TryParse(appId, out var id))
                return new GameDetails("Game", string.Empty, $"App {appId}");

            var info = await Manager.GetAppInfoAsync(id);

            if (info != null)
            {
                var iconUrl = !string.IsNullOrEmpty(info.IconUrl)
                    ? info.IconUrl
                    : await TryGetCdnImageAsync(appId);

                return new GameDetails(info.Type, iconUrl ?? string.Empty, info.Name);
            }

            var fallbackIconUrl = await TryGetCdnImageAsync(appId);
            return new GameDetails("Game", fallbackIconUrl ?? string.Empty, $"App {appId}");
        });
    }

    private static async Task<string?> TryGetCdnImageAsync(string appId)
    {
        string[] cdnUrls =
        [
            $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg",
            $"https://cdn.akamai.steamstatic.com/steam/apps/{appId}/header.jpg",
            $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/capsule_231x87.jpg"
        ];

        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(2);

        foreach (var url in cdnUrls)
            try
            {
                var head = new HttpRequestMessage(HttpMethod.Head, url);
                var headResp = await client.SendAsync(head);
                if (headResp.IsSuccessStatusCode)
                    return url;
            }
            catch
            {
                // ignored
            }

        return null;
    }

    private static string MapSteamTypeToDisplayType(string steamType)
    {
        return steamType.ToLower() switch
        {
            "game" => "Game",
            "dlc" => "DLC",
            "demo" => "Demo",
            "mod" => "Mod",
            "video" => "Video",
            "music" => "Soundtrack",
            "bundle" => "Bundle",
            "episode" => "Episode",
            "tool" or "advertising" => "Software",
            _ => "Game"
        };
    }

    public static async Task PopulateGameDetailsAsync(Game game)
    {
        var details = await FetchGameDetailsAsync(game.AppId);
        if (details.Name != $"App {game.AppId}")
            game.Name = details.Name;
        game.Type = details.Type;
        if (string.IsNullOrEmpty(game.IconUrl))
            game.IconUrl = details.IconUrl;
    }

    public static async Task FetchIconUrlAsync(Game game)
    {
        if (!string.IsNullOrEmpty(game.IconUrl))
            return;

        await PopulateGameDetailsAsync(game);
    }

    public static async Task FetchIconUrlsAsync(List<Game> games)
    {
        await FetchBatchDetailsAsync(games);
    }

    private class SteamApp(string appId, string name)
    {
        public string AppId { get; } = appId;
        public string Name { get; } = name;
    }

    private class GameDetails(string type, string iconUrl, string name)
    {
        public string Type { get; } = type;
        public string IconUrl { get; } = iconUrl;
        public string Name { get; } = name;
    }

    private class SteamManager : IDisposable
    {
        private readonly Task _callbackLoop;
        private readonly CallbackManager _callbackManager;

        private readonly TaskCompletionSource _connectedTcs;
        private readonly CancellationTokenSource _cts;
        private readonly TaskCompletionSource _loggedOnTcs;
        private readonly SteamApps _steamApps;
        private readonly SteamClient _steamClient;
        private readonly SteamUser _steamUser;

        private bool _isConnected;
        private bool _isLoggedOn;
        private bool _isRunning;

        public SteamManager()
        {
            _steamClient = new SteamClient();
            _callbackManager = new CallbackManager(_steamClient);
            _steamUser = _steamClient.GetHandler<SteamUser>()!;
            _steamApps = _steamClient.GetHandler<SteamApps>()!;

            _cts = new CancellationTokenSource();
            _connectedTcs = new TaskCompletionSource();
            _loggedOnTcs = new TaskCompletionSource();

            _callbackManager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
            _callbackManager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
            _callbackManager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);

            _isRunning = true;
            _callbackLoop = Task.Run(CallbackLoop);

            _steamClient.Connect();
        }

        public void Dispose()
        {
            _isRunning = false;
            _cts.Cancel();
            _steamClient.Disconnect();
            _callbackLoop.Wait(1000);
            _cts.Dispose();
        }

        public async Task<GameDetails?> GetAppInfoAsync(uint appId)
        {
            try
            {
                await EnsureReadyAsync();

                var request = new SteamApps.PICSRequest { ID = appId, AccessToken = 0 };
                var job = _steamApps.PICSGetProductInfo(new List<SteamApps.PICSRequest> { request }, []);
                var result = await job.ToTask();

                if (result.Failed || result.Results == null)
                    return null;

                foreach (var callback in result.Results)
                    if (callback.Apps.TryGetValue(appId, out var appData))
                    {
                        var kv = appData.KeyValues;
                        var common = kv["common"];
                        var name = common["name"].Value;
                        var type = common["type"].Value ?? "Game";

                        var headerImage = common["header_image"].Value;
                        var iconUrl = !string.IsNullOrEmpty(headerImage)
                            ? headerImage
                            : $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/header.jpg";

                        return new GameDetails(MapSteamTypeToDisplayType(type), iconUrl, name ?? $"App {appId}");
                    }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private async Task EnsureReadyAsync()
        {
            if (!_isConnected)
                await _connectedTcs.Task;

            if (!_isLoggedOn)
                await _loggedOnTcs.Task;
        }

        private async Task CallbackLoop()
        {
            while (_isRunning && !_cts.Token.IsCancellationRequested)
            {
                _callbackManager.RunWaitCallbacks(TimeSpan.FromMilliseconds(100));
                await Task.Delay(100);
            }
        }

        private void OnConnected(SteamClient.ConnectedCallback callback)
        {
            _isConnected = true;
            _connectedTcs.TrySetResult();
            _steamUser.LogOnAnonymous();
        }

        private void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            _isConnected = false;
            _isLoggedOn = false;

            Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ =>
            {
                if (_isRunning) _steamClient.Connect();
            });
        }

        private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result == EResult.OK)
            {
                _isLoggedOn = true;
                _loggedOnTcs.TrySetResult();
            }
        }
    }
}