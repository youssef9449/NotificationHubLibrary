using Microsoft.Owin;
using Owin;

[assembly: OwinStartup(typeof(NotificationHubLibrary.Startup))]
namespace NotificationHubLibrary
{
    public class Startup
    {
        public void Configuration(IAppBuilder app)
        {
            // Allow cross-origin requests if necessary
            app.UseCors(Microsoft.Owin.Cors.CorsOptions.AllowAll);
            // Map SignalR hubs to the default "/signalr" endpoint
            app.MapSignalR();
        }
    }
}
