using System.Net.Http.Headers;
using System.Text.Json;
using Telegram.Bot;

namespace TaskAssistantBot.Services;

public class AudioService
{
    private readonly ITelegramBotClient _botClient;
    private readonly string _apiKey;
    private readonly HttpClient _httpClient;

    public AudioService(ITelegramBotClient botClient, string apiKey)
    {
        _botClient = botClient;
        _apiKey = apiKey;
        _httpClient = new HttpClient(); // Никаких костылей с SSL больше не нужно!
    }

    // Метод для скачивания голосового сообщения из облака Telegram на локальный диск
    public async Task<string> DownloadVoiceAsync(string fileId, CancellationToken ct)
    {
        // Получаем информацию о файле (путь для скачивания)
        var file = await _botClient.GetFile(fileId, ct);
        // Формируем путь во временной папке системы
        string tempFilePath = Path.Combine(Path.GetTempPath(), $"{fileId}.ogg");

        using (var saveFileStream = File.OpenWrite(tempFilePath))
        {
            // Скачиваем содержимое файла в поток
            await _botClient.DownloadFile(file.FilePath!, saveFileStream, ct);
        }
        return tempFilePath;
    }

    // отправляет аудиофайл на сервер Groq
    public async Task<string> TranscribeAudioAsync(string filePath)
    {
        try
        {
            // Настройка запроса к API
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.groq.com/openai/v1/audio/transcriptions");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var content = new MultipartFormDataContent();
            var fileBytes = await File.ReadAllBytesAsync(filePath);
            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse("audio/ogg");

            content.Add(fileContent, "file", Path.GetFileName(filePath));
            content.Add(new StringContent("whisper-large-v3"), "model");
            content.Add(new StringContent("ru"), "language"); // Указываем язык для ИИ

            request.Content = content;
            var response = await _httpClient.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("text").GetString() ?? "Речь не распознана";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Groq Error]: {ex.Message}");
            return "Ошибка распознавания голоса.";
        }
        // Гарантирует удаление временого файла после обработки
        finally { if (File.Exists(filePath)) File.Delete(filePath); }
    }
}