using System;
using System.Linq;
using System.Threading.Tasks;
using Biz.TKV.AIModelProcess.Tools;
using Biz.TKV.AIModelProcess;
using System.Text.Json;
using System.Net.Http;
using System.Text.RegularExpressions;

class Program
{
    static async Task Main(string[] args)
    {
        
        // Example prompt to analyze
        string userPrompt = "What does superposition mean in quantum physics? In 1 word.";

        // Call the AnalyzePrompt function
        var (intent, subject) = await AnalyzePrompt(userPrompt);

        // Call the RefineSubject function
        var refinedSubject = RefineSubject(subject);

        // Call the EmbedSubject function
        var embeddedSubject = EmbedSubject(refinedSubject);

        // Call the ExtractContent function
        var extractedContent = ExtractContent(embeddedSubject);

        // Construct the final prompt
        var finalPrompt = "User's Query: \n[" + userPrompt + "]\n\nIntent: \n[" + intent + "]\n\nRelevant Information: \n[" + extractedContent + "]\n\nInstructions: \n1. Using the intent and relevant information, provide a clear, concise, and accurate answer to the user's query. \n2. If the information is insufficient, say so politely. \n3. Keep the response natural and directly address the query. \n4. IMPORTANT: Respond in plain text only. \n5. IMPORTANT: Do not use any markdown formatting, bold (**text**), italics (*text*), or special characters like **, *, _, etc. \n6. IMPORTANT: Avoid all formatting.";

        // Prompt LLM with finalPrompt
        var (response, totalMaxTokens, estimatedInputTokens, maxTokens, inputTokens, outputTokens) = PromptLLM(finalPrompt);

        Console.WriteLine("\n===============================\n");
        Console.WriteLine("Intent: " + intent);
        Console.WriteLine("Subject: " + subject);
        Console.WriteLine("Refined Subject: " + refinedSubject);
        Console.WriteLine("Extracted Content: " + extractedContent);
        Console.WriteLine("\n===============================\n");
        Console.WriteLine("Final Prompt: \n" + finalPrompt);
        Console.WriteLine("\n===============================\n");
        Console.WriteLine("LLM Response: \n" + response);
        Console.WriteLine("\n===============================\n");
        Console.WriteLine($"Total Max Tokens: {totalMaxTokens}");
        Console.WriteLine($"Estimated Input Tokens: {estimatedInputTokens} - Max Tokens for Output: {maxTokens}");
        Console.WriteLine($"Actual Input Tokens: {inputTokens} - Actual Output Tokens: {outputTokens}");
    }

    static async Task<(string Intent, string Subject)> AnalyzePrompt(string userPrompt)
    {
        // Initialize the PromptAnalyzerService with default parameters
        var promptAnalyzerService = new PromptAnalyzerService(
            baseURL: "https://192.168.118.23/",
            model: "qwen3:14b",
            maxRetries: 2,
            initialRetryDelayMs: 300,
            generationOptions: new GenerationOptions { Temperature = 0.8, TopP = 0.95 }
        );

        // Call the AnalyzeAsync method and get the result
        var result = await promptAnalyzerService.AnalyzeAsync(userPrompt);

        // Find the intent with the highest confidence
        if (result.Intents != null && result.Intents.Count > 0)
        {
            var highestConfidenceIntent = result.Intents.OrderByDescending(i => i.Confidence).FirstOrDefault();
            if (highestConfidenceIntent != null)
            {
                return (highestConfidenceIntent.Intent, highestConfidenceIntent.Subject);
            }
        }

        return ("NoIntent", "NoSubject");
    }

    static string RefineSubject(string subject)
    {
        // Initialize the RefinedFactory with default parameters
        var refinedModel = new RefinedModel
        {
            HostAPIUrl = "https://192.168.118.23",
            Model = "qwen3:14b",
            RefinerText = "Refine prompt into concise, clear, firm. Output only refined prompt: {0}",
            Options = new { temperature = 0.3, top_p = 0.7 }
        };

        var refinedFactory = new RefinedFactory(refinedModel);

        // Get the refined text
        var refinedResponse = refinedFactory.GetRefinedText(subject);

        if (!string.IsNullOrEmpty(refinedResponse.RefinedText))
        {
            return (refinedResponse.RefinedText);
        }

        return ("NoRefinedSubject");
    }

