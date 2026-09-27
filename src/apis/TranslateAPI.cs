using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Security.Cryptography;
using System.Diagnostics;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.apis
{
    public static class TranslateAPI
    {
        /*
         * The key of this field is used as the content for `translateAPIBox` in the `SettingPage`.
         * If you'd like to add a new API, please insert the key-value pair here.
         */
        public static readonly Dictionary<string, Func<string, CancellationToken, Task<string>>>
            TRANSLATE_FUNCTIONS = new()
        {
            { "Google", Google },
            { "Ollama", Ollama },
            { "OpenAI", OpenAI },
            { "LMStudio", LMStudio },
            { "DeepL", DeepL },
            { "OpenRouter", OpenRouter },
            { "Youdao", Youdao },
            { "MTranServer", MTranServer },
            { "Baidu", Baidu },
            { "LibreTranslate", LibreTranslate },
        };
        public static readonly List<string> LLM_BASED_APIS = new()
        {
            "Ollama", "OpenAI", "OpenRouter", "LMStudio"
        };
        public static readonly List<string> NO_CONFIG_APIS = new()
        {
            "Google"
        };

        public static Func<string, CancellationToken, Task<string>> TranslateFunction =>
            TRANSLATE_FUNCTIONS[Translator.Setting.ApiName];
        public static bool IsLLMBased => LLM_BASED_APIS.Contains(Translator.Setting.ApiName);
        public static bool IsLLMBasedApi(string apiName) => LLM_BASED_APIS.Contains(apiName);

        private static readonly AsyncLocal<TranslationSettingsSnapshot?> CurrentSnapshot = new();
        private static string Prompt => CurrentSnapshot.Value?.Prompt ?? Translator.Setting.Prompt;
        private static string TargetLanguage =>
            CurrentSnapshot.Value?.TargetLanguage ?? Translator.Setting.TargetLanguage;
        private static bool ContextAware =>
            CurrentSnapshot.Value?.ContextAware ?? Translator.Setting.ContextAware;
        private static IEnumerable<TranslationHistoryEntry> Contexts =>
            CurrentSnapshot.Value?.Contexts ?? Translator.Caption.AwareContexts;

        private static TConfig GetConfig<TConfig>(string apiName) where TConfig : TranslateAPIConfig =>
            CurrentSnapshot.Value?.ApiName == apiName
                ? (TConfig)CurrentSnapshot.Value.Config
                : (TConfig)Translator.Setting[apiName];

        public static async Task<string> TranslateAsync(
            TranslationSettingsSnapshot snapshot, string text, CancellationToken token = default)
        {
            TranslationSettingsSnapshot? previous = CurrentSnapshot.Value;
            CurrentSnapshot.Value = snapshot;
            try
            {
                return await TRANSLATE_FUNCTIONS[snapshot.ApiName](text, token);
            }
            finally
            {
                CurrentSnapshot.Value = previous;
            }
        }

        public static async Task<TranslationOutcome> TranslateOutcomeAsync(
            TranslationSettingsSnapshot snapshot, string text, CancellationToken token = default)
        {
            var stopwatch = Stopwatch.StartNew();
            using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                string result = await TranslateAsync(snapshot, text, requestTimeout.Token);
                return new TranslationOutcome(TranslationStatus.Succeeded, result,
                    string.Empty, string.Empty, (long)stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return Failed("Timeout", (long)stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (TranslationServiceException ex)
            {
                return Failed(ex.ErrorCode, (long)stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (HttpRequestException)
            {
                return Failed("NetworkError", (long)stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (JsonException)
            {
                return Failed("InvalidResponse", (long)stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (ResponseTooLargeException)
            {
                return Failed("ResponseTooLarge", (long)stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (Exception ex) when (ex is KeyNotFoundException or IndexOutOfRangeException or NullReferenceException or InvalidOperationException)
            {
                return Failed("InvalidResponse", (long)stopwatch.Elapsed.TotalMilliseconds);
            }
            catch (Exception)
            {
                return Failed("ServiceError", (long)stopwatch.Elapsed.TotalMilliseconds);
            }

            static TranslationOutcome Failed(string code, long elapsed) =>
                new(TranslationStatus.Failed, string.Empty, code, DiagnosticFor(code), elapsed);
        }

        private static string DiagnosticFor(string code) => code switch
        {
            "Timeout" => "The translation request exceeded the 8 second time limit.",
            "NetworkError" => "The translation service could not be reached.",
            "HttpStatus" => "The translation service returned an unsuccessful response.",
            "InvalidResponse" => "The translation service returned an unsupported response.",
            "ResponseTooLarge" => "The translation response exceeded the allowed size.",
            "ProviderRejected" => "The translation service rejected this request.",
            _ => "The translation service could not complete this request."
        };

        internal static HttpClient Client { get; set; } = new HttpClient()
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        private static async Task<HttpResponseMessage> SendAsync(
            HttpMethod method,
            string url,
            HttpContent? content,
            CancellationToken token,
            string? authenticationScheme = null,
            string? authenticationValue = null)
        {
            using var request = new HttpRequestMessage(method, url) { Content = content };
            if (!string.IsNullOrWhiteSpace(authenticationScheme) && authenticationValue != null)
                request.Headers.Authorization = new AuthenticationHeaderValue(authenticationScheme, authenticationValue);
            return await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        }

        private const int MaximumResponseBytes = 1024 * 1024;

        private static async Task<string> ReadResponseTextAsync(
            HttpResponseMessage response, CancellationToken token)
        {
            if (response.Content.Headers.ContentLength is long length && length > MaximumResponseBytes)
                throw new ResponseTooLargeException();
            await using Stream stream = await response.Content.ReadAsStreamAsync(token);
            using var output = new MemoryStream();
            byte[] buffer = new byte[8192];
            while (true)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(), token);
                if (read == 0)
                    break;
                if (output.Length + read > MaximumResponseBytes)
                    throw new ResponseTooLargeException();
                output.Write(buffer, 0, read);
            }
            return Encoding.UTF8.GetString(output.ToArray());
        }

        private sealed class ResponseTooLargeException : Exception { }

        private sealed class TranslationServiceException : Exception
        {
            public TranslationServiceException(string errorCode, string message, Exception? inner = null)
                : base(message, inner) => ErrorCode = errorCode;

            public string ErrorCode { get; }
        }

        public static async Task<string> OpenAI(string text, CancellationToken token = default)
        {
            var config = GetConfig<OpenAIConfig>("OpenAI");
            string language = OpenAIConfig.SupportedLanguages.TryGetValue(
                TargetLanguage, out var langValue) ? langValue : TargetLanguage;

            var messages = new List<BaseLLMConfig.Message>
            {
                new BaseLLMConfig.Message { role = "system", content = string.Format(Prompt, language) },
                new BaseLLMConfig.Message { role = "user", content = $"🔤 {text} 🔤" }
            };

            if (ContextAware)
            {
                int insertIndex = 1;
                foreach (var entry in Contexts)
                {
                    string translatedText = entry.TranslatedText;
                    if (!string.Equals(entry.Status, nameof(TranslationStatus.Succeeded), StringComparison.Ordinal))
                        continue;
                    translatedText = RegexPatterns.NoticePrefix().Replace(translatedText, "");

                    messages.InsertRange(insertIndex, [
                        new BaseLLMConfig.Message { role = "user", content = $"🔤 {entry.SourceText} 🔤" },
                        new BaseLLMConfig.Message { role = "assistant", content = $"{translatedText}" }
                    ]);
                    insertIndex += 2;
                }
            }

            HttpResponseMessage response;
            try
            {
                int fallbackIndex = 0;
                while (true)
                {
                    var requestData = LLMRequestDataFactory.Create(fallbackIndex,
                        config.ModelName, messages, config.Temperature);
                    string jsonContent = JsonSerializer.Serialize(requestData, requestData.GetType());
                    var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                    response = await SendAsync(HttpMethod.Post, TextUtil.NormalizeUrl(config.ApiUrl), content, token,
                        "Bearer", config.ApiKey);
                    if (response.StatusCode != HttpStatusCode.BadRequest &&
                        response.StatusCode != HttpStatusCode.UnprocessableEntity)
                        break;
                    response.Dispose();
                    await Task.Delay(15, token);

                    fallbackIndex++;
                    if (fallbackIndex >= LLMRequestDataFactory.FallbackCount)
                        break;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { throw new TranslationServiceException("NetworkError", "The translation service could not be reached.", ex); }

            using (response)
            if (response.IsSuccessStatusCode)
            {
                string responseString = await ReadResponseTextAsync(response, token);
                var responseObj = JsonSerializer.Deserialize<OpenAIConfig.Response>(responseString);
                string? output = responseObj?.choices?.FirstOrDefault()?.message?.content;
                if (string.IsNullOrWhiteSpace(output))
                    throw new TranslationServiceException("InvalidResponse", "The translation service returned no usable result.");
                return RegexPatterns.ModelThinking().Replace(output, "");
            }
            else
                throw new TranslationServiceException("HttpStatus", "The translation service returned an unsuccessful response.");
        }

        public static async Task<string> Ollama(string text, CancellationToken token = default)
        {
            var config = GetConfig<OllamaConfig>("Ollama");
            string language = OllamaConfig.SupportedLanguages.TryGetValue(
                TargetLanguage, out var langValue) ? langValue : TargetLanguage;
            string apiUrl = TextUtil.NormalizeUrl(config.ApiUrl + "/api/chat");

            var messages = new List<BaseLLMConfig.Message>
            {
                new BaseLLMConfig.Message { role = "system", content = string.Format(Prompt, language) },
                new BaseLLMConfig.Message { role = "user", content = $"🔤 {text} 🔤" }
            };

            if (ContextAware)
            {
                int insertIndex = 1;
                foreach (var entry in Contexts)
                {
                    string translatedText = entry.TranslatedText;
                    if (!string.Equals(entry.Status, nameof(TranslationStatus.Succeeded), StringComparison.Ordinal))
                        continue;
                    translatedText = RegexPatterns.NoticePrefix().Replace(translatedText, "");

                    messages.InsertRange(insertIndex, [
                        new BaseLLMConfig.Message { role = "user", content = $"🔤 {entry.SourceText} 🔤" },
                        new BaseLLMConfig.Message { role = "assistant", content = $"{translatedText}" }
                    ]);
                    insertIndex += 2;
                }
            }

            var requestData = LLMRequestDataFactory.Create("Ollama", config.ModelName, messages, config.Temperature);
            requestData.keep_alive = config.keep_alive;
            string jsonContent = JsonSerializer.Serialize(requestData, requestData.GetType());
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await SendAsync(HttpMethod.Post, apiUrl, content, token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { throw new TranslationServiceException("NetworkError", "The translation service could not be reached.", ex); }

            using (response)
            if (response.IsSuccessStatusCode)
            {
                string responseString = await ReadResponseTextAsync(response, token);
                var responseObj = JsonSerializer.Deserialize<OllamaConfig.Response>(responseString);
                string? output = responseObj?.message?.content;
                if (string.IsNullOrWhiteSpace(output))
                    throw new TranslationServiceException("InvalidResponse", "The translation service returned no usable result.");
                return RegexPatterns.ModelThinking().Replace(output, "");
            }
            else
                throw new TranslationServiceException("HttpStatus", "The translation service returned an unsuccessful response.");
        }

        public static async Task<string> LMStudio(string text, CancellationToken token = default)
        {
            var config = GetConfig<LMStudioConfig>("LMStudio");
            string language = LMStudioConfig.SupportedLanguages.TryGetValue(
                TargetLanguage, out var langValue) ? langValue : TargetLanguage;
            string apiUrl = TextUtil.NormalizeUrl(config.ApiUrl) + "/chat";

            string systemPrompt = string.Format(Prompt, language);

            // Build input with optional context
            string input = $"🔤 {text} 🔤";
            if (ContextAware)
            {
                var contextLines = new List<string>();
                foreach (var entry in Contexts)
                {
                    string translatedText = entry.TranslatedText;
                    if (!string.Equals(entry.Status, nameof(TranslationStatus.Succeeded), StringComparison.Ordinal))
                        continue;
                    translatedText = RegexPatterns.NoticePrefix().Replace(translatedText, "");
                    contextLines.Add($"🔤 {entry.SourceText} 🔤 → {translatedText}");
                }
                if (contextLines.Count > 0)
                    input = string.Join("\n", contextLines) + "\n" + input;
            }

            var requestData = new
            {
                model = config.ModelName,
                system_prompt = systemPrompt,
                input = input,
                temperature = config.Temperature
            };

            string jsonContent = JsonSerializer.Serialize(requestData);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await SendAsync(HttpMethod.Post, apiUrl, content, token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { throw new TranslationServiceException("NetworkError", "The translation service could not be reached.", ex); }

            using (response)
            if (response.IsSuccessStatusCode)
            {
                string responseString = await ReadResponseTextAsync(response, token);
                using var doc = JsonDocument.Parse(responseString);
                var root = doc.RootElement;

                // LMStudio native /api/v1/chat response:
                // { "output": [ { "type": "message", "content": "..." }, ... ] }
                if (root.TryGetProperty("output", out var outputArray) &&
                    outputArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in outputArray.EnumerateArray())
                    {
                        if (item.TryGetProperty("type", out var typeProp) &&
                            typeProp.GetString() == "message" &&
                            item.TryGetProperty("content", out var contentProp))
                        {
                            return RegexPatterns.ModelThinking().Replace(contentProp.GetString() ?? "", "");
                        }
                    }
                }

                throw new TranslationServiceException("InvalidResponse", "The translation service returned an unsupported response.");
            }
            else { throw new TranslationServiceException("HttpStatus", "The translation service returned an unsuccessful response."); }
        }

        public static async Task<string> OpenRouter(string text, CancellationToken token = default)
        {
            var config = GetConfig<OpenRouterConfig>("OpenRouter");
            string language = OpenRouterConfig.SupportedLanguages.TryGetValue(
                TargetLanguage, out var langValue) ? langValue : TargetLanguage;
            string apiUrl = "https://openrouter.ai/api/v1/chat/completions";

            var messages = new List<BaseLLMConfig.Message>
            {
                new BaseLLMConfig.Message { role = "system", content = string.Format(Prompt, language) },
                new BaseLLMConfig.Message { role = "user", content = $"🔤 {text} 🔤" }
            };

            if (ContextAware)
            {
                int insertIndex = 1;
                foreach (var entry in Contexts)
                {
                    string translatedText = entry.TranslatedText;
                    if (!string.Equals(entry.Status, nameof(TranslationStatus.Succeeded), StringComparison.Ordinal))
                        continue;
                    translatedText = RegexPatterns.NoticePrefix().Replace(translatedText, "");

                    messages.InsertRange(insertIndex, [
                        new BaseLLMConfig.Message { role = "user", content = $"🔤 {entry.SourceText} 🔤" },
                        new BaseLLMConfig.Message { role = "assistant", content = $"{translatedText}" }
                    ]);
                    insertIndex += 2;
                }
            }

            var requestData = LLMRequestDataFactory.Create("OpenRouter", config.ModelName, messages, config.Temperature);

            string jsonContent = JsonSerializer.Serialize(requestData, requestData.GetType());
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await SendAsync(HttpMethod.Post, apiUrl, content, token, "Bearer", config?.ApiKey);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { throw new TranslationServiceException("NetworkError", "The translation service could not be reached.", ex); }

            using (response)
            if (response.IsSuccessStatusCode)
            {
                var responseContent = await ReadResponseTextAsync(response, token);
                var jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);
                var output = jsonResponse.GetProperty("choices")[0]
                                         .GetProperty("message")
                                         .GetProperty("content")
                                         .GetString() ?? string.Empty;
                return RegexPatterns.ModelThinking().Replace(output, "");
            }
            else
                throw new TranslationServiceException("HttpStatus", "The translation service returned an unsuccessful response.");
        }

        public static async Task<string> Google(string text, CancellationToken token = default)
        {
            var language = TargetLanguage;

            string encodedText = Uri.EscapeDataString(text);
            var url = $"https://clients5.google.com/translate_a/t?" +
                      $"client=dict-chrome-ex&sl=auto&" +
                      $"tl={language}&" +
                      $"q={encodedText}";

            HttpResponseMessage response;
            try
            {
                response = await SendAsync(HttpMethod.Get, url, null, token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { throw new TranslationServiceException("NetworkError", "The translation service could not be reached.", ex); }

            using (response)
            if (response.IsSuccessStatusCode)
            {
                string responseString = await ReadResponseTextAsync(response, token);

                var responseObj = JsonSerializer.Deserialize<List<List<string>>>(responseString);
                string? translatedText = responseObj?.FirstOrDefault()?.FirstOrDefault();
                return string.IsNullOrWhiteSpace(translatedText)
                    ? throw new TranslationServiceException("InvalidResponse", "The translation service returned no usable result.")
                    : translatedText;
            }
            else
                throw new TranslationServiceException("HttpStatus", "The translation service returned an unsuccessful response.");
        }
        public static async Task<string> DeepL(string text, CancellationToken token = default)
        {
            var config = GetConfig<DeepLConfig>("DeepL");
            string language = DeepLConfig.SupportedLanguages.TryGetValue(
                TargetLanguage, out var langValue) ? langValue : TargetLanguage;
            string apiUrl = TextUtil.NormalizeUrl(config.ApiUrl);

            var requestData = new
            {
                text = new[] { text },
                target_lang = language
            };

            string jsonContent = JsonSerializer.Serialize(requestData);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");


            HttpResponseMessage response;
            try
            {
                response = await SendAsync(HttpMethod.Post, apiUrl, content, token,
                    "DeepL-Auth-Key", config?.ApiKey);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { throw new TranslationServiceException("NetworkError", "The translation service could not be reached.", ex); }

            using (response)
            if (response.IsSuccessStatusCode)
            {
                string responseString = await ReadResponseTextAsync(response, token);
                using var doc = JsonDocument.Parse(responseString);

                if (doc.RootElement.TryGetProperty("translations", out var translations) &&
                    translations.ValueKind == JsonValueKind.Array && translations.GetArrayLength() > 0)
                {
                    string? translatedText = translations[0].GetProperty("text").GetString();
                    if (!string.IsNullOrWhiteSpace(translatedText))
                        return translatedText;
                }
                throw new TranslationServiceException("InvalidResponse", "The translation service returned no usable result.");
            }
            else
                throw new TranslationServiceException("HttpStatus", "The translation service returned an unsuccessful response.");
        }


        public static async Task<string> Youdao(string text, CancellationToken token = default)
        {
            var config = GetConfig<YoudaoConfig>("Youdao");
            string language = YoudaoConfig.SupportedLanguages.TryGetValue(
                TargetLanguage, out var langValue) ? langValue : TargetLanguage;

            string salt = DateTime.Now.Millisecond.ToString();
            string sign = BitConverter.ToString(
                MD5.Create().ComputeHash(
                    Encoding.UTF8.GetBytes($"{config.AppKey}{text}{salt}{config.AppSecret}"))).Replace("-", "").ToLower();

            var parameters = new Dictionary<string, string>
            {
                ["q"] = text,
                ["from"] = "auto",
                ["to"] = language,
                ["appKey"] = config.AppKey,
                ["salt"] = salt,
                ["sign"] = sign
            };

            var content = new FormUrlEncodedContent(parameters);
            HttpResponseMessage response;
            try
            {
                response = await SendAsync(HttpMethod.Post, config.ApiUrl, content, token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { throw new TranslationServiceException("NetworkError", "The translation service could not be reached.", ex); }

            using (response)
            if (response.IsSuccessStatusCode)
            {
                string responseString = await ReadResponseTextAsync(response, token);
                var responseObj = JsonSerializer.Deserialize<YoudaoConfig.TranslationResult>(responseString);
                if (responseObj is null || responseObj.errorCode is null)
                    throw new TranslationServiceException("InvalidResponse", "The translation service returned no usable result.");

                if (responseObj.errorCode != "0")
                    throw new TranslationServiceException("ProviderRejected", "The translation service rejected the request.");

                return responseObj.translation?.FirstOrDefault() ?? throw new TranslationServiceException("InvalidResponse", "The translation service returned no text.");
            }
            else
            {
                throw new TranslationServiceException("HttpStatus", "The translation service returned an unsuccessful response.");
            }
        }

        public static async Task<string> MTranServer(string text, CancellationToken token = default)
        {
            var config = GetConfig<MTranServerConfig>("MTranServer");
            string targetLanguage = MTranServerConfig.SupportedLanguages.TryGetValue(
                TargetLanguage, out var langValue) ? langValue : TargetLanguage;
            string sourceLanguage = config.SourceLanguage;
            string apiUrl = TextUtil.NormalizeUrl(config.ApiUrl);

            var requestData = new
            {
                text = text,
                to = targetLanguage,
                from = sourceLanguage
            };

            string jsonContent = JsonSerializer.Serialize(requestData);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await SendAsync(HttpMethod.Post, apiUrl, content, token, "Bearer", config?.ApiKey);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { throw new TranslationServiceException("NetworkError", "The translation service could not be reached.", ex); }

            using (response)
            if (response.IsSuccessStatusCode)
            {
                string responseString = await ReadResponseTextAsync(response, token);
                var responseObj = JsonSerializer.Deserialize<MTranServerConfig.Response>(responseString);
                return string.IsNullOrWhiteSpace(responseObj?.result)
                    ? throw new TranslationServiceException("InvalidResponse", "The translation service returned no usable result.")
                    : responseObj.result;
            }
            else
                throw new TranslationServiceException("HttpStatus", "The translation service returned an unsuccessful response.");
        }

        public static async Task<string> Baidu(string text, CancellationToken token = default)
        {
            var config = GetConfig<BaiduConfig>("Baidu");
            string language = BaiduConfig.SupportedLanguages.TryGetValue(
                TargetLanguage, out var langValue) ? langValue : TargetLanguage;

            string salt = DateTime.Now.Millisecond.ToString();
            string sign = BitConverter.ToString(
                MD5.Create().ComputeHash(
                    Encoding.UTF8.GetBytes($"{config.AppId}{text}{salt}{config.AppSecret}"))).Replace("-", "").ToLower();

            var parameters = new Dictionary<string, string>
            {
                ["q"] = text,
                ["from"] = "auto",
                ["to"] = language,
                ["appid"] = config.AppId,
                ["salt"] = salt,
                ["sign"] = sign
            };

            var content = new FormUrlEncodedContent(parameters);
            HttpResponseMessage response;
            try
            {
                response = await SendAsync(HttpMethod.Post, config.ApiUrl, content, token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { throw new TranslationServiceException("NetworkError", "The translation service could not be reached.", ex); }

            using (response)
            if (response.IsSuccessStatusCode)
            {
                string responseString = await ReadResponseTextAsync(response, token);
                var responseObj = JsonSerializer.Deserialize<BaiduConfig.TranslationResult>(responseString);
                if (responseObj is null)
                    throw new TranslationServiceException("InvalidResponse", "The translation service returned no usable result.");

                if (responseObj.error_code is not null && responseObj.error_code != "0")
                    throw new TranslationServiceException("ProviderRejected", "The translation service rejected the request.");

                return responseObj.trans_result?.FirstOrDefault()?.dst ?? throw new TranslationServiceException("InvalidResponse", "The translation service returned no text.");
            }
            else
            {
                throw new TranslationServiceException("HttpStatus", "The translation service returned an unsuccessful response.");
            }
        }

        public static async Task<string> LibreTranslate(string text, CancellationToken token = default)
        {
            var config = GetConfig<LibreTranslateConfig>("LibreTranslate");
            string targetLanguage = LibreTranslateConfig.SupportedLanguages.TryGetValue(
                TargetLanguage, out var langValue) ? langValue : TargetLanguage;
            string apiUrl = TextUtil.NormalizeUrl(config.ApiUrl);

            var requestData = new
            {
                q = text,
                target = targetLanguage,
                source = "auto",
                format = "text",
                api_key = config?.ApiKey
            };

            string jsonContent = JsonSerializer.Serialize(requestData);
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await SendAsync(HttpMethod.Post, apiUrl, content, token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { throw new TranslationServiceException("NetworkError", "The translation service could not be reached.", ex); }

            using (response)
            if (response.IsSuccessStatusCode)
            {
                string responseString = await ReadResponseTextAsync(response, token);
                var responseObj = JsonSerializer.Deserialize<LibreTranslateConfig.Response>(responseString);
                return string.IsNullOrWhiteSpace(responseObj?.translatedText)
                    ? throw new TranslationServiceException("InvalidResponse", "The translation service returned no usable result.")
                    : responseObj.translatedText;
            }
            else
                throw new TranslationServiceException("HttpStatus", "The translation service returned an unsuccessful response.");
        }
    }

    public class ConfigDictConverter : JsonConverter<Dictionary<string, List<TranslateAPIConfig>>>
    {
        public override Dictionary<string, List<TranslateAPIConfig>> Read(
            ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
                throw new JsonException("Expected a StartObject token.");
            var configs = new Dictionary<string, List<TranslateAPIConfig>>();

            reader.Read();
            while (reader.TokenType == JsonTokenType.PropertyName)
            {
                string key = reader.GetString() ?? throw new JsonException("A configuration name cannot be null.");
                reader.Read();

                var configType = Type.GetType($"LiveCaptionsTranslator.models.{key}Config");
                TranslateAPIConfig config;

                if (reader.TokenType == JsonTokenType.StartArray)
                {
                    var list = new List<TranslateAPIConfig>();
                    reader.Read();

                    while (reader.TokenType != JsonTokenType.EndArray)
                    {
                        object? value = configType != null && typeof(TranslateAPIConfig).IsAssignableFrom(configType)
                            ? JsonSerializer.Deserialize(ref reader, configType, options)
                            : JsonSerializer.Deserialize(ref reader, typeof(TranslateAPIConfig), options);
                        config = value as TranslateAPIConfig
                            ?? throw new JsonException($"Configuration '{key}' could not be read.");

                        UnprotectSecrets(config);

                        list.Add(config);
                        reader.Read();
                    }
                    configs[key] = list;
                }
                else
                    throw new JsonException("Expected a StartObject token or a StartArray token.");

                reader.Read();
            }

            if (reader.TokenType != JsonTokenType.EndObject)
                throw new JsonException("Expected an EndObject token.");
            return configs;
        }

        public override void Write(
            Utf8JsonWriter writer, Dictionary<string, List<TranslateAPIConfig>> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            foreach (var kvp in value)
            {
                writer.WritePropertyName(kvp.Key);
                var configType = Type.GetType($"LiveCaptionsTranslator.models.{kvp.Key}Config");

                if (kvp.Value is IEnumerable<TranslateAPIConfig> configList)
                {
                    writer.WriteStartArray();
                    foreach (var config in configList)
                    {
                        Type type = configType != null && typeof(TranslateAPIConfig).IsAssignableFrom(configType)
                            ? configType : typeof(TranslateAPIConfig);
                        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(config, type, options));
                        writer.WriteStartObject();
                        foreach (JsonProperty property in document.RootElement.EnumerateObject())
                        {
                            writer.WritePropertyName(property.Name);
                            if (property.Name is "ApiKey" or "AppSecret" && property.Value.ValueKind == JsonValueKind.String)
                                writer.WriteStringValue(SecretProtector.Protect(property.Value.GetString() ?? string.Empty));
                            else
                                property.Value.WriteTo(writer);
                        }
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                }
                else
                    throw new JsonException($"Unsupported config type: {kvp.Value.GetType()}");
            }
            writer.WriteEndObject();
        }

        private static void UnprotectSecrets(TranslateAPIConfig config)
        {
            foreach (var property in config.GetType().GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
            {
                if (property.Name is not ("ApiKey" or "AppSecret") || property.PropertyType != typeof(string) || !property.CanRead || !property.CanWrite)
                    continue;
                string value = (string?)property.GetValue(config) ?? string.Empty;
                if (value.StartsWith("dpapi:v1:", StringComparison.Ordinal) || value.Length != 0)
                    property.SetValue(config, SecretProtector.Unprotect(value));
            }

            if (config.AdditionalData.TryGetValue("AppSecret", out JsonElement additionalSecret) &&
                additionalSecret.ValueKind == JsonValueKind.String)
            {
                string value = additionalSecret.GetString() ?? string.Empty;
                if (value.Length != 0)
                    config.AdditionalData["AppSecret"] = JsonSerializer.SerializeToElement(SecretProtector.Unprotect(value));
            }
        }
    }
}
