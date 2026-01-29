using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VoicePrototype;

class Program
{
    private static readonly HttpClient _httpClient = new();
    private static string? _anthropicApiKey;
    private static List<Message> _conversationHistory = new();
    
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
        
        // Get API key
        _anthropicApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrEmpty(_anthropicApiKey))
        {
            Console.Write("Enter your Anthropic API key: ");
            _anthropicApiKey = Console.ReadLine()?.Trim();
        }
        
        if (string.IsNullOrEmpty(_anthropicApiKey))
        {
            Console.WriteLine("Error: API key required");
            return;
        }

        Console.WriteLine();
        Console.WriteLine("Instructions:");
        Console.WriteLine("  • Press ENTER to start recording");
        Console.WriteLine("  • Speak your message");
        Console.WriteLine("  • Press ENTER again to stop and process");
        Console.WriteLine("  • Type 'quit' to exit, 'summary' for final notes");
        Console.WriteLine();
        
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
            
            // Get Claude response
            Console.WriteLine("🤔 Thinking...");
            var response = await GetClaudeResponseAsync(transcription);
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

    static async Task<string?> GetClaudeResponseAsync(string userMessage)
    {
        _conversationHistory.Add(new Message { Role = "user", Content = userMessage });
        
        var requestBody = new
        {
            model = "claude-sonnet-4-20250514",
            max_tokens = 300,
            system = SystemPrompt,
            messages = _conversationHistory
        };
        
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
        };
        
        request.Headers.Add("x-api-key", _anthropicApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        
        try
        {
            var response = await _httpClient.SendAsync(request);
            var responseBody = await response.Content.ReadAsStringAsync();
            
            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"API Error: {responseBody}");
                return null;
            }
            
            var result = JsonSerializer.Deserialize<ClaudeResponse>(responseBody);
            var assistantMessage = result?.Content?.FirstOrDefault()?.Text ?? "";
            
            _conversationHistory.Add(new Message { Role = "assistant", Content = assistantMessage });
            
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
        
        _conversationHistory.Add(new Message 
        { 
            Role = "user", 
            Content = "Please provide a structured summary of our meeting notes in markdown format with sections for: Meeting Details, Key Discussion Points, Decisions Made, Action Items, and Follow-ups needed." 
        });
        
        var requestBody = new
        {
            model = "claude-sonnet-4-20250514",
            max_tokens = 1000,
            system = SystemPrompt,
            messages = _conversationHistory
        };
        
        var request = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages")
        {
            Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json")
        };
        
        request.Headers.Add("x-api-key", _anthropicApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");
        
        var response = await _httpClient.SendAsync(request);
        var responseBody = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize<ClaudeResponse>(responseBody);
        var summary = result?.Content?.FirstOrDefault()?.Text ?? "Could not generate summary";
        
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine(summary);
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        
        // Save to file
        var fileName = $"meeting_notes_{DateTime.Now:yyyy-MM-dd_HHmmss}.md";
        await File.WriteAllTextAsync(fileName, summary);
        Console.WriteLine($"\n💾 Saved to: {fileName}");
    }

    static async Task SpeakAsync(string text)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "say",
                Arguments = $"-v Samantha \"{text.Replace("\"", "\\\"")}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        
        process.Start();
        await process.WaitForExitAsync();
    }
}

class Message
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = "";
    
    [JsonPropertyName("content")]
    public string Content { get; set; } = "";
}

class ClaudeResponse
{
    [JsonPropertyName("content")]
    public List<ContentBlock>? Content { get; set; }
}

class ContentBlock
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }
}
