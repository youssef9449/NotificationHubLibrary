using Microsoft.AspNet.SignalR;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Threading.Tasks;
using System;
using System.Linq;

public class NotificationHub : Hub
{
    // --- Broadcast methods (now active) ---
    public void SendNotificationToAll(string message)
    {
        Clients.All.receiveNotification(message);
    }

    public void SendNotificationToUser(string userId, string message)
    {
        // Send to a specific user by their user identifier (if using ASP.NET Identity, etc.)
        Clients.User(userId).receiveNotification(message);
    }

    // --- Connection management & pending notifications ---
    private static readonly Dictionary<int, List<string>> _userConnections = new Dictionary<int, List<string>>();
    private static readonly object _connectionLock = new object();
    public static string connectionString = "Data Source=192.168.1.9;Initial Catalog=Users;User ID=sa;Password=123;";
    public override Task OnConnected()
    {
        // When a client connects, let everyone know the user list may have changed.
        Clients.All.updateUserList();
        return base.OnConnected();
    }

    public override Task OnDisconnected(bool stopCalled)
    {
        lock (_connectionLock)
        {
            string connectionID = Context.ConnectionId;
            foreach (var userID in _userConnections.Keys.ToList())
            {
                if (_userConnections[userID].Contains(connectionID))
                {
                    _userConnections[userID].Remove(connectionID);
                    if (_userConnections[userID].Count == 0)
                    {
                        _userConnections.Remove(userID);
                    }
                }
            }
        }
        // Notify all clients that the user list should be updated.
        Clients.All.updateUserList();
        return base.OnDisconnected(stopCalled);
    }


    public static List<string> GetConnectionIDsByUserID(int userID)
    {
        lock (_connectionLock)
        {
            if (_userConnections.TryGetValue(userID, out var connections))
            {
                return connections.ToList(); // Return a copy to avoid modification issues
            }
            return new List<string>();
        }
    }

    public void SendMessageNotification(int receiverID, string message)
    {
        // Attempt to send the notification immediately if the user is connected
        var connectionIDs = GetConnectionIDsByUserID(receiverID);
        if (connectionIDs != null && connectionIDs.Count > 0)
        {
            Clients.Clients(connectionIDs).receiveNotification(message);
        }
        else
        {
            // User offline: log the notification in the PendingNotifications table
            Console.WriteLine($"User {receiverID} is not connected. Message will be queued.");
            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                connection.Open();
                using (var cmd = new SqlCommand(
                    "INSERT INTO PendingNotifications (ReceiverID, Message, CreatedAt, IsDelivered) VALUES (@ReceiverID, @Message, @CreatedAt, 0)",
                    connection))
                {
                    cmd.Parameters.AddWithValue("@ReceiverID", receiverID);
                    cmd.Parameters.AddWithValue("@Message", message);
                    cmd.Parameters.AddWithValue("@CreatedAt", DateTime.Now);
                    cmd.ExecuteNonQuery();
                }
            }
        }
    }

    public void RegisterUserConnection(int userID)
    {
        lock (_connectionLock)
        {
            string connectionID = Context.ConnectionId;
            if (!_userConnections.ContainsKey(userID))
            {
                _userConnections[userID] = new List<string>();
            }
            if (!_userConnections[userID].Contains(connectionID))
            {
                _userConnections[userID].Add(connectionID);
            }
        }

        // Notify all clients that the user list should be updated.
        Clients.All.updateUserList();

        // Deliver any pending notifications to this user.
        DeliverPendingNotifications(userID);
    }

    private void DeliverPendingNotifications(int userID)
    {
        try
        {
            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();
                using (var cmd = new SqlCommand(
                    "SELECT NotificationID, Message FROM PendingNotifications " +
                    "WHERE ReceiverID = @ReceiverID AND IsDelivered = 0 " +
                    "ORDER BY CreatedAt ASC", connection))
                {
                    cmd.Parameters.AddWithValue("@ReceiverID", userID);
                    using (var reader = cmd.ExecuteReader())
                    {
                        List<int> deliveredNotificationIDs = new List<int>();
                        while (reader.Read())
                        {
                            int notificationID = reader.GetInt32(0);
                            string message = reader.GetString(1);
                            var connectionIDs = GetConnectionIDsByUserID(userID);
                            if (connectionIDs != null && connectionIDs.Count > 0)
                            {
                                Clients.Clients(connectionIDs).receiveNotification(message);
                                deliveredNotificationIDs.Add(notificationID);
                            }
                        }

                        // Mark the delivered notifications as sent
                        if (deliveredNotificationIDs.Count > 0)
                        {
                            reader.Close();
                            foreach (int notificationID in deliveredNotificationIDs)
                            {
                                using (var updateCmd = new SqlCommand(
                                    "UPDATE PendingNotifications SET IsDelivered = 1, DeliveredAt = @DeliveredAt " +
                                    "WHERE NotificationID = @NotificationID", connection))
                                {
                                    updateCmd.Parameters.AddWithValue("@DeliveredAt", DateTime.Now);
                                    updateCmd.Parameters.AddWithValue("@NotificationID", notificationID);
                                    updateCmd.ExecuteNonQuery();
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error delivering pending notifications: {ex.Message}");
        }
    }
}
