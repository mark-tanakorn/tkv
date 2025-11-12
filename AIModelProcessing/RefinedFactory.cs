using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Net.Http.Json;
using System.Text.Json;
using System.IO;
using System.Reflection;

namespace Biz.TKV.AIModelProcess
{
    public class RefinedFactory
    {
        private static string _hostAPIUrl = "https://192.168.118.23/api/generate";
        private static string _Model = "llama3.1";
        private string _refinerText = "Refine prompt into concise, clear, firm. Output only refined prompt: {0}";
        private readonly HttpClient _httpClient;

        // qwen3:8b too much consumetion with thinking on response

        public RefinedFactory(RefinedModel refinedModel)
        {
            _httpClient = new HttpClient();
            if (refinedModel != null)
            {
                if (!string.IsNullOrEmpty(refinedModel.HostAPIUrl))
                    _hostAPIUrl = string.Join("/", new string[] { refinedModel.HostAPIUrl, "api/generate" });
                if (!string.IsNullOrEmpty(refinedModel.Model))
                    _Model = refinedModel.Model;
                if (!string.IsNullOrEmpty(refinedModel.RefinerText))
                    _refinerText = refinedModel.RefinerText;
                _options = refinedModel.Options;
            }
        }

        private double _temperature = 0.2;
        private double _topP = 0.8;
        private object _options;

        public RefinedResponse GetRefinedText(string text)
        {
            var url = _hostAPIUrl;
            object requestData = null;
            RefinedResponse responseData = new RefinedResponse();

            try
            {
                if (string.IsNullOrEmpty(text))
                {
                    responseData.StatusCode = "I don't receive anything from you.";
                    return responseData;
                }

                string refinederText = string.Format(_refinerText, text);

                // OpenAI Object
                requestData = new
                {
                    model = _Model,
                    prompt = refinederText,
                    stream = false,
                    options = _options ?? new {
                        temperature = _temperature,
                        top_p = _topP
                    }
                };

                var json = JsonConvert.SerializeObject(requestData);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = _httpClient.PostAsJsonAsync(url, requestData);
                if (!response.Result.IsSuccessStatusCode)
                {
                    responseData.StatusCode = string.Format("Error: There is error on refined API ({0}).", response.Result.StatusCode);
                    return responseData;
                }
                responseData.StatusCode = response.Result.StatusCode.ToString();

                var result = response.Result.Content.ReadAsStringAsync().Result;
                dynamic parsed = Newtonsoft.Json.JsonConvert.DeserializeObject(result);
                if (parsed == null || parsed.response == null)
                    return responseData;

                responseData.RefinedText = parsed.response;
                responseData.PromptToken = parsed.prompt_eval_count != null ? parsed.prompt_eval_count : 0;
                responseData.CompletionToken = parsed.eval_count != null ? parsed.eval_count : 0;
                responseData.TotalToken = responseData.PromptToken + responseData.CompletionToken;
                return responseData;  // You can extract embedding from the response
            }
            catch (Exception ex)
            {
                responseData.StatusCode = "Error: " + ex.Message;
                return responseData;
            }
        }
    }
    public class RefinedModel
    {
        public string HostAPIUrl { get; set; }
        public string Model { get; set; }
        public string RefinerText { get; set; }
        public object Options { get; set; }
    }
    public class generationOptions
    {
        public string HostAPIUrl { get; set; }
        public string Model { get; set; }
        public string RefinerText { get; set; }
        public double Temperature { get; set; } = 0.2;
        public double TopP { get; set; } = 0.8;
    }

    public class RefinedResponse
    {
        public string StatusCode { get; set; }
        public string RefinedText { get; set; }
        public int? PromptToken { get; set; }
        public int? CompletionToken { get; set; }
        public int? TotalToken { get; set; }
    }
}
