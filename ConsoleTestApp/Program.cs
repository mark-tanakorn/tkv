using System;
using Biz.TKV.AIModelProcess.Tools;
using Biz.TKV.AIModelProcess;

class Program
{
    static void Main(string[] args)
    {
        Console.WriteLine("Enter a prompt:");
        string prompt = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(prompt))
        {
            Console.WriteLine("No prompt entered.");
            return;
        }

        // Prompt Analysis
        var result = PromptAnalyzer.Analyze(prompt);
        Console.WriteLine("Analysis Result:");
        Console.WriteLine($"Has Request: {result.HasRequest}");
        Console.WriteLine($"Requests: {result.Requests.Count}");
        foreach (var req in result.Requests)
        {
            Console.WriteLine($"Type: {req.Type}, Keyword: {req.Keyword}, Attributes: {string.Join(", ", req.Attributes)}, Count: {req.Count}");
        }
        Console.WriteLine($"Combined Sentence: {result.CombinedSentence}");
        Console.WriteLine($"Question Flags - IsWhy: {result.IsWhy}, IsWhen: {result.IsWhen}, IsWho: {result.IsWho}, IsWhere: {result.IsWhere}, IsWhat: {result.IsWhat}, IsLatest: {result.IsLatest}");
        Console.WriteLine($"Statement Numbers: {string.Join(", ", result.StatementNumbers)}");

        // LLM Refinement
        Console.WriteLine("\nRefining prompt with LLM...");
        var refinedModel = new RefinedModel
        {
            HostAPIUrl = "https://192.168.118.23",
            Model = "qwen3:14b",
            RefinerText = "Refine prompt into concise, clear, firm. Output only refined prompt: {0}"
        };
        var refinedFactory = new RefinedFactory(refinedModel);
        var refinedResponse = refinedFactory.GetRefinedText(prompt);
        Console.WriteLine("LLM Refined Text:");
        Console.WriteLine(refinedResponse.RefinedText);
        Console.WriteLine($"Status: {refinedResponse.StatusCode}");
        Console.WriteLine($"Tokens - Prompt: {refinedResponse.PromptToken}, Completion: {refinedResponse.CompletionToken}, Total: {refinedResponse.TotalToken}");
    }
}
