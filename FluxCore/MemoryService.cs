using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;
using System.Threading.Tasks;
using System.Linq; // Добавили LINQ для переворота списка

namespace FluxCore
{
    public class MemoryService
    {
        private readonly string _dbPath;

        public MemoryService()
        {
            _dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "flux_memory.db");
            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
            {
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText =
                @"
                    CREATE TABLE IF NOT EXISTS Memories (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Content TEXT NOT NULL,
                        SourceApp TEXT,
                        Timestamp TEXT
                    );
                ";
                command.ExecuteNonQuery();
            }
        }

        public async Task Save(string content, string appName)
        {
            if (string.IsNullOrWhiteSpace(content)) return;
            using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
            {
                await connection.OpenAsync();
                var command = connection.CreateCommand();
                // Сохраняем ПОЛНУЮ дату (с годом и днем)
                command.CommandText = "INSERT INTO Memories (Content, SourceApp, Timestamp) VALUES ($c, $a, $t)";
                command.Parameters.AddWithValue("$c", content);
                command.Parameters.AddWithValue("$a", appName);
                command.Parameters.AddWithValue("$t", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                await command.ExecuteNonQueryAsync();
            }
        }

        public async Task<List<string>> GetRecent(DateTime sessionStart)
        {
            var rawList = new List<string>();
            using (var connection = new SqliteConnection($"Data Source={_dbPath}"))
            {
                await connection.OpenAsync();
                var command = connection.CreateCommand();

                // Берем 20 последних записей
                command.CommandText = "SELECT Timestamp, SourceApp, Content FROM Memories ORDER BY Id DESC LIMIT 20";

                using (var reader = await command.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        string timeStr = reader.GetString(0);
                        string app = reader.GetString(1);
                        string text = reader.GetString(2);

                        // Пытаемся понять, это старая запись или новая
                        string prefix = "[OLD HISTORY]";
                        if (DateTime.TryParse(timeStr, out DateTime recordTime))
                        {
                            if (recordTime >= sessionStart)
                                prefix = "🔴 [CURRENT SESSION]"; // Свежее
                            else
                                prefix = "⚪ [PAST SESSION]";    // Старое
                        }

                        if (text.Length > 150) text = text.Substring(0, 150) + "...";

                        rawList.Add($"{prefix} [{timeStr}] <{app}>: {text}");
                    }
                }
            }

            // Переворачиваем, чтобы шло от старого к новому
            rawList.Reverse();
            return rawList;
        }
    }
}