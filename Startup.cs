using Microsoft.Owin;
using Owin;

[assembly: OwinStartup(typeof(NotificationHubLibrary.Startup))]
namespace NotificationHubLibrary
{
    public class Startup
    {
        public void Configuration(IAppBuilder app)
        {
            // Enable CORS if needed.
            app.UseCors(Microsoft.Owin.Cors.CorsOptions.AllowAll);
            // Map SignalR hubs to the default "/signalr" route.
            app.MapSignalR();
        }
    }
}
