using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Biz.TKV.AIModelProcess.Tools
{
    public class PromptRefinement
    {
        // Default count for list requests
        public static readonly int DefaultListCount = 5;
        // Regex patterns for reuse and configuration
        private static readonly Regex PluralListPattern = new(@"^(list of\s*){2,}", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ProvideListPattern = new(@"^(provide|give) (a|me)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex NewReqPattern = new(@"^(who|which|list)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex SymbolExceptSplitPattern = new(@"[\p{P}-[.?!]]+", RegexOptions.Compiled);
        private static readonly Regex WhitespacePattern = new(@"\s+", RegexOptions.Compiled);
        private static readonly string[] ListVerbs = { "list", "give", "show", "provide", "tell", "obtain", "find", "get" };
        private static readonly string[] StopVerbs = { "is", "are", "was", "were", "be", "been", "being", "have", "has", "had", "do", "does", "did", "will", "would", "shall", "should", "may", "might", "must", "can", "could" };
        private static readonly string[] StopWords = { "the", "a", "an", "of", "in", "on", "with", "this", "these", "those", "their", "one", "ones", "is", "are", "do", "does", "did", "be", "was", "were", "have", "has", "had", "who", "whom", "which" };

        // Configurable polite words/phrases
        public static readonly string[] PoliteWords = new[] {
            "please", "kindly", "could you", "would you", "can you", "may I", "may we", "would it be possible", "if you could", "i would like to", "i want to", "i'd like to", "i wish to", "i am requesting", "i am asking", "i hope you can", "i hope you will", "i hope you could", "i hope you would"
        };

        // Configurable filler patterns
        public static readonly string[] FillerPatterns = new[] {
            @"^i wanted to have( a| the)? ",
            @"^i would like to( have| know| see| get)?( a| the)? ",
            @"^i want( to)?( have| know| see| get)?( a| the)? ",
            @"^give me( a| the)? ",
            @"^show me( a| the)? ",
            @"^provide me( a| the)? ",
            @"^tell me( a| the)? ",
            @"^please ",
            @"^kindly ",
        };
        private static readonly char[] Symbols = { '.', ',', '?', '!', ';', ':', '"', '\'', '(', ')', '[', ']', '{', '}', '-', '_', '/', '\\', '|', '@', '#', '$', '%', '^', '&', '*', '+', '=', '<', '>' };
        public static readonly Regex ListVerbPattern = new(@"\b(" + string.Join("|", ListVerbs) + @")\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ListOfPattern = new(@"\blist of\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly string[] PluralHintWords = { "all", "many", "several", "top", "list", "most", "various", "numerous", "multiple", "variety", "diverse" };

        public class RefinedPromptResult
        {
            public string RefinedPrompt { get; set; }
            public bool RequestList { get; set; }
            public int Count { get; set; }
            public bool IsLatest { get; set; } = true; // Mark if this is the latest segment
        }

        /// <summary>
        /// Refines the input prompt: removes symbols, stop verbs, checks if a list is required, and returns a count.
        /// If the prompt contains 'who', 'which', or 'list' in the middle, splits into multiple requests.
        /// </summary>
        public static List<RefinedPromptResult> Refine(string prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt))
                return new List<RefinedPromptResult> { new RefinedPromptResult { RefinedPrompt = string.Empty, RequestList = false, Count = 0 } };


            // Remove leading filler phrases
            string cleaned = prompt.Trim();
            foreach (var pat in FillerPatterns)
                cleaned = Regex.Replace(cleaned, pat, "", RegexOptions.IgnoreCase).TrimStart();

            // Special: Remove leading 'provide' or 'give' if followed by 'a list of' or 'list of'
            cleaned = ProvideListPattern.Replace(cleaned, "").TrimStart();

            // Remove polite words/phrases (anywhere in the prompt)
            foreach (var word in PoliteWords)
            {
                cleaned = Regex.Replace(cleaned, $@"\b{Regex.Escape(word)}\b", "", RegexOptions.IgnoreCase);
            }
            cleaned = SymbolExceptSplitPattern.Replace(cleaned, ""); // Remove symbols except . ? ! for splitting
            cleaned = WhitespacePattern.Replace(cleaned, " "); // Normalize whitespace

            // Split on punctuation (., ?, !) to get natural sentences
            var rawSegments = Regex.Split(cleaned, "[.!?]+")
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            // Merge segments unless they start with who/which/list (case-insensitive)
            var segments = new List<string>();
            string current = null;
            foreach (var seg in rawSegments)
            {
                if (NewReqPattern.IsMatch(seg))
                {
                    if (!string.IsNullOrWhiteSpace(current))
                        segments.Add(current.Trim());
                    current = seg;
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(current))
                        current = seg;
                    else
                        current += " " + seg;
                }
            }
            if (!string.IsNullOrWhiteSpace(current))
                segments.Add(current.Trim());


            var results = new List<RefinedPromptResult>();
            foreach (var seg in segments)
            {

                // Remove symbols
                var noSymbols = new string(seg.Where(c => !Symbols.Contains(c)).ToArray());

                // Tokenize and remove stop verbs
                var words = noSymbols.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => !StopVerbs.Contains(w.ToLower()))
                    .ToList();

                // Rebuild refined prompt
                var refined = string.Join(" ", words);

                // Remove duplicate 'list of' at the start
                refined = PluralListPattern.Replace(refined, "list of ").TrimStart();

                // Heuristic: check if prompt requires a list
                var lower = refined.ToLower();
                string[] questionWords = { "who", "what", "where", "when", "why", "how" };
                bool isDirectQuestion = questionWords.Any(q => lower.StartsWith(q + " "));
                bool isOlder = IsOlderRequest(lower);
                
                bool requestList = false;
                int count = 0;
                if (!isDirectQuestion)
                {
                    requestList = (ListVerbPattern.IsMatch(lower) || ListOfPattern.IsMatch(lower) || IsPluralRequest(lower));
                    // Improved: Treat a number as count if it is at the start, immediately after a list verb, or within 5 words after a list verb
                    for (int i = 0; i < words.Count; i++)
                    {
                        if (int.TryParse(words[i], out int n))
                        {
                            // If number is at the start, or within 5 words after a list verb, treat as count
                            if (i == 0)
                            {
                                count = n;
                                break;
                            }
                            // Look back up to 5 words for a list verb
                            for (int j = Math.Max(0, i - 5); j < i; j++)
                            {
                                if (ListVerbs.Contains(words[j].ToLower()))
                                {
                                    count = n;
                                    break;
                                }
                            }
                            if (count > 0) break;
                        }
                    }
                    // Default count for list if not found
                    if (requestList && count == 0) count = DefaultListCount;
                    // If count is greater than default, use default
                    if (requestList && count > DefaultListCount) count = DefaultListCount;
                }
                results.Add(new RefinedPromptResult
                {
                    RefinedPrompt = refined,
                    RequestList = requestList,
                    Count = count,
                    IsLatest = (isOlder) ? false : true
                });
            }
            return results;
        }

        private static bool IsPluralRequest(string sentence)
        {
            sentence = sentence.ToLower();

            if (PluralHintWords.Any(p => sentence.Contains(p, StringComparison.OrdinalIgnoreCase))) return true;

            var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var w in words)
            {
                string clean = w.Trim(new char[] { '.', ',', '?', ';' });
                if (!StopWords.Contains(clean) && clean.EndsWith("s"))
                    return true;
            }

            return false;
        }

        public static bool IsOlderRequest(string prompt)
        {
            // Generic keywords
            string[] olderKeywords = {
                "history", "historical", "in the past", "archived", "bygone", "vintage", "retro"
            };
            // Generic regex patterns for time references
            Regex[] olderPatterns = new Regex[] {
                new Regex(@"\blast \w+\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), // last year, last month, last week
                new Regex(@"\bprevious \w+\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), // previous year, previous month
                new Regex(@"\bearlier \w+\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), // earlier period
                new Regex(@"\bin \d{4}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled), // in 1990, in 2020
                new Regex(@"\b(19|20)\d{2}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled) // any year 1900-2099
            };
            string lowered = prompt.ToLower();
            foreach (var k in olderKeywords)
            {
                if (k.Contains(" ")) // phrase
                {
                    if (lowered.Contains(k)) return true;
                }
                else // single word, use word boundary
                {
                    if (Regex.IsMatch(lowered, $@"\b{k}\b")) return true;
                }
            }
            foreach (var pattern in olderPatterns)
            {
                if (pattern.IsMatch(prompt)) return true;
            }
            return false;
        }
    }
}
