using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VideoProduction;

/// <summary>验证模型请求并锁定本地上传文件，使指纹与实际提交字节保持一致。</summary>
internal sealed class ProviderRequest : IDisposable
{
    private readonly List<FileStream> files = [];
    public required string Kind { get; init; }
    public required string Model { get; init; }
    public required string Endpoint { get; init; }
    public string Fingerprint { get; private set; } = "";
    public HttpContent Content { get; private set; } = null!;

    /// <summary>读取一个有界请求；提示词和签名地址仅保留在内存，不写任务记录。</summary>
    public static async Task<ProviderRequest> BuildAsync(string kind, string path, CancellationToken ct)
    {
        path = Path.GetFullPath(path);
        var source = JsonFiles.Read<JsonElement>(path);
        if (source.ValueKind != JsonValueKind.Object) throw new InvalidDataException("请求必须是 JSON 对象。");
        var (model, endpoint, allowed) = kind switch
        {
            "image" => ("qwen-image-2.0-pro", "/images/generations", new[] { "model", "prompt", "size", "n", "response_format" }),
            "voice" => ("Qwen3-TTS", "/async/audio/speech", new[] { "model", "output_format", "inputs" }),
            "motion" => ("ViduQ2-Turbo", "/async/videos/generations", new[] { "model", "prompt", "resolution", "duration", "seed", "first_frame" }),
            "lipsync" => ("Duix-Avatar", "/async/videos/audio-video-to-video", new[] { "model", "ref_video", "ref_audio" }),
            _ => throw new ArgumentException("kind 只支持 image、voice、motion、lipsync。")
        };
        var request = new ProviderRequest { Kind = kind, Model = model, Endpoint = endpoint };
        try
        {
            ValidateKeys(source, allowed);
            var payload = JsonNode.Parse(source.GetRawText(), documentOptions: new JsonDocumentOptions { MaxDepth = 16 })!.AsObject();
            if (payload["model"] is not null && payload["model"]!.GetValue<string>() != model)
                throw new InvalidDataException("模型与用途分类不匹配。");
            payload["model"] = model;
            ValidatePayload(kind, payload);
            if (kind == "lipsync")
            {
                var multipart = new MultipartFormDataContent();
                request.Content = multipart;
                multipart.Add(new StringContent(model), "model");
                // 哈希时锁住文件，并使用同一句柄上传，避免校验后文件被替换。
                foreach (var field in new[] { "ref_video", "ref_audio" })
                {
                    var relative = RequiredText(payload, field, 4096);
                    var filename = Path.GetFullPath(relative, Path.GetDirectoryName(path)!);
                    var stream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    request.files.Add(stream);
                    if (stream.Length < 16 || stream.Length > 512L * 1024 * 1024)
                        throw new InvalidDataException("上传素材必须在16字节至512MiB之间。");
                    using var hashLimit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    hashLimit.CancelAfter(TimeSpan.FromSeconds(30));
                    var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, hashLimit.Token));
                    stream.Position = 0;
                    var header = new byte[12];
                    _ = await stream.ReadAsync(header, hashLimit.Token);
                    stream.Position = 0;
                    var isWave = header.AsSpan(0, 4).SequenceEqual("RIFF"u8) && header.AsSpan(8, 4).SequenceEqual("WAVE"u8);
                    var content = new StreamContent(stream);
                    content.Headers.ContentType = new MediaTypeHeaderValue(field == "ref_video" ? "video/mp4" : isWave ? "audio/wav" : "audio/mpeg");
                    // 文件名固定且不含用户路径；实际内容由文件哈希参与去重。
                    multipart.Add(content, field, field == "ref_video" ? "video.mp4" : isWave ? "audio.wav" : "audio.mp3");
                    payload[field] = "sha256:" + hash;
                }
            }
            else request.Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            request.Fingerprint = ComputeFingerprint(kind, payload);
            return request;
        }
        catch { request.Dispose(); throw; }
    }

    /// <summary>为 JSON 属性排序并对类型、用途和上传文件哈希生成稳定指纹。</summary>
    private static string ComputeFingerprint(string kind, JsonObject payload)
    {
        using var memory = new MemoryStream();
        using (var writer = new Utf8JsonWriter(memory))
        {
            writer.WriteStartObject(); writer.WriteString("kind", kind); writer.WritePropertyName("payload");
            var count = 0;
            WriteCanonical(writer, JsonSerializer.SerializeToElement(payload), ref count, Stopwatch.StartNew(), 0);
            writer.WriteEndObject();
        }
        return Convert.ToHexStringLower(SHA256.HashData(memory.ToArray()));
    }

    /// <summary>最多访问4096个节点、16层及3秒，拒绝异常复杂的指纹输入。</summary>
    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement node, ref int count, Stopwatch timer, int depth)
    {
        if (++count > 4096 || depth > 16 || timer.Elapsed > TimeSpan.FromSeconds(3))
            throw new InvalidDataException("请求结构超过指纹处理上限。");
        if (node.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in node.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
            { writer.WritePropertyName(property.Name); WriteCanonical(writer, property.Value, ref count, timer, depth + 1); }
            writer.WriteEndObject();
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var child in node.EnumerateArray()) WriteCanonical(writer, child, ref count, timer, depth + 1);
            writer.WriteEndArray();
        }
        else if (node.ValueKind == JsonValueKind.Number && node.TryGetDecimal(out var number)) writer.WriteNumberValue(number);
        else node.WriteTo(writer);
    }

    /// <summary>拒绝重复或未核实的字段，确保不会误把凭据作为请求参数发送。</summary>
    private static void ValidateKeys(JsonElement source, string[] allowed)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in source.EnumerateObject().Take(33))
            if (seen.Count >= 32 || !seen.Add(property.Name) || !allowed.Contains(property.Name, StringComparer.Ordinal))
                throw new InvalidDataException("请求包含重复或不支持的字段。");
    }

    /// <summary>校验已实测的模型契约；一次请求只生成一个可追踪的输出。</summary>
    private static void ValidatePayload(string kind, JsonObject payload)
    {
        if (kind == "image")
        {
            _ = RequiredText(payload, "prompt", 2000);
            if (payload["n"] is not null && payload["n"]!.GetValue<int>() != 1) throw new InvalidDataException("图片请求只支持 n=1。");
            payload["n"] = 1;
            payload["response_format"] ??= "url";
            if (RequiredText(payload, "response_format", 16) is not ("url" or "b64_json")) throw new InvalidDataException("图片响应格式无效。");
            if (payload["size"] is not null) _ = RequiredText(payload, "size", 32);
        }
        else if (kind == "voice")
        {
            if (payload["inputs"] is not JsonArray inputs || inputs.Count != 1 || inputs[0] is not JsonObject input)
                throw new InvalidDataException("配音 inputs 必须是只有一项的对象数组。");
            ValidateKeys(JsonSerializer.SerializeToElement(input), ["prompt", "speaker", "language", "instruction"]);
            _ = RequiredText(input, "prompt", 8000);
            _ = RequiredText(input, "speaker", 128);
            if (input["language"] is not null) _ = RequiredText(input, "language", 64);
            if (input["instruction"] is not null) _ = RequiredText(input, "instruction", 2000);
            payload["output_format"] ??= "mp3";
            if (RequiredText(payload, "output_format", 16) is not ("mp3" or "wav")) throw new InvalidDataException("配音只支持 mp3 或 wav。");
        }
        else if (kind == "motion")
        {
            _ = RequiredText(payload, "prompt", 4000);
            var duration = payload["duration"]?.GetValue<double>() ?? throw new InvalidDataException("动作请求缺少 duration。");
            if (!double.IsFinite(duration) || duration is < 1 or > 10) throw new InvalidDataException("动作时长必须为1至10秒。");
            if (payload["first_frame"] is not null)
            {
                var url = RequiredText(payload, "first_frame", 16000);
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.UserInfo.Length > 0)
                    throw new InvalidDataException("first_frame 必须是 HTTPS 图片地址。");
            }
            if (payload["resolution"] is not null) _ = RequiredText(payload, "resolution", 16);
        }
        else { _ = RequiredText(payload, "ref_video", 4096); _ = RequiredText(payload, "ref_audio", 4096); }
    }

    /// <summary>读取有限长度的必要字符串，错误不回显请求内容。</summary>
    private static string RequiredText(JsonObject payload, string field, int max)
    {
        var text = payload[field]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(text) || text.Length > max || text.Contains('\0'))
            throw new InvalidDataException("请求缺少必要文本或字段长度无效。");
        return text;
    }

    /// <summary>释放请求内容和本次打开的文件句柄，不创建后台进程。</summary>
    public void Dispose()
    {
        Content?.Dispose();
        foreach (var file in files) file.Dispose();
    }
}
