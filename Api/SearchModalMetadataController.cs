#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
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
            [FromQuery] string? itemType, // "movie" or "series"
            [FromQuery] string? title,
            [FromQuery] int? year,
            [FromQuery] bool includeCredits = false)
        {
            _logger.LogInformation("⚪ [Cavea.SearchMetadata] GetSearchMetadata: tmdbId={TmdbId}, imdbId={ImdbId}, title={Title}, year={Year}, itemType={ItemType}", 
                tmdbId ?? "null", imdbId ?? "null", title ?? "null", year?.ToString() ?? "null", itemType ?? "null");
            
            try
            {
                // 1. Determine Stremio ID (which is our persistent ItemId in Cavea DB)
                string? stremioId = null;
                if (!string.IsNullOrEmpty(imdbId))
                {
                    stremioId = imdbId;
                }
                else if (!string.IsNullOrEmpty(tmdbId))
                {
                    stremioId = $"tmdb:{tmdbId}";
                }

                // 2. CHECK CACHE (ID Lookup OR Title/Year Fallback)
                CompleteItemMetadata? cachedMetadata = null;

                if (!string.IsNullOrEmpty(stremioId))
                {
                    cachedMetadata = await _caveaDb.GetCompleteItemMetadataAsync(stremioId);
                }
                else if (!string.IsNullOrEmpty(title))
                {
                    // Fallback: Try to find by Title/Year in our DB
                    // This creates a "soft" cache hit if we've seen this item before
                    // cachedMetadata = await _caveaDb.GetCompleteItemMetadataByTitleAsync(title, year, itemType); 
                    // Method missing in DB, disabling fallback optimization for now
                    if (cachedMetadata != null)
                    {
                        stremioId = cachedMetadata.ItemId; // Found it!
                        _logger.LogInformation("⚪ [Cavea.SearchMetadata] Resolved title '{Title}' to ItemId {Id}", title, stremioId);
                    }
                }

                if (string.IsNullOrEmpty(stremioId) && cachedMetadata == null)
                {
                    // If we still have no ID and no cache hit, we can't proceed to Gelato (it requires an ID)
                    // Unless we want to search Gelato by text, but Gelato interface used here is GetMetaAsync(id).
                    // So we must error out if we couldn't resolve it.
                    return BadRequest(new { error = "Either (imdbId or tmdbId) OR (title and a cached match) is required." });
                }

                // 2. CHECK CACHE FIRST (Again using ID if resolved)
                if (cachedMetadata == null && !string.IsNullOrEmpty(stremioId))
                {
                     cachedMetadata = await _caveaDb.GetCompleteItemMetadataAsync(stremioId);
                }

                if (cachedMetadata != null)
                {
                    _logger.LogInformation("⚪ [Cavea.SearchMetadata] Found cached metadata for {Id}", stremioId);
                    
                    // Map to simple format expected by frontend
                    var mappedCache = new Dictionary<string, object?>();
                    mappedCache["name"] = cachedMetadata.Name;
                    mappedCache["description"] = cachedMetadata.Overview;
                    mappedCache["year"] = cachedMetadata.Year;
                    mappedCache["background"] = cachedMetadata.BackdropUrl;
                    mappedCache["logo"] = cachedMetadata.LogoUrl;
                    mappedCache["imdbRating"] = cachedMetadata.CommunityRating;
                    mappedCache["genres"] = cachedMetadata.Genres;
                    mappedCache["runtime"] = cachedMetadata.Runtime; // Ticks or minutes? Assuming usage downstream handles it
                    
                    if (includeCredits && cachedMetadata.People != null)
                    {
                         mappedCache["credits"] = new { cast = cachedMetadata.People.Select(p => new { name = p.Name, role = p.Role, profile = p.ImageUrl }) };
                    }

                    return Ok(mappedCache);
                }

                // 3. Determine Stremio MediaType enum
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
                    // Episodes use series type in Gelato - the IMDB ID identifies the episode
                    mediaTypeEnum = 2; // StremioMediaType.Series
                }
                else
                {
                    return BadRequest(new { error = "Invalid or missing itemType (must be 'movie', 'series', or 'episode')" });
                }

                // 4. Reflect into Gelato to get metadata
                var gelatoData = await FetchMetadataFromGelato(stremioId, mediaTypeEnum);
                
                if (gelatoData != null)
                {
                     // Debug: Log the properties of the result for troubleshooting
                     try 
                     {
                         var json = JsonSerializer.Serialize(gelatoData);
                         _logger.LogInformation("⚪ [Cavea.SearchMetadata] Gelato response: {Json}", json);

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
                         mapped["year"] = yearProp?.ToString(); // Could be int or string

                         var backProp = FindProp(new[] { "Background", "Backdrop", "BackdropImage", "Banner" });
                         mapped["background"] = backProp?.GetString();

                         var posterProp = FindProp(new[] { "Poster", "PosterImage", "Cover" });
                         mapped["poster"] = posterProp?.GetString();

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
                        
                         // 5. SAVE TO DB (Cache it!)
                         var completeMeta = new CompleteItemMetadata
                         {
                             ItemId = stremioId,
                             ItemType = itemType ?? "unknown",
                             Name = mapped["name"]?.ToString(),
                             Overview = mapped["description"]?.ToString(),
                             BackdropUrl = mapped["background"]?.ToString(),
                             LogoUrl = mapped["logo"]?.ToString(),
                             ImdbId = imdbId,
                             TmdbId = tmdbId,
                             LastUpdated = DateTime.UtcNow
                         };

                         if (int.TryParse(mapped["year"]?.ToString()?.Split('-')[0], out var parsedYear))
                             completeMeta.Year = parsedYear;

                         if (float.TryParse(mapped["imdbRating"]?.ToString(), out var rating))
                             completeMeta.CommunityRating = rating;
                             
                         if (mapped["genres"] is List<string> gList)
                             completeMeta.Genres = gList;
                             
                         // Map Credits (Cast)
                         if (includeCredits)
                         {
                             var castProp = FindProp(new[] { "Cast", "Credits", "Actors" });
                             if (castProp.HasValue && castProp.Value.ValueKind == JsonValueKind.Array)
                             {
                                 mapped["credits"] = new { cast = castProp.Value.Clone() };
                                 
                                 // Save people to DB too
                                 completeMeta.People = new List<PersonInfo>();
                                 int sort = 0;
                                 foreach(var person in castProp.Value.EnumerateArray())
                                 {
                                     // Assuming primitive string array or simple object? Gelato usually returns strings or objects
                                     if(person.ValueKind == JsonValueKind.String)
                                     {
                                         completeMeta.People.Add(new PersonInfo { Name = person.GetString()!, Type = "Actor", SortOrder = sort++ });
                                     }
                                     // Else if object... skipping complex parsing for now to keep it safe, Gelato cast is usually simple
                                 }
                             }
                         }
                         
                         // Save to DB asynchronously
                         try 
                         {
                            await _caveaDb.SaveCompleteItemMetadataAsync(completeMeta);
                         } 
                         catch (Exception dbEx)
                         {
                             _logger.LogError(dbEx, "⚪ [Cavea.SearchMetadata] Failed to save metadata to cache");
                             // Don't fail the request
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

                // Resolve StremioMediaType by finding the method first
                var getMetaMethod = stremioProvider.GetType().GetMethods()
                    .FirstOrDefault(m => m.Name == "GetMetaAsync" && m.GetParameters().Length == 2);
                
                if (getMetaMethod == null)
                {
                     _logger.LogError("⚪ [Cavea.SearchMetadata] GetMetaAsync method not found on StremioProvider");
                     return null;
                }

                var paramType = getMetaMethod.GetParameters()[1].ParameterType;
                var mediaTypeEnumValue = Enum.ToObject(paramType, mediaTypeEnum);

                var task = getMetaMethod.Invoke(stremioProvider, new object[] { id, mediaTypeEnumValue }) as Task;
                if (task == null) return null;

                await task.ConfigureAwait(false);

                var resultProp = task.GetType().GetProperty("Result");
                var result = resultProp?.GetValue(task);

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "⚪ [Cavea.SearchMetadata] Reflection error fetching from Gelato");
                return null;
            }
        }
    }
}
