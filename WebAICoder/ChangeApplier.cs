using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using WebAICoder.AiAssistantWindow;

namespace WebAICoder
{
    public class ChangeApplier
    {
        private readonly DTE2 _dte;
        private readonly IServiceProvider _serviceProvider;

        public ChangeApplier(DTE2 dte, IServiceProvider serviceProvider)
        {
            _dte = dte;
            _serviceProvider = serviceProvider;
        }

        public List<FileDiff> Apply(string jsonResponse)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (string.IsNullOrEmpty(jsonResponse)) return null;

            AiResponse response = new AiResponse();
            try
            {
                response.Changes = ParseResponse(jsonResponse);
            }
            catch
            {
                // Если ответ не в формате JSON, просто показываем его
                MessageBox.Show(jsonResponse, "Ответ DeepSeek", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }

            if (response?.Changes == null || response.Changes.Count == 0) return null;
            var diffs = new List<FileDiff>();
            foreach (var change in response.Changes)
            {
                var diff = ApplyChangeWithConfirmation(change);
                if (diff != null) diffs.Add(diff);
            }
            return diffs;
        }

        private FileDiff ApplyChangeWithConfirmation(FileChange change)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // Нормализуем путь (меняем слэши на системные)
            var normalizedPath = NormalizePath(change.FilePath);

            return ApplyChangeViaFile(normalizedPath, change);
        }

        // Нормализация пути (слэши, регистр)
        private string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;

