using System;
using System.Net.Http;
using System.Threading.Tasks;
using Cavea.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Cavea.Api
{
    [ApiController]
    [Route("api/cavea/tmdb")]
    [Produces("application/json")]
    public class TmdbEpisodeController : ControllerBase
    {
        private readonly ILogger<TmdbEpisodeController> _logger;
        private readonly CaveaDbService _caveaDb;
        private readonly IHttpClientFactory _httpClientFactory;

        public TmdbEpisodeController(
            ILogger<TmdbEpisodeController> logger,
            CaveaDbService caveaDb,
            IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _caveaDb = caveaDb;
            _httpClientFactory = httpClientFactory;
        }

        [HttpGet("episode")]
        public async Task<ActionResult> GetEpisode(
            [FromQuery] string tmdbSeriesId,
            [FromQuery] int seasonNumber,
            [FromQuery] int episodeNumber)
        {
            if (string.IsNullOrEmpty(tmdbSeriesId)) return BadRequest("Missing tmdbSeriesId");

            // 1. Check Cache
            var cached = await _caveaDb.GetTmdbEpisodeAsync(tmdbSeriesId, seasonNumber, episodeNumber);
            if (!string.IsNullOrEmpty(cached))
            {
                _logger.LogDebug("[Cavea] Serving cached TMDB episode {S} S{Sn}E{En}", tmdbSeriesId, seasonNumber, episodeNumber);
                return Content(cached, "application/json");
            }

            // 2. Fetch from TMDB
            var apiKey = Plugin.Instance?.Configuration?.TmdbApiKey;
            if (string.IsNullOrEmpty(apiKey))
            {
                return BadRequest("TMDB API Key not configured in Cavea");
            }

            var url = $"https://api.themoviedb.org/3/tv/{tmdbSeriesId}/season/{seasonNumber}/episode/{episodeNumber}?api_key={apiKey}";
            
            try 
            {
                var client = _httpClientFactory.CreateClient("TMDB");
                // Add Referer/User-Agent if needed
                var response = await client.GetStringAsync(url);
                
                // 3. Cache
                await _caveaDb.SaveTmdbEpisodeAsync(tmdbSeriesId, seasonNumber, episodeNumber, response);
                
                return Content(response, "application/json");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching from TMDB: {Url}", url);
                return StatusCode(502, "Error fetching from TMDB");
            }
        }
    }
}
