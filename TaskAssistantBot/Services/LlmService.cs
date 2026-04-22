using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TaskAssistantBot.Models;

namespace TaskAssistantBot.Services;

public class LlmService
{
    private readonly string _apiKey;
    private readonly HttpClient _httpClient;

    public LlmService(string apiKey)
    {
        _apiKey = apiKey;
        _httpClient = new HttpClient();
    }

    string currentDateTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm");

    // Превращение фразы в структурированный объект TaskDraft
    public async Task<TaskDraft> ParseTaskAsync(string userText)
    {
        try
        {
            var url = "https://api.groq.com/openai/v1/chat/completions";

            // Формируем запрос
            var requestBody = new
            {
                model = "llama-3.3-70b-versatile", // Мощная и быстрая модель
                messages = new[]
                {
                    new {
                        role = "system",
                        content = $"Ты — ассистент по планированию. Твоя задача — извлекать данные из текста в формате JSON. Текущая дата и время: {currentDateTime}" +
                                  "Верни СТРОГО JSON с полями: RecipientName, TaskDescription, Deadline. " +
                                  "Если данных нет, пиши 'Не указано'."+
                                  "Всегда возвращай полное имя (Имя и Фамилия), если оно упоминалось."+
                                  "ПРАВИЛА ДЛЯ Deadline: " +
                                  "1. Используй формат 'yyyy-MM-dd HH:mm'. " +
                                  "2. Если год не указан, используй текущий (2026). Если дата уже прошла, используй следующий год. " +
                                  "3. Если время НЕ указано пользователем, СТРОГО подставляй '10:00'. " +
                                  "4. Если данных совсем нет, пиши 'Не указано'."
                    },
                    new { role = "user", content = userText }
                },
                // Гарантирует, что ИИ не пришлет лишнего текста вокруг JSON
                response_format = new { type = "json_object" },
                temperature = 0.1 
            };



            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

            var response = await _httpClient.SendAsync(request);
            var jsonResponse = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"[Groq LLM Error]: {response.StatusCode} - {jsonResponse}");
                return new TaskDraft { TaskDescription = userText };
            }

            // Парсим ответ от Groq (OpenAI format)
            using var doc = JsonDocument.Parse(jsonResponse);
            var content = doc.RootElement
                             .GetProperty("choices")[0]
                             .GetProperty("message")
                             .GetProperty("content")
                             .GetString();

            return JsonSerializer.Deserialize<TaskDraft>(content!) ?? new TaskDraft { TaskDescription = userText };
        }

        catch (JsonException ex)
        {
            // Ошибка, если ИИ прислал «битый» JSON
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[LlmService] Ошибка парсинга JSON: {ex.Message}");
            Console.ResetColor();
            return new TaskDraft { TaskDescription = userText };
        }
        catch (HttpRequestException ex)
        {
            // Ошибка сети или API Groq недоступен
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[LlmService] Ошибка сети: {ex.StatusCode} - {ex.Message}");
            Console.ResetColor();
            return new TaskDraft { TaskDescription = userText };
        }
        catch (Exception ex)
        {
            // Любая другая критическая ошибка
            Console.ForegroundColor = ConsoleColor.DarkRed;
            Console.WriteLine($"[LlmService] КРИТИЧЕСКАЯ ОШИБКА: {ex.GetType().Name}");
            Console.WriteLine($"Детали: {ex.Message}");
            Console.WriteLine($"Стек: {ex.StackTrace}");
            Console.ResetColor();
            return new TaskDraft { TaskDescription = userText };
        }
        

    }
}