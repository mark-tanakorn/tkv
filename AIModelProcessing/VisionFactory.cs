using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Numerics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static System.Net.Mime.MediaTypeNames;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Biz.TKV.AIModelProcess.AIModelProcessing
{
    public class VisionFactory
    {
        private string _hostAPIUrl = "https://localhost/api/generate";
        private string _Model = "qwen2.5:7b";
        private double _Temperature = 0;
        private double _FrequencyPenalty = 0;
        private double _PresencePenalty = 0;
        private int _MaxTokens = 200;
        private double _TopP = 0.95;
        private int _TopK = 50;
        private string _PromptRules = string.Empty;
        private int _defaultTimeoutSeconds = 1200; // configurable default
        private readonly HttpClient _httpClient;

        public VisionFactory(VisionModel visionModel)
        {
            _httpClient = new HttpClient();

            // Use per-request CancellationToken for timeouts; keep client timeout stable to allow concurrency
            _httpClient.Timeout = Timeout.InfiniteTimeSpan;
            if (visionModel != null)
            {
                if (!string.IsNullOrWhiteSpace(visionModel.HostAPIUrl))
                    _hostAPIUrl = string.Join("/", new string[] { visionModel.HostAPIUrl.TrimEnd('/'), "v1/chat/completions" });
                if (!string.IsNullOrWhiteSpace(visionModel.Model))
                    _Model = visionModel.Model;
                if (!string.IsNullOrWhiteSpace(visionModel.PromptRules))
                    _PromptRules = visionModel.PromptRules;

                // Only assign if caller provided valid values; else keep defaults
                if (visionModel.Temperature > 0)
                    _Temperature = visionModel.Temperature;
                if (visionModel.FrequencyPenalty > 0)
                    _FrequencyPenalty = visionModel.FrequencyPenalty;
                if (visionModel.PresencePenalty > 0)
                    _PresencePenalty = visionModel.PresencePenalty;
                if (visionModel.MaxTokens > 0)
                    _MaxTokens = visionModel.MaxTokens;
                if (visionModel.TopP > 0 && visionModel.TopP <= 1)
                    _TopP = visionModel.TopP;
                if (visionModel.TopK > 0)
                    _TopK = visionModel.TopK;
                if (visionModel.TimeoutSeconds > 0)
                    _defaultTimeoutSeconds = visionModel.TimeoutSeconds;
            }
        }

        // Async implementation — preferred
        public async Task<VisionResponse> GetVisionTextAsync(string imageBase64, int? timeoutSeconds = null)
        {
            var url = _hostAPIUrl;
            VisionResponse responseData = new VisionResponse();

            if (string.IsNullOrWhiteSpace(imageBase64))
            {
                responseData.StatusCode = "I don't receive any image from you.";
                return responseData;
            }

            string base64DataUri = "data:image/png;base64," + imageBase64;

            // Build messages for a single request
            var messages = new List<object>
            {
                new
                {
                    role = "system",
                    content = @"
                        You are a professional visual art analyst.
                        Describe each unique element once, avoid restating or paraphrasing.
                        Be concise, factual, and analytical.
                        Use only commas and full stops.
                        Never loop or repeat single words or phrases."
                },
                new
                {
                    role = "user",
                    content = new object[] {
                            new { type = "text", text =_PromptRules },
                            new { type = "image_url", image_url = new { url = base64DataUri } } 
                    }
                }
            };

            var effectiveTimeout = (timeoutSeconds.HasValue && timeoutSeconds.Value > 0)
                ? timeoutSeconds.Value
                : _defaultTimeoutSeconds;

            // local function to send one request and parse the response
            async Task<(bool Ok, Newtonsoft.Json.Linq.JObject? Obj, string Content, string Error)> SendOnceAsync()
            {
                var requestData = new
                {
                    model = _Model,
                    messages = messages.ToArray(),
                    // OpenAI top-level params
                    temperature = _Temperature,
                    top_p = _TopP,
                    n = 1,
                    max_tokens = _MaxTokens,
                    presence_penalty = _PresencePenalty,
                    frequency_penalty = _FrequencyPenalty,
                    stream = false
                };

                var jsonContent = Newtonsoft.Json.JsonConvert.SerializeObject(requestData);
                using var requestMessage = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(jsonContent, Encoding.UTF8, "application/json")
                };
                requestMessage.Headers.Accept.Clear();
                requestMessage.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(effectiveTimeout));
                var resp = await _httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
                responseData.StatusCode = resp.StatusCode.ToString();
                var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (!resp.IsSuccessStatusCode)
                {
                    var reason = resp.ReasonPhrase ?? resp.StatusCode.ToString();
                    return (false, null, string.Empty, $"{(int)resp.StatusCode} ({reason})");
                }

                // parse single JSON object (non-stream)
                var jObj = Newtonsoft.Json.Linq.JObject.Parse(body);
                var choices = jObj["choices"] as Newtonsoft.Json.Linq.JArray;
                var firstChoice = choices != null && choices.Count > 0 ? (Newtonsoft.Json.Linq.JObject)choices[0] : null;
                var messageContent = firstChoice?["message"]?["content"]?.ToString() ?? firstChoice?["text"]?.ToString() ?? string.Empty;
                return (true, jObj, messageContent, string.Empty);
            }

            try
            {
                var (ok, jObj, content, error) = await SendOnceAsync();
                if (!ok)
                {
                    responseData.StatusCode = error;
                    return responseData;
                }

                responseData.VisionText = SanitizeEmpty(CleanArtifacts(content));

                // record usage
                if (jObj != null)
                {
                    var usage = jObj["usage"] as Newtonsoft.Json.Linq.JObject;
                    responseData.PromptToken = usage?["prompt_tokens"]?.ToObject<int?>() ?? 0;
                    responseData.CompletionToken = usage?["completion_tokens"]?.ToObject<int?>() ?? 0;
                    responseData.TotalToken = usage?["total_tokens"]?.ToObject<int?>() ?? 0;
                }

                SetTruncationInfo(responseData, jObj, _MaxTokens);
                return responseData;
            }
            catch (OperationCanceledException)
            {
                responseData.StatusCode = "Request timed out";
                return responseData;
            }
            catch (HttpRequestException httpEx)
            {
                if (httpEx.StatusCode.HasValue)
                {
                    responseData.StatusCode = $"{(int)httpEx.StatusCode.Value} ({httpEx.StatusCode.Value})";
                }
                else
                {
                    responseData.StatusCode = httpEx.Message;
                }
            }
            catch (Exception ex)
            {
                responseData.StatusCode = ex.Message;
            }

            return responseData;
        }

        // Synchronous wrapper for backward compatibility; prefer the async API.
        public VisionResponse GetVisionText(string imageBase64)
        {
            // use configured default timeout
            return GetVisionTextAsync(imageBase64, _defaultTimeoutSeconds).GetAwaiter().GetResult();
        }

        private static string SanitizeEmpty(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var t = text.Trim();
            var lower = t.ToLowerInvariant();
            // common placeholders that should be treated as empty
            string[] placeholders = new[] { "blank", "none", "n/a", "na", "no content", "nothing", "empty", "unknown" };
            if (placeholders.Contains(lower)) return string.Empty;
            // if only punctuation or quotes
            if (t.All(ch => char.IsWhiteSpace(ch) || char.IsPunctuation(ch))) return string.Empty;
            return t;
        }

        // Remove common chat-template artifacts like <|im_start|>
        private static string CleanArtifacts(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            // explicit tokens
            string[] tokens = new[]
            {
                "<|im_start|>", "<|im_end|>", "<|assistant|>", "<|system|>", "<|user|>",
                "<|endoftext|>", "<|eot_id|>", "<|eom_id|>", "<s>", "</s>"
            };
            foreach (var tok in tokens)
                text = text.Replace(tok, string.Empty, StringComparison.OrdinalIgnoreCase);

            // collapse leftover multiple spaces/newlines
            text = System.Text.RegularExpressions.Regex.Replace(text, "[\u0000-\u001F]+", " ");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
            return text;
        }

        private void SetTruncationInfo(VisionResponse responseData, Newtonsoft.Json.Linq.JObject? lastObj = null, int requestedMaxTokens = 0)
        {
            if (responseData == null) return;
            var (isTruncated, reason) = ResponseCompletenessChecker.IsLikelyTruncated(responseData.VisionText ?? string.Empty, lastObj, requestedMaxTokens: requestedMaxTokens);
            responseData.IsTruncated = isTruncated;
            responseData.TruncationReason = reason;
        }
    }

    public class VisionModel
    {
        public string HostAPIUrl { get; set; }
        public string Model { get; set; }
        public double Temperature { get; set; }
        public int MaxTokens { get; set; } // retained for compatibility
        public double TopP { get; set; }
        public int TopK { get; set; }
        public double FrequencyPenalty { get; set; }
        public double PresencePenalty { get; set; }
        public string PromptRules { get; set; }
        public int TimeoutSeconds { get; set; } // new configurable timeout
    }

    public class VisionResponse
    {
        public string StatusCode { get; set; }
        public string VisionText { get; set; }
        public int? PromptToken { get; set; }
        public int? CompletionToken { get; set; }
        public int? TotalToken { get; set; }

        // New fields to indicate possible truncation (no MaxOutputTokens required)
        public bool IsTruncated { get; set; }
        public string TruncationReason { get; set; } = string.Empty;
    }

    // Inline helper: checks if a response is likely truncated using API metadata and simple heuristics.
    internal static class ResponseCompletenessChecker
    {
        public static (bool IsTruncated, string Reason) IsLikelyTruncated(string text, Newtonsoft.Json.Linq.JObject? lastObj = null, int requestedMaxTokens = 0)
        {
            int outputChars = string.IsNullOrEmpty(text) ? 0 : text.Length;
            int outputTokenEstimate = outputChars == 0 ? 0 : (int)Math.Ceiling(outputChars / 4.0);

            int? promptTokens = null;
            int? completionTokens = null;
            int? totalTokens = null;

            string WithMetrics(string baseReason, bool truncated)
            {
                if (!truncated)
                    return baseReason;
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(baseReason)) parts.Add(baseReason);
                parts.Add($"prompt_tokens={promptTokens?.ToString() ?? "?"}");
                parts.Add($"completion_tokens={completionTokens?.ToString() ?? "?"}");
                parts.Add($"total_tokens={totalTokens?.ToString() ?? "?"}");
                if (requestedMaxTokens > 0) parts.Add($"requested_max={requestedMaxTokens}");
                parts.Add($"output_chars={outputChars}");
                parts.Add($"token_estimate~={outputTokenEstimate}");
                return string.Join("; ", parts);
            }

            // 1) Prefer API metadata (finish_reason / stop_reason / truncated)
            if (lastObj != null)
            {
                // usage if present
                var usage = lastObj["usage"] as Newtonsoft.Json.Linq.JObject;
                promptTokens = usage?["prompt_tokens"]?.ToObject<int?>();
                completionTokens = usage?["completion_tokens"]?.ToObject<int?>();
                totalTokens = usage?["total_tokens"]?.ToObject<int?>() ?? lastObj["total_tokens"]?.ToObject<int?>();

                // OpenAI chat/completions: choices[0].finish_reason
                var choices = lastObj["choices"] as Newtonsoft.Json.Linq.JArray;
                string? choiceFinish = null;
                if (choices != null && choices.Count > 0)
                {
                    var first = choices[0] as Newtonsoft.Json.Linq.JObject;
                    choiceFinish = first?["finish_reason"]?.ToString();
                }

                var finishToken = choiceFinish
                                   ?? (lastObj["finish_reason"]
                                       ?? lastObj["stop_reason"]
                                       ?? lastObj["reason"]
                                       ?? lastObj["truncated"]
                                       ?? lastObj["done"])?.ToString();
                if (!string.IsNullOrEmpty(finishToken))
                {
                    var f = finishToken.ToLowerInvariant();
                    if (f.Contains("length") || f.Contains("max") || f.Contains("trunc") || f.Contains("timeout"))
                        return (true, WithMetrics($"finish_reason={finishToken}", true));
                    if (f.Contains("stop") || f.Contains("complete") || f.Contains("eos") || f.Contains("done"))
                        return (false, $"finish_reason={finishToken}");
                }

                var truncatedFlag = lastObj["truncated"]?.ToObject<bool?>();
                if (truncatedFlag.HasValue)
                    return (truncatedFlag.Value, WithMetrics("truncated_flag", truncatedFlag.Value));

                // token-based check: prefer completion_tokens against requestedMaxTokens
                if (requestedMaxTokens > 0)
                {
                    if (completionTokens.HasValue && completionTokens.Value >= requestedMaxTokens)
                        return (true, WithMetrics($"completion_tokens={completionTokens.Value} >= requested_max={requestedMaxTokens}", true));
                    if (totalTokens.HasValue && totalTokens.Value >= requestedMaxTokens)
                        return (true, WithMetrics($"tokens_used={totalTokens.Value} >= requested_max={requestedMaxTokens}", true));
                }
            }

            // 2) Heuristics on text
            if (string.IsNullOrWhiteSpace(text)) return (false, "empty_text");

            var s = text.TrimEnd();

            // obvious truncation markers
            if (s.EndsWith("...") || s.EndsWith("..") || s.EndsWith("-") || s.EndsWith("/") || s.EndsWith("\\"))
                return (true, WithMetrics("ends_with_truncation_marker", true));

            // If output is long and lacks sentence terminator, suspect truncation
            bool hasEndPunct = s.Length > 0 && ".!?。！？".IndexOf(s[s.Length - 1]) >= 0;
            if (!hasEndPunct && s.Length > 120)
                return (true, WithMetrics("no_sentence_terminator_on_long_output", true));

            // detect short cut final token (e.g., 'exampl')
            var tokens = System.Text.RegularExpressions.Regex.Split(s, @"\s+");
            var lastToken = tokens.Length > 0 ? tokens[tokens.Length - 1] : string.Empty;
            if (lastToken.Length > 0 && lastToken.Length <= 3 && s.Length > 60)
            {
                return (true, WithMetrics($"short_last_token='{lastToken}'", true));
            }

            return (false, "heuristics_ok");
        }
    }
}
