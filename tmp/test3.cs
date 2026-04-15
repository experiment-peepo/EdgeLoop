using System;
using System.Net.Http;
using System.Threading.Tasks;

class Program {
    static async Task Main() {
        var handler = new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Add("Accept", "application/json, text/plain, */*");
        try {
            var json = await client.GetStringAsync("https://api.iwara.tv/video/4fCgpgm5p3MyK");
            Console.WriteLine("JSON saved: " + json.Length + " bytes");
            Console.WriteLine(json);
        } catch (Exception ex) {
            Console.WriteLine("Error: " + ex.Message);
        }
    }
}
