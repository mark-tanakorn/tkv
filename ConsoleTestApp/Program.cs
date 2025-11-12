using System;
using System.Linq;
using System.Threading.Tasks;
using Biz.TKV.AIModelProcess.Tools;
using Biz.TKV.AIModelProcess;
using System.Text.Json;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.IO;
using Microsoft.Extensions.Configuration;

class Program
{
    static async Task Main(string[] args)
    {
        // Load configuration settings from appsettings.json
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        // Retrieve the user prompt from configuration
        string userPrompt = config["userPrompt"];

        // Call the AnalyzePrompt function
        var (intent, subject) = await AnalyzePrompt(config, userPrompt);

        // Call the RefineSubject function
        var refinedSubject = RefineSubject(config, subject);

        // Call the EmbedSubject function
        var embeddedSubject = EmbedSubject(config, refinedSubject);

        // Call the ExtractContent function
        var extractedContent = ExtractContent(config, embeddedSubject);

        // Construct the final prompt
        var finalPrompt = "User's Query: \n[" + userPrompt + "]\n\nIntent: \n[" + intent + "]\n\nRelevant Information: \n[" + extractedContent + "]\n\nInstructions: \n1. Using the intent and relevant information, provide a clear, concise, and accurate answer to the user's query. \n2. If the information is insufficient, say so politely. \n3. Keep the response natural and directly address the query. \n4. IMPORTANT: Respond in plain text only. \n5. IMPORTANT: Do not use any markdown formatting, bold (**text**), italics (*text*), or special characters like **, *, _, etc. \n6. IMPORTANT: Avoid all formatting.";

        // Prompt LLM with finalPrompt
        var (response, totalMaxTokens, estimatedInputTokens, maxTokens, inputTokens, outputTokens) = PromptLLM(config, finalPrompt);

        Console.WriteLine("\n===============================\n");
        Console.WriteLine("Prompt: " + userPrompt);
        Console.WriteLine("Subject: " + subject);
        Console.WriteLine("Refined Subject: " + refinedSubject);
        Console.WriteLine("\n===============================\n");
        Console.WriteLine(finalPrompt);
        Console.WriteLine("\n===============================\n");
        Console.WriteLine("LLM Response: \n" + response);
        Console.WriteLine("\n===============================\n");
        Console.WriteLine($"Total Max Tokens: {totalMaxTokens}");
        Console.WriteLine($"Estimated Input Tokens: {estimatedInputTokens} - Max Tokens for Output: {maxTokens}");
        Console.WriteLine($"Actual Input Tokens: {inputTokens} - Actual Output Tokens: {outputTokens}");
    }

