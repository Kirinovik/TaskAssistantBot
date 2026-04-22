using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using TaskAssistantBot.Models;

namespace TaskAssistantBot
{
    public class DatabaseContext
    {
        // Подключение к бд
        private readonly string _connectionString = "Data Source=assistant.db;Cache=Shared;Default Timeout=30;";

        public void Initialize()
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();

            // Создание всех таблиц
            cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS Drafts (ChatId INTEGER PRIMARY KEY, FullText TEXT);
            CREATE TABLE IF NOT EXISTS Users (ChatId INTEGER PRIMARY KEY, IsAuthenticated INTEGER DEFAULT 0);
            CREATE TABLE IF NOT EXISTS Contacts (Name TEXT PRIMARY KEY, Email TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS TaskHistory (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ChatId INTEGER,
                Recipient TEXT,
                TaskText TEXT,
                DueDate DATETIME,
                Status TEXT,
                Timestamp DATETIME DEFAULT CURRENT_TIMESTAMP
            );";
            cmd.ExecuteNonQuery();
        }

        // Сохранение или дополнение текста задачи
        public void UpdateDraft(long chatId, string newText)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO Drafts (ChatId, FullText) VALUES (@id, @txt) ON CONFLICT(ChatId) DO UPDATE SET FullText = FullText || ' ' || @txt";
            cmd.Parameters.AddWithValue("@id", chatId);
            cmd.Parameters.AddWithValue("@txt", newText);
            cmd.ExecuteNonQuery();
        }

        public string GetDraft(long chatId) => ExecuteScalar("SELECT FullText FROM Drafts WHERE ChatId = @id", ("@id", chatId))?.ToString() ?? "";

        public void ClearDraft(long chatId) => ExecuteNonQuery("DELETE FROM Drafts WHERE ChatId = @id", ("@id", chatId));

        public bool IsUserAuthenticated(long chatId) => (long)(ExecuteScalar("SELECT COUNT(*) FROM Users WHERE ChatId = @id AND IsAuthenticated = 1", ("@id", chatId)) ?? 0L) > 0;

        public void AuthenticateUser(long chatId) => ExecuteNonQuery("INSERT OR REPLACE INTO Users (ChatId, IsAuthenticated) VALUES (@id, 1)", ("@id", chatId));

        public void AddContact(string name, string email) => ExecuteNonQuery("INSERT OR REPLACE INTO Contacts (Name, Email) VALUES (@name, @email)", ("@name", name), ("@email", email));

        public string GetEmailByName(string name) => ExecuteScalar("SELECT Email FROM Contacts WHERE Name LIKE @name LIMIT 1", ("@name", $"%{name}%"))?.ToString();

        public void SaveFinalTask(long chatId, TaskDraft draft)
        {
            DateTime finalDate;

            // 1. Пытаемся распарсить то, что прислал ИИ
            if (!DateTime.TryParse(draft.Deadline, out finalDate))
            {
                // 2. Если ИИ прислал "Не указано" или мусор, ставим дефолт: завтра в 10:00
                finalDate = DateTime.Today.AddDays(1).AddHours(10);
            }
            else
            {
                // 3. Если год определился как 0001 (ошибка парсинга года), исправляем на текущий
                if (finalDate.Year < 2000)
                    finalDate = new DateTime(DateTime.Now.Year, finalDate.Month, finalDate.Day, finalDate.Hour, finalDate.Minute, 0);

                // 4. Если время осталось 00:00 (пользователь не указал его), ставим 10:00
                if (finalDate.Hour == 0 && finalDate.Minute == 0)
                    finalDate = finalDate.Date.AddHours(10);
            }

            LogTask(chatId, draft.RecipientName, draft.TaskDescription, "Sent", finalDate);
        }



