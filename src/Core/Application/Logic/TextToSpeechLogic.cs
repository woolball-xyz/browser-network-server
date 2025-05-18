using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Domain.Contracts;
using Domain.Utilities;
using StackExchange.Redis;

namespace Application.Logic;

public sealed class TextToSpeechLogic : ITextToSpeechLogic
{
    private readonly IConnectionMultiplexer _redis;

    private class StreamBuffer
    {
        public int NextExpected { get; set; } = 1;
        public SortedDictionary<int, List<TTSResponse>> Pending { get; } = new();
        public int? LastOrder { get; set; }
    }

    private static readonly ConcurrentDictionary<string, StreamBuffer> _streamBuffers = new();

    public TextToSpeechLogic(IConnectionMultiplexer redis) => _redis = redis;

    public async Task ProcessTaskResponseAsync(TaskResponse taskResponse, TaskRequest taskRequest)
    {
        try
        {
            var requestId = taskResponse.Data.RequestId;

            TTSResponse tts = ExtractTTSResponse(taskResponse.Data.Response);
            if (tts == null || string.IsNullOrEmpty(tts.AudioBase64))
            {
                tts = new TTSResponse
                {
                    AudioBase64 = "",
                    Format = "wav",
                    SampleRate = 16000,
                };
            }

            var ttsResponseList = new List<TTSResponse> { tts };

            bool hasParent = taskRequest.PrivateArgs.TryGetValue("parent", out var parentObj);
            string responseQueueId =
                hasParent && parentObj != null ? parentObj.ToString()! : taskRequest.Id.ToString();

            bool isStream =
                taskRequest.Kwargs.TryGetValue("stream", out var streamObj)
                && bool.TryParse(streamObj?.ToString(), out var s)
                && s;

            if (isStream && !_streamBuffers.ContainsKey(responseQueueId))
            {
                _streamBuffers.TryAdd(responseQueueId, new StreamBuffer());
            }

            bool isLast =
                taskRequest.PrivateArgs.TryGetValue("last", out var lastObj)
                && bool.TryParse(lastObj?.ToString(), out var l)
                && l;

            if (!hasParent)
            {
                await DispatchBatchAsync(responseQueueId, ttsResponseList, sendCompletion: true);
                return;
            }

            if (isStream)
            {
                if (
                    taskRequest.PrivateArgs.TryGetValue("order", out var ordObj)
                    && int.TryParse(ordObj?.ToString(), out var streamOrder)
                )
                {
                    var buf = _streamBuffers.GetOrAdd(responseQueueId, _ => new StreamBuffer());
                    List<TTSResponse> streamToSend = new();
                    bool sendCompletion = false;

                    lock (buf)
                    {
                        if (isLast)
                        {
                            buf.LastOrder = streamOrder;
                        }

                        if (!buf.Pending.TryGetValue(streamOrder, out var list))
                        {
                            list = new List<TTSResponse>();
                            buf.Pending[streamOrder] = list;
                        }
                        list.Add(tts);

                        while (buf.Pending.TryGetValue(buf.NextExpected, out var ready))
                        {
                            streamToSend.AddRange(ready);
                            buf.Pending.Remove(buf.NextExpected);
                            buf.NextExpected++;
                        }

                        if (buf.LastOrder.HasValue && buf.NextExpected > buf.LastOrder.Value)
                        {
                            sendCompletion = true;
                            _streamBuffers.TryRemove(responseQueueId, out _);
                        }
                    }

                    if (streamToSend.Count > 0)
                    {
                        await DispatchBatchAsync(
                            responseQueueId,
                            streamToSend,
                            sendCompletion: sendCompletion
                        );
                    }
                    else if (sendCompletion)
                    {
                        await DispatchBatchAsync(
                            responseQueueId,
                            new List<TTSResponse>(),
                            sendCompletion: true
                        );
                    }
                }
                else
                {
                    var simpleBuffer = _streamBuffers.GetOrAdd(
                        responseQueueId,
                        _ => new StreamBuffer()
                    );

                    int simpleOrder;
                    lock (simpleBuffer)
                    {
                        simpleOrder = simpleBuffer.NextExpected++;
                    }

                    List<TTSResponse> simpleToSend = new();
                    bool simpleSendCompletion = false;

                    lock (simpleBuffer)
                    {
                        if (isLast)
                        {
                            simpleBuffer.LastOrder = simpleOrder;
                        }

                        simpleToSend.Add(tts);

                        if (isLast)
                        {
                            simpleSendCompletion = true;
                            _streamBuffers.TryRemove(responseQueueId, out _);
                        }
                    }

                    await DispatchBatchAsync(
                        responseQueueId,
                        simpleToSend,
                        sendCompletion: simpleSendCompletion
                    );
                }

                return;
            }

            var batchBuffer = _streamBuffers.GetOrAdd(responseQueueId, _ => new StreamBuffer());

            int batchOrder = 1;

            if (taskRequest.PrivateArgs.TryGetValue("order", out var batchOrderObj))
            {
                int.TryParse(batchOrderObj?.ToString(), out batchOrder);
            }

            bool shouldSendResponse = false;
            List<TTSResponse> batchToSend = new List<TTSResponse>();

            lock (batchBuffer)
            {
                if (isLast)
                {
                    batchBuffer.LastOrder = batchOrder;
                }

                if (!batchBuffer.Pending.TryGetValue(batchOrder, out var list))
                {
                    list = new List<TTSResponse>();
                    batchBuffer.Pending[batchOrder] = list;
                }
                list.AddRange(ttsResponseList);

                if (batchBuffer.LastOrder.HasValue)
                {
                    bool hasAllChunks = true;
                    for (int i = 1; i <= batchBuffer.LastOrder.Value; i++)
                    {
                        if (!batchBuffer.Pending.ContainsKey(i))
                        {
                            hasAllChunks = false;
                            break;
                        }
                    }

                    if (hasAllChunks)
                    {
                        for (int i = 1; i <= batchBuffer.LastOrder.Value; i++)
                        {
                            if (batchBuffer.Pending.TryGetValue(i, out var chunks))
                            {
                                batchToSend.AddRange(chunks);
                            }
                        }

                        batchBuffer.Pending.Clear();
                        _streamBuffers.TryRemove(responseQueueId, out _);
                        shouldSendResponse = true;
                    }
                }
            }

            if (shouldSendResponse && batchToSend.Any())
            {
                await DispatchBatchAsync(responseQueueId, batchToSend, sendCompletion: true);
            }
        }
        catch (Exception ex)
        {
            try
            {
                var subscriber = _redis.GetSubscriber();

                bool hasParent = taskRequest.PrivateArgs.TryGetValue("parent", out var parentObj);
                string responseQueueId =
                    hasParent && parentObj != null
                        ? parentObj.ToString()!
                        : taskRequest.Id.ToString();
                var resultQueueName = $"result_queue_{responseQueueId}";

                Console.WriteLine(
                    $"[TextToSpeechLogic] Sending error response to queue {resultQueueName}"
                );

                await DispatchBatchAsync(
                    responseQueueId,
                    new List<TTSResponse>
                    {
                        new TTSResponse
                        {
                            AudioBase64 = "",
                            Format = "wav",
                            SampleRate = 16000,
                        },
                    },
                    sendCompletion: true
                );

                Console.WriteLine(
                    $"[TextToSpeechLogic] Sent error response to prevent retries for task queue {resultQueueName}"
                );
            }
            catch (Exception innerEx)
            {
                Console.WriteLine(
                    $"[TextToSpeechLogic] Failed to send error response: {innerEx.Message}"
                );
            }
        }
    }

