#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Net.Http;
using System.Net.Http.Json;
using Cavea.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Cavea.Api
{
    [ApiController]
    [Route("api/cavea/metadata")]
    [Produces("application/json")]
    public class SearchModalMetadataController : ControllerBase
    {
        private readonly ILogger<SearchModalMetadataController> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly CaveaDbService _caveaDb;

        public SearchModalMetadataController(
            ILogger<SearchModalMetadataController> logger,
            ILibraryManager libraryManager,
            CaveaDbService caveaDb)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _caveaDb = caveaDb;
        }

        [HttpGet("search")]
        public async Task<ActionResult> GetSearchMetadata(
            [FromQuery] string? tmdbId,
            [FromQuery] string? imdbId,
            [FromQuery] string? jellyfinId, // Jellyfin item ID to lookup provider IDs
            [FromQuery] string? itemType, // "movie" or "series"
            [FromQuery] string? title,
            [FromQuery] int? year,
            [FromQuery] bool includeCredits = false,
            [FromQuery] bool includeReviews = false)
        {
            // Robust ID handling: strip prefixes
            if (tmdbId != null && tmdbId.StartsWith("tmdb:")) tmdbId = tmdbId.Substring(5);
            if (imdbId != null && imdbId.StartsWith("imdb:")) imdbId = imdbId.Substring(5);

            _logger.LogInformation("⚪ [Cavea.SearchMetadata] GetSearchMetadata (On-Demand): tmdbId={TmdbId}, imdbId={ImdbId}, jellyfinId={JellyfinId}, title={Title}, year={Year}, itemType={ItemType}", 
                tmdbId ?? "null", imdbId ?? "null", jellyfinId ?? "null", title ?? "null", year?.ToString() ?? "null", itemType ?? "null");
            
            try
            {
                // 1. If we have a Jellyfin ID, try to get provider IDs from it first (does NOT trigger import)
                if (string.IsNullOrEmpty(tmdbId) && string.IsNullOrEmpty(imdbId) && !string.IsNullOrEmpty(jellyfinId))
                {
                    if (Guid.TryParse(jellyfinId, out var itemGuid))
                    {
                        var item = _libraryManager.GetItemById(itemGuid);
                        if (item != null)
                        {
                            item.ProviderIds.TryGetValue("Imdb", out var foundImdb);
                            item.ProviderIds.TryGetValue("Tmdb", out var foundTmdb);
                            if (!string.IsNullOrEmpty(foundImdb)) imdbId = foundImdb;
                            if (!string.IsNullOrEmpty(foundTmdb)) tmdbId = foundTmdb;
                            
                            // Also infer itemType from Jellyfin item
                            if (string.IsNullOrEmpty(itemType))
                            {
                                itemType = item.GetType().Name switch
                                {
                                    "Movie" => "movie",
                                    "Series" => "series",
                                    "Episode" => "series",
                                    _ => itemType
                                };
                            }
                            _logger.LogInformation("⚪ [Cavea.SearchMetadata] Resolved Jellyfin ID {JellyfinId} to Imdb={ImdbId}, Tmdb={TmdbId}, Type={Type}", 
                                jellyfinId, imdbId ?? "null", tmdbId ?? "null", itemType ?? "null");
                        }
                        else
                        {
                            // Try hyphenless GUID if not found
                            if (jellyfinId.Length == 32 && !jellyfinId.Contains("-"))
                            {
                                var withHyphens = $"{jellyfinId.Substring(0, 8)}-{jellyfinId.Substring(8, 4)}-{jellyfinId.Substring(12, 4)}-{jellyfinId.Substring(16, 4)}-{jellyfinId.Substring(20)}";
                                if (Guid.TryParse(withHyphens, out var hyphenatedGuid))
                                {
                                    item = _libraryManager.GetItemById(hyphenatedGuid);
                                    if (item != null)
                                    {
                                        item.ProviderIds.TryGetValue("Imdb", out var foundImdb);
                                        item.ProviderIds.TryGetValue("Tmdb", out var foundTmdb);
                                        if (!string.IsNullOrEmpty(foundImdb)) imdbId = foundImdb;
                                        if (!string.IsNullOrEmpty(foundTmdb)) tmdbId = foundTmdb;
                                        _logger.LogInformation("⚪ [Cavea.SearchMetadata] Resolved hyphenated Jellyfin ID {Guid} to Imdb={ImdbId}, Tmdb={TmdbId}", withHyphens, imdbId ?? "null", tmdbId ?? "null");
                                    }
                                }
                            }
                        }
                    }
                }

                // 2. Determine Stremio ID
                string? stremioId = null;
                if (!string.IsNullOrEmpty(imdbId))
                {
                    stremioId = imdbId;
                }
                else if (!string.IsNullOrEmpty(tmdbId))
                {
                    stremioId = $"tmdb:{tmdbId}";
                }

                // 2. Determine Stremio MediaType enum FIRST (needed for search)
                // Gelato.Common.StremioMediaType: Unknown=0, Movie=1, Series=2
                object? mediaTypeEnum = null;
                if (string.Equals(itemType, "movie", StringComparison.OrdinalIgnoreCase))
                {
                    mediaTypeEnum = 1; // StremioMediaType.Movie
                }
                else if (string.Equals(itemType, "series", StringComparison.OrdinalIgnoreCase) || 
                         string.Equals(itemType, "tv", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(itemType, "episode", StringComparison.OrdinalIgnoreCase))
                {
                    mediaTypeEnum = 2; // StremioMediaType.Series
                }
                else
                {
                    // Default to movie if not specified
                    mediaTypeEnum = 1;
                }

                // If no ID, try Gelato search by title (doesn't need TMDB API key!)
                if (string.IsNullOrEmpty(stremioId) && !string.IsNullOrEmpty(title))
                {
                    var cleanTitle = title.Trim(' ', '"', '\'');
                    _logger.LogInformation("⚪ [Cavea.SearchMetadata] No ID, searching Gelato for '{Title}' (type={Type})", cleanTitle, mediaTypeEnum);
                    
                    // Try the specified type first
                    var searchResult = await SearchGelatoByTitle(cleanTitle, mediaTypeEnum, year);
                    
                    // If series search fails, try movie (some providers don't have series catalog)
                    if (searchResult == null && (int)mediaTypeEnum == 2)
                    {
                        _logger.LogInformation("⚪ [Cavea.SearchMetadata] Series search failed, trying movie catalog");
                        searchResult = await SearchGelatoByTitle(cleanTitle, 1, year); // 1 = Movie
                    }
                    
                    if (searchResult != null)
                    {
                        return Ok(searchResult);
                    }
                    
                    // Fallback to TMDB search if Gelato search fails
                    _logger.LogInformation("⚪ [Cavea.SearchMetadata] Gelato search failed, trying TMDB API");
                    var resolvedId = await ResolveIdFromTmdb(cleanTitle, year, itemType);
                    if (!string.IsNullOrEmpty(resolvedId))
                    {
                        stremioId = $"tmdb:{resolvedId}";
                        _logger.LogInformation("⚪ [Cavea.SearchMetadata] Resolved title '{Title}' to {Id}", title, stremioId);
                    }
                }

                if (string.IsNullOrEmpty(stremioId))
                {
                    return BadRequest(new { error = "Could not find metadata. No IDs available and search failed." });
                }

                // 3. Reflect into Gelato to get metadata
                var gelatoData = await FetchMetadataFromGelato(stremioId, mediaTypeEnum);

                if (gelatoData == null && stremioId != null && stremioId.StartsWith("tmdb:"))
                {
                    _logger.LogInformation("⚪ [Cavea.SearchMetadata] Gelato returned null, trying direct TMDB metadata fetch for {Id}", stremioId);
                    var tmdbIdOnly = stremioId.Substring(5);
                    var tmdbData = await FetchMetadataFromTMDB(tmdbIdOnly, itemType ?? "movie");
                    if (tmdbData != null)
                    {
                        var json = JsonSerializer.Serialize(tmdbData);
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        var mapped = MapStremioMeta(root, includeCredits);
                        
                        if (includeReviews)
                        {
                            var mediaTypeForReviews = string.Equals(itemType, "series", StringComparison.OrdinalIgnoreCase) || 
                                                    string.Equals(itemType, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";
                            var reviews = await FetchTMDBReviews(tmdbIdOnly, mediaTypeForReviews);
                            if (reviews != null) mapped["reviews"] = reviews;
                        }
                        return Ok(mapped);
                    }
                }

                if (gelatoData != null)
                {
                     try 
                     {
                         var json = JsonSerializer.Serialize(gelatoData);
                         using var doc = JsonDocument.Parse(json);
                         var root = doc.RootElement;

                         // Handle Stremio wrapper: common for search results/meta to be nested under "meta"
                         if (root.TryGetProperty("meta", out var metaNested)) root = metaNested;
                         else if (root.TryGetProperty("Meta", out var metaNestedUpper)) root = metaNestedUpper;

                         var mapped = MapStremioMeta(root, includeCredits);
                         
                         if (includeReviews)
                         {
                             string? workingTmdbId = tmdbId;
                             if (string.IsNullOrEmpty(workingTmdbId) && !string.IsNullOrEmpty(stremioId) && stremioId.StartsWith("tmdb:"))
                             {
                                 workingTmdbId = stremioId.Substring(5);
                             }

                             if (!string.IsNullOrEmpty(workingTmdbId))
                             {
                                 var mediaTypeForReviews = string.Equals(itemType, "series", StringComparison.OrdinalIgnoreCase) || 
                                                         string.Equals(itemType, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";
                                 var reviews = await FetchTMDBReviews(workingTmdbId, mediaTypeForReviews);
                                 if (reviews != null)
                                 {
                                     mapped["reviews"] = reviews;
                                 }
                             }
                         }
                         
                         return Ok(mapped);
                     }
                     catch(Exception ex)
                     {
                         _logger.LogError(ex, "⚪ [Cavea.SearchMetadata] Error mapping Gelato response");
                         return Ok(gelatoData); // Fallback
                     }
                }

                return NotFound(new { error = "No metadata found in Gelato or TMDB" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "⚪ [Cavea.SearchMetadata] Error getting metadata from Gelato");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        [HttpGet("external-ids")]
        public async Task<ActionResult> GetExternalIds(
            [FromQuery] string? tmdbId,
            [FromQuery] string? imdbId,
            [FromQuery] string? mediaType)
        {
            string? workingMediaType = mediaType;
            if (string.Equals(workingMediaType, "series", StringComparison.OrdinalIgnoreCase)) workingMediaType = "tv";
            if (string.IsNullOrEmpty(workingMediaType)) workingMediaType = "movie";

            try
            {
                var cfg = Plugin.Instance?.Configuration;
                var apiKey = cfg?.TmdbApiKey;
                if (string.IsNullOrEmpty(apiKey)) return BadRequest(new { error = "TMDB API Key not configured" });

                using var client = new HttpClient();
                if (!string.IsNullOrEmpty(tmdbId))
                {
                    if (tmdbId.StartsWith("tmdb:")) tmdbId = tmdbId.Substring(5);
                    var url = $"https://api.themoviedb.org/3/{workingMediaType}/{tmdbId}/external_ids?api_key={apiKey}";
                    var response = await client.GetAsync(url);
                    if (response.IsSuccessStatusCode)
                    {
                        var data = await response.Content.ReadFromJsonAsync<JsonElement>();
                        return Ok(data);
                    }
                }
                else if (!string.IsNullOrEmpty(imdbId))
                {
                    if (imdbId.StartsWith("imdb:")) imdbId = imdbId.Substring(5);
                    var url = $"https://api.themoviedb.org/3/find/{imdbId}?api_key={apiKey}&external_source=imdb_id";
                    var response = await client.GetAsync(url);
                    if (response.IsSuccessStatusCode)
                    {
                        var data = await response.Content.ReadFromJsonAsync<JsonElement>();
                        if (data.TryGetProperty("movie_results", out var movies) && movies.GetArrayLength() > 0) 
                        {
                            var movie = movies[0];
                            // Add media_type to the object if it's not there
                            var json = JsonSerializer.Serialize(movie);
                            var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                            if (dict != null) {
                                dict["media_type"] = "movie";
                                return Ok(dict);
                            }
                        }
                        if (data.TryGetProperty("tv_results", out var tvs) && tvs.GetArrayLength() > 0)
                        {
                            var tv = tvs[0];
                            var json = JsonSerializer.Serialize(tv);
                            var dict = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                            if (dict != null) {
                                dict["media_type"] = "tv";
                                return Ok(dict);
                            }
                        }
                    }
                }
                return NotFound(new { error = "Could not resolve external IDs" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "⚪ [Cavea.SearchMetadata] Error in GetExternalIds");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }

        private async Task<object?> FetchMetadataFromGelato(string id, object mediaTypeEnum)
        {
            try
            {
                var gelatoPlugin = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Gelato");

                if (gelatoPlugin == null)
                {
                    _logger.LogWarning("⚪ [Cavea.SearchMetadata] Gelato plugin assembly not found");
                    return null;
                }

                // Get GelatoPlugin.Instance
                var gelatoPluginType = gelatoPlugin.GetType("Gelato.GelatoPlugin");
                var instanceProp = gelatoPluginType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                var gelatoInstance = instanceProp?.GetValue(null);

                if (gelatoInstance == null)
                {
                    _logger.LogWarning("⚪ [Cavea.SearchMetadata] Gelato plugin instance is null");
                    return null;
                }

                // Get Config
                var getConfigMethod = gelatoInstance.GetType().GetMethod("GetConfig");
                var config = getConfigMethod?.Invoke(gelatoInstance, new object[] { Guid.Empty }); // Guid.Empty for system config

                if (config == null) return null;

                // Get Stremio Provider ("stremio" field)
                var stremioProviderField = config.GetType().GetField("stremio");
                var stremioProvider = stremioProviderField?.GetValue(config);

                if (stremioProvider == null) return null;

                // Get GetMetaAsync method - use more robust lookup to avoid AmbiguousMatchException
                var getMetaMethod = stremioProvider.GetType().GetMethods()
                    .FirstOrDefault(m => m.Name == "GetMetaAsync" && m.GetParameters().Length == 2);
                
                if (getMetaMethod == null) return null;

                // Call GetMetaAsync(id, type)
                var paramsInfo = getMetaMethod.GetParameters();
                var mediaTypeType = paramsInfo[1].ParameterType;
                var convertedMediaType = Enum.ToObject(mediaTypeType, mediaTypeEnum);

                var task = (Task)getMetaMethod.Invoke(stremioProvider, new object[] { id, convertedMediaType })!;
                await task.ConfigureAwait(false);

                // Get Result property from Task<T>
                var resultProperty = task.GetType().GetProperty("Result");
                return resultProperty?.GetValue(task);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "⚪ [Cavea.SearchMetadata] Error reflecting into Gelato");
                return null;
            }
        }

        private async Task<object?> FetchTMDBReviews(string tmdbId, string mediaType)
        {
            try
            {
                var cfg = Plugin.Instance?.Configuration;
                var apiKey = cfg?.TmdbApiKey;
                if (string.IsNullOrEmpty(apiKey)) return null;

                using var client = new HttpClient();
                var url = $"https://api.themoviedb.org/3/{mediaType}/{tmdbId}/reviews?api_key={apiKey}";
                var response = await client.GetAsync(url);
                if (!response.IsSuccessStatusCode) return null;

                return await response.Content.ReadFromJsonAsync<object>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "⚪ [Cavea.SearchMetadata] Error fetching TMDB reviews for {Id}", tmdbId);
                return null;
            }
        }

        private async Task<Dictionary<string, object?>?> SearchGelatoByTitle(string title, object mediaTypeEnum, int? year)
        {
            try
            {
                var gelatoPlugin = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Gelato");

                if (gelatoPlugin == null)
                {
                    _logger.LogWarning("⚪ [Cavea.SearchMetadata] Gelato plugin assembly not found for search");
                    return null;
                }

                // Get GelatoPlugin.Instance
                var gelatoPluginType = gelatoPlugin.GetType("Gelato.GelatoPlugin");
                var instanceProp = gelatoPluginType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                var gelatoInstance = instanceProp?.GetValue(null);

                if (gelatoInstance == null)
                {
                    _logger.LogWarning("⚪ [Cavea.SearchMetadata] Gelato plugin instance is null");
                    return null;
                }

                // Get Config
                var getConfigMethod = gelatoInstance.GetType().GetMethod("GetConfig");
                var config = getConfigMethod?.Invoke(gelatoInstance, new object[] { Guid.Empty });

                if (config == null) return null;

                // Get Stremio Provider ("stremio" field)
                var stremioProviderField = config.GetType().GetField("stremio");
                var stremioProvider = stremioProviderField?.GetValue(config);

                if (stremioProvider == null) return null;

                // Get SearchAsync method
                var searchMethod = stremioProvider.GetType().GetMethods()
                    .FirstOrDefault(m => m.Name == "SearchAsync" && m.GetParameters().Length >= 2);
                
                if (searchMethod == null)
                {
                    _logger.LogWarning("⚪ [Cavea.SearchMetadata] SearchAsync method not found in Gelato");
                    return null;
                }

                // Call SearchAsync(query, mediaType)
                var paramsInfo = searchMethod.GetParameters();
                var mediaTypeType = paramsInfo[1].ParameterType;
                var convertedMediaType = Enum.ToObject(mediaTypeType, mediaTypeEnum);

                object?[] methodParams;
                if (paramsInfo.Length == 3)
                    methodParams = new object?[] { title, convertedMediaType, null };
                else
                    methodParams = new object?[] { title, convertedMediaType };

                var task = (Task)searchMethod.Invoke(stremioProvider, methodParams)!;
                await task.ConfigureAwait(false);

                // Get Result property from Task<IReadOnlyList<StremioMeta>>
                var resultProperty = task.GetType().GetProperty("Result");
                var results = resultProperty?.GetValue(task);

                if (results == null) return null;

                // Get the list and find best match
                var resultsEnumerable = results as System.Collections.IEnumerable;
                if (resultsEnumerable == null) return null;

                object? bestMatch = null;
                foreach (var item in resultsEnumerable)
                {
                    if (item == null) continue;
                    
                    // Try to match by year if provided
                    if (year.HasValue)
                    {
                        var yearProp = item.GetType().GetProperty("Year");
                        var itemYear = yearProp?.GetValue(item);
                        if (itemYear != null && int.TryParse(itemYear.ToString(), out var parsedYear))
                        {
                            if (parsedYear == year.Value)
                            {
                                bestMatch = item;
                                break;
                            }
                        }
                    }
                    
                    // Take first result if no year match
                    if (bestMatch == null)
                        bestMatch = item;
                }

                if (bestMatch == null)
                {
                    _logger.LogInformation("⚪ [Cavea.SearchMetadata] Gelato search returned no results for '{Title}'", title);
                    return null;
                }

                // Get the ID from search result and fetch FULL metadata (includes cast)
                var searchJson = JsonSerializer.Serialize(bestMatch);
                using var searchDoc = JsonDocument.Parse(searchJson);
                var searchRoot = searchDoc.RootElement;
                
                var foundId = searchRoot.TryGetProperty("Id", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrEmpty(foundId))
                {
                    foundId = searchRoot.TryGetProperty("imdb_id", out var imdbEl) ? imdbEl.GetString() : null;
                }

                if (!string.IsNullOrEmpty(foundId))
                {
                    _logger.LogInformation("⚪ [Cavea.SearchMetadata] Found ID {Id}, fetching full metadata", foundId);
                    
                    // Fetch full metadata using the ID (this includes App_Extras.Cast)
                    var fullMeta = await FetchMetadataFromGelato(foundId, mediaTypeEnum);
                    if (fullMeta != null)
                    {
                        var json = JsonSerializer.Serialize(fullMeta);
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        
                        // Handle Stremio wrapper
                        if (root.TryGetProperty("meta", out var metaNested)) root = metaNested;
                        else if (root.TryGetProperty("Meta", out var metaNestedUpper)) root = metaNestedUpper;

                        return MapStremioMeta(root, true); // true for credits
                    }
                }
                
                // Fallback: return null if full metadata fetch failed
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "⚪ [Cavea.SearchMetadata] Error searching Gelato by title");
                return null;
            }
        }

        private Dictionary<string, object?> MapStremioMeta(JsonElement root, bool includeCredits)
        {
            var mapped = new Dictionary<string, object?>();

            JsonElement? FindProp(string[] names)
            {
                foreach (var name in names)
                {
                    if (root.TryGetProperty(name, out var prop)) return prop;
                    if (root.TryGetProperty(name.ToLowerInvariant(), out var propLower)) return propLower;
                }
                return null;
            }

            var nameProp = FindProp(new[] { "Name", "Title" });
            mapped["name"] = nameProp?.GetString();

            var descProp = FindProp(new[] { "Description", "Overview", "Plot" });
            mapped["description"] = descProp?.GetString();

            var yearPropEl = FindProp(new[] { "Year", "ProductionYear", "ReleaseInfo", "first_air_date", "release_date" });
            if (yearPropEl.HasValue)
            {
                var yearStr = yearPropEl.Value.ToString();
                if (!string.IsNullOrEmpty(yearStr) && yearStr.Length >= 4)
                {
                    // Extract year from YYYY-MM-DD
                    mapped["year"] = yearStr.Substring(0, 4);
                }
                else
                {
                    mapped["year"] = yearStr;
                }
            }

            var backProp = FindProp(new[] { "Background", "Backdrop", "BackdropImage", "Banner" });
            mapped["background"] = backProp?.GetString();

            var posterProp = FindProp(new[] { "Poster", "PosterImage" });
            mapped["Poster"] = posterProp?.GetString();
            
            var tmdbPosterProp = FindProp(new[] { "poster_path" });
            mapped["poster_path"] = tmdbPosterProp?.GetString();

            var logoProp = FindProp(new[] { "Logo", "LogoImage" });
            mapped["logo"] = logoProp?.GetString();

            var ratingProp = FindProp(new[] { "ImdbRating", "Rating", "CommunityRating", "vote_average" });
            var ratingVal = ratingProp?.ToString();
            mapped["imdbRating"] = ratingVal;
            mapped["rating"] = ratingVal;
            mapped["vote_average"] = ratingVal;

            var genresProp = FindProp(new[] { "Genres", "Genre" });
            if (genresProp.HasValue && genresProp.Value.ValueKind == JsonValueKind.Array)
            {
                mapped["genres"] = genresProp.Value.EnumerateArray()
                    .Select(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("name", out var n) ? n.GetString() : x.GetString())
                    .Where(x => !string.IsNullOrEmpty(x))
                    .ToList();
            }

            var runtimeProp = FindProp(new[] { "Runtime", "Duration" });
            mapped["runtime"] = runtimeProp?.ToString();

            var rtProp = FindProp(new[] { "RottenTomatoes", "CriticRating", "TomatoMeter" });
            mapped["rottenTomatoes"] = rtProp?.ToString();

            // ID Mapping Alignment with Baklava
            var imdbIdProp = FindProp(new[] { "imdb_id", "external_ids.imdb_id" });
            var tmdbIdProp = FindProp(new[] { "id", "tmdb_id" });
            
            string? finalImdbId = imdbIdProp?.GetString();
            
            // Manual fallback for nested external_ids.imdb_id
            if (string.IsNullOrEmpty(finalImdbId) && root.TryGetProperty("external_ids", out var extIds))
            {
                if (extIds.TryGetProperty("imdb_id", out var eid) && eid.ValueKind == JsonValueKind.String)
                    finalImdbId = eid.GetString();
            }

            string? finalTmdbId = null;
            if (tmdbIdProp.HasValue)
            {
                if (tmdbIdProp.Value.ValueKind == JsonValueKind.Number) finalTmdbId = tmdbIdProp.Value.GetInt32().ToString();
                else {
                    var rawTmdb = tmdbIdProp.Value.GetString();
                    if (rawTmdb != null && rawTmdb.StartsWith("tmdb:")) finalTmdbId = rawTmdb.Substring(5);
                    else finalTmdbId = rawTmdb;
                }
            }

            // Strip prefixes from IDs before sending to frontend
            if (finalImdbId != null && finalImdbId.StartsWith("imdb:")) finalImdbId = finalImdbId.Substring(5);

            // If we don't have IMDB ID yet, check the main ID if it's tt...
            var mainIdProp = root.TryGetProperty("Id", out var mid) ? mid.GetString() : null;
            if (string.IsNullOrEmpty(finalImdbId) && mainIdProp != null && mainIdProp.StartsWith("tt"))
            {
                finalImdbId = mainIdProp;
            }

            if (!string.IsNullOrEmpty(finalImdbId)) mapped["imdb_id"] = finalImdbId;
            if (!string.IsNullOrEmpty(finalTmdbId)) mapped["tmdb_id"] = finalTmdbId;

            // Baklava primary ID logic: ttID or tmdb:ID
            if (!string.IsNullOrEmpty(finalImdbId))
            {
                mapped["id"] = finalImdbId;
                mapped["Id"] = finalImdbId;
            }
            else if (!string.IsNullOrEmpty(finalTmdbId))
            {
                mapped["id"] = $"tmdb:{finalTmdbId}";
                mapped["Id"] = $"tmdb:{finalTmdbId}";
            }

            if (includeCredits)
            {
                JsonElement? creditsProp = FindProp(new[] { "Cast", "Credits", "Actors", "credits" });
                JsonElement? castArray = null;

                if (creditsProp.HasValue)
                {
                    if (creditsProp.Value.ValueKind == JsonValueKind.Array)
                    {
                        castArray = creditsProp.Value;
                    }
                    else if (creditsProp.Value.ValueKind == JsonValueKind.Object)
                    {
                        if (creditsProp.Value.TryGetProperty("cast", out var c)) castArray = c;
                        else if (creditsProp.Value.TryGetProperty("Cast", out var c2)) castArray = c2;
                    }
                }

                // Try App_Extras if not found
                if (!castArray.HasValue && root.TryGetProperty("App_Extras", out var extras))
                {
                    if (extras.TryGetProperty("Cast", out var appCast)) castArray = appCast;
                    else if (extras.TryGetProperty("Credits", out var appCredits)) castArray = appCredits;
                }

                if (castArray.HasValue && castArray.Value.ValueKind == JsonValueKind.Array)
                {
                    var castList = new List<object>();
                    foreach (var actor in castArray.Value.EnumerateArray())
                    {
                        var aName = actor.TryGetProperty("Name", out var n) ? n.GetString() :
                                  (actor.TryGetProperty("name", out var n2) ? n2.GetString() : "");

                        var aPhoto = actor.TryGetProperty("Photo", out var p) ? p.GetString() :
                                   (actor.TryGetProperty("profile_path", out var p2) ? p2.GetString() : "");

                        var aChar = actor.TryGetProperty("Character", out var c) ? c.GetString() :
                                   (actor.TryGetProperty("character", out var c2) ? c2.GetString() : "");

                        // Handle TMDB relative paths for profile photos
                        if (!string.IsNullOrEmpty(aPhoto) && !aPhoto.StartsWith("http") && !aPhoto.StartsWith("/"))
                        {
                            aPhoto = "/" + aPhoto;
                        }

                        castList.Add(new
                        {
                            name = aName,
                            character = aChar,
                            profile_path = aPhoto
                        });
                    }
                    mapped["credits"] = new { cast = castList };
                }
            }

            return mapped;
        }

        private async Task<string?> ResolveIdFromTmdb(string title, int? year, string? itemType)
        {
            try
            {
                var config = Plugin.Instance?.Configuration;
                var apiKey = config?.TmdbApiKey;
                if (string.IsNullOrEmpty(apiKey)) 
                {
                    _logger.LogWarning("⚪ [Cavea.SearchMetadata] TMDB API Key not configured, cannot resolve title-only search");
                    return null;
                }

                using var client = new HttpClient();
                var searchType = string.Equals(itemType, "series", StringComparison.OrdinalIgnoreCase) || 
                               string.Equals(itemType, "tv", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";
                
                var url = $"https://api.themoviedb.org/3/search/{searchType}?api_key={apiKey}&query={Uri.EscapeDataString(title)}";
                if (year.HasValue)
                {
                    var yearParam = searchType == "tv" ? "first_air_date_year" : "primary_release_year";
                    url += $"&{yearParam}={year.Value}";
                }

                var response = await client.GetAsync(url);
                if (!response.IsSuccessStatusCode) return null;

                using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
                if (doc.RootElement.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array && results.GetArrayLength() > 0)
                {
                    var firstMatch = results[0];
                    if (firstMatch.TryGetProperty("id", out var idProp))
                    {
                        return idProp.ToString();
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "⚪ [Cavea.SearchMetadata] Error resolving TMDB ID for '{Title}'", title);
            }
            return null;
        }

        private async Task<object?> FetchMetadataFromTMDB(string tmdbId, string itemType)
        {
            try
            {
                var config = Plugin.Instance?.Configuration;
                var apiKey = config?.TmdbApiKey;
                if (string.IsNullOrEmpty(apiKey)) return null;

                using var client = new HttpClient();
                var mediaType = string.Equals(itemType, "series", StringComparison.OrdinalIgnoreCase) || 
                              string.Equals(itemType, "tv", StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(itemType, "episode", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";
                
                var url = $"https://api.themoviedb.org/3/{mediaType}/{tmdbId}?api_key={apiKey}&append_to_response=credits,external_ids";
                var response = await client.GetAsync(url);
                if (!response.IsSuccessStatusCode) return null;

                return await response.Content.ReadFromJsonAsync<object>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "⚪ [Cavea.SearchMetadata] Error fetching full TMDB metadata for {Id}", tmdbId);
                return null;
            }
        }
    }
}
