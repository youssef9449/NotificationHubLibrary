using Microsoft.AspNet.SignalR;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;

public class NotificationHub : Hub
{
    private static readonly Dictionary<int, List<string>> _userConnections = new Dictionary<int, List<string>>();
    private static readonly object _connectionLock = new object();
    public static string connectionString = "Data Source=192.168.1.9;Initial Catalog=Users;User ID=sa;Password=123;";

    /// <summary>
    /// When a client connects, broadcast an update.
    /// </summary>
    public override Task OnConnected()
    {
        Clients.All.updateUserList();
        return base.OnConnected();
    }

    /// <summary>
    /// When a client disconnects, remove its connection and broadcast an update.
    /// </summary>
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
        // Broadcast update so that clients refresh their user list.
        Clients.All.updateUserList();
        return base.OnDisconnected(stopCalled);
    }

    /// <summary>
    /// Registers the current user's connection and delivers any pending notifications.
    /// </summary>
    /// <param name="userID">The user ID to register.</param>
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

        // Deliver any pending notifications for this user.
        DeliverPendingNotifications(userID);
    }

    /// <summary>
    /// Called by the client just before disconnecting (if using graceful shutdown).
    /// This method can be invoked to force an update to all clients.
    /// </summary>
    /// <param name="userName">The name of the disconnecting user (optional).</param>
    public void UserDisconnected(string userName)
    {
        // Optionally, update the database or perform other logic here.
        // Then broadcast that the user list should be refreshed.
        Clients.All.updateUserList();
    }

    /// <summary>
    /// Sends a broadcast message to all connected clients.
    /// </summary>
    /// <param name="message">The message to send.</param>
    public void SendNotificationToAll(string message)
    {
        Clients.All.receiveNotification(message);
    }

    /// <summary>
    /// Sends a notification to a specific user. If the user is offline, it queues the message.
    /// </summary>
    /// <param name="receiverID">The receiver's user ID.</param>
    /// <param name="message">The message text.</param>
    public void SendMessageNotification(int receiverID, string message)
    {
        var connectionIDs = GetConnectionIDsByUserID(receiverID);
        if (connectionIDs != null && connectionIDs.Count > 0)
        {
            Clients.Clients(connectionIDs).receiveNotification(message);
        }
        else
        {
            // User is offline: log or queue the notification.
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

    /// <summary>
    /// Retrieves the connection IDs associated with a specific user.
    /// </summary>
    public static List<string> GetConnectionIDsByUserID(int userID)
    {
        lock (_connectionLock)
        {
            if (_userConnections.TryGetValue(userID, out var connections))
            {
                return connections.ToList();
            }
            return new List<string>();
        }
    }

    /// <summary>
    /// Delivers any pending notifications stored in the database to the connected user.
    /// </summary>
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
