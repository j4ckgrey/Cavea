using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Cavea.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Jellyfin.Data.Enums;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Cavea.Api
{
    [ApiController]
    [Route("api/cavea/tmdb")]
    [Produces("application/json")]
    public class TmdbMetadataController : ControllerBase
    {
        private readonly ILogger<TmdbMetadataController> _logger;
        private readonly CaveaDbService _caveaDb;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILibraryManager _libraryManager;

        public TmdbMetadataController(
            ILogger<TmdbMetadataController> logger,
            CaveaDbService caveaDb,
            IHttpClientFactory httpClientFactory,
            ILibraryManager libraryManager)
        {
            _logger = logger;
            _caveaDb = caveaDb;
            _httpClientFactory = httpClientFactory;
            _libraryManager = libraryManager;
        }

        [HttpGet("movie/{tmdbId}")]
        public async Task<ActionResult> GetMovie(string tmdbId)
        {
            return await GetTmdbContent(tmdbId, "movie", $"https://api.themoviedb.org/3/movie/{tmdbId}?append_to_response=credits,videos,images,release_dates,external_ids,reviews&include_image_language=en,null");
        }

        [HttpGet("series/{tmdbId}")]
        public async Task<ActionResult> GetSeries(string tmdbId)
        {
            return await GetTmdbContent(tmdbId, "tv", $"https://api.themoviedb.org/3/tv/{tmdbId}?append_to_response=credits,videos,images,external_ids,content_ratings,reviews&include_image_language=en,null");
        }

        [HttpGet("episode/{tmdbId}/season/{season}/episode/{episode}")]
        public async Task<ActionResult> GetEpisode(string tmdbId, int season, int episode)
        {
            var key = $"tv_{tmdbId}_s{season}_e{episode}";
            var url = $"https://api.themoviedb.org/3/tv/{tmdbId}/season/{season}/episode/{episode}?append_to_response=credits,images,videos";
            return await GetTmdbContent(key, "episode", url, tmdbId, season, episode);
        }

        private async Task<ActionResult> GetTmdbContent(string idOrKey, string type, string tmdbUrl, string? seriesId = null, int? season = null, int? episode = null)
        {
            // 1. Check Cache
            var cacheKey = type == "episode" ? idOrKey : $"{type}_{idOrKey}";
            var cached = await _caveaDb.GetTmdbCacheAsync(cacheKey);
            if (!string.IsNullOrEmpty(cached))
            {
                _logger.LogDebug("[Cavea] Serving cached TMDB {Type} {Key}", type, cacheKey);
                return Content(cached, "application/json");
            }

            // 2. Fetch from TMDB
            var apiKey = Plugin.Instance?.Configuration?.TmdbApiKey;
            if (!string.IsNullOrEmpty(apiKey))
            {
                try
                {
                    var client = _httpClientFactory.CreateClient("Cavea_TMDB");
                    var fullUrl = $"{tmdbUrl}&api_key={apiKey}";
                    // Handle query param separator if url already has params (it does usually)
                    if (!tmdbUrl.Contains("?")) fullUrl = $"{tmdbUrl}?api_key={apiKey}";
                    
                    var response = await client.GetStringAsync(fullUrl);
                    
                    // Cache it
                    await _caveaDb.SaveTmdbCacheAsync(cacheKey, response);
                    return Content(response, "application/json");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error fetching from TMDB: {Url}", tmdbUrl);
                    // Fallthrough to fallback
                }
            }

            // 3. Fallback to Jellyfin Library
            _logger.LogInformation("[Cavea] Fallback: Searching local library for {Type} {Key}", type, idOrKey);
            
            BaseItem? item = null;
            if (type == "episode" && !string.IsNullOrEmpty(seriesId) && season.HasValue && episode.HasValue)
            {
                // Find series first
                var series = FindInLibrary(seriesId, "Series");
                if (series is MediaBrowser.Controller.Entities.TV.Series s)
                {
                     // Find episode logic (simplified)
                     // This is expensive if we iterate, but fallback is fallback.
                     // Better to lookup by indices if possible.
                     // ILibraryManager doesn't have direct "GetEpisode" by indices easily exposed without query.
                     var query = new InternalItemsQuery
                     {
                         Parent = s,
                         IncludeItemTypes = new[] { BaseItemKind.Episode },
                         ParentIndexNumber = season,
                         IndexNumber = episode,
                         Recursive = true,
                         Limit = 1
                     };
                     item = _libraryManager.GetItemList(query).FirstOrDefault();
                }
            }
            else
            {
                item = FindInLibrary(idOrKey, type == "movie" ? "Movie" : "Series");
            }

            if (item != null)
            {
                var mapped = MapToTmdbLikeJson(item, type);
                // We don't cache fallback responses in 'TmdbCache' to avoid poisoning with local data? 
                // Or maybe we do? User said "when looking for any metadata search in db first... if not in db and no tmdb then use jellyfin".
                // If we don't cache it, we scan library every time. 
                // But local data changes. Let's NOT cache fallback in TmdbCache table loop, avoiding key collisions if they add a key later.
                return Ok(mapped);
            }

            return NotFound(new { error = "Metadata not found in TMDB or Local Library" });
        }

        private BaseItem? FindInLibrary(string id, string jellyfinType)
        {
            // NOTE: InternalItemsQuery properties vary by version. 
            // robust way:
            // This pulls ALL items with TmdbId? No "HasTmdbId" is not a standard prop on all versions.
            
            var itemKind = jellyfinType == "Movie" ? BaseItemKind.Movie : BaseItemKind.Series;
            // 2. CHECK LOCAL JELLYFIN DB FOR ITEM BY TMDB ID
            // We use LibraryManager to find items with matching ProviderId
            var item = _libraryManager.GetItemList(new InternalItemsQuery
            {
                Recursive = true,
                IncludeItemTypes = new[] { itemKind },
                HasAnyProviderId = new Dictionary<string, string> { { "Tmdb", id } },
                Limit = 1
            }).FirstOrDefault();

            return item;
        }

        private object MapToTmdbLikeJson(BaseItem item, string type)
        {
            // Minimal mapping to satisfy frontend basic display
            // Frontend expects: title, overview, release_date, poster_path, backdrop_path, vote_average
            
            string? poster = null;
            if (item.HasImage(ImageType.Primary))
            {
                // We return a specialized local path indicator or absolute URL?
                // Frontend uses: `https://image.tmdb.org/t/p/w...${path}` or just assumes it's a path.
                // We need to trick frontend or support local.
                // Use a marker? "/jellyfin/Items/{Id}/Images/Primary"
                // The frontend will likely prepend TMDB url.
                // We will handle this in frontend logic.
                poster = $"/Client/Items/{item.Id}/Images/Primary";
            }

            string? backdrop = null;
            if (item.HasImage(ImageType.Backdrop))
            {
                backdrop = $"/Client/Items/{item.Id}/Images/Backdrop/0";
            }

            return new 
            {
                id = item.GetProviderId("Tmdb") ?? "0",
                tmdb_id = item.GetProviderId("Tmdb"),
                imdb_id = item.GetProviderId("Imdb"),
                overview = item.Overview,
                vote_average = item.CommunityRating,
                title = item.Name,
                name = item.Name, // For series
                release_date = item.PremiereDate?.ToString("yyyy-MM-dd"),
                first_air_date = item.PremiereDate?.ToString("yyyy-MM-dd"), // Series
                poster_path = poster,
                backdrop_path = backdrop,
                genres = item.Genres.Select(g => new { name = g }).ToList(),
                credits = new {
                    cast = _libraryManager.GetPeople(item).Select(p => new {
                        name = p.Name,
                        character = p.Role,
                        profile_path = $"/Client/Items/{p.Id}/Images/Primary" // Hacky for people?
                    }).Take(10)
                }
            };
        }
    }
}
