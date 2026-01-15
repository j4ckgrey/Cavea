using Cavea.Filters;
using Cavea.Services;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Cavea
{
    /// <summary>
    /// Service registrator for Cavea plugin.
    /// Registers action filters and other services with the Jellyfin dependency injection container.
    /// </summary>
    public class ServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection services, MediaBrowser.Controller.IServerApplicationHost host)
        {
            // Register the SearchActionFilter as a singleton
            services.AddSingleton<SearchActionFilter>();

            // Register IStartupFilter to inject UI middleware
            services.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, Cavea.Api.UIInjectionStartupFilter>();

            // Register Cavea Database Service as a singleton
            services.AddSingleton<CaveaDbService>();
            
            // Register Refactored Services

            services.AddSingleton<StreamService>();
            services.AddSingleton<SubtitleService>();

            // Register Scheduled Tasks
            services.AddSingleton<MediaBrowser.Model.Tasks.IScheduledTask, Cavea.Tasks.CatalogSyncTask>();
        }
    }
}
