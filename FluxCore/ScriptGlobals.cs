using System.Net.Http;
using FluxCore.LLM;

namespace FluxCore
{
    /// <summary>
    /// Services injected into the RUN_CSHARP script execution sandbox.
    ///
    /// In scripts, all properties are accessible directly by name:
    ///   var chats = await Telegram.GetAvailableChatsAsync();
    ///   await DataLake.WriteAsync("note", "my observation");
    ///   var result = await Http.GetStringAsync("https://api.example.com/data");
    /// </summary>
    public class ScriptGlobals
    {
        /// <summary>Telegram MTProto client — query chats, send messages.</summary>
        public TelegramService? Telegram { get; init; }

        /// <summary>Persistent event log — read/write observations and notes.</summary>
        public DataLakeService? DataLake { get; init; }

        /// <summary>Knowledge graph — entity and relationship queries.</summary>
        public KnowledgeGraphService? KnowledgeGraph { get; init; }

        /// <summary>Vector memory — semantic search over past interactions.</summary>
        public MemoryService? Memory { get; init; }

        /// <summary>Application settings — read/write Davos configuration.</summary>
        public AppSettings? Settings { get; init; }

        /// <summary>Shared HTTP client — make web API calls without opening a browser.</summary>
        public HttpClient Http { get; init; } = new HttpClient();

        /// <summary>Gemini LLM — call the model from within a script.</summary>
        public GeminiService? Gemini { get; init; }
    }
}
