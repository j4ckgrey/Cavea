#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cavea.Services;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Cavea.Tasks
{
    /// <summary>
    /// Scheduled task to sync staged catalog items from Cavea DB to Jellyfin
    /// </summary>
    public class CaveaSyncTask : IScheduledTask
    {
        private readonly ILogger<CaveaSyncTask> _logger;
        private readonly CaveaDbService _dbService;
        private readonly IServiceProvider _serviceProvider;
        private readonly ILibraryManager _libraryManager;
        private readonly ICollectionManager _collectionManager;

        public CaveaSyncTask(
            ILogger<CaveaSyncTask> logger,
            CaveaDbService dbService,
            IServiceProvider serviceProvider,
            ILibraryManager libraryManager,
            ICollectionManager collectionManager)
        {
            _logger = logger;
            _dbService = dbService;
            _serviceProvider = serviceProvider;
            _libraryManager = libraryManager;
            _collectionManager = collectionManager;
        }

        public string Name => "Sync Cavea Staged Items";

        public string Key => "CaveaSyncTask";

        public string Description => "Syncs catalog items from Cavea staging database to Jellyfin collections";

        public string Category => "Cavea";

        public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
        {
            var cfg = Plugin.Instance?.Configuration;
            if (cfg == null || !cfg.UseCaveaStaging)
            {
                _logger.LogDebug("⚪ [CaveaSyncTask] Staging disabled, skipping sync");
                return;
            }

            _logger.LogInformation("⚪ [CaveaSyncTask] Starting sync of staged items");
            progress.Report(0);

            try
            {
                // Get pending items (limit to 20 per run to avoid overload)
                var pendingItems = await _dbService.GetCatalogItemsByStatusAsync("pending", 20);
                
                if (pendingItems.Count == 0)
                {
                    _logger.LogInformation("⚪ [CaveaSyncTask] No pending items to sync");
                    progress.Report(100);
                    return;
                }

                _logger.LogInformation("[CaveaSyncTask] Syncing {Count} pending items", pendingItems.Count);

                var syncedCount = 0;
                var failedCount = 0;

                for (int i = 0; i < pendingItems.Count; i++)
                {
                    if (cancellationToken.IsCancellationRequested) break;

                    var item = pendingItems[i];
                    progress.Report((double)i / pendingItems.Count * 100);

                    try
                    {
                        // Check if item already exists in Jellyfin
                        var existingItem = _libraryManager.GetItemList(new InternalItemsQuery
                        {
                            HasAnyProviderId = new Dictionary<string, string> { { "Imdb", item.ImdbId } },
                            Recursive = true,
                            Limit = 1,
                            IncludeItemTypes = new[] { item.ItemType == "series" ? BaseItemKind.Series : BaseItemKind.Movie }
                        }).FirstOrDefault();

                        string jellyfinItemId;

                        if (existingItem != null)
                        {
                            jellyfinItemId = existingItem.Id.ToString();
                            _logger.LogDebug("[CaveaSyncTask] Item already exists: {Title} ({ImdbId})", item.Title, item.ImdbId);
                        }
                        else
                        {
                            // Import via Gelato
                            jellyfinItemId = await ImportItemViaGelatoAsync(item, cancellationToken);
                            
                            if (string.IsNullOrEmpty(jellyfinItemId))
                            {
                                await _dbService.UpdateCatalogItemStatusAsync(item.Id, "failed", null, "Gelato import returned null");
                                failedCount++;
                                _logger.LogWarning("[CaveaSyncTask] Failed to import: {Title} ({ImdbId})", item.Title, item.ImdbId);
                                continue;
                            }

                            _logger.LogInformation("[CaveaSyncTask] Successfully imported: {Title} ({ImdbId})", item.Title, item.ImdbId);
                        }

                        // Update status to synced
                        await _dbService.UpdateCatalogItemStatusAsync(item.Id, "synced", jellyfinItemId);
                        syncedCount++;

                        // Small delay to prevent DB lock issues
                        await Task.Delay(500, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "[CaveaSyncTask] Error syncing item: {Title} ({ImdbId})", item.Title, item.ImdbId);
                        await _dbService.UpdateCatalogItemStatusAsync(item.Id, "failed", null, ex.Message);
                        failedCount++;
                    }
                }

                _logger.LogInformation(
                    "[CaveaSyncTask] Sync completed. Synced: {Synced}, Failed: {Failed}",
                    syncedCount, failedCount);

                progress.Report(100);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CaveaSyncTask] Sync task failed");
                throw;
            }
        }

        private async Task<string?> ImportItemViaGelatoAsync(CatalogItemInfo item, CancellationToken cancellationToken)
        {
            try
            {
                // Get scoped Gelato references
                using var scope = _serviceProvider.CreateScope();
                
                // Get Gelato plugin via reflection (avoids direct dependency)
                var gelatoAssembly = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Gelato");
                    
                if (gelatoAssembly == null)
                {
                    _logger.LogWarning("⚪ [CaveaSyncTask] Gelato assembly not found");
                    return null;
                }

                var gelatoPluginType = gelatoAssembly.GetType("Gelato.GelatoPlugin");
                if (gelatoPluginType == null)
                {
                    _logger.LogWarning("⚪ [CaveaSyncTask] GelatoPlugin type not found");
                    return null;
                }

                var instanceProp = gelatoPluginType.GetProperty("Instance", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                var gelatoPlugin = instanceProp?.GetValue(null);
                if (gelatoPlugin == null)
                {
                    _logger.LogWarning("⚪ [CaveaSyncTask] Gelato plugin not available");
                    return null;
                }

                var gelatoManager = gelatoPlugin.GetType().GetProperty("Manager")?.GetValue(gelatoPlugin);
                if (gelatoManager == null)
                {
                    _logger.LogWarning("⚪ [CaveaSyncTask] Gelato manager not available");
                    return null;
                }

                var managerType = gelatoManager.GetType();
                var metaTypeEnum = managerType.Assembly.GetType("Gelato.StremioMediaType");
                if (metaTypeEnum == null)
                {
                    _logger.LogWarning("⚪ [CaveaSyncTask] StremioMediaType not found");
                    return null;
                }

                // Get folder path
                var cfgProp = gelatoPlugin.GetType().GetProperty("Configuration");
                var cfg = cfgProp?.GetValue(gelatoPlugin);
                if (cfg == null)
                {
                    _logger.LogWarning("⚪ [CaveaSyncTask] Gelato configuration not available");
                    return null;
                }
                
                var folderPath = item.ItemType == "series" 
                    ? cfg.GetType().GetProperty("SeriesFolder")?.GetValue(cfg) as string
                    : cfg.GetType().GetProperty("MovieFolder")?.GetValue(cfg) as string;

                if (string.IsNullOrEmpty(folderPath))
                {
                    _logger.LogWarning("⚪ [CaveaSyncTask] Folder path not configured");
                    return null;
                }

                // Find parent folder
                var scopedLibraryManager = scope.ServiceProvider.GetRequiredService<ILibraryManager>();
                var parentFolder = scopedLibraryManager.GetItemList(new InternalItemsQuery
                {
                    Path = folderPath,
                    Recursive = false,
                    Limit = 1,
                    IncludeItemTypes = new[] { BaseItemKind.Folder }
                }).OfType<Folder>().FirstOrDefault();

                if (parentFolder == null)
                {
                    _logger.LogWarning("[CaveaSyncTask] Parent folder not found: {Path}", folderPath);
                    return null;
                }

                // Create StremioMeta object
                var metaType = managerType.Assembly.GetType("Gelato.StremioMeta");
                var metaResult = Activator.CreateInstance(metaType!);
                
                metaType.GetProperty("Id")?.SetValue(metaResult, item.ImdbId);
                metaType.GetProperty("Name")?.SetValue(metaResult, item.Title);
                metaType.GetProperty("Poster")?.SetValue(metaResult, item.Poster);
                metaType.GetProperty("Background")?.SetValue(metaResult, item.Background);
                metaType.GetProperty("Description")?.SetValue(metaResult, item.Overview);
                metaType.GetProperty("ImdbRating")?.SetValue(metaResult, item.Rating);
                
                var mediaTypeValue = Enum.Parse(metaTypeEnum, item.ItemType == "series" ? "Series" : "Movie", true);
                metaType.GetProperty("Type")?.SetValue(metaResult, mediaTypeValue);

                // Clear Videos for series
                if (item.ItemType == "series")
                {
                    metaType.GetProperty("Videos")?.SetValue(metaResult, null);
                }

                // Call InsertMeta
                var insertMetaMethod = managerType.GetMethod("InsertMeta");
                if (insertMetaMethod == null)
                {
                    _logger.LogWarning("⚪ [CaveaSyncTask] InsertMeta method not found");
                    return null;
                }

                var insertTask = (Task)insertMetaMethod.Invoke(gelatoManager, new object[]
                {
                    parentFolder,
                    metaResult,
                    Guid.Empty,
                    true,  // allowRemoteRefresh
                    true,  // refreshItem
                    false, // queueRefreshItem
                    cancellationToken
                });

                await insertTask.ConfigureAwait(false);

                // Extract Item.Id from result
                var resultTuple = insertTask.GetType().GetProperty("Result")?.GetValue(insertTask);
                if (resultTuple == null) return null;

                var itemField = resultTuple.GetType().GetField("Item1");
                var importedItem = itemField?.GetValue(resultTuple);

                if (importedItem != null)
                {
                    var idProp = importedItem.GetType().GetProperty("Id");
                    return idProp?.GetValue(importedItem)?.ToString();
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CaveaSyncTask] Failed to import via Gelato: {ImdbId}", item.ImdbId);
                return null;
            }
        }

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            // Run every 5 minutes
            return new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.IntervalTrigger,
                    IntervalTicks = TimeSpan.FromMinutes(5).Ticks
                }
            };
        }
    }
}