    private async Task DispatchBatchAsync(
        string requestId,
        IEnumerable<TTSResponse> responses,
        bool sendCompletion
    )
    {
        var subscriber = _redis.GetSubscriber();
        var queueName = $"result_queue_{requestId}";

        if (responses != null && responses.Any())
        {
            var responsesList = responses.ToList();

            if (responsesList.Count > 1 && sendCompletion)
            {
                var audioChunks = responsesList
                    .Where(r => !string.IsNullOrEmpty(r.AudioBase64))
                    .Select(r => r.AudioBase64)
                    .ToList();

                if (audioChunks.Any())
                {
                    try
                    {
                        string combinedAudio = AudioCombiner.CombineWavBase64(audioChunks);

                        var combinedResponse = new TTSResponse
                        {
                            AudioBase64 = combinedAudio,
                            Format = responsesList.First().Format,
                            SampleRate = responsesList.First().SampleRate,
                        };

                        responsesList = new List<TTSResponse> { combinedResponse };
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(
                            $"[TextToSpeechLogic] Error combining audio: {ex.Message}"
                        );
                    }
                }
            }

            var payload = JsonSerializer.Serialize(responsesList);
            await subscriber.PublishAsync(RedisChannel.Literal(queueName), payload);
        }

        if (sendCompletion)
        {
            var completion = JsonSerializer.Serialize(new { Status = "Completed" });
            await subscriber.PublishAsync(RedisChannel.Literal(queueName), completion);
        }
    }

