using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Biz.TKV.AIModelProcess.Tools
{
    [Flags]
    public enum QuestionFacets
    {
        None = 0,
        Who = 1 << 0,
        What = 1 << 1,
        When = 1 << 2,
        Where = 1 << 3,
        Why = 1 << 4
    }

    public class IntentResult
    {
        public string Intent { get; set; } = string.Empty;
        public string? Subject { get; set; }
        public double Confidence { get; set; }
        public int? Count { get; set; }
        public bool IsLatest { get; set; }
        public string Source { get; set; } = "AI";
        public QuestionFacets Facets { get; set; } = QuestionFacets.None;
    }

    public class TokenUsage
    {
        public int InputTokens { get; set; }
        public int OutputTokens { get; set; }
        public int TotalTokens { get; set; }
        public long PromptEvalDurationMs { get; set; }
        public long GenerationDurationMs { get; set; }
        public long TotalDurationMs { get; set; }
    }

    public class IntentAnalysisResult
    {
        public List<IntentResult> Intents { get; set; } = new();
        public TokenUsage Usage { get; set; } = new();
        public string RawPrompt { get; set; } = string.Empty;
        public string? Error { get; set; }
        public string? RawModelOutput { get; set; }
        public string? NormalizedJsonTried { get; set; }
    }

    // Options that can be bound from appsettings.json and passed into the service
    public class GenerationOptions
    {
        public double Temperature { get; set; } = 0.2;
        public double TopP { get; set; } = 0.8;
        public int TopK { get; set; } = 50;
        public int NumPredict { get; set; } = 256;
        public double RepeatPenalty { get; set; } = 1.1;
        public int Seed { get; set; } = 42; // -1 for random seed
    }

    public class PromptAnalyzerService
    {
        private readonly string _baseUrl;
        private readonly string _model;
        private readonly string _systemPrompt;
        private readonly HttpClient _httpClient;
        private readonly int _maxRetries;
        private readonly int _initialRetryDelayMs;
        private readonly GenerationOptions _generationOptions;

        private static readonly Regex WhoRx = new(@"\bwho'?s?\b|\bwhos\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex WhatRx = new(@"\bwhat\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex WhenRx = new(@"\bwhen\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex WhereRx = new(@"\bwhere\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex WhyRx = new(@"\bwhy\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ReasonWordRx = new(@"\b(reason|reasons|cause|causes|explanation|because)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ClauseSplitRx = new(@"\?|;|\band\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex CodeFenceRx = new(@"^```(?:json|JSON)?\s*|\s*```$", RegexOptions.Multiline | RegexOptions.Compiled);
        private static readonly Regex TrailingCommasRx = new(@",(\s*[\]\}])", RegexOptions.Compiled);
        private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
        private static readonly Regex MultiSpaceRx = new(@"\s+", RegexOptions.Compiled);

        public PromptAnalyzerService(
            string baseURL = "https://192.168.118.23/",
            string model = "llama3.1",
            string? systemPrompt = null,
            int maxRetries = 2,
            int initialRetryDelayMs = 300,
            GenerationOptions? generationOptions = null)
        {
            _baseUrl = baseURL.TrimEnd('/');
            _model = model;
            _systemPrompt = systemPrompt ?? """
            ROLE: Intent extractor.

            OUTPUT Format: JSON array.
            [
                {
                    "intent": "",
                    "confidence": 0.90,
                },
                {
                    "intent": "",
                    "confidence": 0.85,
                },
                {
                    "intent": "",
                    "confidence": 0.92,
                },
                {
                    "intent": "",
                    "confidence": 0.88,
                },
                {
                    "intent": "",
                    "confidence": 0.91,
                }
            ]

            INSTRUCTIONS:
            1. Extract intents from the user's prompt.
            2. Provide the output strictly in the specified JSON array format.
            3. Each intent object must include:
               - "intent": The extracted intents from the user's prompt.
               - "confidence": A confidence score between 0 and 1 indicating the certainty of the intent.
            4. Generate 5 intent objects always, even if confidence is low.
            """;
            _httpClient = new HttpClient();
            _maxRetries = Math.Max(0, maxRetries);
            _initialRetryDelayMs = Math.Max(0, initialRetryDelayMs);
            _generationOptions = generationOptions ?? new GenerationOptions();
        }

        private object GetDefaultOptions() => new
        {
            temperature = _generationOptions.Temperature,
            top_p = _generationOptions.TopP,
            top_k = _generationOptions.TopK,
            num_predict = _generationOptions.NumPredict,
            repeat_penalty = _generationOptions.RepeatPenalty,
            seed = _generationOptions.Seed
        };

        public async Task<IntentAnalysisResult> AnalyzeAsync(string userPrompt)
        {
            var result = new IntentAnalysisResult { RawPrompt = userPrompt ?? string.Empty };
            if (string.IsNullOrWhiteSpace(userPrompt))
            {
                result.Error = "EmptyPrompt";
                return result;
            }

            var payload = new
            {
                model = _model,
                messages = new[]
                {
                    new { role = "system", content = _systemPrompt },
                    new { role = "user", content = userPrompt }
                },
                stream = false,
                options = GetDefaultOptions()
            };

            try
            {
                for (int attempt = 1; attempt <= _maxRetries + 1; attempt++)
                {
                    // var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/api/chat", payload); // Got Role
                    // var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/v1/chat/completions", payload); // Got Role
                    // var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/api/generate", payload);
                    var response = await _httpClient.PostAsJsonAsync($"{_baseUrl}/v1/chat/completions", payload);
                    if (!response.IsSuccessStatusCode)
                    {
                        if (attempt <= _maxRetries)
                        {
                            await DelayForRetryAsync(attempt);
                            continue;
                        }
                        result.Error = $"HTTP {(int)response.StatusCode}";
                        EnsureDefaultIntentIfEmpty(result, userPrompt);
                        return result;
                    }

                    var jsonResponse = await response.Content.ReadAsStringAsync();
                    using var rootDoc = JsonDocument.Parse(jsonResponse);
                    var root = rootDoc.RootElement;

                    string? content = ExtractModelMessageContent(root);
                    result.RawModelOutput = content;
                    ExtractUsage(root, result);

                    if (string.IsNullOrWhiteSpace(content))
                    {
                        if (attempt <= _maxRetries)
                        {
                            await DelayForRetryAsync(attempt);
                            continue;
                        }
                        result.Error = "EmptyModelOutput";
                        EnsureDefaultIntentIfEmpty(result, userPrompt);
                        return result;
                    }

                    if (TryParseArray(content.Trim(), out var intents) ||
                        TryParseArray(NormalizeToJsonArray(content), out intents) ||
                        TryParseArray(TrailingCommasRx.Replace(NormalizeToJsonArray(content), "$1"), out intents))
                    {
                        PostProcess(intents, userPrompt);
                        result.Intents = intents;
                        EnsureDefaultIntentIfEmpty(result, userPrompt);
                        return result;
                    }

                    // Parsing failed - retry if we still have attempts left
                    if (attempt <= _maxRetries)
                    {
                        await DelayForRetryAsync(attempt);
                        continue;
                    }

                    result.Error = "ParseError: Unable to locate valid JSON array in model output.";
                    EnsureDefaultIntentIfEmpty(result, userPrompt);
                    return result;
                }

                // Fallback (should not reach here)
                result.Error = "UnknownError";
                EnsureDefaultIntentIfEmpty(result, userPrompt);
                return result;
            }
            catch (Exception ex)
            {
                result.Error = "Exception: " + ex.Message;
                EnsureDefaultIntentIfEmpty(result, userPrompt);
                return result;
            }
        }

        private Task DelayForRetryAsync(int attempt)
        {
            if (_initialRetryDelayMs <= 0) return Task.CompletedTask;
            // Exponential backoff with jitter (10%)
            var baseDelay = _initialRetryDelayMs * (int)Math.Pow(2, Math.Max(0, attempt - 1));
            var jitter = (int)(baseDelay * 0.1);
            var rnd = Random.Shared.Next(-jitter, jitter + 1);
            var delay = Math.Max(0, baseDelay + rnd);
            return Task.Delay(delay);
        }

        private static void PostProcess(List<IntentResult> intents, string userPrompt)
        {
            if (intents == null) return;

            var filtered = new List<IntentResult>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Precompute normalized prompt subject for meaning-preserving enforcement
            var normalizedPromptSubject = NormalizeSubject(userPrompt);
            var normalizedPromptTokens = Tokenize(normalizedPromptSubject);

            foreach (var i in intents)
            {
                if (i.Count < 0) i.Count = null;
                if (string.IsNullOrWhiteSpace(i.Subject)) i.Subject = userPrompt;
                if (i.Confidence <= 0 || i.Confidence > 1) i.Confidence = ClampConfidence(i.Confidence);
                if (!i.IsLatest) i.IsLatest = true;
                if (string.IsNullOrWhiteSpace(i.Source)) i.Source = "Ollama"; // preserve pre-set source if provided

                // Normalize provided subject and enforce meaning-preserving constraint against prompt
                var normalizedSubject = NormalizeSubject(i.Subject);
                var subjectTokens = Tokenize(normalizedSubject);

                // Enforce: subject should not drop meaningful tokens. If it's not a subsequence
                // of the normalized prompt or it is too short (<60% tokens), force to normalized prompt.
                bool isSubseq = IsSubsequence(subjectTokens, normalizedPromptTokens);
                double coverage = normalizedPromptTokens.Count == 0 ? 0 : (double)subjectTokens.Count / normalizedPromptTokens.Count;
                if (!isSubseq || coverage < 0.6)
                {
                    normalizedSubject = normalizedPromptSubject;
                    subjectTokens = normalizedPromptTokens;
                }

                i.Subject = normalizedSubject;

                var allowSameSubjectDifferentIntent = i.Intent == "GenericQuestion" || i.Intent == "ShowDocument" || i.Intent == "ShowImage";
                var key = allowSameSubjectDifferentIntent ? $"{i.Intent}||{i.Subject}" : $"GEN||{i.Subject}"; // generic dedupe logic
                if (seen.Add(key)) filtered.Add(i);
            }

            intents.Clear();
            intents.AddRange(filtered);

            AssignFacets(intents, userPrompt);
            EnrichFacets(intents, userPrompt);
        }

        private static string NormalizeSubject(string? subject)
        {
            if (string.IsNullOrWhiteSpace(subject)) return string.Empty;
            var s = subject.Trim().ToLowerInvariant();
            // Remove common fillers, wh-words and function words that don't carry domain meaning
            s = Regex.Replace(s, @"\b(show|display|list|give|fetch|retrieve|present|provide|please|kindly|because|reason|reasons|explanation|explain|tell|tell me|who|what|when|where|why|how|which|is|are|am|was|were|be|been|being|to|for|a|an|the|of)\b", " ");
            // Remove punctuation except spaces
            s = Regex.Replace(s, @"[^\w\s]", " ");
            s = MultiSpaceRx.Replace(s, " ").Trim();
            return s;
        }

        private static List<string> Tokenize(string s) => string.IsNullOrWhiteSpace(s)
            ? new List<string>()
            : s.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        private static bool IsSubsequence(List<string> sub, List<string> full)
        {
            if (sub.Count == 0) return true;
            int i = 0, j = 0;
            while (i < sub.Count && j < full.Count)
            {
                if (string.Equals(sub[i], full[j], StringComparison.OrdinalIgnoreCase))
                {
                    i++; j++;
                }
                else j++;
            }
            return i == sub.Count;
        }

        private static double ClampConfidence(double raw)
        {
            if (double.IsNaN(raw) || double.IsInfinity(raw)) return 0.80;
            if (raw > 1) return 0.95;
            if (raw < 0.5) return 0.50;
            if (raw >= 0.99) return 0.99 - 0.01;
            return Math.Round(raw, 2);
        }

        private static string? ExtractModelMessageContent(JsonElement root)
        {
            // Ollama / simple schema: { message: { content: "..." } }
            if (root.TryGetProperty("message", out var msg) &&
                msg.ValueKind == JsonValueKind.Object &&
                msg.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
                return content.GetString();

            // Direct content (rare)
            if (root.TryGetProperty("content", out var direct) && direct.ValueKind == JsonValueKind.String)
                return direct.GetString();

            // OpenAI-style chat completions: { choices: [ { message: { content: "..." } } ] }
            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.ValueKind == JsonValueKind.Object)
                {
                    if (first.TryGetProperty("message", out var choiceMsg) &&
                        choiceMsg.ValueKind == JsonValueKind.Object &&
                        choiceMsg.TryGetProperty("content", out var choiceContent) &&
                        choiceContent.ValueKind == JsonValueKind.String)
                    {
                        return choiceContent.GetString();
                    }
                    if (first.TryGetProperty("text", out var choiceText) && choiceText.ValueKind == JsonValueKind.String)
                    {
                        return choiceText.GetString();
                    }
                }
            }

            // If the root is an array, assume it's already the content
            if (root.ValueKind == JsonValueKind.Array)
                return root.GetRawText();

            // Sometimes content is wrapped in a data array
            if (root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array)
                return dataEl.GetRawText();

            return null;
        }

        private static void ExtractUsage(JsonElement root, IntentAnalysisResult result)
        {
            try
            {
                // Ollama-like stats
                int ip = root.TryGetProperty("prompt_eval_count", out var ipEl) ? ipEl.GetInt32() : 0;
                int op = root.TryGetProperty("eval_count", out var opEl) ? opEl.GetInt32() : 0;
                long ped = root.TryGetProperty("prompt_eval_duration", out var pedEl) ? pedEl.GetInt64() : 0;
                long ed = root.TryGetProperty("eval_duration", out var edEl) ? edEl.GetInt64() : 0;
                long td = root.TryGetProperty("total_duration", out var tdEl) ? tdEl.GetInt64() : 0;

                // OpenAI-style usage
                if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                {
                    if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.ValueKind == JsonValueKind.Number)
                        ip = pt.GetInt32();
                    if (usage.TryGetProperty("completion_tokens", out var ct) && ct.ValueKind == JsonValueKind.Number)
                        op = ct.GetInt32();
                    if (usage.TryGetProperty("total_tokens", out var tt) && tt.ValueKind == JsonValueKind.Number)
                        _ = tt.GetInt32(); // total will be recomputed below
                }

                result.Usage = new TokenUsage
                {
                    InputTokens = ip,
                    OutputTokens = op,
                    TotalTokens = ip + op,
                    PromptEvalDurationMs = NsToMs(ped),
                    GenerationDurationMs = NsToMs(ed),
                    TotalDurationMs = NsToMs(td)
                };
            }
            catch { }
        }

        private static bool TryParseArray(string maybeJson, out List<IntentResult> intents)
        {
            intents = new();
            if (string.IsNullOrWhiteSpace(maybeJson)) return false;
            try
            {
                var trimmed = maybeJson.TrimStart();
                if (!trimmed.StartsWith("[")) return false;
                intents = JsonSerializer.Deserialize<List<IntentResult>>(maybeJson, JsonOpts) ?? new();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string NormalizeToJsonArray(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "[]";
            string s = CodeFenceRx.Replace(raw, "").Trim();
            int firstBracket = s.IndexOf('[');
            int lastBracket = s.LastIndexOf(']');
            if (firstBracket >= 0 && lastBracket > firstBracket)
                s = s.Substring(firstBracket, lastBracket - firstBracket + 1);
            s = TrailingCommasRx.Replace(s, "$1");
            if (!s.StartsWith("[") || !s.EndsWith("]")) return "[]";
            return s;
        }

        private static QuestionFacets DetectClauseFacets(string clause)
        {
            QuestionFacets f = QuestionFacets.None;
            if (WhoRx.IsMatch(clause)) f |= QuestionFacets.Who;
            if (WhatRx.IsMatch(clause)) f |= QuestionFacets.What;
            if (WhenRx.IsMatch(clause)) f |= QuestionFacets.When;
            if (WhereRx.IsMatch(clause)) f |= QuestionFacets.Where;
            if (WhyRx.IsMatch(clause) || ReasonWordRx.IsMatch(clause)) f |= QuestionFacets.Why;
            return f;
        }

        private static void AssignFacets(List<IntentResult> intents, string prompt)
        {
            if (intents == null || intents.Count == 0) return;
            var clauses = ClauseSplitRx.Split(prompt).Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
            var clauseFacets = clauses.Select(DetectClauseFacets).ToList();
            for (int i = 0; i < intents.Count; i++)
            {
                var f = QuestionFacets.None;
                if (i < clauseFacets.Count) f |= clauseFacets[i];
                else
                {
                    foreach (var cf in clauseFacets) f |= cf;
                }
                var subj = intents[i].Subject ?? string.Empty;
                if (ReasonWordRx.IsMatch(subj) || subj.StartsWith("why ", StringComparison.OrdinalIgnoreCase))
                    f |= QuestionFacets.Why;
                intents[i].Facets = f;
            }
        }

        private static void EnrichFacets(List<IntentResult> intents, string prompt)
        {
            if (intents == null || intents.Count == 0) return;
            // If only one intent but prompt contains multiple 5W signals, aggregate them.
            if (intents.Count == 1)
            {
                var f = QuestionFacets.None;
                if (WhoRx.IsMatch(prompt)) f |= QuestionFacets.Who;
                if (WhatRx.IsMatch(prompt)) f |= QuestionFacets.What;
                if (WhenRx.IsMatch(prompt)) f |= QuestionFacets.When;
                if (WhereRx.IsMatch(prompt)) f |= QuestionFacets.Where;
                if (WhyRx.IsMatch(prompt) || ReasonWordRx.IsMatch(prompt)) f |= QuestionFacets.Why;
                intents[0].Facets |= f;
                return;
            }
            // If multiple intents share same subject distribute facets based on clause ordering then fill gaps.
            var groupBySubject = intents.GroupBy(i => i.Subject ?? string.Empty, StringComparer.OrdinalIgnoreCase);
            foreach (var grp in groupBySubject)
            {
                var list = grp.ToList();
                if (list.Count < 2) continue;
                // Collect union of facets detectible from prompt
                var union = QuestionFacets.None;
                if (WhoRx.IsMatch(prompt)) union |= QuestionFacets.Who;
                if (WhatRx.IsMatch(prompt)) union |= QuestionFacets.What;
                if (WhenRx.IsMatch(prompt)) union |= QuestionFacets.When;
                if (WhereRx.IsMatch(prompt)) union |= QuestionFacets.Where;
                if (WhyRx.IsMatch(prompt) || ReasonWordRx.IsMatch(prompt)) union |= QuestionFacets.Why;
                // If all current facets are None but union has multiple, assign sequential distinct facets preference order Who,Why,What,When,Where
                if (list.All(i => i.Facets == QuestionFacets.None) && union != QuestionFacets.None)
                {
                    var ordered = new[] { QuestionFacets.Who, QuestionFacets.Why, QuestionFacets.What, QuestionFacets.When, QuestionFacets.Where };
                    int idx = 0;
                    foreach (var intent in list)
                    {
                        while (idx < ordered.Length && !union.HasFlag(ordered[idx])) idx++;
                        if (idx < ordered.Length)
                        {
                            intent.Facets |= ordered[idx];
                            idx++;
                        }
                    }
                }
            }
        }

        private static long NsToMs(long ns) => ns <= 0 ? 0 : ns / 1_000_000;

        private static IntentResult CreateDefaultIntent(string prompt)
        {
            return new IntentResult
            {
                Intent = "GenericQuestion",
                Subject = NormalizeSubject(prompt),
                Confidence = 0.40,
                Count = null,
                IsLatest = true,
                Source = "Default"
            };
        }

        private static void EnsureDefaultIntentIfEmpty(IntentAnalysisResult result, string prompt)
        {
            if (result.Intents == null || result.Intents.Count == 0)
            {
                result.Intents = new List<IntentResult> { CreateDefaultIntent(prompt) };
                PostProcess(result.Intents, prompt);
            }
        }
    }
}
