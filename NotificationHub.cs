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
    //public static string connectionString = "Data Source=192.168.1.114;Initial Catalog=Users;Trusted_Connection=True;";


    public override Task OnConnected()
    {
        Clients.All.updateUserList();
        return base.OnConnected();
    }
    public async Task DisconnectUser(int userId)
    {
        lock (_connectionLock)
        {
            if (_userConnections.ContainsKey(userId))
            {
                _userConnections.Remove(userId);
            }
        }

        await UpdateUserConnectionStatusAsync(userId, false);
        Clients.All.updateUserList();
    }

    public override async Task OnDisconnected(bool stopCalled)
    {
        string connectionID = Context.ConnectionId;
        int? disconnectedUserID = null;

        lock (_connectionLock)
        {
            foreach (var userID in _userConnections.Keys.ToList())
            {
                if (_userConnections[userID].Contains(connectionID))
                {
                    _userConnections[userID].Remove(connectionID);
                    if (_userConnections[userID].Count == 0)
                    {
                        disconnectedUserID = userID;
                        _userConnections.Remove(userID);
                    }
                    break;
                }
            }
        }

        if (disconnectedUserID.HasValue)
        {
            await DisconnectUser(disconnectedUserID.Value); // Reuse the centralized method
        }
        else
        {
            Clients.All.updateUserList();       // Still notify if no user found
        }

        await base.OnDisconnected(stopCalled);
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
                Console.WriteLine($"Registered connection {connectionID} for user {userID}");
            }
        }
        Clients.All.updateUserList();
        DeliverPendingChatMessages(userID);
    }

    private async Task UpdateUserConnectionStatusAsync(int userId, bool isConnected)
    {
        try
        {
            using (var connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();
                using (var cmd = new SqlCommand(
                    "UPDATE Users SET isConnected = @IsConnected WHERE UserID = @UserID",
                    connection))
                {
                    cmd.Parameters.AddWithValue("@IsConnected", isConnected ? 1 : 0);
                    cmd.Parameters.AddWithValue("@UserID", userId);
                    await cmd.ExecuteNonQueryAsync();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error updating user connection status: {ex.Message}\nStackTrace: {ex.StackTrace}");
        }
    }
    public List<int> GetConnectedUserIDs()
    {
        lock (_connectionLock)
        {
            return _userConnections.Keys.ToList();
        }
    }

    /* public void RegisterUserConnection(int userID)
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
         Clients.All.updateUserList();
         DeliverPendingChatMessages(userID);
         UpdateUserConnectionStatus(userID, true); // Add this
     }*/

    public async Task SendNotification(List<int> receiverIDs, string message, int? senderID = null, bool isChatMessage = false, bool queueIfOffline = false)
    {

        if (receiverIDs == null || !receiverIDs.Any())
        {
            if (isChatMessage && senderID.HasValue)
            {
                Console.WriteLine($"Broadcasting chat message from {senderID.Value} to all: {message}");
                Clients.All.receivePendingMessage(senderID.Value, message);

            }
            else
            {
                Console.WriteLine($"Broadcasting general notification to all: {message}");
                Clients.All.receiveGeneralNotification(senderID.Value, message);

                await InsertGeneralNotificationAsync(null, senderID, message); // Insert for all users
            }
            return;
        }

        foreach (int receiverID in receiverIDs.Distinct())
        {
            var connectionIDs = GetConnectionIDsByUserID(receiverID);

            if (connectionIDs != null && connectionIDs.Count > 0)
            {
                if (isChatMessage && senderID.HasValue)
                {
                    //Console.WriteLine($"Sending chat message from {senderID.Value} to {receiverID}: {message}");
                    Clients.Clients(connectionIDs).receivePendingMessage(senderID.Value, message);
                }
                else
                {
                    Console.WriteLine($"Sending general notification to {receiverID}: {message}");
                    Clients.Clients(connectionIDs).receiveGeneralNotification(senderID.Value, message);
                }
            }
            else if (queueIfOffline)
            {
                if (isChatMessage)
                {
                    using (SqlConnection connection = new SqlConnection(connectionString))
                    {
                        await connection.OpenAsync();
                        using (var cmd = new SqlCommand(
                            "INSERT INTO PendingChatMessages (SenderID, ReceiverID, Message, CreatedAt, IsDelivered) " +
                            "VALUES (@SenderID, @ReceiverID, @Message, @CreatedAt, 0)",
                            connection))
                        {
                            cmd.Parameters.AddWithValue("@SenderID", senderID ?? (object)DBNull.Value);
                            cmd.Parameters.AddWithValue("@ReceiverID", receiverID);
                            cmd.Parameters.AddWithValue("@Message", message);
                            cmd.Parameters.AddWithValue("@CreatedAt", DateTime.Now);
                            await cmd.ExecuteNonQueryAsync();
                            Console.WriteLine($"Queued chat message for offline user {receiverID} from {senderID}");
                        }
                    }
                }
                else
                {
                    // Queue general notification for offline users
                    await InsertGeneralNotificationAsync(receiverID, senderID, message);
                }
            }
        }
    }

    private async Task InsertGeneralNotificationAsync(int? receiverID, int? senderID, string messageText)
    {
        try
        {
            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                await connection.OpenAsync();
                using (var cmd = new SqlCommand(
                    "INSERT INTO Notifications (ReceiverID, SenderID, MessageText, IsSeen, Timestamp) " +
                    "VALUES (@ReceiverID, @SenderID, @MessageText, 0, GETDATE())",
                    connection))
                {
                    cmd.Parameters.AddWithValue("@ReceiverID", (object)receiverID ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@SenderID", (object)senderID ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@MessageText", messageText);
                    await cmd.ExecuteNonQueryAsync();
                    Console.WriteLine($"Inserted general notification for ReceiverID: {receiverID}, SenderID: {senderID}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error inserting notification: {ex.Message}\nStackTrace: {ex.StackTrace}");
        }
    }
    public static List<string> GetConnectionIDsByUserID(int userID)
    {
        lock (_connectionLock)
        {
            return _userConnections.TryGetValue(userID, out var connections) ? connections.ToList() : new List<string>();
        }
    }

    public Dictionary<int, PendingMessageData> GetPendingMessageCounts(int receiverId)
    {
        var result = new Dictionary<int, PendingMessageData>();
        using (var connection = new SqlConnection(connectionString))
        {
            connection.Open();
            using (var cmd = new SqlCommand(
                @"SELECT 
                SenderID AS SenderId, 
                COUNT(*) AS MessageCount, 
                COALESCE(STRING_AGG(Message, '|'), '') AS Messages 
              FROM PendingChatMessages 
              WHERE ReceiverID = @ReceiverID AND IsDelivered = 0 
              GROUP BY SenderID",
                connection))
            {
                cmd.Parameters.AddWithValue("@ReceiverID", receiverId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var senderId = reader.GetInt32(reader.GetOrdinal("SenderId"));
                        var count = reader.GetInt32(reader.GetOrdinal("MessageCount"));
                        var messages = reader.GetString(reader.GetOrdinal("Messages"));
                        result[senderId] = new PendingMessageData
                        {
                            Count = count,
                            Messages = messages.Split('|').ToList()
                        };
                    }
                }
            }
        }
        return result;
    }

    private void DeliverPendingChatMessages(int userID)
    {
        try
        {
            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();
                using (var cmd = new SqlCommand(
                    "SELECT NotificationID, SenderID, Message FROM PendingChatMessages " +
                    "WHERE ReceiverID = @ReceiverID AND IsDelivered = 0 " +
                    "ORDER BY CreatedAt ASC", connection))
                {
                    cmd.Parameters.AddWithValue("@ReceiverID", userID);
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            int senderID = reader.GetInt32(1);
                            string message = reader.GetString(2);
                            Clients.Clients(GetConnectionIDsByUserID(userID)).receivePendingMessage(senderID, message);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error delivering pending chat messages: {ex.Message}");
        }
    }

    public List<string> GetPendingMessages(int receiverId, int senderId)
    {
        var messages = new List<string>();
        using (var connection = new SqlConnection(connectionString))
        {
            connection.Open();
            using (var cmd = new SqlCommand(
                "SELECT Message FROM PendingChatMessages " +
                "WHERE ReceiverId = @ReceiverId AND SenderId = @SenderId",
                connection))
            {
                cmd.Parameters.AddWithValue("@ReceiverId", receiverId);
                cmd.Parameters.AddWithValue("@SenderId", senderId);
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        messages.Add(reader["Message"].ToString());
                    }
                }
            }
        }
        return messages;
    }

    public void MarkMessagesAsDelivered(int senderId, int receiverId)
    {
        try
        {
            using (var connection = new SqlConnection(connectionString))
            {
                connection.Open();
                using (var cmd = new SqlCommand(
                    @"UPDATE PendingChatMessages 
                  SET IsDelivered = 1, 
                      DeliveredAt = GETDATE() 
                  WHERE SenderID = @SenderID 
                    AND ReceiverID = @ReceiverID 
                    AND IsDelivered = 0",
                    connection))
                {
                    cmd.Parameters.AddWithValue("@SenderID", senderId);
                    cmd.Parameters.AddWithValue("@ReceiverID", receiverId);
                    cmd.ExecuteNonQuery();
                }
            }
            // Notify clients to update message counts.
            Clients.All.updateMessageCounts(receiverId, senderId, 0);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in MarkMessagesAsDelivered: {ex.Message}\nStackTrace: {ex.StackTrace}");
            throw; // Propagate the error to the client for debugging.
        }
    }

    public class PendingMessageData
    {
        public int Count { get; set; }
        public List<string> Messages { get; set; }
    }
}