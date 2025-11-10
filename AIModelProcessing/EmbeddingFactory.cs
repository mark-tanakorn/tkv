using Newtonsoft.Json;
using System;
using System.Net.Http;
using System.Text;

namespace Biz.TKV.AIModelProcess
{
    public class EmbeddingFactory
    {
        private static string _hostAPIUrl = "https://192.168.118.23/v1/embeddings";
        private static string _textModel = "bge-m3";
        private static string _imageModel;
        private static string _videoModel;
        private static string _audioModel;
        private readonly HttpClient _httpClient;

        public EmbeddingFactory(EmbeddingModel embeddingModel)
        {
            _httpClient = new HttpClient();
            if (embeddingModel != null)
            {
                if (!string.IsNullOrEmpty(embeddingModel.HostAPIUrl))
                    _hostAPIUrl = string.Join("/", new string[] { embeddingModel.HostAPIUrl, "v1/embeddings" });
                if (!string.IsNullOrEmpty(embeddingModel.TextModel))
                    _textModel = embeddingModel.TextModel;
                if (!string.IsNullOrEmpty(embeddingModel.ImageModel))
                    _imageModel = embeddingModel.ImageModel;
                if (!string.IsNullOrEmpty(embeddingModel.VideoModel))
                    _videoModel = embeddingModel.VideoModel;
                if (!string.IsNullOrEmpty(embeddingModel.AudioModel))
                    _audioModel = embeddingModel.AudioModel;
            }
        }

        public EmbeddingResponse GetTextEmbedding(string text)
        {
            var url = _hostAPIUrl;
            object requestData = null;
            EmbeddingResponse responseData = new EmbeddingResponse(); 
            
            try
            {
                // OpenAI Object
                requestData = new
                {
                    model = _textModel,
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

                responseData.EmbeddedModel = _textModel;
                responseData.EmbeddedText = "[" + string.Join(",", parsed.data[0].embedding) + "]";
                responseData.PromptToken = parsed.usage.prompt_tokens != null ? parsed.usage.prompt_tokens : 0;
                responseData.CompletionToken = parsed.usage.answer_tokens != null ? parsed.usage.answer_tokens : 0;
                responseData.TotalToken = parsed.usage.total_tokens != null ? parsed.usage.total_tokens : 0;
                return responseData;  // You can extract embedding from the response
            }
            catch(Exception ex)
            {
                responseData.StatusCode = "Error: " + ex.Message + ", " + ex.StackTrace;
                return responseData;
            }
        }
    }

    public class EmbeddingModel
    { 
        public string HostAPIUrl { get; set; }
        public string TextModel { get; set; }
        public string ImageModel { get; set; }
        public string VideoModel { get; set; }
        public string AudioModel { get; set; }
    }

    public class EmbeddingResponse
    {
        public string StatusCode { get; set; }
        public string EmbeddedText { get; set; }
        public string EmbeddedModel { get; set; }
        public int? PromptToken { get; set; }
        public int? CompletionToken { get; set; }
        public int? TotalToken { get; set; }
    }
}