            return Path.GetFullPath(path)
                       .Replace('/', '\\')
                       .TrimEnd('\\');
        }

        private FileDiff ApplyChangeViaFile(string normalizedPath, FileChange change)
        {
            try
            {
                // Проверяем, существует ли файл; если нет — создаем новый
                if (!File.Exists(normalizedPath))
                {
                    var directory = Path.GetDirectoryName(normalizedPath);
                    if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);

                    File.WriteAllText(normalizedPath, change.NewCodeSnippet, Encoding.UTF8);

                    return new FileDiff
                    {
                        FilePath = normalizedPath,
                        ShortFileName = Path.GetFileName(normalizedPath),
                        LinesAdded = change.NewCodeSnippet.Split('\n').Length,
                        LinesRemoved = 0,
                        OriginalFileContent = "", // новый файл, нечего откатывать
                        IsReverted = false
                    };
                }

                // Читаем оригинальный файл ДО изменений
                var originalFileContent = File.ReadAllText(normalizedPath, Encoding.UTF8);

                // 1. Читаем файл
                var allText = originalFileContent;

                // 2. Нормализуем все переносы строк к \n для поиска
                var normalizedAll = allText.Replace("\r\n", "\n").Replace("\r", "\n");
                var normalizedSearch = change.OriginalCodeSnippet.Replace("\r\n", "\n").Replace("\r", "\n");

                var lines = normalizedAll.Split('\n');
                var searchLines = normalizedSearch.Split('\n');

                bool found = false;
                string newAllText = "";

                for (int i = 0; i <= lines.Length - searchLines.Length; i++)
                {
                    bool match = true;
                    for (int j = 0; j < searchLines.Length; j++)
                    {
                        if (lines[i + j].Trim() != searchLines[j].Trim())
                        {
                            match = false;
                            break;
                        }
                    }

                    if (match)
                    {
                        // Вычисляем позиции в оригинальном тексте
                        int startPos = 0;
                        for (int k = 0; k < i; k++)
                            startPos += lines[k].Length + Environment.NewLine.Length;

                        int endPos = startPos;
                        for (int k = i; k < i + searchLines.Length; k++)
                            endPos += lines[k].Length + Environment.NewLine.Length;

                        // Убираем завершающий перенос строки если есть
                        if (endPos > startPos && endPos <= allText.Length)
                        {
                            var newLineLen = Environment.NewLine.Length;
                            if (endPos >= newLineLen)
                            {
                                var possibleNewLine = allText.Substring(endPos - newLineLen, newLineLen);
                                if (possibleNewLine == Environment.NewLine)
                                    endPos -= newLineLen;
                            }
                        }

                        // 3. Нормализуем NewCodeSnippet: заменяем \n на системные переносы
                        var newCode = change.NewCodeSnippet
                            .Replace("\r\n", "\n")
                            .Replace("\r", "\n")
                            .Replace("\n", Environment.NewLine);

                        // 4. Определяем отступ (берём отступ первой строки заменяемого блока)
                        var firstLine = lines[i];
                        var indentation = firstLine.Length - firstLine.TrimStart().Length;
                        var indent = firstLine.Substring(0, indentation);

                        // 5. Добавляем отступы к новому коду
                        var newCodeLines = newCode.Split(new[] { Environment.NewLine }, StringSplitOptions.None);
                        var indentedNewCode = string.Join(Environment.NewLine,
                            newCodeLines.Select((line, idx) =>
                                string.IsNullOrWhiteSpace(line) && idx < newCodeLines.Length - 1
                                    ? ""
                                    : indent + line));

                        // 6. Собираем новый текст
                        newAllText = allText.Substring(0, startPos) +
                                         indentedNewCode +
                                         allText.Substring(endPos);

                        found = true;
                        break;
                    }
                }

                if (found)
                {
                    File.WriteAllText(normalizedPath, newAllText, Encoding.UTF8);

                    // Возвращаем дифф
                    return CalculateDiff(change.OriginalCodeSnippet, change.NewCodeSnippet, normalizedPath, originalFileContent, change.Explain);
                }
                else
                {
                    // Добавляем в конец файла
                    var currentContent = File.ReadAllText(normalizedPath, Encoding.UTF8);
                    var newContent = currentContent.TrimEnd() + Environment.NewLine + change.NewCodeSnippet + Environment.NewLine;
                    File.WriteAllText(normalizedPath, newContent, Encoding.UTF8);

                    // Для нового кода: все строки добавлены
                    var newLines = change.NewCodeSnippet.Replace("\r\n", "\n").Split('\n').Length;
                    return new FileDiff
                    {
                        FilePath = normalizedPath,
                        ShortFileName = Path.GetFileName(normalizedPath),
                        LinesAdded = newLines,
                        LinesRemoved = 0,
                        Explain = change.Explain
                    };
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка применения изменений:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }
        }

        private FileDiff CalculateDiff(string originalCode, string newCode, string filePath, string originalFileContent, string explain)
        {
            if (string.IsNullOrEmpty(originalCode) && string.IsNullOrEmpty(newCode))
                return null;

            var origLines = (originalCode ?? "").Replace("\r\n", "\n").Split('\n');
            var newLines = (newCode ?? "").Replace("\r\n", "\n").Split('\n');

            int added = 0;
            int removed = 0;

            int maxLen = Math.Max(origLines.Length, newLines.Length);

            for (int i = 0; i < maxLen; i++)
            {
                string origLine = i < origLines.Length ? origLines[i].Trim() : null;
                string newLine = i < newLines.Length ? newLines[i].Trim() : null;

                if (origLine == null && newLine != null)
                {
                    // Строка добавлена
                    added++;
                }
                else if (origLine != null && newLine == null)
                {
                    // Строка удалена
                    removed++;
                }
                else if (origLine != newLine)
                {
                    // Строка изменена = удалена старая + добавлена новая
                    removed++;
                    added++;
                }
            }

            return new FileDiff
            {
                FilePath = filePath,
                ShortFileName = Path.GetFileName(filePath),
                LinesAdded = added,
                LinesRemoved = removed,
                OriginalFileContent = originalFileContent, // ← ВОТ ЭТО ГЛАВНОЕ
                IsReverted = false,
                Explain = explain
            };
        }

        private List<FileChange> ParseResponse(string response)
        {
            var changes = new List<FileChange>();

            var sections = response.Split(new[] { "===FILE_START===" }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var section in sections)
            {
                if (!section.Contains("===FILE_END===")) continue;

                var change = new FileChange();

                change.FilePath = ExtractBetween(section, null, "===ORIGINAL===")?.Trim();
                change.OriginalCodeSnippet = ExtractBetween(section, "===ORIGINAL===", "===NEW===")?.Trim() ?? "";
                change.NewCodeSnippet = ExtractBetween(section, "===NEW===", "===EXPLAIN===")?.Trim() ?? "";
                change.Explain = ExtractBetween(section, "===EXPLAIN===", "===FILE_END===")?.Trim() ?? "";

                if (!string.IsNullOrEmpty(change.FilePath) && !string.IsNullOrEmpty(change.NewCodeSnippet))
                {
                    changes.Add(change);
                }
            }

            return changes;
        }

        private string ExtractBetween(string text, string startMarker, string endMarker)
        {
            int start = startMarker != null ? text.IndexOf(startMarker) : 0;
            if (start < 0) return null;

            if (startMarker != null) start += startMarker.Length;

            int end = text.IndexOf(endMarker, start);
            if (end < 0) return null;

            return text.Substring(start, end - start);
        }
    }
}
