using Microsoft.AspNet.SignalR;
using NotificationHubLibrary;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Linq;
using System.Threading.Tasks;

public class NotificationHub : Hub
{
    private static readonly Dictionary<int, List<string>> _userConnections = new Dictionary<int, List<string>>();
    private static readonly object _connectionLock = new object();

    private static readonly string[] hrRoles = new string[] { "HR" }; // Adjust based on your system

    #region Chat Methods
    public async Task SendNotification(List<int> receiverIDs, string message, int? senderID = null, bool isChatMessage = false, bool queueIfOffline = false)
    {
        if (isChatMessage && senderID.HasValue)
        {
            // Get the chat partner (receiver) ID, ensuring it excludes the sender
            int chatPartnerId = receiverIDs.FirstOrDefault(id => id != senderID.Value);
            if (chatPartnerId == 0 || chatPartnerId == senderID.Value)
            {
                Console.WriteLine($"Invalid chat partner ID for sender {senderID}. Skipping message.");
                return;
            }

            var targetIDs = new List<int> { chatPartnerId };
            foreach (int targetID in targetIDs.Distinct())
            {
                var connectionIDs = GetConnectionIDsByUserID(targetID);
                if (connectionIDs != null && connectionIDs.Any())
                {
                    // Console.WriteLine($"Sending chat message from {senderID} to {targetID}: {message}");
                    Clients.Clients(connectionIDs).receivePendingMessage(senderID.Value, message); // Send only senderId and message
                }
                else if (queueIfOffline)
                {
                    using (SqlConnection connection = Database.getConnection())
                    {
                        await connection.OpenAsync();
                        using (var cmd = new SqlCommand(
                            "INSERT INTO PendingChatMessages (SenderID, ReceiverID, Message, CreatedAt, IsDelivered) VALUES (@SenderID, @ReceiverID, @Message, @CreatedAt, 0)",
                            connection))
                        {
                            cmd.Parameters.AddWithValue("@SenderID", senderID.Value);
                            cmd.Parameters.AddWithValue("@ReceiverID", targetID);
                            cmd.Parameters.AddWithValue("@Message", message);
                            cmd.Parameters.AddWithValue("@CreatedAt", DateTime.Now);
                            await cmd.ExecuteNonQueryAsync();
                            //Console.WriteLine($"Queued chat message for offline user {targetID} from {senderID}");
                        }
                    }
                }
            }
        }
        else if (receiverIDs != null && receiverIDs.Any())
        {
            // Handle broadcast to specific users (non-chat notification)
            foreach (int receiverID in receiverIDs.Distinct())
            {
                if (senderID.HasValue && receiverID == senderID.Value)
                {
                    // Console.WriteLine($"Skipping broadcast for sender {senderID} to {receiverID}");
                    continue;
                }
                var connectionIDs = GetConnectionIDsByUserID(receiverID);
                if (connectionIDs != null && connectionIDs.Any())
                {
                    Console.WriteLine($"Sending broadcast notification from {senderID ?? -1} to {receiverID}: {message}");
                    Clients.Clients(connectionIDs).receiveGeneralNotification(senderID.Value, message);
                }
            }
        }
        else
        {
            // Handle broadcast to all online users (non-chat notification)
            Console.WriteLine($"Broadcasting general notification from {senderID ?? -1} to all online users: {message}");
            var onlineUserIds = GetConnectedUserIDs();
            if (senderID.HasValue)
            {
                onlineUserIds = onlineUserIds.Except(new[] { senderID.Value }).ToList();
            }
            foreach (int userId in onlineUserIds)
            {
                var connectionIDs = GetConnectionIDsByUserID(userId);
                if (connectionIDs != null && connectionIDs.Any())
                {
                    Console.WriteLine($"Sending to online user {userId} with connections: {string.Join(", ", connectionIDs)}");
                    Clients.Clients(connectionIDs).receiveGeneralNotification(senderID.Value, message);
                }
            }
            await InsertGeneralNotificationForOnlineUsers(onlineUserIds, senderID, message);
        }
    }
    public Dictionary<int, PendingMessageData> GetPendingMessageCounts(int receiverId)
    {
        var result = new Dictionary<int, PendingMessageData>();
        using (var connection = Database.getConnection())
        {
            connection.Open();
            using (var cmd = new SqlCommand(
                        @"SELECT SenderID, COUNT(*) AS MessageCount, COALESCE(STRING_AGG(Message, '|'), '') AS Messages 
              FROM PendingChatMessages 
              WHERE ReceiverID = @ReceiverID AND IsDelivered = 0 AND SenderID != @ReceiverID 
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
            using (var connection = Database.getConnection())
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
        using (var connection = Database.getConnection())
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
            using (var connection = Database.getConnection())
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
    public void MarkMessagesAsRead(int senderId, int receiverId)
    {
        try
        {
            using (var connection = Database.getConnection())
            {
                connection.Open();
                using (var cmd = new SqlCommand(
                    @"UPDATE Messages 
                  SET IsRead = 1, IsDelivered = 1
                  WHERE SenderID = @SenderID 
                    AND ReceiverID = @ReceiverID 
                    AND IsRead = 0",
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
            Console.WriteLine($"Error in MarkMessagesAsRead: {ex.Message}\nStackTrace: {ex.StackTrace}");
            throw; // Propagate the error to the client for debugging.
        }
    }
    public List<int> GetConnectedUserIDs()
    {
        lock (_connectionLock)
        {
            return _userConnections.Keys.ToList();
        }
    }
    // New method to send global chat messages
    //public async Task SendGlobalChatMessage(string messageId, string message, int senderId)
    //{
    //    // Broadcast the message to all users in the "global" group
    //    await Clients.Group("global").receiveGlobalChatMessage(messageId, senderId, message);
    //    // Save the message to the database
    //    await SaveGlobalChatMessage(messageId, senderId, message);
    //}
    //// Helper method to save messages to the database
    //private async Task SaveGlobalChatMessage(string messageId, int senderId, string message)
    //{
    //    using (SqlConnection connection = Database.getConnection())
    //    {
    //        await connection.OpenAsync();
    //        string query = "INSERT INTO GlobalChatMessages (MessageID, SenderID, Message, Timestamp) " +
    //                      "VALUES (@MessageID, @SenderID, @Message, @Timestamp)";
    //        using (SqlCommand cmd = new SqlCommand(query, connection))
    //        {
    //            cmd.Parameters.AddWithValue("@MessageID", messageId);
    //            cmd.Parameters.AddWithValue("@SenderID", senderId);
    //            cmd.Parameters.AddWithValue("@Message", message);
    //            cmd.Parameters.AddWithValue("@Timestamp", DateTime.Now);
    //            await cmd.ExecuteNonQueryAsync();
    //        }
    //    }
    //}
    //public async Task SendDepartmentChatMessage(string messageId, string message, int senderId, string department)
    //{
    //    await Clients.Group(department).receiveDepartmentChatMessage(messageId, senderId, message);
    //    await SaveDepartmentChatMessage(messageId, senderId, message, department);
    //}
    //private async Task SaveDepartmentChatMessage(string messageId, int senderId, string message, string department)
    //{
    //    using (SqlConnection connection = Database.getConnection())
    //    {
    //        await connection.OpenAsync();
    //        string query = "INSERT INTO DepartmentChatMessages (MessageID, SenderID, Message, Department, Timestamp) " +
    //                      "VALUES (@MessageID, @SenderID, @Message, @Department, @Timestamp)";
    //        using (SqlCommand cmd = new SqlCommand(query, connection))
    //        {
    //            cmd.Parameters.AddWithValue("@MessageID", messageId);
    //            cmd.Parameters.AddWithValue("@SenderID", senderId);
    //            cmd.Parameters.AddWithValue("@Message", message);
    //            cmd.Parameters.AddWithValue("@Department", department);
    //            cmd.Parameters.AddWithValue("@Timestamp", DateTime.Now);
    //            await cmd.ExecuteNonQueryAsync();
    //        }
    //    }
    //}
    #endregion

    #region User Connection Methods
    public override Task OnConnected()
    {
        // Avoid full refresh here, handled in RegisterUserConnection
        return base.OnConnected();
    }
    // 1. First, in your Hub class, modify DisconnectUser method:
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
        // Notice the change here - we now call updateUserStatus instead of updateUserList
        Clients.All.updateUserStatus(userId, false);
    }
    public void RegisterUserConnection(int userId)
    {
        lock (_connectionLock)
        {
            string connectionId = Context.ConnectionId;
            if (!_userConnections.ContainsKey(userId))
            {
                _userConnections[userId] = new List<string>();
            }
            if (!_userConnections[userId].Contains(connectionId))
            {
                _userConnections[userId].Add(connectionId);
                //    Console.WriteLine($"Registered connection {connectionId} for user {userId}");
            }
        }

        // Add to the "global" group for all users
        Groups.Add(Context.ConnectionId, "global");
        // Console.WriteLine($"User {userId} added to 'global' group");

        DeliverPendingChatMessages(userId);

        using (var connection = Database.getConnection())
        {
            connection.Open();
            string query = "SELECT Role, Department FROM Users WHERE UserID = @UserID";
            using (var cmd = new SqlCommand(query, connection))
            {
                cmd.Parameters.AddWithValue("@UserID", userId);
                using (var reader = cmd.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        string role = reader.GetString(0);
                        string department = reader.IsDBNull(1) ? "" : reader.GetString(1).Trim();

                        //  // Add to department-specific groups (split by " - " if multiple departments)
                        //  if (!string.IsNullOrEmpty(department))
                        //  {
                        //      string[] departments = department.Split(new[] { " - " }, StringSplitOptions.RemoveEmptyEntries);
                        //      foreach (string dept in departments)
                        //      {
                        //          string deptGroup = dept.Trim();
                        //          if (!string.IsNullOrEmpty(deptGroup))
                        //          {
                        //              Groups.Add(Context.ConnectionId, deptGroup);
                        ////              Console.WriteLine($"User {userId} added to '{deptGroup}' group");
                        //          }
                        //      }
                        //  }

                        // Add users to "HR" group if their role or department is "Human Resources"
                        if (hrRoles.Any(r => role.Contains(r)) || department.Equals("Human Resources", StringComparison.OrdinalIgnoreCase))
                        {
                            Groups.Add(Context.ConnectionId, "HR");
                            //   Console.WriteLine($"User {userId} added to 'HR' group");
                        }

                        // Preserve role-based group logic for managers and team leaders
                        if (role == "Manager" || role == "Team Leader")
                        {
                            string[] managerDepartments = department.Split(new[] { " - " }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (string dept in managerDepartments)
                            {
                                string deptGroup = "Manager_" + dept.Trim();
                                if (!string.IsNullOrEmpty(dept))
                                {
                                    Groups.Add(Context.ConnectionId, deptGroup);
                                    //           Console.WriteLine($"User {userId} added to '{deptGroup}' group");
                                }
                            }
                        }
                    }
                }
            }
        }

        Clients.AllExcept(Context.ConnectionId).updateUserStatus(userId, true);
    }
    public override Task OnDisconnected(bool stopCalled)
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
            DisconnectUser(disconnectedUserID.Value); // This already calls updateUserStatus
                                                      // Remove this redundant line:
                                                      // Clients.All.updateUserStatus(disconnectedUserID.Value, false);
        }
        return base.OnDisconnected(stopCalled);
    }
    private async Task UpdateUserConnectionStatusAsync(int userId, bool isConnected)
    {
        try
        {
            using (var connection = Database.getConnection())
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
    #endregion

    #region Requests Methods
    // Update the NotifyNewRequest method to pass the requester's ID
    public async Task NotifyNewRequest(string department, int requesterId)
    {
        // Notify HR and manager groups to refresh requests and notifications
        // while passing the ID of the user who created the request
        await Clients.Group("HR").RefreshRequests();
        await Clients.Group("Manager_" + department).RefreshRequests();
        await Clients.Group("HR").RefreshNotifications(requesterId);
        await Clients.Group("Manager_" + department).RefreshNotifications(requesterId);
    }
    public async Task NotifyNewRequestWithDetails(string department, int requestId, string userFullName, string requestType, string requestFromDay, string requestStatus, int requesterId)
    {
        await Clients.Group("HR").AddNewRequest(requestId, userFullName, requestType, requestFromDay, requestStatus);
        await Clients.Group("Manager_" + department).AddNewRequest(requestId, userFullName, requestType, requestFromDay, requestStatus);
        await Clients.Group("HR").RefreshNotifications(requesterId);
        await Clients.Group("Manager_" + department).RefreshNotifications(requesterId);
    }
    public void NotifyAffectedUsers(List<int> userIds)
    {
        // Get the connection ID of the caller to avoid notifying the sender
        string callerConnectionId = Context.ConnectionId;

        foreach (int userId in userIds)
        {
            var connectionIDs = GetConnectionIDsByUserID(userId);
            if (connectionIDs != null && connectionIDs.Count > 0)
            {
                // Filter out the caller's connection ID to prevent self-notification
                var filteredConnectionIDs = connectionIDs.Where(id => id != callerConnectionId).ToList();

                if (filteredConnectionIDs.Any())
                {
                    //Console.WriteLine($"Notifying user {userId} to refresh requests.");
                    Clients.Clients(filteredConnectionIDs).RefreshRequests();
                }
            }
        }
    }
    #endregion

    #region General Notification Methods

    private async Task InsertGeneralNotificationForOnlineUsers(List<int> onlineUserIds, int? senderID, string message)
    {
        try
        {
            using (SqlConnection connection = Database.getConnection())
            {
                await connection.OpenAsync();
                string query = @"
                INSERT INTO Notifications (ReceiverID, SenderID, MessageText, IsSeen, Timestamp)
                VALUES (@ReceiverID, @SenderID, @MessageText, 0, GETDATE())";
                using (SqlCommand cmd = new SqlCommand(query, connection))
                {
                    cmd.Parameters.AddWithValue("@SenderID", (object)senderID ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@MessageText", message);

                    foreach (int userId in onlineUserIds)
                    {
                        cmd.Parameters.Clear(); // Clear previous parameters
                        cmd.Parameters.AddWithValue("@ReceiverID", userId);
                        cmd.Parameters.AddWithValue("@SenderID", (object)senderID ?? DBNull.Value);
                        cmd.Parameters.AddWithValue("@MessageText", message);
                        await cmd.ExecuteNonQueryAsync();
                        Console.WriteLine($"Inserted general notification for online user {userId}, SenderID: {senderID}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error inserting notification for online users: {ex.Message}\nStackTrace: {ex.StackTrace}");
        }
    }
    private async Task InsertGeneralNotificationAsync(int? receiverID, int? senderID, string messageText)
    {
        try
        {
            using (SqlConnection connection = Database.getConnection())
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
            if (_userConnections.TryGetValue(userID, out var connections))
            {
         //       Console.WriteLine($"Connections for {userID}: {string.Join(", ", connections)}");
                return connections.ToList();
            }
            return new List<string>();
        }
    }

    #endregion


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

    public class PendingMessageData
    {
        public int Count { get; set; }
        public List<string> Messages { get; set; }
    }
}