    static string EmbedSubject(string refinedSubject)
    {
        // Initialize the EmbeddingFactory with default parameters
        var embeddingModel = new EmbeddingModel
        {
            HostAPIUrl = "https://192.168.118.23",
            TextModel = "bge-m3"
        };

        var embeddingFactory = new EmbeddingFactory(embeddingModel);

        // Get the embedding
        var embeddingResponse = embeddingFactory.GetTextEmbedding(refinedSubject);

        if (embeddingResponse.StatusCode.StartsWith("Error"))
        {
            return "Embedding failed: " + embeddingResponse.StatusCode;
        }

        return embeddingResponse.EmbeddedText;
    }

    static string ExtractContent(string embeddedSubject)
    {
        // Parse the embedding string to a list of floats
        var embeddingString = embeddedSubject.Trim('[', ']');
        var embeddingValues = embeddingString.Split(',').Select(s => double.Parse(s.Trim())).ToList();

        // Prepare the query payload for ChromaDB
        var queryPayload = new
        {
            query_embeddings = new[] { embeddingValues },
            n_results = 5  // Get the top 5 results
        };

        var jsonPayload = JsonSerializer.Serialize(queryPayload);

        using var httpClient = new HttpClient();
        var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

        try
        {
            var response = httpClient.PostAsync("http://localhost:8000/api/v1/collections/4862b10e-6917-4176-86db-4ad0426ddd23/query", content).Result;
            if (response.IsSuccessStatusCode)
            {
                var responseJson = response.Content.ReadAsStringAsync().Result;
                var result = JsonSerializer.Deserialize<JsonElement>(responseJson);

                // Extract the top chunks from metadatas
                if (result.TryGetProperty("metadatas", out var metadatas) &&
                    metadatas.GetArrayLength() > 0 &&
                    metadatas[0].GetArrayLength() > 0)
                {
                    var extractedChunks = new System.Collections.Generic.List<string>();
                    for (int i = 0; i < metadatas[0].GetArrayLength(); i++) // Process all returned results (up to 5)
                    {
                        var metadata = metadatas[0][i];
                        if (metadata.TryGetProperty("chunk", out var chunk))
                        {
                            extractedChunks.Add(chunk.GetString() ?? "");
                        }
                    }

                    // Join the extracted chunks into a single string
                    string joined = string.Join("\n\n", extractedChunks);
                    joined = Regex.Replace(joined, @"\s+", " ");
                    joined = joined.Trim();
                    return joined;
                }
            }
            return "Query failed: " + response.StatusCode + " - " + response.Content.ReadAsStringAsync().Result;
        }
        catch (Exception ex)
        {
            return "Error: " + ex.Message;
        }
    }

    static (string Response, int TotalMaxTokens, int EstimatedInputTokens, int MaxTokens, int InputTokens, int OutputTokens) PromptLLM(string finalPrompt)
    {
        const int totalMaxTokens = 2000;
        int estimatedInputTokens = (int)Math.Ceiling(finalPrompt.Length / 4.5);
        int maxTokens = Math.Max(totalMaxTokens - estimatedInputTokens, 1); // Ensure at least 1

        using var httpClient = new HttpClient();
        var payload = new
        {
            model = "qwen3:14b",
            messages = new[] { new { role = "user", content = finalPrompt } },
            temperature = 0.3,
            top_p = 0.7,
            max_tokens = maxTokens
        };
        var jsonPayload = JsonSerializer.Serialize(payload);
        var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

        try
        {
            var response = httpClient.PostAsync("https://192.168.118.23/v1/chat/completions", content).Result;
            if (response.IsSuccessStatusCode)
            {
                var responseJson = response.Content.ReadAsStringAsync().Result;
                var result = JsonSerializer.Deserialize<JsonElement>(responseJson);

                int inputTokens = 0, outputTokens = 0;
                if (result.TryGetProperty("usage", out var usage))
                {
                    inputTokens = usage.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
                    outputTokens = usage.TryGetProperty("completion_tokens", out var ct) ? ct.GetInt32() : 0;
                }

                if (result.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var message = choices[0].GetProperty("message");
                    return (message.GetProperty("content").GetString() ?? "No response", totalMaxTokens, estimatedInputTokens, maxTokens, inputTokens, outputTokens);
                }
            }
            return ("LLM Error: " + response.StatusCode, totalMaxTokens, estimatedInputTokens, maxTokens, 0, 0);
        }
        catch (Exception ex)
        {
            return ("Error: " + ex.Message, totalMaxTokens, estimatedInputTokens, maxTokens, 0, 0);
        }
    }
}