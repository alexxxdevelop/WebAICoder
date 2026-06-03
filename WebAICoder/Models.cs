using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WebAICoder
{
    public class ChatMessage
    {
        public string Role { get; set; }           // "User", "AI", "System"
        public string Content { get; set; }         // текст сообщения
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public List<FileDiff> FileDiffs { get; set; } // список изменённых файлов
    }

    public class FileDiff
    {
        public string FilePath { get; set; }
        public string ShortFileName { get; set; }
        public int LinesAdded { get; set; }
        public int LinesRemoved { get; set; }
        public bool IsReverted { get; set; }
        public string Explain { get; set; }

        // Для отката: сохраняем ВЕСЬ оригинальный файл
        public string OriginalFileContent { get; set; }
    }

    public class EmbeddingOutput
    {
        public List<ChunkResult> Chunks { get; set; }
    }

    public class ChunkResult
    {
        public string FilePath { get; set; }
        public string Content { get; set; }
        public float Score { get; set; }
    }

    public class AiResponse
    {
        [JsonProperty("changes")]
        public List<FileChange> Changes { get; set; }
    }

    public class FileChange
    {
        [JsonProperty("FilePath")]
        public string FilePath { get; set; }

        [JsonProperty("OriginalCodeSnippet")]
        public string OriginalCodeSnippet { get; set; }

        [JsonProperty("NewCodeSnippet")]
        public string NewCodeSnippet { get; set; }

        [JsonProperty("Explain")]
        public string Explain { get; set; }
    }

    public class AskResponse
    {
        [JsonProperty("answer")]
        public string Answer { get; set; }
    }

    public class EmbeddingInput
    {
        public List<FileData> Files { get; set; }
        public string Query { get; set; }
        public string SolutionPath { get; set; } // для идентификации кэша
    }

    public class FileData
    {
        public string FilePath { get; set; }
        public string Content { get; set; }
        public string Hash { get; set; } // SHA256 в Base64
    }
}
