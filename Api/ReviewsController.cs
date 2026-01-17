using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Cavea.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

#nullable enable
namespace Cavea.Api
{
    [ApiController]
    [Route("api/cavea/reviews")]
    [Produces("application/json")]
    [Authorize]
    public class ReviewsController : ControllerBase
    {
        private readonly ILogger<ReviewsController> _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly CaveaDbService _dbService;
        private const string TMDB_BASE = "https://api.themoviedb.org/3";
        private const int REVIEWS_LIMIT = 15;

        public ReviewsController(
            ILogger<ReviewsController> logger,
            ILibraryManager libraryManager,
            CaveaDbService dbService)
        {
            _logger = logger;
            _libraryManager = libraryManager;
            _dbService = dbService;
        }

        /// <summary>
        /// Get reviews for a specific Jellyfin item
        /// </summary>
        /// <param name="itemId">Jellyfin Item ID</param>
        /// <param name="forceRefresh">Force refresh from external APIs</param>
        [HttpGet("{itemId}")]
        public async Task<IActionResult> GetReviewsForItem(
            [FromRoute] string itemId,
            [FromQuery] bool forceRefresh = false)
        {
            _logger.LogInformation("[ReviewsController]  GetReviewsForItem called: itemId={ItemId}, forceRefresh={ForceRefresh}", itemId, forceRefresh);
            
            try
            {
                if (!Guid.TryParse(itemId, out var itemGuid))
                {
                    _logger.LogWarning("[ReviewsController]  Invalid item ID format: {ItemId}", itemId);
                    return BadRequest(new { error = "Invalid item ID format" });
                }
                
                var item = _libraryManager.GetItemById(itemGuid);
                if (item == null)
                {
                    _logger.LogWarning("[ReviewsController]  Item not found: {ItemId}", itemId);
                    return NotFound(new { error = "Item not found" });
                }

                _logger.LogInformation("[ReviewsController]  Found item: {ItemName} ({ItemType})", item.Name, item.GetType().Name);

                // Determine item type
                string itemType;
                if (item is Movie)
                {
                    itemType = "movie";
                }
                else if (item is Series)
                {
                    itemType = "series";
                }
                else if (item is Episode)
                {
                    itemType = "episode";
                }
                else
                {
                    _logger.LogWarning("[ReviewsController]  Unsupported item type: {Type}", item.GetType().Name);
                    return BadRequest(new { error = "Unsupported item type" });
                }

                // Get provider IDs
                var imdbId = item.ProviderIds?.GetValueOrDefault("Imdb");
                var tmdbId = item.ProviderIds?.GetValueOrDefault("Tmdb");

                _logger.LogInformation("⚪ [ReviewsController]  Provider IDs: IMDB={ImdbId}, TMDB={TmdbId}", imdbId ?? "null", tmdbId ?? "null");

                if (string.IsNullOrEmpty(imdbId) && string.IsNullOrEmpty(tmdbId))
                {
                    _logger.LogWarning("[ReviewsController]  No IMDB or TMDB ID found for item {ItemId}", itemId);
                    return NotFound(new { error = "No IMDB or TMDB ID found for this item" });
                }

                // NO DB SAVING for item metadata
                _logger.LogDebug("[ReviewsController] Skipping metadata save for {ItemId}", itemId);

                // Get configured review source
                var cfg = Plugin.Instance?.Configuration;
                var reviewSource = cfg?.ReviewSource ?? "tmdb";
                
                _logger.LogInformation("[ReviewsController]  Using review source: {Source}. TMDB Key: {Tmdb}, RapidAPI Key: {Rapid}", 
                    reviewSource, 
                    !string.IsNullOrEmpty(cfg?.TmdbApiKey) ? "Set" : "Missing",
                    !string.IsNullOrEmpty(cfg?.RapidApiKey) ? "Set" : "Missing");

                // Check cache first (unless force refresh)
                if (!forceRefresh)
                {
                    var cachedReviews = await _dbService.GetReviewsAsync(itemId, reviewSource);
                    if (cachedReviews != null)
                    {
                        _logger.LogInformation("[ReviewsController]  Returning cached reviews for {ItemId}", itemId);
                        return Content(cachedReviews, "application/json");
                    }
                    _logger.LogInformation("⚪ [ReviewsController]  No cached reviews found, fetching fresh...");
                }

                // Fetch fresh reviews based on source
                JsonDocument? reviewsDoc = null;
                var mediaType = itemType == "series" ? "tv" : itemType; // TMDB uses "tv" not "series"

                if (reviewSource == "tmdb")
                {
                    _logger.LogInformation("[ReviewsController]  Fetching TMDB reviews for {MediaType}/{TmdbId}...", mediaType, tmdbId);
                    reviewsDoc = await FetchTMDBReviews(imdbId, tmdbId, mediaType);
                }
                else if (reviewSource == "trakt" && !string.IsNullOrEmpty(imdbId))
                {
                    _logger.LogInformation("[ReviewsController]  Fetching Trakt reviews for {ImdbId}...", imdbId);
                    reviewsDoc = await FetchTraktReviews(imdbId, mediaType, cfg?.TraktClientId);
                }
                else if (reviewSource == "imdb" && !string.IsNullOrEmpty(imdbId))
                {
                    _logger.LogInformation("[ReviewsController]  Fetching IMDb reviews for {ImdbId}...", imdbId);
                    reviewsDoc = await FetchIMDbReviews(imdbId, cfg?.RapidApiKey);
                }

                if (reviewsDoc == null)
                {
                    _logger.LogWarning("[ReviewsController]  No reviews returned from {Source}", reviewSource);
                    return Ok(new { results = new List<object>() });
                }

                // Save to database
                var reviewsJson = reviewsDoc.RootElement.GetRawText();
                await _dbService.SaveReviewsAsync(itemId, imdbId, tmdbId, itemType, reviewSource, reviewsJson);

                _logger.LogInformation("[ReviewsController]  Successfully fetched and cached reviews for {ItemId}", itemId);
                return Content(reviewsJson, "application/json");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReviewsController]  Error fetching reviews for item {ItemId}", itemId);
                return StatusCode(500, new { error = "Failed to fetch reviews" });
            }
        }

