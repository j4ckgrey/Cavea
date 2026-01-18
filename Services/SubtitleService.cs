#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Cavea.Api;
using Cavea.Services;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace Cavea.Services
{
    public class SubtitleService
    {
        private readonly ILogger<SubtitleService> _logger;
        private readonly CaveaDbService _dbService;
        private readonly ILibraryManager _libraryManager;

        public SubtitleService(
            ILogger<SubtitleService> logger, 
            CaveaDbService dbService,
            ILibraryManager libraryManager)
        {
            _logger = logger;
            _dbService = dbService;
            _libraryManager = libraryManager;
        }

        public async Task<List<Cavea.Api.ExternalSubtitleInfo>?> FetchExternalSubtitlesAsync(string itemId)
        {
            try
            {
                _logger.LogInformation("⚪ [Cavea.Subtitle] FetchExternalSubtitles START for {ItemId}", itemId);

                var cfg = Plugin.Instance?.Configuration;
                if (cfg == null || !cfg.EnableExternalSubtitles)
                {
                    _logger.LogInformation("⚪ [Cavea.Subtitle] External subtitles DISABLED in config or config is NULL");
                    return null;
                }

                // DIRECT FETCH - NO PERSISTENT CACHING
                _logger.LogInformation("⚪ [Cavea.Subtitle] No cache, fetching from Gelato...");

                if (!Guid.TryParse(itemId, out var itemGuid))
                {
                    _logger.LogWarning("⚪ [Cavea.Subtitle] Invalid GUID format: {ItemId}", itemId);
                    return null;
                }

                var gelatoPlugin = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Gelato");

                if (gelatoPlugin == null)
                {
                    _logger.LogWarning("⚪ [Cavea.Subtitle] Gelato plugin assembly NOT FOUND");
                    return null;
                }

                var gelatoPluginType = gelatoPlugin.GetType("Gelato.GelatoPlugin");
                var instanceProp = gelatoPluginType?.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                var gelatoInstance = instanceProp?.GetValue(null);

                if (gelatoInstance == null)
                {
                    _logger.LogWarning("⚪ [Cavea.Subtitle] Gelato plugin instance is NULL");
                    return null;
                }

                // Get the item to build StremioUri
                var item = _libraryManager.GetItemById(itemGuid);
                if (item == null)
                {
                    _logger.LogWarning("⚪ [Cavea.Subtitle] Item not found in library: {ItemId}", itemId);
                    return null;
                }

                // Build StremioUri using reflection
                object? stremioUri = null;
                try
                {
                    var stremioUriType = gelatoPlugin.GetType("Gelato.Common.StremioUri");
                    var fromBaseItemMethod = stremioUriType?.GetMethod("FromBaseItem", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    stremioUri = fromBaseItemMethod?.Invoke(null, new object[] { item });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "⚪ [Cavea.Subtitle] FromBaseItem threw exception");
                }

                if (stremioUri == null)
                {
                    _logger.LogWarning("⚪ [Cavea.Subtitle] Could not build StremioUri for item {ItemId}", itemId);
                    return null;
                }

                // Get the config to access the stremio provider
                var getConfigMethod = gelatoInstance.GetType().GetMethod("GetConfig");
                var config = getConfigMethod?.Invoke(gelatoInstance, new object[] { Guid.Empty });
                
                if (config == null) return null;

                var stremioProviderField = config.GetType().GetField("stremio");
                var stremioProvider = stremioProviderField?.GetValue(config);

                if (stremioProvider == null) return null;

                // Call GetSubtitlesAsync
                var getSubtitlesMethod = stremioProvider.GetType().GetMethod("GetSubtitlesAsync");
                var subtitlesTask = getSubtitlesMethod?.Invoke(stremioProvider, new object?[] { stremioUri, null }) as Task;

                if (subtitlesTask == null) return null;

                await subtitlesTask.ConfigureAwait(false);
                
                var resultProperty = subtitlesTask.GetType().GetProperty("Result");
                var cachedSubs = resultProperty?.GetValue(subtitlesTask);

                if (cachedSubs == null)
                {
                    _logger.LogInformation("⚪ [Cavea.Subtitle] GetSubtitlesAsync result is NULL for {ItemId}", itemId);
                    return null;
                }

                var subsList = cachedSubs as System.Collections.IEnumerable;
                var subtitles = new List<Cavea.Api.ExternalSubtitleInfo>();

                foreach (var sub in subsList!)
                {
                    var subType = sub.GetType();
                    var url = subType.GetProperty("Url")?.GetValue(sub) as string;
                    var lang = subType.GetProperty("Lang")?.GetValue(sub) as string;
                    var title = subType.GetProperty("Title")?.GetValue(sub) as string;
                    var id = subType.GetProperty("Id")?.GetValue(sub) as string;

                    if (!string.IsNullOrEmpty(url))
                    {
                        subtitles.Add(new Cavea.Api.ExternalSubtitleInfo
                        {
                            Url = url,
                            Language = lang,
                            Title = GetLanguageDisplayName(lang) ?? title ?? "Unknown", // Prefer pretty name
                            Id = id
                        });
                    }
                }

                if (subtitles.Count > 0)
                {
                    _logger.LogInformation("⚪ [Cavea.Subtitle] Fetched {Count} external subtitles for {ItemId}", subtitles.Count, itemId);
                }

                return subtitles;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "⚪ [Cavea.Subtitle] EXCEPTION in FetchExternalSubtitlesAsync for {ItemId}: {Message}", itemId, ex.Message);
                return null;
            }
        }

        private string GetLanguageDisplayName(string? langCode)
        {
            if (string.IsNullOrEmpty(langCode)) return "Unknown";
            try
            {
                // Map common codes to full names using CultureInfo or switch
                return langCode.ToLowerInvariant() switch
                {
                    "eng" => "English",
                    "spa" => "Spanish",
                    "fre" => "French",
                    "ger" => "German",
                    "ita" => "Italian",
                    "jpn" => "Japanese",
                    "chi" => "Chinese",
                    "zho" => "Chinese",
                    "rus" => "Russian",
                    "por" => "Portuguese",
                    "dut" => "Dutch",
                    "lat" => "Latin",
                    "kor" => "Korean",
                    "swe" => "Swedish",
                    "fin" => "Finnish",
                    "nor" => "Norwegian",
                    "dan" => "Danish",
                    "pol" => "Polish",
                    "tur" => "Turkish",
                    "hin" => "Hindi",
                    "und" => "Undetermined",
                    _ => new CultureInfo(langCode).DisplayName
                };
            }
            catch
            {
                return langCode;
            }
        }
    }
}
