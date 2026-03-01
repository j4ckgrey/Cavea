#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cavea.Services;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Cavea.Api
{
    [ApiController]
    [Route("api/cavea/metadata")]
    [Produces("application/json")]
    public class StreamController : ControllerBase
    {
        private readonly ILogger<StreamController> _logger;
        private readonly StreamService _streamService;

        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly IMediaSourceManager _mediaSourceManager;

        public StreamController(
            ILogger<StreamController> logger,
            StreamService streamService,

            ILibraryManager libraryManager,
            IUserManager userManager,
            IMediaSourceManager mediaSourceManager)
        {
            _logger = logger;
            _streamService = streamService;

            _libraryManager = libraryManager;
            _userManager = userManager;
            _mediaSourceManager = mediaSourceManager;
        }

        [HttpGet("streams")]
        public async Task<ActionResult> GetMediaStreams(
            [FromQuery] string itemId,
            [FromQuery] string? mediaSourceId,
            [FromQuery] string? client) // "web" = filter out embedded subs
        {
            bool isWebClient = string.Equals(client, "web", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(itemId) || !Guid.TryParse(itemId, out var itemGuid)) return BadRequest(new { error = "Invalid itemId" });

            var item = _libraryManager.GetItemById(itemGuid);
            if (item == null) return NotFound(new { error = "Item not found" });

            // 1. Resolve Target Source
            bool explicitRequest = !string.IsNullOrEmpty(mediaSourceId);
            var currentUserId = ResolveCurrentUserId();
            var currentUser = currentUserId.HasValue ? _userManager.GetUserById(currentUserId.Value) : null;

            var mediaSourceResult = await _mediaSourceManager.GetPlaybackMediaSources(item, currentUser, true, true, CancellationToken.None);
            var mediaSources = mediaSourceResult.ToList();
            if (mediaSources.Count == 0) return NotFound(new { error = "No media sources" });

            // FIX: Ensure Protocol is HTTP for URL paths
            foreach (var ms in mediaSources)
            {
               if (!string.IsNullOrEmpty(ms.Path) && 
                   (ms.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || 
                    ms.Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
                   ms.Protocol != MediaBrowser.Model.MediaInfo.MediaProtocol.Http)
               {
                   _logger.LogWarning("⚪ [Cavea.Stream] Fixing Protocol for {Id}. Was {Proto}, setting to Http. Path: {Path}", ms.Id, ms.Protocol, ms.Path);
                   ms.Protocol = MediaBrowser.Model.MediaInfo.MediaProtocol.Http;
               }
            }
            
            var targetSource = explicitRequest 
                ? mediaSources.FirstOrDefault(ms => ms.Id == mediaSourceId) 
                : mediaSources.FirstOrDefault();
            if (targetSource == null) targetSource = mediaSources.First(); // Fallback

            _logger.LogInformation(
                "⚪ [Cavea.Stream] GetMediaStreams: ItemId={ItemId}, MediaSourceId={SourceId}, Client={Client}, User={User}",
                itemId,
                targetSource.Id,
                client ?? "native",
                currentUser?.Username ?? "anonymous"
            );

            // 2. Use existing MediaStreams from Jellyfin (no probing)
            var audioStreams = targetSource.MediaStreams
                .Where(s => s.Type == MediaStreamType.Audio)
                .Select(a => new AudioTrackOption
                {
                    Index = a.Index,
                    Title = BuildStreamTitle(a),
                    Language = (string?)a.Language,
                    Codec = (string?)a.Codec,
                    Channels = a.Channels,
                    Bitrate = (long?)a.BitRate,
                    IsDefault = a.IsDefault,
                })
                .ToList();

            _logger.LogInformation("⚪ [Cavea.Stream] Found {Count} audio streams in MediaSource from manager.", audioStreams.Count);

            // 3. Get embedded subtitles from Jellyfin
            // For web clients: skip embedded subs - they can only use external Gelato subs or burn-in
            List<object> embeddedSubs;
            if (isWebClient)
            {
                _logger.LogInformation("⚪ [Cavea.Stream] Web client - skipping embedded subtitles (use burn-in)");
                embeddedSubs = new List<object>();
            }
            else
            {
                embeddedSubs = targetSource.MediaStreams
                    .Where(s => s.Type == MediaStreamType.Subtitle)
                    .Select(s => (object)new SubtitleTrackOption
                    {
                        Index = s.Index,
                        Title = BuildStreamTitle(s),
                        Language = (string?)s.Language,
                        Codec = (string?)s.Codec,
                        IsForced = s.IsForced,
                        IsDefault = s.IsDefault,
                        IsExternal = s.IsExternal,
                        Url = !string.IsNullOrEmpty(s.DeliveryUrl) ? s.DeliveryUrl : (string?)null
                    })
                    .ToList();
                _logger.LogInformation("⚪ [Cavea.Stream] Found {Count} embedded subtitle streams.", embeddedSubs.Count);
            }

            // Some first-load requests race probing. Retry once with the same user context.
            if (audioStreams.Count == 0 && embeddedSubs.Count == 0)
            {
                _logger.LogInformation("⚪ [Cavea.Stream] No track metadata on first pass, retrying media source fetch once.");
                await Task.Delay(200);

                var retrySources = (await _mediaSourceManager.GetPlaybackMediaSources(item, currentUser, true, true, CancellationToken.None)).ToList();
                if (retrySources.Count > 0)
                {
                    var retryTarget = explicitRequest
                        ? retrySources.FirstOrDefault(ms => ms.Id == mediaSourceId)
                        : retrySources.FirstOrDefault();
                    if (retryTarget != null)
                    {
                        targetSource = retryTarget;
                        audioStreams = targetSource.MediaStreams
                            .Where(s => s.Type == MediaStreamType.Audio)
                            .Select(a => new AudioTrackOption
                            {
                                Index = a.Index,
                                Title = BuildStreamTitle(a),
                                Language = (string?)a.Language,
                                Codec = (string?)a.Codec,
                                Channels = a.Channels,
                                Bitrate = (long?)a.BitRate,
                                IsDefault = a.IsDefault,
                            })
                            .ToList();

                        if (!isWebClient)
                        {
                            embeddedSubs = targetSource.MediaStreams
                                .Where(s => s.Type == MediaStreamType.Subtitle)
                                .Select(s => (object)new SubtitleTrackOption
                                {
                                    Index = s.Index,
                                    Title = BuildStreamTitle(s),
                                    Language = (string?)s.Language,
                                    Codec = (string?)s.Codec,
                                    IsForced = s.IsForced,
                                    IsDefault = s.IsDefault,
                                    IsExternal = s.IsExternal,
                                    Url = !string.IsNullOrEmpty(s.DeliveryUrl) ? s.DeliveryUrl : (string?)null
                                })
                                .ToList();
                        }
                    }
                }
            }


            var allSubs = new List<object>();
            allSubs.Add(new SubtitleTrackOption
            {
                Index = -1,
                Title = "None",
                Language = null,
                Codec = null,
                IsForced = false,
                IsDefault = false,
                IsExternal = false,
                Url = null,
            });
            
            // Add embedded subs first (only for native clients)
            allSubs.AddRange(embeddedSubs);
            
            // If no embedded streams found, try probing if applicable (fallback logic)
            if (
                audioStreams.Count == 0
                && embeddedSubs.Count == 0
                && !string.IsNullOrEmpty(targetSource.Path)
                && (
                    targetSource.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || targetSource.Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                )
            )
            {
               _logger.LogWarning("⚪ [Cavea.Stream] No streams found in MediaSource. Attempting Backup Probing for {Url}", targetSource.Path);
               var url = targetSource.Path;
               if (url.StartsWith("stremio://", StringComparison.OrdinalIgnoreCase))
               {
                   var resolved = await _streamService.ResolveStremioStreamAsync(url);
                   if (!string.IsNullOrEmpty(resolved)) url = resolved;
               }

               if (url != null && !url.StartsWith("stremio://", StringComparison.OrdinalIgnoreCase))
               {
                   var probe = await _streamService.RunFfprobeAsync(url);
                   if (probe != null)
                   {
                       // Merge probe results if needed, but for now we follow the original logic which seemed to return 'data' object.
                       // The original code constructed 'data' but returned anonymous object.
                       // We'll stick to returning what we have + probe data if empty.
                       // Implementation note: The original code returned mixed sources.
                       // Here we only probed if empty.
                       
                       // For Audio
                       audioStreams.AddRange(probe.Audio.Select(a => new AudioTrackOption
                       {
                           Index = a.Index,
                           Title = a.Title ?? $"Audio {a.Index}",
                           Language = a.Language,
                           Codec = a.Codec,
                           Channels = a.Channels,
                           Bitrate = a.Bitrate,
                           IsDefault = false,
                       }));

                       // For Subs (Embedded)
                       allSubs.AddRange(probe.Subtitles.Select(s => new SubtitleTrackOption
                       {
                           Index = s.Index,
                           Title = s.Title ?? $"Subtitle {s.Index}",
                           Language = s.Language,
                           Codec = s.Codec,
                           IsForced = s.IsForced,
                           IsDefault = s.IsDefault,
                           IsExternal = false,
                           Url = null
                       }).Cast<object>());
                   }
               }
            }

            var preferredAudio = currentUser is null ? null : GetUserStringValue(currentUser, "AudioLanguagePreference");
            var preferredSubtitle = currentUser is null ? null : GetUserStringValue(currentUser, "SubtitleLanguagePreference");
            var selectedAudio = SelectAudioIndex(audioStreams, preferredAudio);
            foreach (var audio in audioStreams)
            {
                audio.Selected = audio.Index == selectedAudio;
            }

            var selectedSubtitle = SelectSubtitleIndex(allSubs.OfType<SubtitleTrackOption>().ToList(), preferredSubtitle);
            foreach (var subtitle in allSubs.OfType<SubtitleTrackOption>())
            {
                subtitle.Selected = subtitle.Index == selectedSubtitle;
            }

            return Ok(new {
                audio = audioStreams,
                subs = allSubs,
                mediaSourceId = targetSource.Id,
                url = targetSource.Path
            });
        }

        private Guid? ResolveCurrentUserId()
        {
            try
            {
                var principal = HttpContext?.User;
                if (principal?.Identity?.IsAuthenticated != true)
                {
                    return null;
                }

                foreach (var claim in principal.Claims)
                {
                    if (!Guid.TryParse(claim.Value, out var userId))
                    {
                        if (claim.Type.EndsWith("nameidentifier", StringComparison.OrdinalIgnoreCase))
                        {
                            _logger.LogDebug("⚪ [Cavea.Stream] NameIdentifier claim was present but non-guid: {Claim}", claim.Value);
                        }
                        continue;
                    }

                    var user = _userManager.GetUserById(userId);
                    if (user != null)
                    {
                        return userId;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "⚪ [Cavea.Stream] Could not resolve current user from claims");
            }

            return null;
        }

        private static string? GetUserStringValue(object user, string propertyName)
        {
            var prop = user.GetType().GetProperty(propertyName);
            return prop?.GetValue(user) as string;
        }

        private static int SelectAudioIndex(List<AudioTrackOption> tracks, string? preferredLanguage)
        {
            if (tracks.Count == 0)
            {
                return -1;
            }

            if (!string.IsNullOrWhiteSpace(preferredLanguage))
            {
                var preferred = NormalizeLanguage(preferredLanguage);
                var preferredMatch = tracks.FirstOrDefault(a => IsLanguageMatch(a.Language, preferred));
                if (preferredMatch != null)
                {
                    return preferredMatch.Index;
                }
            }

            var defaultTrack = tracks.FirstOrDefault(a => a.IsDefault);
            return defaultTrack?.Index ?? tracks[0].Index;
        }

        private static int SelectSubtitleIndex(List<SubtitleTrackOption> tracks, string? preferredLanguage)
        {
            if (tracks.Count == 0)
            {
                return -1;
            }

            if (!string.IsNullOrWhiteSpace(preferredLanguage))
            {
                var preferred = NormalizeLanguage(preferredLanguage);
                var preferredMatch = tracks.FirstOrDefault(s => s.Index >= 0 && IsLanguageMatch(s.Language, preferred));
                if (preferredMatch != null)
                {
                    return preferredMatch.Index;
                }
            }

            var defaultTrack = tracks.FirstOrDefault(s =>
                s.Index >= 0 && ((s.IsDefault == true) || (s.IsForced == true))
            );
            return defaultTrack?.Index ?? -1;
        }

        private static bool IsLanguageMatch(string? actualLanguage, string preferredNormalized)
        {
            if (string.IsNullOrWhiteSpace(actualLanguage))
            {
                return false;
            }

            var actual = NormalizeLanguage(actualLanguage);
            if (string.Equals(actual, preferredNormalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return actual.StartsWith(preferredNormalized, StringComparison.OrdinalIgnoreCase)
                || preferredNormalized.StartsWith(actual, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeLanguage(string language)
        {
            var value = language.Trim().ToLowerInvariant();
            return value switch
            {
                "en" => "eng",
                "es" => "spa",
                "fr" => "fre",
                "de" => "ger",
                "it" => "ita",
                "pt" => "por",
                "ja" => "jpn",
                "zh" => "chi",
                "ru" => "rus",
                "ko" => "kor",
                "ar" => "ara",
                _ => value,
            };
        }

        private sealed class AudioTrackOption
        {
            public int Index { get; set; }
            public string? Title { get; set; }
            public string? Language { get; set; }
            public string? Codec { get; set; }
            public int? Channels { get; set; }
            public long? Bitrate { get; set; }
            public bool IsDefault { get; set; }
            public bool Selected { get; set; }
        }

        private sealed class SubtitleTrackOption
        {
            public int Index { get; set; }
            public string? Title { get; set; }
            public string? Language { get; set; }
            public string? Codec { get; set; }
            public bool? IsForced { get; set; }
            public bool? IsDefault { get; set; }
            public bool IsExternal { get; set; }
            public string? Url { get; set; }
            public bool Selected { get; set; }
        }

        private string BuildStreamTitle(MediaStream stream)
        {
            var title = !string.IsNullOrWhiteSpace(stream.DisplayTitle) ? stream.DisplayTitle : 
                        (!string.IsNullOrWhiteSpace(stream.Title) ? stream.Title : $"{stream.Type} {stream.Index}");
            
            if (!string.IsNullOrEmpty(stream.Language))
             title += $" ({GetLanguageDisplayName(stream.Language)})";
             
             return title;
        }

        private string GetLanguageDisplayName(string langCode)
        {
            try
            {
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
