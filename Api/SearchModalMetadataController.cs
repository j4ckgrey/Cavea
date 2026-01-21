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
                            _logger.LogInformation("⚪ [Cavea.SearchMetadata] Got provider IDs from Jellyfin item: imdbId={ImdbId}, tmdbId={TmdbId}, itemType={ItemType}", 
                                imdbId ?? "null", tmdbId ?? "null", itemType ?? "null");
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
                
                if (gelatoData != null)
                {
                     try 
                     {
                         var json = JsonSerializer.Serialize(gelatoData);
                         // Don't log entire JSON - too verbose
                         _logger.LogDebug("⚪ [Cavea.SearchMetadata] Gelato returned data successfully");

                         // Robust Mapping
                         using var doc = JsonDocument.Parse(json);
                         var root = doc.RootElement;
                         
                         var mapped = new Dictionary<string, object?>();
                         
                         // Helper to find property case-insensitive
                         JsonElement? FindProp(string[] names) 
                         {
                             foreach(var name in names) 
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

                         var yearProp = FindProp(new[] { "Year", "ProductionYear", "ReleaseInfo" });
                         mapped["year"] = yearProp?.ToString();

                         var backProp = FindProp(new[] { "Background", "Backdrop", "BackdropImage", "Banner" });
                         mapped["background"] = backProp?.GetString();

                         var logoProp = FindProp(new[] { "Logo", "LogoImage" });
                         mapped["logo"] = logoProp?.GetString();

                         var ratingProp = FindProp(new[] { "ImdbRating", "Rating", "CommunityRating" });
                         mapped["imdbRating"] = ratingProp?.ToString();

                         var genresProp = FindProp(new[] { "Genres", "Genre" });
                         if (genresProp.HasValue && genresProp.Value.ValueKind == JsonValueKind.Array)
                         {
                             mapped["genres"] = genresProp.Value.EnumerateArray().Select(x => x.GetString()).ToList();
                         }

                         var runtimeProp = FindProp(new[] { "Runtime", "Duration" });
                         mapped["runtime"] = runtimeProp?.ToString();

                         var rtProp = FindProp(new[] { "RottenTomatoes", "CriticRating", "TomatoMeter" });
                         mapped["rottenTomatoes"] = rtProp?.ToString();
                        
                         if (includeCredits)
                         {
                             JsonElement? castProp = FindProp(new[] { "Cast", "Credits", "Actors" });
                             
                             // Try App_Extras if not at root
                             if (!castProp.HasValue && root.TryGetProperty("App_Extras", out var extras))
                             {
                                 if (extras.TryGetProperty("Cast", out var appCast)) castProp = appCast;
                                 else if (extras.TryGetProperty("Credits", out var appCredits)) castProp = appCredits;
                             }

                             if (castProp.HasValue && castProp.Value.ValueKind == JsonValueKind.Array)
                             {
                                 var castList = new List<object>();
                                 foreach (var actor in castProp.Value.EnumerateArray())
                                 {
                                     var aName = actor.TryGetProperty("Name", out var n) ? n.GetString() : 
                                               (actor.TryGetProperty("name", out var n2) ? n2.GetString() : "");
                                     
                                     var aPhoto = actor.TryGetProperty("Photo", out var p) ? p.GetString() : 
                                                (actor.TryGetProperty("profile_path", out var p2) ? p2.GetString() : "");
                                     
                                     var aChar = actor.TryGetProperty("Character", out var c) ? c.GetString() : 
                                                (actor.TryGetProperty("character", out var c2) ? c2.GetString() : "");

                                     // Extract path from full TMDB URL if present
                                     if (!string.IsNullOrEmpty(aPhoto) && aPhoto.Contains("tmdb.org/t/p/"))
                                     {
                                         var parts = aPhoto.Split('/');
                                         aPhoto = "/" + parts.Last();
                                     }
                                     
                                     castList.Add(new { 
                                         name = aName, 
                                         character = aChar, 
                                         profile_path = aPhoto 
                                     });
                                 }
                                 mapped["credits"] = new { cast = castList };
                             }
                         }

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

                return NotFound(new { error = "No metadata found in Gelato" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "⚪ [Cavea.SearchMetadata] Error getting metadata from Gelato");
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

                var yearPropEl = FindProp(new[] { "Year", "ProductionYear", "ReleaseInfo" });
                mapped["year"] = yearPropEl?.ToString();

                var backProp = FindProp(new[] { "Background", "Backdrop", "BackdropImage", "Banner" });
                mapped["background"] = backProp?.GetString();

                var posterProp = FindProp(new[] { "Poster", "PosterImage" });
                mapped["Poster"] = posterProp?.GetString();

                var ratingProp = FindProp(new[] { "ImdbRating", "Rating", "CommunityRating" });
                mapped["imdbRating"] = ratingProp?.ToString();

                var genresProp = FindProp(new[] { "Genres", "Genre" });
                if (genresProp.HasValue && genresProp.Value.ValueKind == JsonValueKind.Array)
                {
                    mapped["genres"] = genresProp.Value.EnumerateArray().Select(x => x.GetString()).ToList();
                }

                var runtimeProp = FindProp(new[] { "Runtime", "Duration" });
                mapped["runtime"] = runtimeProp?.ToString();

                var idProp = FindProp(new[] { "Id", "imdb_id" });
                mapped["Id"] = idProp?.GetString();

                // Extract cast from App_Extras
                if (root.TryGetProperty("App_Extras", out var appExtras))
                {
                    if (appExtras.TryGetProperty("Cast", out var castArray) && castArray.ValueKind == JsonValueKind.Array)
                    {
                        var castList = new List<object>();
                        foreach (var actor in castArray.EnumerateArray())
                        {
                            var actorName = actor.TryGetProperty("Name", out var n) ? n.GetString() : null;
                            var actorPhoto = actor.TryGetProperty("Photo", out var p) ? p.GetString() : null;
                            var actorChar = actor.TryGetProperty("Character", out var c) ? c.GetString() : null;

                            if (!string.IsNullOrEmpty(actorName))
                            {
                                castList.Add(new { name = actorName, character = actorChar, profile_path = actorPhoto });
                            }
                        }
                        if (castList.Count > 0)
                        {
                            mapped["credits"] = new { cast = castList };
                        }
                    }
                }

                        return mapped;
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
    }
}
