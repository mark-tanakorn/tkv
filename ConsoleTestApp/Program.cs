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
        string userPrompt = "What is entanglement?";

        // Call the AnalyzePrompt function
        var (intent, subject) = await AnalyzePrompt(userPrompt);
        // Console.WriteLine("Intent: " + intent);
        // Console.WriteLine("Subject: " + subject);

        // Call the RefineSubject function
        var refinedSubject = RefineSubject(subject);
        // Console.WriteLine("Refined Subject: " + refinedSubject);

        // Call the EmbedSubject function
        var embeddedIntentRefinedSubject = EmbedSubject(intent + " " + refinedSubject);

        // Call the ExtractContent function
        var extractedContent = ExtractContent(embeddedIntentRefinedSubject);
        var finalPrompt = "User's Query: \n[" + userPrompt + "]\n\nIntent: \n[" + intent + "]\n\nRelevant Information: \n[" + extractedContent + "]\n\nInstructions: \n1. Using the intent and relevant information, provide a clear, concise, and accurate answer to the user's query. \n2. If the information is insufficient, say so politely. \n3. Keep the response natural and directly address the query. \n4. IMPORTANT: Respond in plain text only. \n5. IMPORTANT: Do not use any markdown formatting, bold (**text**), italics (*text*), or special characters like **, *, _, etc. \n6. IMPORTANT: Avoid all formatting.";
        Console.WriteLine(finalPrompt);

        // Prompt LLM with finalPrompt
        var response = promptLLM(finalPrompt);
        Console.WriteLine("\nLLM Response: \n" + response);
    }

    static async Task<(string Intent, string Subject)> AnalyzePrompt(string userPrompt)
    {
        // Initialize the PromptAnalyzerService with default parameters
        var promptAnalyzerService = new PromptAnalyzerService(
            baseURL: "https://192.168.118.23/",
            model: "qwen3:14b",
            maxRetries: 2,
            initialRetryDelayMs: 300
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
            RefinerText = "Refine prompt into concise, clear, firm. Output only refined prompt: {0}"
        };

        var refinedFactory = new RefinedFactory(refinedModel);

        // Get the refined text
        var refinedResponse = refinedFactory.GetRefinedText(subject);

        if (!string.IsNullOrEmpty(refinedResponse.RefinedText))
        {
            return refinedResponse.RefinedText;
        }

        return "NoRefinedSubject";
    }

    static string EmbedSubject(string intentRefinedSubject)
    {
        // Initialize the EmbeddingFactory with default parameters
        var embeddingModel = new EmbeddingModel
        {
            HostAPIUrl = "https://192.168.118.23",
            TextModel = "bge-m3"
        };

        var embeddingFactory = new EmbeddingFactory(embeddingModel);

        // Get the embedding
        var embeddingResponse = embeddingFactory.GetTextEmbedding(intentRefinedSubject);

        if (embeddingResponse.StatusCode.StartsWith("Error"))
        {
            return "Embedding failed: " + embeddingResponse.StatusCode;
        }

        return embeddingResponse.EmbeddedText;
    }

    static string ExtractContent(string embeddedIntentRefinedSubject)
    {
        // Parse the embedding string to a list of floats
        var embeddingString = embeddedIntentRefinedSubject.Trim('[', ']');
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

    static string promptLLM(string finalPrompt)
    {
        using var httpClient = new HttpClient();
        var payload = new
        {
            model = "qwen3:14b",
            messages = new[] { new { role = "user", content = finalPrompt } }
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
                if (result.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var message = choices[0].GetProperty("message");
                    return message.GetProperty("content").GetString() ?? "No response";
                }
            }
            return "LLM Error: " + response.StatusCode;
        }
        catch (Exception ex)
        {
            return "Error: " + ex.Message;
        }
    }
}