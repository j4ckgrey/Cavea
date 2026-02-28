#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Cavea;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Cavea.Api
{
    [ApiController]
    [Route("api/cavea/metadata")]
    [Produces("application/json")]
    public class LibraryController : ControllerBase
    {
        private readonly ILogger<LibraryController> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;

        public LibraryController(
            ILogger<LibraryController> logger,
            ILibraryManager libraryManager,
            IUserManager userManager)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _userManager = userManager;
        }

        [HttpGet("library-status")]
        public ActionResult CheckLibraryStatus(
            [FromQuery] string? imdbId,
            [FromQuery] string? tmdbId,
            [FromQuery] string itemType,
            [FromQuery] string? jellyfinId)
        {
            // Robust ID handling: strip prefixes
            if (tmdbId != null && tmdbId.StartsWith("tmdb:")) tmdbId = tmdbId.Substring(5);
            if (imdbId != null && imdbId.StartsWith("imdb:")) imdbId = imdbId.Substring(5);

            _logger.LogInformation("⚪ [Cavea.Library] CheckLibraryStatus: imdbId={ImdbId}, tmdbId={TmdbId}, itemType={ItemType}, jellyfinId={JellyfinId}",
                imdbId ?? "null", tmdbId ?? "null", itemType ?? "null", jellyfinId ?? "null");
            
            try
            {
                if (string.IsNullOrEmpty(imdbId) && string.IsNullOrEmpty(tmdbId) && string.IsNullOrEmpty(jellyfinId))
                {
                    return BadRequest(new { error = "Either imdbId, tmdbId, or jellyfinId is required" });
                }

                var inLibraryResult = false;
                string? foundImdbId = imdbId;
                string? foundTmdbId = tmdbId;
                
                string? discoveredType = null;

                try
                {
                    // Fast path: Check by Jellyfin ID
                    if (!string.IsNullOrEmpty(jellyfinId))
                    {
                        BaseItem? itemById = null;
                        if (Guid.TryParse(jellyfinId, out var jfGuid))
                        {
                            itemById = _libraryManager.GetItemById(jfGuid);
                        }
                        
                        // Try hyphenless format if not found (32 hex chars)
                        if (itemById == null && jellyfinId.Length == 32 && !jellyfinId.Contains("-"))
                        {
                            var withHyphens = $"{jellyfinId.Substring(0, 8)}-{jellyfinId.Substring(8, 4)}-{jellyfinId.Substring(12, 4)}-{jellyfinId.Substring(16, 4)}-{jellyfinId.Substring(20)}";
                            if (Guid.TryParse(withHyphens, out var hyphenatedGuid))
                            {
                                itemById = _libraryManager.GetItemById(hyphenatedGuid);
                            }
                        }

                        if (itemById != null)
                        {
                            _logger.LogInformation("⚪ [Cavea.Library] Library item found: {Name} (ID: {Id})", itemById.Name, jellyfinId);
                            var jfType = itemById.GetType().Name;
                            discoveredType = jfType == "Movie" ? "movie" : (jfType == "Series" ? "series" : null);

                            if ((itemType == "series" && jfType == "Series") || (itemType == "movie" && jfType == "Movie") || string.IsNullOrEmpty(itemType))
                            {
                                inLibraryResult = true;
                                if (itemById.ProviderIds != null)
                                {
                                    itemById.ProviderIds.TryGetValue("Imdb", out foundImdbId);
                                    itemById.ProviderIds.TryGetValue("Tmdb", out foundTmdbId);
                                    _logger.LogInformation("⚪ [Cavea.Library] Got provider IDs from Jellyfin item: imdbId={ImdbId}, tmdbId={TmdbId}, itemType={ItemType}", 
                                        foundImdbId ?? "null", foundTmdbId ?? "null", itemType ?? "null");
                                }
                            }
                        }
                        else
                        {
                             _logger.LogInformation("⚪ [Cavea.Library] No library item found by ID: {Id}", jellyfinId);
                        }
                    }

                    // Slow path: Search library by Provider ID
                    if (!inLibraryResult && (!string.IsNullOrEmpty(imdbId) || !string.IsNullOrEmpty(tmdbId)))
                    {
                        _logger.LogInformation("⚪ [Cavea.Library] Searching library by Provider IDs (imdb={Imdb}, tmdb={Tmdb}, typeFilter={Type})", imdbId ?? "null", tmdbId ?? "null", itemType ?? "none");
                        
                        var query = new InternalItemsQuery { Recursive = true };
                        if (itemType == "series") query.IncludeItemTypes = new[] { BaseItemKind.Series };
                        else if (itemType == "movie") query.IncludeItemTypes = new[] { BaseItemKind.Movie };

                        var foundItem = _libraryManager.GetItemList(query).FirstOrDefault(item =>
                        {
                            var providerIds = item.ProviderIds;
                            if (providerIds == null) return false;
                            if (imdbId != null && providerIds.TryGetValue("Imdb", out var itemImdb) && itemImdb == imdbId) return true;
                            if (tmdbId != null && providerIds.TryGetValue("Tmdb", out var itemTmdb) && itemTmdb == tmdbId) return true;
                            return false;
                        });

                        // FALLBACK: If not found with type filter, try without type filter
                        if (foundItem == null && !string.IsNullOrEmpty(itemType))
                        {
                            _logger.LogInformation("⚪ [Cavea.Library] Not found with {Type} filter, trying type-agnostic search", itemType);
                            var agnosticQuery = new InternalItemsQuery { Recursive = true, IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Series } };
                            foundItem = _libraryManager.GetItemList(agnosticQuery).FirstOrDefault(item =>
                            {
                                var providerIds = item.ProviderIds;
                                if (providerIds == null) return false;
                                if (imdbId != null && providerIds.TryGetValue("Imdb", out var itemImdb) && itemImdb == imdbId) return true;
                                if (tmdbId != null && providerIds.TryGetValue("Tmdb", out var itemTmdb) && itemTmdb == tmdbId) return true;
                                return false;
                            });
                        }

                        if (foundItem != null)
                        {
                            _logger.LogInformation("⚪ [Cavea.Library] Found item in library: {Name} ({Type})", foundItem.Name, foundItem.GetType().Name);
                            inLibraryResult = true;
                            var jfType = foundItem.GetType().Name;
                            discoveredType = jfType == "Movie" ? "movie" : (jfType == "Series" ? "series" : null);

                            if (foundItem.ProviderIds != null)
                            {
                                if (string.IsNullOrEmpty(foundImdbId)) foundItem.ProviderIds.TryGetValue("Imdb", out foundImdbId);
                                if (string.IsNullOrEmpty(foundTmdbId)) foundItem.ProviderIds.TryGetValue("Tmdb", out foundTmdbId);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "⚪ [Cavea.Library] Error querying library items");
                }

                var config = Plugin.Instance?.Configuration;
                var requests = config?.Requests ?? new List<MediaRequest>();
                
                var existingRequest = requests.FirstOrDefault(r =>
                    (r.ItemType == itemType || r.ItemType == discoveredType) &&
                    (
                        ((foundImdbId != null && !string.IsNullOrEmpty(r.ImdbId) && r.ImdbId == foundImdbId) || 
                         (foundTmdbId != null && !string.IsNullOrEmpty(r.TmdbId) && r.TmdbId == foundTmdbId)) ||
                        (inLibraryResult && !string.IsNullOrEmpty(jellyfinId) && !string.IsNullOrEmpty(r.JellyfinId) && r.JellyfinId == jellyfinId)
                    )
                );

                string actualUsername = null;
                if (existingRequest != null && !string.IsNullOrEmpty(existingRequest.UserId))
                {
                    try
                    {
                        var userId = Guid.Parse(existingRequest.UserId);
                        var user = _userManager.GetUserById(userId);
                        actualUsername = user?.Username ?? existingRequest.Username;
                    }
                    catch
                    {
                        actualUsername = existingRequest.Username;
                    }
                }


                return Ok(new
                {
                    inLibrary = inLibraryResult,
                    itemType = discoveredType,
                    existingRequest = existingRequest != null ? new
                    {
                        id = existingRequest.Id,
                        status = existingRequest.Status,
                        username = actualUsername ?? existingRequest.Username,
                        title = existingRequest.Title
                    } : null
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "⚪ [Cavea.Library] Error checking library status");
                return StatusCode(500, new { error = "Internal server error" });
            }
        }
    }
}