        /// <summary>
        /// Bulk sync reviews for all items in library
        /// </summary>
        [HttpPost("sync")]
        public async Task<IActionResult> SyncReviewsForLibrary()
        {
            try
            {
                var items = await _dbService.GetItemsNeedingReviewUpdateAsync();
                var updated = 0;
                var failed = 0;

                _logger.LogInformation("[ReviewsController] Starting bulk sync for {Count} items", items.Count);

                foreach (var (itemId, imdbId, tmdbId, itemType) in items)
                {
                    try
                    {
                        var cfg = Plugin.Instance?.Configuration;
                        var reviewSource = cfg?.ReviewSource ?? "tmdb";
                        var mediaType = itemType == "series" ? "tv" : itemType;

                        JsonDocument? reviewsDoc = null;

                        if (reviewSource == "tmdb")
                        {
                            reviewsDoc = await FetchTMDBReviews(imdbId, tmdbId, mediaType);
                        }
                        else if (reviewSource == "trakt" && !string.IsNullOrEmpty(imdbId))
                        {
                            reviewsDoc = await FetchTraktReviews(imdbId, mediaType, cfg?.TraktClientId);
                        }
                        else if (reviewSource == "imdb" && !string.IsNullOrEmpty(imdbId))
                        {
                            reviewsDoc = await FetchIMDbReviews(imdbId, cfg?.RapidApiKey);
                        }

                        if (reviewsDoc != null)
                        {
                            var reviewsJson = reviewsDoc.RootElement.GetRawText();
                            await _dbService.SaveReviewsAsync(itemId, imdbId, tmdbId, itemType, reviewSource, reviewsJson);
                            updated++;
                        }
                        else
                        {
                            failed++;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[ReviewsController] Failed to sync reviews for item {ItemId}", itemId);
                        failed++;
                    }

                    // Small delay to avoid rate limiting
                    await Task.Delay(100);
                }

                _logger.LogInformation("[ReviewsController] Sync complete: {Updated} updated, {Failed} failed", updated, failed);
                return Ok(new { updated, failed, total = items.Count });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReviewsController] Error during bulk sync");
                return StatusCode(500, new { error = "Failed to sync reviews" });
            }
        }

        #region Review Fetchers

