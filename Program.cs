using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoicePrototype;

class Program
{
    private static readonly HttpClient _httpClient = new();
    private static List<ChatMessage> _conversationHistory = new();
    
    // Clawdbot Gateway settings
    private const string ClawdbotUrl = "http://127.0.0.1:18789/v1/chat/completions";
    private const string ClawdbotToken = "36dca5a3847f032b4f46b023343372811d5ff1c16eca5f9b";
    
    private const string SystemPrompt = """
        You are a helpful assistant for a meeting debrief conversation.
        The user just finished a meeting and wants to capture the key points while driving.
        
        Have a natural two-way conversation to help them capture:
        - Who they met with (company, people)
        - Main topics discussed
        - Key outcomes and decisions
        - Action items and next steps
        - Any follow-up needed
        
        Ask clarifying questions one at a time. Keep responses brief and conversational 
        (they'll be spoken aloud). When you have enough info, offer to summarize.
        
        Keep responses under 2-3 sentences so they're easy to listen to while driving.
        """;

    static async Task Main(string[] args)
    {
        Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║           Sector97 Voice Prototype - Meeting Debrief         ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        Console.WriteLine();
        Console.WriteLine("Powered by Clawdbot 🤖");
        Console.WriteLine();
        Console.WriteLine("Instructions:");
        Console.WriteLine("  • Press ENTER to start recording");
        Console.WriteLine("  • Speak your message");
        Console.WriteLine("  • Press ENTER again to stop and process");
        Console.WriteLine("  • Type 'quit' to exit, 'summary' for final notes");
        Console.WriteLine();
        
        // Add system message
        _conversationHistory.Add(new ChatMessage { Role = "system", Content = SystemPrompt });
        
        // Initial greeting
        var greeting = "Hi! I'm ready to help capture your meeting notes. When you're ready, just tell me who you met with and we'll go from there.";
        Console.WriteLine($"🤖 Assistant: {greeting}");
        await SpeakAsync(greeting);
        Console.WriteLine();

        while (true)
        {
            Console.Write("Press ENTER to speak (or type 'quit'/'summary'): ");
            var input = Console.ReadLine()?.Trim().ToLower();
            
            if (input == "quit" || input == "exit")
            {
                Console.WriteLine("Goodbye!");
                break;
            }
            
            if (input == "summary")
            {
                await GenerateSummary();
                continue;
            }

            // Record audio
            var audioFile = await RecordAudioAsync();
            if (audioFile == null) continue;
            
            // Transcribe
            Console.WriteLine("📝 Transcribing...");
            var transcription = await TranscribeAsync(audioFile);
            if (string.IsNullOrEmpty(transcription))
            {
                Console.WriteLine("Could not transcribe audio. Try again.");
                continue;
            }
            
            Console.WriteLine($"🎤 You said: {transcription}");
            Console.WriteLine();
            
            // Get response via Clawdbot
            Console.WriteLine("🤔 Thinking...");
            var response = await GetResponseAsync(transcription);
            if (string.IsNullOrEmpty(response))
            {
                Console.WriteLine("Error getting response. Try again.");
                continue;
            }
            
            Console.WriteLine($"🤖 Assistant: {response}");
            
            // Speak response
            await SpeakAsync(response);
            Console.WriteLine();
            
            // Cleanup temp file
            try { File.Delete(audioFile); } catch { }
        }
    }

    static async Task<string?> RecordAudioAsync()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"voice_{Guid.NewGuid()}.wav");
        
        Console.WriteLine("🎙️  Recording... (Press ENTER to stop)");
        
        var ffmpegProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = $"-f avfoundation -i \":0\" -ar 16000 -ac 1 -y \"{tempFile}\"",
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        
        ffmpegProcess.Start();
        
        // Wait for Enter key
        Console.ReadLine();
        
        // Send 'q' to ffmpeg to stop gracefully
        await ffmpegProcess.StandardInput.WriteAsync("q");
        ffmpegProcess.StandardInput.Close();
        
        await ffmpegProcess.WaitForExitAsync();
        
        Console.WriteLine("🛑 Recording stopped.");
        
        if (File.Exists(tempFile) && new FileInfo(tempFile).Length > 1000)
        {
            return tempFile;
        }
        
        Console.WriteLine("Recording too short or failed.");
        return null;
    }

    static async Task<string?> TranscribeAsync(string audioFile)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "whisper",
                Arguments = $"\"{audioFile}\" --model base --output_format txt --output_dir /tmp",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        
        process.Start();
        await process.WaitForExitAsync();
        
        // Read the output txt file
        var baseName = Path.GetFileNameWithoutExtension(audioFile);
        var txtFile = $"/tmp/{baseName}.txt";
        
        if (File.Exists(txtFile))
        {
            var text = await File.ReadAllTextAsync(txtFile);
            File.Delete(txtFile);
            return text.Trim();
        }
        
        return null;
    }

    static async Task<string?> GetResponseAsync(string userMessage)
    {
        _conversationHistory.Add(new ChatMessage { Role = "user", Content = userMessage });
        
        var requestBody = new
        {
            model = "clawdbot",
            messages = _conversationHistory,
            max_tokens = 300
        };
        
        var request = new HttpRequestMessage(HttpMethod.Post, ClawdbotUrl)
        {
            Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
        };
        
        request.Headers.Add("Authorization", $"Bearer {ClawdbotToken}");
        request.Headers.Add("x-clawdbot-agent-id", "main");
        
        try
        {
            var response = await _httpClient.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"API Error ({response.StatusCode}): {responseBody}");
                return null;
            }
            
            var result = JsonSerializer.Deserialize<OpenAIResponse>(responseBody);
            var assistantMessage = result?.Choices?.FirstOrDefault()?.Message?.Content ?? "";
            
            _conversationHistory.Add(new ChatMessage { Role = "assistant", Content = assistantMessage });
            
            return assistantMessage;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            return null;
        }
    }

    static async Task GenerateSummary()
    {
        Console.WriteLine("\n📋 Generating meeting summary...\n");
        
        var summaryRequest = "Please provide a structured summary of our meeting notes in markdown format with sections for: Meeting Details, Key Discussion Points, Decisions Made, Action Items, and Follow-ups needed.";
        
        var response = await GetResponseAsync(summaryRequest);
        
        if (!string.IsNullOrEmpty(response))
        {
            Console.WriteLine("═══════════════════════════════════════════════════════════════");
            Console.WriteLine(response);
            Console.WriteLine("═══════════════════════════════════════════════════════════════");
            
            // Save to file
            var fileName = $"meeting_notes_{DateTime.Now:yyyy-MM-dd_HHmmss}.md";
            await File.WriteAllTextAsync(fileName, response);
            Console.WriteLine($"\n💾 Saved to: {fileName}");
        }
    }

    static async Task SpeakAsync(string text)
    {
        // Escape quotes and remove markdown that sounds weird when spoken
        var cleanText = text
            .Replace("\"", "\\\"")
            .Replace("#", "")
            .Replace("*", "")
            .Replace("_", "");
            
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "say",
                Arguments = $"-v Samantha \"{cleanText}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        
        process.Start();
        await process.WaitForExitAsync();
    }
}

class ChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";
    
    [JsonPropertyName("content")]
    public string Content { get; set; } = "";
}

class OpenAIResponse
{
    [JsonPropertyName("choices")]
    public List<Choice>? Choices { get; set; }
}

class Choice
{
    [JsonPropertyName("message")]
    public ChatMessage? Message { get; set; }
}