    private TTSResponse ExtractTTSResponse(object responseObj)
    {
        try
        {
            if (responseObj is TTSResponse ttsResponse)
            {
                return ttsResponse;
            }

            if (responseObj is JsonElement jsonElement)
            {
                if (jsonElement.TryGetProperty("audio", out var audioProperty))
                {
                    return new TTSResponse
                    {
                        AudioBase64 = audioProperty.GetString() ?? string.Empty,
                        Format = jsonElement.TryGetProperty("format", out var formatProp)
                            ? formatProp.GetString() ?? "wav"
                            : "wav",
                        SampleRate = jsonElement.TryGetProperty(
                            "sample_rate",
                            out var sampleRateProp
                        )
                            ? sampleRateProp.GetInt32()
                            : 16000,
                    };
                }

                if (jsonElement.TryGetProperty("response", out var responseProp))
                {
                    if (
                        responseProp.ValueKind == JsonValueKind.Object
                        && responseProp.TryGetProperty("audio", out var innerAudioProp)
                    )
                    {
                        return new TTSResponse
                        {
                            AudioBase64 = innerAudioProp.GetString() ?? string.Empty,
                            Format = responseProp.TryGetProperty("format", out var formatProp)
                                ? formatProp.GetString() ?? "wav"
                                : "wav",
                            SampleRate = responseProp.TryGetProperty(
                                "sample_rate",
                                out var sampleRateProp
                            )
                                ? sampleRateProp.GetInt32()
                                : 16000,
                        };
                    }
                }
            }

            string json = JsonSerializer.Serialize(responseObj);
            Console.WriteLine(
                $"[TextToSpeechLogic] Attempting to extract from JSON: {json.Substring(0, Math.Min(100, json.Length))}..."
            );

            try
            {
                var result = JsonSerializer.Deserialize<TTSResponse>(json);
                if (result != null && !string.IsNullOrEmpty(result.AudioBase64))
                {
                    Console.WriteLine(
                        "[TextToSpeechLogic] Successfully extracted TTSResponse from JSON"
                    );
                    return result;
                }

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string audioBase64 = FindAudioProperty(root);
                if (!string.IsNullOrEmpty(audioBase64))
                {
                    Console.WriteLine("[TextToSpeechLogic] Found audio property in JSON");
                    return new TTSResponse
                    {
                        AudioBase64 = audioBase64,
                        Format = "wav",
                        SampleRate = 16000,
                    };
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TextToSpeechLogic] Error parsing JSON: {ex.Message}");
            }

            Console.WriteLine($"[TextToSpeechLogic] Could not extract TTSResponse");
            return new TTSResponse();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TextToSpeechLogic] Error extracting TTSResponse: {ex.Message}");
            return new TTSResponse();
        }
    }

    private string FindAudioProperty(JsonElement element, int depth = 0)
    {
        if (depth > 3)
            return string.Empty;

        if (
            element.TryGetProperty("audio", out var audioProp)
            && audioProp.ValueKind == JsonValueKind.String
        )
        {
            return audioProp.GetString() ?? string.Empty;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (
                    prop.Value.ValueKind == JsonValueKind.Object
                    || prop.Value.ValueKind == JsonValueKind.Array
                )
                {
                    string result = FindAudioProperty(prop.Value, depth + 1);
                    if (!string.IsNullOrEmpty(result))
                    {
                        return result;
                    }
                }
            }
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                string result = FindAudioProperty(item, depth + 1);
                if (!string.IsNullOrEmpty(result))
                {
                    return result;
                }
            }
        }

        return string.Empty;
    }
}
