using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Biz.TKV.AIModelProcess
{
    public class ChatFactory
    {
        private static string _hostAPIUrl = "https://localhost/v1/chat/completions";
        private static string _Model = "llama3.1";
        private readonly HttpClient _httpClient;
        public ChatFactory(ChatModel chatModel)
        {
            _httpClient = new HttpClient();
            if (chatModel != null)
            {
                if (!string.IsNullOrEmpty(chatModel.HostAPIUrl))
                    _hostAPIUrl = chatModel.HostAPIUrl;
                if (!string.IsNullOrEmpty(chatModel.Model))
                    _Model = chatModel.Model;
            }
        }

        public ChatResponse GetRefinedText(string text)
        {
            var url = _hostAPIUrl;
            object requestData = null;
            ChatResponse responseData = new ChatResponse();

            try
            {
                // OpenAI Object
                requestData = new
                {
                    model = _Model,
                    input = text
                };

                var jsonContent = JsonConvert.SerializeObject(requestData);

                var requestMessage = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(jsonContent, Encoding.UTF8, "application/json")
                };

                var response = _httpClient.Send(requestMessage);
                response.EnsureSuccessStatusCode();
                responseData.StatusCode = response.StatusCode.ToString();

                var result = response.Content.ReadAsStringAsync().Result;
                dynamic parsed = Newtonsoft.Json.JsonConvert.DeserializeObject(result);
                if (parsed == null || parsed.data == null || parsed.data.Count == 0 || parsed.data[0].embedding == null)
                    return responseData;

                responseData.ChatText = "[" + string.Join(",", parsed.data[0].embedding) + "]";
                responseData.PromptToken = parsed.usage.prompt_tokens != null ? parsed.usage.prompt_tokens : 0;
                responseData.CompletionToken = parsed.usage.answer_tokens != null ? parsed.usage.answer_tokens : 0;
                responseData.TotalToken = parsed.usage.total_tokens != null ? parsed.usage.total_tokens : 0;
                return responseData;  // You can extract embedding from the response
            }
            catch (Exception ex)
            {
                responseData.StatusCode = "Error: " + ex.Message;
                return responseData;
            }

        }
    }

    public class ChatModel
    {
        public string HostAPIUrl { get; set; }
        public string Model { get; set; }
    }

    public class ChatResponse
    {
        public string StatusCode { get; set; }
        public string ChatText { get; set; }
        public int? PromptToken { get; set; }
        public int? CompletionToken { get; set; }
        public int? TotalToken { get; set; }
    }
}