        // Логирование задачи с автоматической очисткой старых записей
        public void LogTask(long chatId, string recipient, string text, string status, DateTime? dueDate = null)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();
            try
            {
                using var cmd = connection.CreateCommand();
                cmd.Transaction = transaction;
                // Добавляем DueDate в запрос
                cmd.CommandText = "INSERT INTO TaskHistory (ChatId, Recipient, TaskText, Status, DueDate) VALUES (@chat, @rec, @txt, @stat, @due)";
                cmd.Parameters.AddWithValue("@chat", chatId);
                cmd.Parameters.AddWithValue("@rec", recipient);
                cmd.Parameters.AddWithValue("@txt", text);
                cmd.Parameters.AddWithValue("@stat", status);
                // Если дата есть — сохраняем, если нет — записываем NULL
                cmd.Parameters.AddWithValue("@due", (object)dueDate ?? DBNull.Value);
                cmd.ExecuteNonQuery();

                cmd.CommandText = "DELETE FROM TaskHistory WHERE Id NOT IN (SELECT Id FROM TaskHistory ORDER BY Id DESC LIMIT 25)";
                cmd.ExecuteNonQuery();

                transaction.Commit();
            }
            catch { transaction.Rollback(); throw; }
        }

        public TaskDraft GetLastTaskForRecipient(string name)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            // Ищем по частичному совпадению имени и берем самую последнюю запись
            cmd.CommandText = "SELECT Recipient, TaskText FROM TaskHistory WHERE Recipient LIKE @name ORDER BY Id DESC LIMIT 1";
            cmd.Parameters.AddWithValue("@name", $"%{name}%");

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return new TaskDraft
                {
                    RecipientName = reader.GetString(0),
                    TaskDescription = reader.GetString(1),
                    Deadline = "Из истории"
                };
            }
            return null;
        }

        public TaskDraft GetTaskById(int taskId)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Recipient, TaskText FROM TaskHistory WHERE Id = @id";
            cmd.Parameters.AddWithValue("@id", taskId);

            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return new TaskDraft
                {
                    RecipientName = reader.GetString(0),
                    TaskDescription = reader.GetString(1),
                    Deadline = "Отменено"
                };
            }
            return null;
        }

        public List<(int Id, string Recipient, string Text)> GetActiveTasks()
        {
            var tasks = new List<(int Id, string Recipient, string Text)>();
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();

            // Выбираем только те, что не отменены
            cmd.CommandText = "SELECT Id, Recipient, TaskText FROM TaskHistory WHERE Status != 'Cancelled' ORDER BY Id DESC LIMIT 25";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                tasks.Add((
                    reader.GetInt32(0), // Id
                    reader.GetString(1), // Recipient
                    reader.GetString(2)  // TaskText
                ));
            }
            return tasks;
        }

        public List<TaskDraft> GetLast25Tasks()
        {
            var tasks = new List<TaskDraft>();
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();

            // Выбираем: 0 - Recipient, 1 - TaskText
            cmd.CommandText = "SELECT Recipient, TaskText FROM TaskHistory ORDER BY Id DESC LIMIT 25";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                tasks.Add(new TaskDraft
                {
                    // Индекс 0 — это Recipient (Александр)
                    RecipientName = reader.GetString(0),
                    // Индекс 1 — это TaskText (съездить за документами...)
                    TaskDescription = reader.GetString(1)
                });
            }
            return tasks;
        }

        public List<(int Id, string Recipient, string Text, DateTime DueDate, string CurrentStatus)> GetTasksPendingReminder()
        {
            var tasks = new List<(int Id, string Recipient, string Text, DateTime DueDate, string CurrentStatus)>();
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();

            // ЗАМЕНЯЕМ TimeStamp на DueDate везде в запросе
            cmd.CommandText = @"
        SELECT Id, Recipient, TaskText, DueDate, Status  
        FROM TaskHistory  
        WHERE Status NOT IN ('Completed', 'Cancelled', 'ReminderAfter')  
        AND DueDate IS NOT NULL  
        AND (
            (Status = 'Sent' AND DueDate <= datetime('now', '+1 day') AND DueDate > datetime('now'))
            OR  
            (Status IN ('Sent', 'ReminderDayBefore') AND DueDate <= datetime('now') AND DueDate > datetime('now', '-12 hours'))
            OR  
            (Status = 'ReminderToday' AND DueDate <= datetime('now', '-1 day'))
        )";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                tasks.Add((
                    reader.GetInt32(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetDateTime(3), // Это теперь DueDate
                    reader.GetString(4)
                ));
            }
            return tasks;
        }

        private object ExecuteScalar(string sql, params (string, object)[] p)
        {
            using var c = new SqliteConnection(_connectionString); c.Open();
            using var cmd = c.CreateCommand(); cmd.CommandText = sql;
            foreach (var item in p) cmd.Parameters.AddWithValue(item.Item1, item.Item2);
            return cmd.ExecuteScalar();
        }
        private void ExecuteNonQuery(string sql, params (string, object)[] p)
        {
            using var c = new SqliteConnection(_connectionString); c.Open();
            using var cmd = c.CreateCommand(); cmd.CommandText = sql;
            foreach (var item in p) cmd.Parameters.AddWithValue(item.Item1, item.Item2);
            cmd.ExecuteNonQuery();
        }

        public void UpdateTaskStatus(int taskId, string newStatus)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE TaskHistory SET Status = @status WHERE Id = @id";
            cmd.Parameters.AddWithValue("@status", newStatus);
            cmd.Parameters.AddWithValue("@id", taskId);
            cmd.ExecuteNonQuery();
        }

        public int GetLastTaskId(string recipientName)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT Id FROM TaskHistory WHERE Recipient LIKE @name ORDER BY Id DESC LIMIT 1";
            cmd.Parameters.AddWithValue("@name", $"%{recipientName}%");
            var result = cmd.ExecuteScalar();
            return result != null ? Convert.ToInt32(result) : -1;
        }

        public void LogoutUser(long chatId)
        {
            ExecuteNonQuery("UPDATE Users SET IsAuthenticated = 0 WHERE ChatId = @id", ("@id", chatId));

            // Очистка черновиков при выходе
            ClearDraft(chatId);
        }

        public List<(string Name, string Email)> GetAllContacts()
        {
            var contacts = new List<(string Name, string Email)>();
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var cmd = connection.CreateCommand();

            cmd.CommandText = "SELECT Name, Email FROM Contacts ORDER BY Name ASC";

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                contacts.Add((
                    reader.GetString(0), // Name
                    reader.GetString(1)  // Email
                ));
            }
            return contacts;
        }
    }
}