    static async Task<(string Intent, string Subject)> AnalyzePrompt(IConfiguration config, string userPrompt)
    {
        // Retrieve configuration settings for the PromptAnalyzerService
        string baseURL = config["AnalyzePromptSettings:baseURL"];
        string model = config["AnalyzePromptSettings:model"];
        int maxRetries = int.Parse(config["AnalyzePromptSettings:maxRetries"]);
        int initialRetryDelayMs = int.Parse(config["AnalyzePromptSettings:initialRetryDelayMs"]);
        double Temperature = double.Parse(config["AnalyzePromptSettings:Temperature"]);
        double TopP = double.Parse(config["AnalyzePromptSettings:TopP"]);

        // Initialize the PromptAnalyzerService
        var promptAnalyzerService = new PromptAnalyzerService(
            baseURL: baseURL,
            model: model,
            maxRetries: maxRetries,
            initialRetryDelayMs: initialRetryDelayMs,
            generationOptions: new GenerationOptions { Temperature = Temperature, TopP = TopP }
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

    static string RefineSubject(IConfiguration config, string subject)
    {
        // Retrieve configuration settings for the RefinedFactory
        string HostAPIUrl = config["RefineSubjectSettings:HostAPIUrl"];
        string Model = config["RefineSubjectSettings:Model"];
        string RefinerText = config["RefineSubjectSettings:RefinerText"];
        double Temperature = double.Parse(config["RefineSubjectSettings:Temperature"]);
        double TopP = double.Parse(config["RefineSubjectSettings:TopP"]);

        // Initialize the RefinedFactory
        var refinedModel = new RefinedModel
        {
            HostAPIUrl = HostAPIUrl,
            Model = Model,
            RefinerText = RefinerText,
            Options = new { Temperature, TopP }
        };

        // Get the refined text
        var refinedFactory = new RefinedFactory(refinedModel);
        var refinedResponse = refinedFactory.GetRefinedText(subject);
        if (!string.IsNullOrEmpty(refinedResponse.RefinedText))
        {
            return refinedResponse.RefinedText;
        }
        return "NoRefinedSubject";
    }

    static string EmbedSubject(IConfiguration config, string refinedSubject)
    {
        // Retrieve configuration settings for the EmbeddingFactory
        string HostAPIUrl = config["EmbedSubjectSettings:HostAPIUrl"];
        string TextModel = config["EmbedSubjectSettings:TextModel"];

        // Initialize the EmbeddingFactory
        var embeddingModel = new EmbeddingModel
        {
            HostAPIUrl = HostAPIUrl,
            TextModel = TextModel
        };

        // Get the embedding
        var embeddingFactory = new EmbeddingFactory(embeddingModel);
        var embeddingResponse = embeddingFactory.GetTextEmbedding(refinedSubject);
        if (embeddingResponse.StatusCode.StartsWith("Error"))
        {
            return "Embedding failed: " + embeddingResponse.StatusCode;
        }
        return embeddingResponse.EmbeddedText;
    }

    static string ExtractContent(IConfiguration config, string embeddedSubject)
    {
        // Retrieve configuration settings from appsettings.json
        int n_results = int.Parse(config["ExtractContentSettings:n_results"]);
        string db_API = config["ExtractContentSettings:db_API"];

        // Parse the embedding string to a list of floats
        var embeddingString = embeddedSubject.Trim('[', ']');
        var embeddingValues = embeddingString.Split(',').Select(s => double.Parse(s.Trim())).ToList();

        // Prepare the query payload for ChromaDB
        var queryPayload = new
        {
            query_embeddings = new[] { embeddingValues },
            n_results
        };
        var jsonPayload = JsonSerializer.Serialize(queryPayload);
        using var httpClient = new HttpClient();
        var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

        try
        {
            // Send the query to ChromaDB
            var response = httpClient.PostAsync(db_API, content).Result;
            if (response.IsSuccessStatusCode)
            {
                // Deserialize the JSON response from the HTTP request into a JsonElement
                var responseJson = response.Content.ReadAsStringAsync().Result;
                var result = JsonSerializer.Deserialize<JsonElement>(responseJson);

                // Extract the top chunks from metadata
                if (result.TryGetProperty("metadatas", out var metadatas) &&
                    metadatas.GetArrayLength() > 0 &&
                    metadatas[0].GetArrayLength() > 0)
                {
                    var extractedChunks = new System.Collections.Generic.List<string>();
                    for (int i = 0; i < metadatas[0].GetArrayLength(); i++)
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

    static (string Response, int TotalMaxTokens, int EstimatedInputTokens, int MaxTokens, int InputTokens, int OutputTokens) PromptLLM(IConfiguration config, string finalPrompt)
    {
        // Retrieve configuration settings from appsettings.json
        int totalMaxTokens = int.Parse(config["PromptLLMSettings:totalMaxTokens"]);
        double char2TokenRatio = double.Parse(config["PromptLLMSettings:char2TokenRatio"]);
        string model = config["PromptLLMSettings:model"];
        double Temperature = double.Parse(config["PromptLLMSettings:Temperature"]);
        double TopP = double.Parse(config["PromptLLMSettings:TopP"]);
        string llm_api = config["PromptLLMSettings:llm_api"];

        // Calculate the estimated input tokens and maximum tokens for the response
        int estimatedInputTokens = (int)Math.Ceiling(finalPrompt.Length / char2TokenRatio);
        int maxTokens = Math.Max(totalMaxTokens - estimatedInputTokens, 1);

        // Create the HTTP payload and content for the LLM API request
        using var httpClient = new HttpClient();
        var payload = new
        {
            model,
            messages = new[] { new { role = "user", content = finalPrompt } },
            Temperature,
            TopP,
            max_tokens = maxTokens
        };
        var jsonPayload = JsonSerializer.Serialize(payload);
        var content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json");

        try
        {
            // Send the prompt to the LLM API
            var response = httpClient.PostAsync(llm_api, content).Result;
            if (response.IsSuccessStatusCode)
            {
                // Deserialize the JSON response from the HTTP request into a JsonElement
                var responseJson = response.Content.ReadAsStringAsync().Result;
                var result = JsonSerializer.Deserialize<JsonElement>(responseJson);

                // Extract token usage and response content from the API result
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