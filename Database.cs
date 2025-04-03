using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace NotificationHubLibrary
{
    public static class Database
    {
        private static string connectionString;

        static Database()
        {
            connectionString = buildConnectionString();

        }

        private const string ConfigFileName = "ConnectionConfig.ini";
        static string exeDirectory = AppDomain.CurrentDomain.BaseDirectory;
        static string iniFilePath = Path.Combine(exeDirectory, ConfigFileName);
        static IniFile iniFile = new IniFile(iniFilePath);

        public static string buildConnectionString()
        {
            // Read connection method from the config file
            string connectionMethod = iniFile.Read("Database", "ConnectionMethod", "").ToLower();
            if (string.IsNullOrWhiteSpace(connectionMethod) || (connectionMethod != "namedpipes" && connectionMethod != "tcp"))
            {
                return null;
            }

            // Check for server IP
            string serverIP = iniFile.Read("Database", "ServerIP", "");
            if (string.IsNullOrWhiteSpace(serverIP))
            {

                return null;
            }

            // Check for database name
            string databaseName = iniFile.Read("Database", "Database", "");
            if (string.IsNullOrWhiteSpace(databaseName))
            {
                return null;
            }

            // Check for trusted connection
            string trustedConnection = iniFile.Read("Database", "Trusted Connection", "").ToLower();
            if (string.IsNullOrWhiteSpace(trustedConnection) || (trustedConnection != "true" && trustedConnection != "false"))
            {
                return null;
            }

            // Initialize connection string
            string connectionString = null;

            if (trustedConnection == "true")
            {
                if (connectionMethod == "namedpipes")
                {
                    connectionString = $"Data Source={serverIP};Initial Catalog={databaseName};Trusted_Connection=True;";
                }
                else if (connectionMethod == "tcp")
                {
                    string port = iniFile.Read("Database", "Port", "1433"); // Default port is 1433
                    connectionString = $"Data Source={serverIP},{port};Network Library=DBMSSOCN;Initial Catalog={databaseName};Trusted_Connection=True;";
                }
            }
            else
            {
                string userId = iniFile.Read("Database", "User Id", "");
                string password = iniFile.Read("Database", "Password", "");

                if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(password))
                {
                    return null;
                }

                if (connectionMethod == "namedpipes")
                {
                    connectionString = $"Data Source={serverIP};Initial Catalog={databaseName};User Id={userId};Password={password};";
                }
                else if (connectionMethod == "tcp")
                {
                    string port = iniFile.Read("Database", "Port", "1433"); // Default port is 1433
                    connectionString = $"Data Source={serverIP},{port};Network Library=DBMSSOCN;Initial Catalog={databaseName};User Id={userId};Password={password};";
                }
            }

            return connectionString;
        }
        public static SqlConnection getConnection()
        {
            return new SqlConnection(connectionString);
        }
    }
    public class IniFile
    {
        private readonly string path;

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern long WritePrivateProfileString(string section, string key, string value, string filePath);

        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        private static extern int GetPrivateProfileString(string section, string key, string defaultValue, StringBuilder value, int size, string filePath);

        public IniFile(string iniPath)
        {
            path = iniPath;
        }

        // Method to read a specific key within a section
        public string Read(string section, string key, string defaultValue = "")
        {
            var result = new StringBuilder(255);
            GetPrivateProfileString(section, key, defaultValue, result, 255, path);
            return result.ToString();
        }

        public List<string> ReadSectionValues(string sectionName)
        {
            List<string> values = new List<string>();
            bool isInSection = false;

            foreach (var line in File.ReadLines(path))
            {
                string trimmedLine = line.Trim();

                // Check if we've reached the target section
                if (trimmedLine.StartsWith("[") && trimmedLine.EndsWith("]"))
                {
                    isInSection = trimmedLine.Equals($"[{sectionName}]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                // If we're inside the section, add each non-empty line as a value
                if (isInSection && !string.IsNullOrWhiteSpace(trimmedLine))
                {
                    values.Add(trimmedLine);
                }
            }

            return values;
        }

    }
}
