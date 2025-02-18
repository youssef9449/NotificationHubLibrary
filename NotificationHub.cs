using Microsoft.AspNet.SignalR;


namespace NotificationHubLibrary
{
    public class NotificationHub : Hub
    {
        // This method pushes a notification message to all connected clients.
        public void SendNotification(string message)
        {
            Clients.All.receiveNotification(message);
        }
    }
}
