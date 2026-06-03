using ElBruno.LocalEmbeddings;
using Microsoft.Extensions.AI;
using System.Text;
using System.Text.Json;

namespace EmbeddingService
{
    internal class Program
    {
        static async Task Main()
        {
            try
            {
                // Устанавливаем кодировку для корректной работы с UTF-8
                Console.InputEncoding = System.Text.Encoding.UTF8;
                Console.OutputEncoding = System.Text.Encoding.UTF8;
                Console.Error.Write(""); // инициализация stderr
                // Явно устанавливаем кодировку для stderr
                var standardError = new StreamWriter(Console.OpenStandardError());
                standardError.AutoFlush = true;
                Console.SetError(standardError);

                // 1. Читаем Base64-строку из stdin
                var base64Input = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(base64Input))
                {
                    // Выводим пустой результат в Base64
                    var emptyResult = Convert.ToBase64String(Encoding.UTF8.GetBytes("{}"));
                    Console.WriteLine(emptyResult);
                    return;
                }

                // 2. Декодируем Base64 в JSON
                var jsonBytes = Convert.FromBase64String(base64Input.Trim());
                var inputJson = Encoding.UTF8.GetString(jsonBytes);

                // 3. Десериализуем
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var input = JsonSerializer.Deserialize<EmbeddingInput>(inputJson, options);

                if (input?.Files == null || input.Files.Count == 0)
                {
                    var emptyResult = Convert.ToBase64String(Encoding.UTF8.GetBytes("{}"));
                    Console.WriteLine(emptyResult);
                    return;
                }

                // Инициализация генератора эмбеддингов (модель загрузится один раз)
                var generator = await LocalEmbeddingGenerator.CreateAsync();

                // Загрузка кэша с диска
                var cache = LoadCache(input.SolutionPath ?? "default");
                var allChunks = new List<(string FilePath, string Content, float[] Embedding)>();

                // Индексация всех переданных файлов
                var embeddings = new List<(string FilePath, string Content, float[] Embedding)>();
                foreach (var file in input.Files)
                {
                    // Сообщаем в stderr имя файла для статусной строки
                    Console.Error.WriteLine($"Индексирую: {Path.GetFileName(file.FilePath)}");

                    if (cache.TryGetValue(file.FilePath, out var cached) && cached.Hash == file.Hash)
                    {
                        // Используем кэшированные чанки
                        foreach (var chunk in cached.Chunks) allChunks.Add((file.FilePath, chunk.Text, chunk.Embedding));
                    }
                    else
                    {
                        // Разбиваем содержимое на чанки (например, по 50 строк)
                        var chunks = ChunkCode(file.Content, 50);
                        var newCachedChunks = new List<ChunkEmbedding>();
                        foreach (var chunk in chunks)
                        {
                            var emb = await generator.GenerateEmbeddingAsync(chunk.Text);
                            allChunks.Add((file.FilePath, chunk.Text, emb.Vector.ToArray()));
                            newCachedChunks.Add(new ChunkEmbedding { Text = chunk.Text, Embedding = emb.Vector.ToArray() });
                        }
                        cache[file.FilePath] = new CachedFileData { Hash = file.Hash, Chunks = newCachedChunks };
                    }
                }

                // Сохраняем обновлённый кэш
                SaveCache(input.SolutionPath ?? "default", cache);

                // Поиск по запросу
                var queryEmb = await generator.GenerateEmbeddingAsync(input.Query ?? "");
                var queryVector = queryEmb.Vector.ToArray();

                var results = allChunks
                    .Select(e => new { e.FilePath, e.Content, Score = CosineSimilarity(e.Embedding, queryVector) })
                    .OrderByDescending(e => e.Score)
                    .Take(5)
                    .ToList();

                // Формируем результат
                var output = new EmbeddingOutput
                {
                    Chunks = results.Select(r => new ChunkResult
                    {
                        FilePath = r.FilePath,
                        Content = r.Content,
                        Score = r.Score
                    }).ToList()
                };

                // Сериализуем в JSON и кодируем в Base64
                var outputJson = JsonSerializer.Serialize(output);
                var outputBytes = Encoding.UTF8.GetBytes(outputJson);
                var outputBase64 = Convert.ToBase64String(outputBytes);
                Console.WriteLine(outputBase64);
            }
            catch (Exception ex)
            {
                var error = new { error = ex.Message };
                var errorJson = JsonSerializer.Serialize(error);
                var errorBytes = Encoding.UTF8.GetBytes(errorJson);
                var errorBase64 = Convert.ToBase64String(errorBytes);
                Console.WriteLine(errorBase64);
            }
        }

        static List<(string Text, int StartLine)> ChunkCode(string code, int linesPerChunk)
        {
            var allLines = code.Split('\n');
            var chunks = new List<(string Text, int StartLine)>();
            for (int i = 0; i < allLines.Length; i += linesPerChunk)
            {
                var chunk = string.Join("\n", allLines.Skip(i).Take(linesPerChunk));
                chunks.Add((chunk, i + 1));
            }
            return chunks;
        }

        static float CosineSimilarity(float[] a, float[] b)
        {
            float dot = 0, magA = 0, magB = 0;
            for (int i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                magA += a[i] * a[i];
                magB += b[i] * b[i];
            }
            return dot / (float)(Math.Sqrt(magA) * Math.Sqrt(magB));
        }

        // --- Методы для работы с кэшем ---
        static string GetCacheDirectory()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                "WebAICoder", "EmbeddingCache");
        }

        static string GetCacheFilePath(string solutionPath)
        {
            var dir = GetCacheDirectory();
            Directory.CreateDirectory(dir);
            // Делаем имя файла безопасным, кодируя путь
            var safeName = Convert.ToBase64String(Encoding.UTF8.GetBytes(solutionPath))
                                  .Replace('/', '_').Replace('+', '-');
            return Path.Combine(dir, $"{safeName}.json");
        }

        static Dictionary<string, CachedFileData> LoadCache(string solutionPath)
        {
            var path = GetCacheFilePath(solutionPath);
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<Dictionary<string, CachedFileData>>(json)
                       ?? new Dictionary<string, CachedFileData>();
            }
            return new Dictionary<string, CachedFileData>();
        }

        static void SaveCache(string solutionPath, Dictionary<string, CachedFileData> cache)
        {
            var path = GetCacheFilePath(solutionPath);
            var json = JsonSerializer.Serialize(cache);
            File.WriteAllText(path, json);
        }
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

    class CachedFileData
    {
        public string Hash { get; set; }
        public List<ChunkEmbedding> Chunks { get; set; }
    }

    class ChunkEmbedding
    {
        public string Text { get; set; }
        public float[] Embedding { get; set; }
    }
}