        private async Task<JsonDocument?> FetchTMDBReviews(string? imdbId, string? tmdbId, string mediaType)
        {
            try
            {
                var cfg = Plugin.Instance?.Configuration;
                var apiKey = cfg?.TmdbApiKey;
                
                if (string.IsNullOrEmpty(apiKey))
                {
                    _logger.LogWarning("⚪ [ReviewsController] TMDB API key not configured");
                    return null;
                }

                if (string.IsNullOrEmpty(tmdbId))
                {
                    _logger.LogWarning("⚪ [ReviewsController] No TMDB ID provided");
                    return null;
                }

                var url = $"{TMDB_BASE}/{mediaType}/{tmdbId}/reviews?api_key={apiKey}";
                
                using var client = new HttpClient();
                var response = await client.GetStringAsync(url);
                var reviewsDoc = JsonDocument.Parse(response);

                // Limit to REVIEWS_LIMIT reviews
                var root = reviewsDoc.RootElement;
                if (root.TryGetProperty("results", out var results) && results.GetArrayLength() > REVIEWS_LIMIT)
                {
                    var limitedReviews = new List<JsonElement>();
                    var count = 0;
                    foreach (var review in results.EnumerateArray())
                    {
                        if (count >= REVIEWS_LIMIT) break;
                        limitedReviews.Add(review);
                        count++;
                    }
                    
                    var limitedJson = JsonSerializer.Serialize(new { results = limitedReviews });
                    reviewsDoc = JsonDocument.Parse(limitedJson);
                }

                return reviewsDoc;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReviewsController] Failed to fetch TMDB reviews");
                return null;
            }
        }

        private async Task<JsonDocument?> FetchTraktReviews(string imdbId, string mediaType, string? clientId)
        {
            if (string.IsNullOrEmpty(clientId))
            {
                _logger.LogWarning("⚪ [ReviewsController] Trakt Client ID not configured");
                return null;
            }
            
            try
            {
                // Trakt uses "movies" or "shows"
                var traktType = mediaType == "tv" ? "shows" : "movies";
                var url = $"https://api.trakt.tv/{traktType}/{imdbId}/comments?limit={REVIEWS_LIMIT}";
                
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("Content-Type", "application/json");
                client.DefaultRequestHeaders.Add("trakt-api-version", "2");
                client.DefaultRequestHeaders.Add("trakt-api-key", clientId);
                
                var response = await client.GetStringAsync(url);
                
                // Transform Trakt comments to TMDB-like review format
                using var doc = JsonDocument.Parse(response);
                var comments = doc.RootElement;
                
                var reviews = new List<object>();
                foreach (var comment in comments.EnumerateArray())
                {
                    var user = comment.GetProperty("user").GetProperty("username").GetString();
                    var content = comment.GetProperty("comment").GetString();
                    var createdAt = comment.GetProperty("created_at").GetString();
                    
                    reviews.Add(new
                    {
                        author = user ?? "Anonymous",
                        author_details = new { username = user, rating = (int?)null },
                        content = content ?? "",
                        created_at = createdAt ?? "",
                        id = comment.GetProperty("id").GetInt32().ToString(),
                        url = $"https://trakt.tv/comments/{comment.GetProperty("id").GetInt32()}"
                    });
                }
                
                var reviewsJson = JsonSerializer.Serialize(new { results = reviews });
                return JsonDocument.Parse(reviewsJson);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReviewsController] Failed to fetch Trakt reviews for {ImdbId}", imdbId);
                return null;
            }
        }

        private async Task<JsonDocument?> FetchIMDbReviews(string imdbId, string? rapidApiKey)
        {
            if (string.IsNullOrEmpty(rapidApiKey))
            {
                _logger.LogWarning("⚪ [ReviewsController] RapidAPI Key not configured");
                return null;
            }
            
            try
            {
                // IMDb8 API v2 endpoint - uses featuredReviews
                var url = $"https://imdb8.p.rapidapi.com/title/v2/get-user-reviews-summary?tconst={imdbId}&country=US&language=en-US";
                
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("x-rapidapi-key", rapidApiKey);
                client.DefaultRequestHeaders.Add("x-rapidapi-host", "imdb8.p.rapidapi.com");
                
                var response = await client.GetStringAsync(url);
                using var reviewsDoc = JsonDocument.Parse(response);
                var root = reviewsDoc.RootElement;
                
                // Transform IMDb8 v2 response to TMDB-like format
                var reviews = new List<object>();
                
                // Navigate to data.title.featuredReviews.edges
                if (root.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("title", out var title) &&
                    title.TryGetProperty("featuredReviews", out var featuredReviews) &&
                    featuredReviews.TryGetProperty("edges", out var edges) &&
                    edges.ValueKind == JsonValueKind.Array)
                {
                    var count = 0;
                    foreach (var edge in edges.EnumerateArray())
                    {
                        if (count >= REVIEWS_LIMIT) break;
                        
                        if (!edge.TryGetProperty("node", out var node))
                            continue;
                        
                        // Extract author
                        var author = "Anonymous";
                        if (node.TryGetProperty("author", out var authorObj) &&
                            authorObj.TryGetProperty("nickName", out var nickName))
                        {
                            author = nickName.GetString() ?? "Anonymous";
                        }
                        
                        // Extract author rating
                        double? authorRating = null;
                        if (node.TryGetProperty("authorRating", out var ratingProp) &&
                            ratingProp.ValueKind == JsonValueKind.Number)
                        {
                            authorRating = ratingProp.GetDouble();
                        }
                        
                        // Extract review title (summary.originalText)
                        var reviewTitle = "";
                        if (node.TryGetProperty("summary", out var summary) &&
                            summary.TryGetProperty("originalText", out var summaryText))
                        {
                            reviewTitle = summaryText.GetString() ?? "";
                        }
                        
                        // Extract review content (text.originalText.plainText)
                        var content = "";
                        if (node.TryGetProperty("text", out var textObj) &&
                            textObj.TryGetProperty("originalText", out var originalText) &&
                            originalText.TryGetProperty("plainText", out var plainText))
                        {
                            content = plainText.GetString() ?? "";
                        }
                        
                        // Prepend title to content if exists
                        if (!string.IsNullOrEmpty(reviewTitle))
                        {
                            content = $"**{reviewTitle}**\n\n{content}";
                        }
                        
                        // Extract submission date
                        var createdAt = DateTime.UtcNow.ToString("O");
                        if (node.TryGetProperty("submissionDate", out var dateProp))
                        {
                            createdAt = dateProp.GetString() ?? DateTime.UtcNow.ToString("O");
                        }
                        
                        // Extract review ID
                        var id = Guid.NewGuid().ToString();
                        if (node.TryGetProperty("id", out var idProp))
                        {
                            id = idProp.GetString() ?? Guid.NewGuid().ToString();
                        }
                        
                        // Check for spoiler flag
                        var isSpoiler = false;
                        if (node.TryGetProperty("spoiler", out var spoilerProp) &&
                            spoilerProp.ValueKind == JsonValueKind.True)
                        {
                            isSpoiler = true;
                        }
                        
                        reviews.Add(new
                        {
                            author = author,
                            author_details = new { username = author, rating = authorRating },
                            content = content,
                            created_at = createdAt,
                            id = id,
                            rating = authorRating,
                            url = $"https://www.imdb.com/title/{imdbId}/reviews",
                            spoiler = isSpoiler
                        });
                        count++;
                    }
                }
                
                _logger.LogInformation("[ReviewsController] Fetched {Count} IMDb reviews for {ImdbId} (v2 API)", reviews.Count, imdbId);
                
                var reviewsJson = JsonSerializer.Serialize(new { results = reviews });
                return JsonDocument.Parse(reviewsJson);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ReviewsController] Failed to fetch IMDb reviews for {ImdbId}", imdbId);
                return null;
            }
        }

        private string CleanHTMLContent(string content)
        {
            if (string.IsNullOrEmpty(content)) return content;
            
            // Replace HTML line breaks with newlines
            content = System.Text.RegularExpressions.Regex.Replace(
                content, @"<br\s*/?>\s*", "\n", 
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            
            // Decode HTML entities
            content = System.Net.WebUtility.HtmlDecode(content);
            
            // Remove any remaining HTML tags
            content = System.Text.RegularExpressions.Regex.Replace(content, @"<[^>]*>", "");
            
            // Clean up multiple spaces and newlines
            content = System.Text.RegularExpressions.Regex.Replace(content, @"\s+", " ").Trim();
            
            return content;
        }

        #endregion
    }
}
