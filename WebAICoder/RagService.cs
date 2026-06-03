using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace WebAICoder
{
    public class RagService
    {
        private readonly DTE2 _dte;
        private readonly string _serviceExePath;

        public RagService(DTE2 dte, string serviceExePath)
        {
            _dte = dte;
            _serviceExePath = serviceExePath;
        }

        public async Task<string> GetRelevantCodeAsync(string query, TextBlock StatusTextBlock)
        {
            // 1. Собираем все файлы из решения
            var files = GetAllProjectFiles();

            // 2. Формируем входные данные
            var input = new EmbeddingInput
            {
                Files = files.Select(f => new FileData
                {
                    FilePath = f.Key,
                    Content = f.Value,
                    Hash = ComputeHash(f.Value)
                }).ToList(),
                Query = query,
                SolutionPath = _dte.Solution.FullName
            };
            var json = JsonConvert.SerializeObject(input);
            var jsonBytes = Encoding.UTF8.GetBytes(json);
            var base64Json = Convert.ToBase64String(jsonBytes);

            // 3. Запускаем процесс
            var process = new System.Diagnostics.Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = _serviceExePath,
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    // Указываем кодировку для вывода ошибок
                    StandardErrorEncoding = Encoding.UTF8,
                    StandardOutputEncoding = Encoding.UTF8
                }
            };
            process.Start();

            // Асинхронно читаем stderr и обновляем статус
            process.ErrorDataReceived += (sender, e) =>
            {
                if (e.Data != null)
                {
                    ThreadHelper.JoinableTaskFactory.Run(async () =>
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        StatusTextBlock.Text = $"📂 {e.Data}";
                    });
                }
            };
            process.BeginErrorReadLine();

            // 4. Отправляем Base64-строку
            await process.StandardInput.WriteLineAsync(base64Json);
            process.StandardInput.Close();

            // 5. Читаем результат
            var outputBase64 = await process.StandardOutput.ReadToEndAsync();
            process.WaitForExit();

            // 6. Парсим и формируем контекст
            var outputBytes = Convert.FromBase64String(outputBase64.Trim());
            var outputJson = Encoding.UTF8.GetString(outputBytes);
            var result = JsonConvert.DeserializeObject<EmbeddingOutput>(outputJson);
            var sb = new StringBuilder();
            foreach (var chunk in result.Chunks)
            {
                sb.AppendLine($"// {chunk.FilePath}");
                sb.AppendLine(chunk.Content);
                sb.AppendLine();
            }
            return sb.ToString();
        }

        private Dictionary<string, string> GetAllProjectFiles()
        {
            var files = new Dictionary<string, string>();
            foreach (Project project in _dte.Solution.Projects)
            {
                CollectFiles(project.ProjectItems, files);
            }
            return files;
        }

        private void CollectFiles(ProjectItems items, Dictionary<string, string> accumulator)
        {
            if (items == null) return;

            foreach (ProjectItem item in items)
            {
                try
                {
                    // Проверяем количество файлов у элемента
                    for (short i = 0; i < item.FileCount; i++)
                    {
                        try
                        {
                            string path = item.FileNames[i];

                            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                            {
                                var exts = new string[] { ".cs", ".xaml", ".csproj", ".config", ".js", ".css", ".html", ".cshtml" };
                                var ext = Path.GetExtension(path).ToLower();
                                if (exts.Contains(ext))
                                {
                                    if (!accumulator.ContainsKey(path))
                                    {
                                        var fileInfo = new FileInfo(path);
                                        if (fileInfo.Length <= 102400) // 100 КБ в байтах
                                        {
                                            accumulator[path] = File.ReadAllText(path);
                                        }
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                // Рекурсивный обход вложенных элементов
                try
                {
                    if (item.ProjectItems != null && item.ProjectItems.Count > 0)
                    {
                        CollectFiles(item.ProjectItems, accumulator);
                    }
                }
                catch { }
            }
        }

        private string ComputeHash(string content)
        {
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(content);
                byte[] hash = sha256.ComputeHash(bytes);
                return Convert.ToBase64String(hash);
            }
        }
    }
}
