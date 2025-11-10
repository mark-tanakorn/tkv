using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Biz.TKV.AIModelProcess.Tools
{
    public enum RequestType { List, HowTo, Info }

    public class Request
    {
        public RequestType Type { get; set; }
        public string Keyword { get; set; }
        public int? Count { get; set; }
        public bool HasExplicitCount { get; set; }
        public List<string> Attributes { get; set; } = new List<string>();
        public string Sentence { get; set; }
    }

    public class PromptAnalysisResult
    {
        public bool HasRequest => Requests.Any();
        public List<Request> Requests { get; set; } = new List<Request>();
        public List<int> StatementNumbers { get; set; } = new List<int>();

        // Flags for question detection
        public bool IsWhy { get; set; }
        public bool IsWhen { get; set; }
        public bool IsWho { get; set; }
        public bool IsWhere { get; set; }
        public bool IsWhat { get; set; }
        public bool IsLatest { get; set; }

        public string CombinedSentence
        {
            get
            {
                // Refined: Use declared regex for deduplication/normalization, avoid hardcoded replacements, ensure only meaningful, non-duplicated, natural sentences.
                var phrases = new List<string>();
                // Group requests by type and attribute for possible joining
                var grouped = Requests
                    .Where(r => r.Type == RequestType.List)
                    .GroupBy(r => string.Join("|", (r.Attributes ?? new List<string>()).Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim().ToLower())))
                    .ToList();

                var handled = new HashSet<Request>();
                foreach (var group in grouped)
                {
                    var reqs = group.ToList();
                    if (reqs.Count > 1 && reqs.All(r => (r.Attributes?.Count ?? 0) == 1 && r.Attributes[0] == reqs[0].Attributes[0]))
                    {
                        // Join keywords with 'and' if they appeared together in the original input
                        var attr = reqs[0].Attributes[0];
                        var keywords = reqs.Select(r => r.Keyword).ToList();
                        string joinedKeywords = string.Join(" and ", keywords);
                        string sentence = attr.StartsWith("the ", StringComparison.OrdinalIgnoreCase)
                            ? $"List {attr} for {joinedKeywords}"
                            : $"List {attr} {joinedKeywords}";
                        sentence = Regex.Replace(sentence, @"\s+", " ");
                        sentence = char.ToUpper(sentence[0]) + sentence.Substring(1);
                        if (!phrases.Contains(sentence, StringComparer.OrdinalIgnoreCase))
                            phrases.Add(sentence);
                        foreach (var r in reqs) handled.Add(r);
                        continue;
                    }
                }

                foreach (var r in Requests)
                {
                    if (handled.Contains(r)) continue;
                    var keyword = (r.Keyword ?? "").Trim();
                    var attributes = (r.Attributes ?? new List<string>()).Where(a => !string.IsNullOrWhiteSpace(a)).ToList();

                    // Generic handling: if attribute starts with 'the' and both attribute and keyword are present
                    if (attributes.Count == 1 && !string.IsNullOrWhiteSpace(keyword))
                    {
                        var attr = attributes[0].Trim();
                        if (attr.StartsWith("the ", StringComparison.OrdinalIgnoreCase))
                        {
                            string sentence;
                            // If keyword contains 'and', use 'for' (e.g., 'the synonyms for happy and sad')
                            if (Regex.IsMatch(keyword, @"\band\b", RegexOptions.IgnoreCase))
                                sentence = $"List {attr} for {keyword}";
                            else
                                sentence = $"List {attr} of {keyword}";
                            sentence = Regex.Replace(sentence, @"\s+", " ");
                            sentence = char.ToUpper(sentence[0]) + sentence.Substring(1);
                            if (!phrases.Contains(sentence, StringComparer.OrdinalIgnoreCase))
                                phrases.Add(sentence);
                            continue;
                        }
                    }

                    // Always generate a phrase for each attribute (even if only one)
                    if (attributes.Count > 0)
                    {
                        foreach (var attr in attributes)
                        {
                            string baseText = (attr + " " + keyword).Trim();
                            baseText = PromptAnalyzer.MultiSpacePattern.Replace(baseText, " ").Trim();
                            baseText = Regex.Replace(baseText, @"\b(\w+) \1\b", "$1", RegexOptions.IgnoreCase);
                            if (string.IsNullOrWhiteSpace(baseText) || baseText.Length < 2) continue;
                            string sentence = string.Empty;
                            if (r.Type == RequestType.List)
                            {
                                var prefix = "List";
                                var prefixTokens = new List<string> { prefix };
                                string countStr = null;
                                if (r.Count.HasValue && r.HasExplicitCount)
                                {
                                    countStr = r.Count.Value.ToString();
                                    var baseTokensPreview = baseText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                                    if (baseTokensPreview.Length == 0 || !string.Equals(baseTokensPreview[0], countStr, StringComparison.OrdinalIgnoreCase))
                                        prefixTokens.Add(countStr);
                                }
                                var baseTokens = baseText.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                                // Remove all occurrences of countStr from baseTokens (handle cases like '5 5 best selling books')
                                if (!string.IsNullOrEmpty(countStr))
                                {
                                    baseTokens = baseTokens.Where(t => !string.Equals(t, countStr, StringComparison.OrdinalIgnoreCase)).ToList();
                                }
                                while (baseTokens.Count > 0 && prefixTokens.Count > 0 && string.Equals(baseTokens[0], prefixTokens.Last(), StringComparison.OrdinalIgnoreCase))
                                {
                                    baseTokens.RemoveAt(0);
                                }
                                if (baseTokens.Count > 0 && baseTokens[0].Equals("List", StringComparison.OrdinalIgnoreCase))
                                    baseTokens.RemoveAt(0);
                                sentence = string.Join(" ", prefixTokens.Concat(baseTokens));
                            }
                            else if (r.Type == RequestType.HowTo)
                                sentence = $"Show how to {baseText}";
                            else if (r.Type == RequestType.Info)
                                sentence = $"Provide information about {baseText}";
                            if (!string.IsNullOrWhiteSpace(sentence))
                            {
                                sentence = char.ToUpper(sentence[0]) + sentence.Substring(1);
                                if (!phrases.Contains(sentence, StringComparer.OrdinalIgnoreCase))
                                    phrases.Add(sentence);
                            }
                        }
                        continue;
                    }
                    // If no attributes, fall back to keyword only
                    if (!string.IsNullOrWhiteSpace(keyword))
                    {
                        string baseText = keyword;
                        baseText = PromptAnalyzer.MultiSpacePattern.Replace(baseText, " ").Trim();
                        baseText = Regex.Replace(baseText, @"\b(\w+) \1\b", "$1", RegexOptions.IgnoreCase);
                        if (string.IsNullOrWhiteSpace(baseText) || baseText.Length < 2) continue;
                        string sentence = string.Empty;
                        if (r.Type == RequestType.List)
                        {
                            var prefix = "List";
                            var prefixTokens = new List<string> { prefix };
                            string countStr = null;
                            if (r.Count.HasValue && r.HasExplicitCount)
                            {
                                countStr = r.Count.Value.ToString();
                                var baseTokensPreview = baseText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                                if (baseTokensPreview.Length == 0 || !string.Equals(baseTokensPreview[0], countStr, StringComparison.OrdinalIgnoreCase))
                                    prefixTokens.Add(countStr);
                            }
                            var baseTokens = baseText.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
                            // Remove all occurrences of countStr from baseTokens (handle cases like '5 5 best selling books')
                            if (!string.IsNullOrEmpty(countStr))
                            {
                                baseTokens = baseTokens.Where(t => !string.Equals(t, countStr, StringComparison.OrdinalIgnoreCase)).ToList();
                            }
                            while (baseTokens.Count > 0 && prefixTokens.Count > 0 && string.Equals(baseTokens[0], prefixTokens.Last(), StringComparison.OrdinalIgnoreCase))
                            {
                                baseTokens.RemoveAt(0);
                            }
                            if (baseTokens.Count > 0 && baseTokens[0].Equals("List", StringComparison.OrdinalIgnoreCase))
                                baseTokens.RemoveAt(0);
                            sentence = string.Join(" ", prefixTokens.Concat(baseTokens));
                        }
                        else if (r.Type == RequestType.HowTo)
                            sentence = $"Show how to {baseText}";
                        else if (r.Type == RequestType.Info)
                            sentence = $"Provide information about {baseText}";
                        if (!string.IsNullOrWhiteSpace(sentence))
                        {
                            sentence = char.ToUpper(sentence[0]) + sentence.Substring(1);
                            if (!phrases.Contains(sentence, StringComparer.OrdinalIgnoreCase))
                                phrases.Add(sentence);
                        }
                    }
                }
                // Remove only exact duplicates, keep all valid phrases
                var filtered = phrases.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                return string.Join("; ", filtered).Replace(";;", ";").Trim();
            }
        }
    }

    public static class PromptAnalyzer
    {
        // Question detection helpers
        public static bool IsWhyQuestion(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return false;
            return Regex.IsMatch(input, @"\bwhy\b", RegexOptions.IgnoreCase);
        }

        public static bool IsWhenQuestion(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return false;
            return Regex.IsMatch(input, @"\bwhen\b", RegexOptions.IgnoreCase);
        }

        public static bool IsWhoQuestion(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return false;
            return Regex.IsMatch(input, @"\bwho\b", RegexOptions.IgnoreCase);
        }

        public static bool IsWhereQuestion(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return false;
            return Regex.IsMatch(input, @"\bwhere\b", RegexOptions.IgnoreCase);
        }

        public static bool IsWhatQuestion(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return false;
            return Regex.IsMatch(input, @"\bwhat\b", RegexOptions.IgnoreCase);
        }

        public static bool IsLatestRecordRequest(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return true;
            // Common phrases for old records
            string[] patterns = new[] {
                @"\boldest\b",
                @"\bearliest\b",
                @"\bprevious\b",
                @"\bhistory\b",
                @"\bhistorical\b",
                @"\barchive\b",
                @"\bpast\b",
                @"\bbefore\b",
                @"\bprior\b",
                @"\bolder\b",
                @"\bformer\b"
            };
            foreach (var pat in patterns)
            {
                if (Regex.IsMatch(input, pat, RegexOptions.IgnoreCase))
                    return false;
            }
            return true;
        }

        // 🔹 Configurable regex patterns (all at top)
        private static readonly Regex SentenceSplitPattern = new(@"[.!?]", RegexOptions.Compiled);
        private static readonly Regex HowToPattern = new(@"\b(how to|steps to|guide to|ways to)\b\s*(.+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex CountPattern = new(@"\b(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ListOfPattern = new(@"\blist of\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex QuestionSingularPattern = new(@"\b(who is|whose|which\s+\w+|what\s+is)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ParenthesesPattern = new(@"\([^)]*\)", RegexOptions.Compiled);
        private static readonly Regex SpecialCharPattern = new(@"[;,:]", RegexOptions.Compiled);
        public static readonly Regex WordSplitPattern = new(@"\W+", RegexOptions.Compiled);
        // Only split on 'and' or 'or' when they are standalone words (surrounded by word boundaries)
        private static readonly Regex ConjunctionPattern = new(@"\b(?:and|or)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AttributeCleaner = new(@"[.,?;()]", RegexOptions.Compiled);
        private static readonly Regex KeywordCleanupPattern = new(@"^(what\s+(is|are)\s+(the\s+)?)|(give\s+me\s+(the\s+)?)|(show\s+me\s+(the\s+)?)|(provide\s+me\s+(the\s+)?)|(i\s+(want(ed)?|would\s+like|need|wish)\s+(to\s+)?(have|see|get|know)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.IgnorePatternWhitespace);
        private static readonly Regex FillerPattern = new(@"\b(i\s+(want(ed)?|would\s+like|need|wish)\s+(to\s+)?(have|see|get|know))\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex PolitePattern = new(@"\b(give\s+me|show\s+me|provide\s+me|please|kindly|provide|show)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex DuplicateListOfPattern = new(@"(list of\s+)+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        public static readonly Regex MultiSpacePattern = new(@"\s+", RegexOptions.Compiled);
        private static readonly Regex DanglingConjunctionPattern = new(@"\b(and|or)\b\s*;?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex LeadingListOfPattern = new(@"^\s*list\s+of\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // 🔹 Word dictionaries
        private static readonly string[] ListVerbs = { "list", "give", "show", "provide", "tell", "obtain", "find", "get" };
        private static readonly string[] VerbsForAttributes = { "have", "has", "own", "achieve", "get", "contain", "score", "reach", "earn", "possess" };
        public static readonly string[] StopWords = { "the", "a", "an", "of", "in", "on", "with", "this", "these", "those", "their", "one", "ones", "is", "are", "do", "does", "did", "be", "was", "were", "have", "has", "had", "who", "whom", "which" };
        private static readonly string[] PluralHintWords = { "all", "many", "several", "top", "list", "most", "various", "numerous", "multiple", "variety", "diverse" };

        // Derived regex (depends on word lists)
        public static readonly Regex ListVerbPattern = new(@"\b(" + string.Join("|", ListVerbs) + @")\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        // Only split on ',' or 'and' or 'or' when they are exact standalone words
        private static readonly Regex DescriptorSplitPattern = new(@"\s*,\s*|\b(?:and|or)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);


        public static PromptAnalysisResult Analyze(string input)
        {

            var result = new PromptAnalysisResult();
            if (string.IsNullOrWhiteSpace(input)) return result;

            // Set question flags
            result.IsWhy = IsWhyQuestion(input);
            result.IsWhen = IsWhenQuestion(input);
            result.IsWho = IsWhoQuestion(input);
            result.IsWhere = IsWhereQuestion(input);
            result.IsWhat = IsWhatQuestion(input);
            result.IsLatest = IsLatestRecordRequest(input);

            // --- Hyphen normalization function ---
            // 1) Hyphen normalization: if word contains '-' and length < 6, merge; else replace '-' with space
            Func<string, string> normalizeHyphen = s =>
            {
                if (string.IsNullOrWhiteSpace(s)) return s;
                if (s.Contains("-"))
                {
                    var merged = s.Replace("-", "");
                    if (merged.Length < 6) return merged;
                    else return s.Replace("-", " ");
                }
                return s;
            };

            input = normalizeHyphen(input); // 1) Hyphen normalization
            input = input.Trim();
            var sentences = SentenceSplitPattern.Split(input).Where(s => !string.IsNullOrWhiteSpace(s));

            foreach (var sentence in sentences)
            {
                // Improved: Only split on ' and ' if not part of 'of ... and ...' (e.g., 'the founders of Microsoft and Apple' should NOT split)
                List<string> andSegments;
                // Special handling: if pattern matches 'the <attr> of <keywords>' (with possible 'and'), treat as one segment
                var theOfPattern = new Regex(@"the ([^?]+?) of ([^?]+)", RegexOptions.IgnoreCase);
                var match = theOfPattern.Match(sentence);
                if (match.Success)
                {
                    andSegments = new List<string> { sentence };
                }
                else if (sentence.Contains(" and ", StringComparison.OrdinalIgnoreCase))
                {
                    andSegments = sentence.Split(new[] { " and " }, StringSplitOptions.RemoveEmptyEntries).ToList();
                }
                else
                {
                    andSegments = new List<string> { sentence };
                }

                // Normal processing for all cases
                List<string> sharedAttributes = new List<string>();
                List<(RequestType type, int? count, bool explicitCount, List<string> attributes, List<string> keywords, string s, string original)> segmentData = new();
                foreach (var seg in andSegments)
                {
                    string original = seg.Trim();
                    string s = NormalizeInput(original);

                    // 2) Identify request type
                    RequestType type = RequestType.Info;
                    var howToMatch = HowToPattern.Match(s);
                    if (howToMatch.Success)
                        type = RequestType.HowTo;
                    else if (ListVerbPattern.IsMatch(s) || ListOfPattern.IsMatch(s) || IsPluralRequest(s))
                        type = RequestType.List;
                    // If split by 'and', default both to List if ambiguous
                    if (sentence.Contains(" and ", StringComparison.OrdinalIgnoreCase) && type == RequestType.Info)
                        type = RequestType.List;

                    // 3) Identify/capture count (number at start, after list verb, or after ranking words like 'top')
                    int? count = null;
                    bool explicitCount = false;
                    var tokens = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (tokens.Length > 1)
                    {
                        // e.g. "list 5 ..." or "show 10 ..."
                        if (ListVerbs.Contains(tokens[0].ToLower()) && int.TryParse(tokens[1], out int parsedCount))
                        {
                            count = parsedCount;
                            explicitCount = true;
                        }
                        // e.g. "5 tallest buildings ..."
                        else if (int.TryParse(tokens[0], out int parsedCount2))
                        {
                            count = parsedCount2;
                            explicitCount = true;
                        }
                        // e.g. "list the top 8 ..." or "list the first 10 ..."
                        else
                        {
                            for (int i = 0; i < tokens.Length - 1; i++)
                            {
                                var t = tokens[i].ToLower();
                                if ((t == "top" || t == "first" || t == "last" || t == "bottom") && int.TryParse(tokens[i + 1], out int parsedCount3))
                                {
                                    count = parsedCount3;
                                    explicitCount = true;
                                    break;
                                }
                            }
                        }
                    }

                    // 4) Identify multiple attributes (remove noise)
                    List<string> attributes = new List<string>();
                    List<string> keywords = new List<string>();
                    if (type == RequestType.List)
                    {
                        // Preserve 'the' in attribute if present in the original phrase
                        var theOfMatch = theOfPattern.Match(original);
                        if (theOfMatch.Success)
                        {
                            // Use the exact matched group for attribute (may include 'the')
                            var attr = theOfMatch.Groups[1].Value.Trim();
                            if (!attr.StartsWith("the ", StringComparison.OrdinalIgnoreCase) && original.ToLower().Contains("the " + attr.ToLower()))
                                attr = "the " + attr;
                            var kw = theOfMatch.Groups[2].Value.Trim();
                            attributes = new List<string> { attr };
                            keywords = new List<string> { kw };
                        }
                        // New: if attribute starts with 'the' and phrase contains 'for', treat everything after 'for' as a single keyword
                        else if (Regex.IsMatch(original, @"the [^ ]+ for ", RegexOptions.IgnoreCase))
                        {
                            var forMatch = Regex.Match(original, @"(the [^ ]+) for (.+)", RegexOptions.IgnoreCase);
                            if (forMatch.Success)
                            {
                                var attr = forMatch.Groups[1].Value.Trim();
                                var kw = forMatch.Groups[2].Value.Trim();
                                attributes = new List<string> { attr };
                                keywords = new List<string> { kw };
                            }
                        }
                        else
                        {
                            // For 'in' pattern: attributes before 'in', keywords after 'in'
                            var lower = original.ToLower();
                            int inIdx = lower.IndexOf(" in ");
                            if (inIdx > 0)
                            {
                                var beforeIn = original.Substring(0, inIdx).Trim();
                                var afterIn = original.Substring(inIdx + 4).Trim();
                                attributes = Regex.Split(beforeIn, "\b(?:and|or)\b", RegexOptions.IgnoreCase)
                                    .Select(p => NormalizeInput(AttributeCleaner.Replace(p.Trim(), "").Trim()))
                                    .Where(p => !string.IsNullOrWhiteSpace(p))
                                    .Select(p => p.ToLower())
                                    .Where(p => p != "list" && p != "top" && p != "of" && !StopWords.Contains(p))
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .ToList();
                                keywords = Regex.Split(afterIn, "\b(?:and|or)\b", RegexOptions.IgnoreCase)
                                    .Select(p => NormalizeInput(AttributeCleaner.Replace(p.Trim(), "").Trim()))
                                    .Where(p => !string.IsNullOrWhiteSpace(p))
                                    .Select(p => p.ToLower())
                                    .Where(p => p != "list" && p != "top" && p != "of" && !StopWords.Contains(p))
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .ToList();
                            }
                            else
                            {
                                // fallback: treat all as keywords, remove noise
                                attributes = Regex.Split(original, "\b(?:and|or)\b", RegexOptions.IgnoreCase)
                                    .Select(p => NormalizeInput(AttributeCleaner.Replace(p.Trim(), "").Trim()))
                                    .Where(p => !string.IsNullOrWhiteSpace(p))
                                    .Select(p => p.ToLower())
                                    .Where(p => p != "list" && p != "top" && p != "of" && !StopWords.Contains(p))
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .ToList();
                                keywords = Regex.Split(original, "\b(?:and|or)\b", RegexOptions.IgnoreCase)
                                    .Select(p => NormalizeInput(AttributeCleaner.Replace(p.Trim(), "").Trim()))
                                    .Where(p => !string.IsNullOrWhiteSpace(p))
                                    .Select(p => p.ToLower())
                                    .Where(p => p != "list" && p != "top" && p != "of" && !StopWords.Contains(p))
                                    .Distinct(StringComparer.OrdinalIgnoreCase)
                                    .ToList();
                            }
                        }
                    }
                    else if (type == RequestType.HowTo)
                    {
                        // For HowTo, treat everything after 'how to' as keyword
                        var howToMatch2 = HowToPattern.Match(original);
                        if (howToMatch2.Success)
                        {
                            var mainAction = NormalizeInput(howToMatch2.Groups[2].Value.Trim());
                            keywords.Add(mainAction);
                        }
                    }
                    else
                    {
                        attributes = Regex.Split(original, "\b(?:and|or)\b", RegexOptions.IgnoreCase)
                                .Select(p => NormalizeInput(AttributeCleaner.Replace(p.Trim(), "").Trim()))
                                .Where(p => !string.IsNullOrWhiteSpace(p))
                                .Select(p => p.ToLower())
                                .Where(p => p != "list" && p != "top" && p != "of" && !StopWords.Contains(p))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList();
                        // Info: treat the whole as keyword, remove noise
                        keywords = Regex.Split(original, "\b(?:and|or)\b", RegexOptions.IgnoreCase)
                            .Select(p => NormalizeInput(AttributeCleaner.Replace(p.Trim(), "").Trim()))
                            .Where(p => !string.IsNullOrWhiteSpace(p))
                            .Select(p => p.ToLower())
                            .Where(p => p != "list" && p != "top" && p != "of" && !StopWords.Contains(p))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                    }

                    // 5) Remove duplicates between attributes and keywords
                    attributes = attributes.Except(keywords, StringComparer.OrdinalIgnoreCase).ToList();

                    // Post-process attributes to remove noise and non-related info
                    string[] extraNoise = new[] { "list", "of", "top", "show", "me", "please", "kindly", "what" };
                    Func<string, string> cleanAttribute = p =>
                    {
                        var words = p.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                            .Where(w => !StopWords.Contains(w.ToLower()) && !extraNoise.Contains(w.ToLower()))
                            .ToArray();
                        return string.Join(" ", words).Trim();
                    };
                    attributes = attributes
                        .Select(cleanAttribute)
                        .Where(a => !string.IsNullOrWhiteSpace(a))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    // Also normalize keywords after all processing
                    keywords = keywords
                        .Select(k => NormalizeInput(k))
                        .Where(k => !string.IsNullOrWhiteSpace(k))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    // Collect for sharing if needed
                    if (attributes.Count > 0)
                    {
                        foreach (var attr in attributes)
                        {
                            if (!sharedAttributes.Contains(attr, StringComparer.OrdinalIgnoreCase))
                                sharedAttributes.Add(attr);
                        }
                    }

                    segmentData.Add((type, count, explicitCount, attributes, keywords, s, original));
                }

                // Propagate attribute to all conjunction-separated keywords if attribute is only specified once
                foreach (var (type, count, explicitCount, attributes, keywords, s, original) in segmentData)
                {
                    var useAttributes = attributes.Count == 0 && sharedAttributes.Count > 0 ? sharedAttributes : attributes;

                    // If only one attribute and multiple keywords, propagate attribute to all keywords
                    if (type == RequestType.List && useAttributes.Count == 1 && keywords.Count > 1)
                    {
                        foreach (var kw in keywords)
                        {
                            // If the keyword already contains the attribute, don't duplicate
                            if (kw.Contains(useAttributes[0], StringComparison.OrdinalIgnoreCase))
                            {
                                result.Requests.Add(new Request
                                {
                                    Type = type,
                                    Keyword = kw,
                                    Count = count,
                                    HasExplicitCount = explicitCount,
                                    Attributes = new List<string>(),
                                    Sentence = s
                                });
                            }
                            else
                            {
                                result.Requests.Add(new Request
                                {
                                    Type = type,
                                    Keyword = kw,
                                    Count = count,
                                    HasExplicitCount = explicitCount,
                                    Attributes = new List<string> { useAttributes[0] },
                                    Sentence = s
                                });
                            }
                        }
                    }
                    // If only one attribute and only one keyword, keep as is
                    else if (type == RequestType.List && useAttributes.Count == 1 && keywords.Count == 1)
                    {
                        result.Requests.Add(new Request
                        {
                            Type = type,
                            Keyword = keywords[0],
                            Count = count,
                            HasExplicitCount = explicitCount,
                            Attributes = new List<string> { useAttributes[0] },
                            Sentence = s
                        });
                    }
                    // If multiple attributes and multiple keywords, pair each attribute with each keyword (default)
                    else if (type == RequestType.List && useAttributes.Count > 0 && keywords.Count > 0)
                    {
                        foreach (var attr in useAttributes)
                        {
                            foreach (var kw in keywords)
                            {
                                result.Requests.Add(new Request
                                {
                                    Type = type,
                                    Keyword = kw,
                                    Count = count,
                                    HasExplicitCount = explicitCount,
                                    Attributes = new List<string> { attr },
                                    Sentence = s
                                });
                            }
                        }
                    }
                    // Only keywords
                    else if (type == RequestType.List && useAttributes.Count == 0 && keywords.Count > 0)
                    {
                        foreach (var kw in keywords)
                        {
                            result.Requests.Add(new Request
                            {
                                Type = type,
                                Keyword = kw,
                                Count = count,
                                HasExplicitCount = explicitCount,
                                Attributes = new List<string>(),
                                Sentence = s
                            });
                        }
                    }
                    else if (type == RequestType.HowTo && keywords.Count > 0)
                    {
                        foreach (var kw in keywords)
                        {
                            result.Requests.Add(new Request
                            {
                                Type = type,
                                Keyword = kw,
                                Count = count,
                                HasExplicitCount = explicitCount,
                                Attributes = new List<string>(),
                                Sentence = s
                            });
                        }
                    }
                    else if (type == RequestType.Info && keywords.Count > 0)
                    {
                        foreach (var kw in keywords)
                        {
                            result.Requests.Add(new Request
                            {
                                Type = type,
                                Keyword = kw,
                                Count = count,
                                HasExplicitCount = explicitCount,
                                Attributes = new List<string>(),
                                Sentence = s
                            });
                        }
                    }
                }
            }

            // numbers not linked to counts
            var allNumbers = Regex.Matches(input, @"\d+").Cast<Match>().Select(m => int.Parse(m.Value));
            var listCounts = result.Requests.Where(r => r.Count.HasValue).Select(r => r.Count ?? -1);
            result.StatementNumbers = allNumbers.Except(listCounts).ToList();

            // After all requests are extracted, set default Count to 5 for List requests if Count is null
            foreach (var req in result.Requests)
            {
                if (req.Type == RequestType.List && !req.Count.HasValue)
                {
                    req.Count = 5;
                }
            }
            return result;
        }

        private static bool IsSingularRequest(string sentence)
        {
            sentence = sentence.ToLower();
            bool questionHint = QuestionSingularPattern.IsMatch(sentence);

            var words = sentence.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < words.Length; i++)
            {
                string clean = words[i].Trim(new char[] { '.', ',', '?', ';' });
                if (StopWords.Contains(clean) || clean.Length <= 2) continue;

                if (i > 0)
                {
                    string prev = words[i - 1].ToLower();
                    if (prev == "the" || prev == "this" || prev == "that")
                        return true;
                }

                if (!clean.EndsWith("s") && !int.TryParse(clean, out _))
                    return true;
            }

            return questionHint;
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

        public static List<string> DetectAttributes(string sentence, string keyword)
        {
            var attributes = new List<string>();
            // Helper to remove noise words from within attribute phrases
            string[] extraNoise = new[] { "list", "of", "top", "show", "me", "please", "kindly", "what" };
            Func<string, string> removeNoiseWords = p =>
            {
                var words = p.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Where(w => !StopWords.Contains(w.ToLower()) && !extraNoise.Contains(w.ToLower()))
                    .ToArray();
                return string.Join(" ", words).Trim();
            };

            // Special handling for 'in' pattern: attributes are before 'in', split by and/or
            var lower = sentence.ToLower();
            int inIdx = lower.IndexOf(" in ");
            if (inIdx > 0)
            {
                var beforeIn = sentence.Substring(0, inIdx).Trim();
                var attrParts = Regex.Split(beforeIn, "\b(?:and|or)\b", RegexOptions.IgnoreCase)
                    .Select(p => AttributeCleaner.Replace(p.Trim(), "").Trim())
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(removeNoiseWords)
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .ToList();
                foreach (var part in attrParts)
                {
                    if (!attributes.Contains(part, StringComparer.OrdinalIgnoreCase)
                        && !string.Equals(part, keyword, StringComparison.OrdinalIgnoreCase))
                    {
                        attributes.Add(part);
                    }
                }
                return attributes;
            }
            // Fallback: original logic
            var parts = Regex.Split(sentence, "\b(?:and|or)\b", RegexOptions.IgnoreCase)
                .Select(p => AttributeCleaner.Replace(p.Trim(), "").Trim())
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(removeNoiseWords)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();
            foreach (var part in parts)
            {
                if (!attributes.Contains(part, StringComparer.OrdinalIgnoreCase))
                {
                    attributes.Add(part);
                }
            }
            return attributes;
        }

        public static string ExtractKeyword(string sentence)
        {
            string s = sentence;
            // Special handling for 'in' pattern: keyword is after 'in', preserve 'and'/'or' as in the original
            var lower = s.ToLower();
            int inIdx = lower.IndexOf(" in ");
            if (inIdx > 0)
            {
                var afterIn = s.Substring(inIdx + 4).Trim();
                // Do not split by 'and' or 'or', just clean up noise words
                var kw = AttributeCleaner.Replace(afterIn, "").Trim();
                if (!string.IsNullOrWhiteSpace(kw))
                {
                    // Remove 'list' and 'top' from keyword
                    if (kw.Equals("list", StringComparison.OrdinalIgnoreCase) || kw.Equals("top", StringComparison.OrdinalIgnoreCase))
                        return string.Empty;
                    return kw;
                }
            }
            s = ParenthesesPattern.Replace(s, "");
            s = KeywordCleanupPattern.Replace(s, "").Trim();
            s = SpecialCharPattern.Replace(s, " ");
            // also remove any inline "list of" or "top" that might linger
            s = Regex.Replace(s, @"\blist\s+of\b", " ", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\blist\b", " ", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\btop\b", " ", RegexOptions.IgnoreCase);
            s = MultiSpacePattern.Replace(s, " ").Trim();
            return s;
        }

        private static string CleanText(string text)
        {
            var words = WordSplitPattern.Split(text)
                             .Where(w => !StopWords.Contains(w.ToLower()) && !string.IsNullOrWhiteSpace(w))
                             .ToList();

            return string.Join(" ", words);
        }

        private static string NormalizeInput(string s)
        {
            s = Regex.Replace(s, @"\bi\s+wanted\s+to\s+(have\s+)?", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bgive\s+me\s+(the\s+)?", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bshow\s+me\s+(the\s+)?", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\bprovide\s+me\s+(the\s+)?", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\btell\s+me\b", "", RegexOptions.IgnoreCase); // Remove 'Tell me'

            s = FillerPattern.Replace(s, "");
            s = PolitePattern.Replace(s, "");
            s = DuplicateListOfPattern.Replace(s, "list of ");
            s = DanglingConjunctionPattern.Replace(s, " "); // remove lone "and"/"or"
            s = MultiSpacePattern.Replace(s, " ").Trim();

            return s;
        }

        private static (string Subject, List<string> Descriptors)? TryExtractDescriptorsWithSubject(string originalSentence)
        {
            // Force "list of" at the start to normalize parsing
            string probe = "list of " + originalSentence.Trim();

            // find after "list of"
            var afterListOf = Regex.Replace(probe, @"^\s*list\s+of\s+", "", RegexOptions.IgnoreCase).Trim();
            if (string.IsNullOrWhiteSpace(afterListOf)) return null;

            // split words
            var tokens = afterListOf.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2) return null;

            // subject is the LAST token
            string subject = tokens[^1];

            // everything before subject = descriptors
            string descriptorsPart = string.Join(" ", tokens.Take(tokens.Length - 1));

            var raw = DescriptorSplitPattern.Split(descriptorsPart)
                        .Select(d => d.Trim())
                        .Where(d => !string.IsNullOrWhiteSpace(d))
                        .ToList();

            var descriptors = raw
                .Select(d => AttributeCleaner.Replace(d, ""))
                .Select(d => CleanText(d))
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (descriptors.Count == 0) return null;

            return (subject, descriptors);
        }

        private static List<string> ExpandListRequests(string sentence)
        {
            var results = new List<string>();

            // normalize
            var normalized = Regex.Replace(sentence, @"\blist of\b", "", RegexOptions.IgnoreCase).Trim();
            if (string.IsNullOrWhiteSpace(normalized)) return results;

            var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2) return new List<string> { "list of " + normalized };

            // subject is last word
            string subject = tokens[^1];

            // everything before subject = descriptors
            string descriptorsPart = string.Join(" ", tokens.Take(tokens.Length - 1));

            // split descriptors by and/or/comma
            var descriptors = Regex.Split(descriptorsPart, @"\s*(?:,|and|or)\s*", RegexOptions.IgnoreCase)
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Select(d => d.Trim())
                .ToList();

            // rebuild clean sentences
            foreach (var desc in descriptors)
            {
                results.Add($"list of {desc} {subject}".Trim());
            }

            return results;
        }

    }
}