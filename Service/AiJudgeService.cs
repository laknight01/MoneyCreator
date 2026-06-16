using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace MoneyCreator.Service
{
    public class AiJudgeService
    {
        private readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        private const string LM_STUDIO_CHAT_URL = "http://localhost:1234/v1/chat/completions";
        private const string LM_STUDIO_MODELS_URL = "http://localhost:1234/v1/models";

        public async Task<bool> IsAvailableAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync(LM_STUDIO_MODELS_URL);
                return response.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        public async Task<string> ExecuteAsync(string modelFilter, string systemPrompt, string userPrompt)
        {
            string[] models = await GetAvailableModelsAsync();
            if (models == null || models.Length == 0) return string.Empty;

            string filter = modelFilter ?? string.Empty;
            string targetModel = models.FirstOrDefault(m => m.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                ?? models.FirstOrDefault(m => m.IndexOf("llama", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? models.FirstOrDefault(m => m.IndexOf("qwen2.5-coder", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? models.FirstOrDefault(m => m.IndexOf("qwen2.5-vl", StringComparison.OrdinalIgnoreCase) >= 0)
                ?? models.FirstOrDefault();

            string jsonResponse = await SendRequestAsync(targetModel, systemPrompt, userPrompt);
            if (string.IsNullOrWhiteSpace(jsonResponse)) return string.Empty;

            var fullResponse = JObject.Parse(jsonResponse);
            string content = fullResponse["choices"]?[0]?["message"]?["content"]?.ToString();
            return content?.Replace("```json", "").Replace("```", "").Trim() ?? string.Empty;
        }

        private async Task<string> SendRequestAsync(string model, string system, string user)
        {
            var request = new
            {
                model = model,
                messages = new[] {
                    new { role = "system", content = system },
                    new { role = "user", content = user }
                },
                temperature = 0.1,
                max_tokens = 300
            };

            var content = new StringContent(JsonConvert.SerializeObject(request), Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync(LM_STUDIO_CHAT_URL, content);
            if (!response.IsSuccessStatusCode) return string.Empty;
            return await response.Content.ReadAsStringAsync();
        }

        private async Task<string[]> GetAvailableModelsAsync()
        {
            try
            {
                var response = await _httpClient.GetStringAsync(LM_STUDIO_MODELS_URL);
                var json = JObject.Parse(response);
                return json["data"]?.Select(m => m["id"]?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray()
                    ?? new string[0];
            }
            catch { return new string[0]; }
        }
    }
}
