using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WebAICoder.AiAssistantWindow
{
    /// <summary>
    /// Interaction logic for AiAssistantWindowControl.
    /// </summary>
    public partial class AiAssistantWindowControl : UserControl
    {
        // История чатов по путям решений
        private static readonly Dictionary<string, List<ChatMessage>> ChatHistories =
            new Dictionary<string, List<ChatMessage>>();
        private List<ChatMessage> _currentChat;
        private string _currentSolutionPath;
        private RagService _ragService;
        string lastMessage;
        bool _isEditMode = false; // true = изменять код, false = спросить

        public AiAssistantWindowControl()
        {
            this.InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Находим текущее решение
            var dte = (DTE2)Package.GetGlobalService(typeof(SDTE));
            _currentSolutionPath = dte?.Solution?.FullName ?? "default";
            if (!ChatHistories.ContainsKey(_currentSolutionPath))
                ChatHistories[_currentSolutionPath] = new List<ChatMessage>();
            _currentChat = ChatHistories[_currentSolutionPath];
            RefreshChatView();

            // Инициализируем RAG-сервис
            _ragService = new RagService(dte, @"C:\путь\к\EmbeddingService.exe"); // Укажите реальный путь

            // Инициализируем WebView2
            InitializeWebViewAsync();
        }

        private async void InitializeWebViewAsync()
        {
            await DeepSeekWebView.EnsureCoreWebView2Async(null);
            DeepSeekWebView.CoreWebView2.Navigate("https://chat.deepseek.com/");
        }

        private void RefreshChatView()
        {
            ChatHistoryItems.ItemsSource = null;
            ChatHistoryItems.ItemsSource = _currentChat;
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            await SendPromptAsync();
        }

        private async void PromptTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) > 0)
            {
                await SendPromptAsync();
            }
        }

        private async Task SendPromptAsync()
        {
            var prompt = PromptTextBox.Text.Trim();
            if (string.IsNullOrEmpty(prompt)) return;

            StatusTextBlock.Text = "🔍 Анализирую проект...";

            // Добавляем сообщение пользователя в историю
            _currentChat.Add(new ChatMessage { Role = "User", Content = prompt });
            PromptTextBox.Clear();
            RefreshChatView();

            // Получаем контекст проекта через RAG
            StatusTextBlock.Text = "📂 Индексирую файлы...";
            string context = "";
            try
            {
                var dte = (DTE2)ServiceProvider.GlobalProvider.GetService(typeof(DTE));
                var service = new RagService(dte, @"d:\Projects\Me\WebAICoder\EmbeddingService\bin\Debug\net10.0\EmbeddingService.exe");
                context = await service.GetRelevantCodeAsync(prompt, StatusTextBlock);
            }
            catch (Exception ex)
            {
                // Обработка ошибок RAG
            }

            // Формируем полный промпт с контекстом
            string fullPrompt;
            if (_isEditMode)
                fullPrompt = $@"[Контекст проекта]
{context}

[Инструкция]
На основе предоставленного контекста проекта выполни следующую задачу:
{prompt}

ВАЖНО: 
- В путях файлов используй ПРЯМЫЕ слэши (/) вместо обратных (\)
- Пример правильного пути: ""D:/Projects/MyApp/Program.cs""
- OriginalCodeSnippet должен содержать ТОЛЬКО тот код, который будет заменён (без внешних скобок namespace/class)
- NewCodeSnippet должен содержать ТОЛЬКО новый код для замены (без внешних скобок)

Ответь в ТОЧНОСТИ в таком формате (это не JSON, а простой текст):

===FILE_START===
полный/путь/к/файлу
===ORIGINAL===
исходный код (если файл новый - оставь пустым)
===NEW===
новый код
===EXPLAIN===
краткое описание
===FILE_END===

Можно повторить сколько угодно раз. НИКАКОГО JSON. НИКАКИХ КАВЫЧЕК. Просто текст."";";
            else
                fullPrompt = $@"[Контекст проекта]
{context}

[Инструкция]
Ответь на следующий вопрос по проекту:
{prompt}";

/*Верни ответ в формате JSON:
{{
  ""answer"": ""твой ответ""
}}";*/

            // Отправляем в DeepSeek через WebView2
            StatusTextBlock.Text = "🤖 Отправляю в DeepSeek...";
            await SendToDeepSeek(fullPrompt);

            // Ждем ответа
            StatusTextBlock.Text = "⏳ Ожидаю ответ...";
            await Task.Delay(10000);
            var response = await WaitForDeepSeekResponse();

            // Применяем изменения
            if (_isEditMode)
            {
                // Применяем изменения
                StatusTextBlock.Text = "✏️ Применяю изменения...";
                ApplyChanges(response);
            }
            else
            {
                // Показываем ответ как обычное сообщение
                /*var answer = ExtractAnswer(response);
                _currentChat.Add(new ChatMessage
                {
                    Role = "AI",
                    Content = answer,
                    Timestamp = DateTime.Now
                });
                RefreshChatView();*/
            }

            StatusTextBlock.Text = "✅ Готово";
        }

        private async Task SendToDeepSeek(string prompt)
        {
            prompt = Uri.EscapeDataString(prompt);

            string script = $@"
                function inject1() {{
                    const decodedText = decodeURIComponent('{prompt}');

                    const editor = document.querySelector('textarea');
                    if (!editor) {{ alert('editor not found'); return; }}
                    editor.value = decodedText;
                    editor.dispatchEvent(new Event('input', {{ bubbles: true }}));

                    setTimeout(() => {{
                        editor.dispatchEvent(new KeyboardEvent('keydown', {{ keyCode: 13, bubbles: true }}));
                    }}, 500);
                }}
                inject1();";
            await DeepSeekWebView.CoreWebView2.ExecuteScriptAsync(script);
        }

        private async Task<string> WaitForDeepSeekResponse()
        {
            // Ожидаем появления нового сообщения в виртуальном списке
            while (true)
            {
                await Task.Delay(3000);
                try
                {
                    string script = @"(function() {
                        const msgs = document.querySelectorAll('.ds-virtual-list-visible-items > div[style=""--assistant-last-padding-bottom: 24px;""]');
                        if (msgs.length === 0) return '';
                        const lastMsg = msgs[msgs.length - 1];
                        const contentElements = lastMsg.querySelectorAll('.ds-assistant-message-main-content');
                        if (contentElements.length === 0) return '';
                        return contentElements[0].innerText || contentElements[0].textContent;
                    })();";

                    var result = await DeepSeekWebView.CoreWebView2.ExecuteScriptWithResultAsync(script);
                    if (result.Succeeded)
                    {
                        string json = null;
                        try
                        {
                            // ResultAsJson имеет вид "\"...\"", поэтому десериализуем
                            json = JsonConvert.DeserializeObject<string>(result.ResultAsJson);
                        }
                        catch
                        {
                            // Если вдруг не JSON, пробуем убрать внешние кавычки вручную
                            json = result.ResultAsJson.Trim('"').Replace("\\\"", "\"").Replace("\\n", "\n");
                        }
                        if (!string.IsNullOrEmpty(json))
                        {
                            if (json != lastMessage) lastMessage = json;
                            else return json;
                        }
                    }
                }
                catch { }
            }
            return "Превышено время ожидания ответа.";
        }

        private void ApplyChanges(string jsonResponse)
        {
            var dte = (DTE2)Package.GetGlobalService(typeof(SDTE));
            var applier = new ChangeApplier(dte, ServiceProvider.GlobalProvider);
            var diffs = applier.Apply(jsonResponse);
            if (diffs != null)
            {
                _currentChat.Add(new ChatMessage
                {
                    Role = "AI",
                    Content = "", // текста нет, только дифф
                    FileDiffs = diffs,
                    Timestamp = DateTime.Now
                });

                RefreshChatView();
            }
        }

        private void ClearContextButton_Click(object sender, RoutedEventArgs e)
        {
            _currentChat.Clear();
            RefreshChatView();
        }

        private void ModelSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DeepSeekWebView?.CoreWebView2 == null) return;
            var selectedModel = (ModelSelector.SelectedItem as ComboBoxItem)?.Content?.ToString();
            if (selectedModel == "DeepSeek")
            {
                DeepSeekWebView.CoreWebView2.Navigate("https://chat.deepseek.com/");
            }
            // Добавить другие модели позже
        }

        private bool IsJsonStringEnd(string json, int quoteIndex)
        {
            // Ищем следующий значимый символ после кавычки
            for (int i = quoteIndex + 1; i < json.Length; i++)
            {
                char c = json[i];

                // Пропускаем пробелы, табы, переносы
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r')
                    continue;

                // Структурные символы JSON — это конец строки
                if (c == ':' || c == ',' || c == '}' || c == ']')
                    return true;

                // Любой другой символ — это не конец строки (кавычка внутри)
                return false;
            }

            // Дошли до конца файла — это конец строки
            return true;
        }

        private void FileName_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is TextBlock textBlock && textBlock.Tag is string filePath)
            {
                OpenFileInVisualStudio(filePath);
            }
        }

        private void OpenFileInVisualStudio(string filePath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var dte = (DTE2)Package.GetGlobalService(typeof(SDTE));

                if (dte != null && File.Exists(filePath))
                {
                    // Открываем файл в редакторе
                    dte.ItemOperations.OpenFile(filePath, EnvDTE.Constants.vsViewKindCode);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Ошибка открытия файла: {ex.Message}");
            }
        }

        private void RevertButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is FileDiff diff)
            {
                RevertChange(diff);
            }
        }

        private void RevertChange(FileDiff diff)
        {
            try
            {
                if (string.IsNullOrEmpty(diff.OriginalFileContent))
                {
                    MessageBox.Show("Нет сохранённых данных для отката.", "Ошибка",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // Восстанавливаем оригинальный файл целиком
                File.WriteAllText(diff.FilePath, diff.OriginalFileContent, Encoding.UTF8);

                diff.IsReverted = true;
                RefreshChatView();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при отмене изменений:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ModeToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _isEditMode = !_isEditMode;

            if (_isEditMode)
            {
                ModeToggleButton.Content = "✏️ Изменить";
                ModeToggleButton.ToolTip = "Режим изменения кода";
            }
            else
            {
                ModeToggleButton.Content = "💬 Спросить";
                ModeToggleButton.ToolTip = "Режим вопроса по проекту";
            }
        }

        private string ExtractAnswer(string jsonResponse)
        {
            try
            {
                var response = JsonConvert.DeserializeObject<AskResponse>(jsonResponse);

                if (!string.IsNullOrEmpty(response?.Answer))
                {
                    var sb = new StringBuilder();
                    sb.AppendLine(response.Answer);

                    return sb.ToString();
                }
            }
            catch { }

            // Если не удалось распарсить — возвращаем как есть
            return jsonResponse;
        }
    }
}