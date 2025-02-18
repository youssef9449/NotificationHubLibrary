using Microsoft.AspNet.SignalR;

public class NotificationHub : Hub
{
    // Method to send notifications to all clients
    public void SendNotificationToAll(string message)
    {
        Clients.All.receiveNotification(message);  // Sends the message to all connected clients
    }

    // You can also send to specific clients if needed
    public void SendNotificationToUser(string userId, string message)
    {
        Clients.User(userId).receiveNotification(message);  // Sends to a specific user
    }
}