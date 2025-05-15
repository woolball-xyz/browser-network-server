﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Domain.Contracts;
using StackExchange.Redis;

namespace Application.Logic
{
    public sealed class SpeechToTextLogic : ISpeechToTextLogic
    {
        private readonly IConnectionMultiplexer _redis;

        private static readonly ConcurrentDictionary<string, List<STTChunk>> _buffers = new();

        private class StreamBuffer
        {
            public int NextExpected { get; set; } = 1;
            public SortedDictionary<int, List<STTChunk>> Pending { get; } = new();
            public int? LastOrder { get; set; }
        }

        private static readonly ConcurrentDictionary<string, StreamBuffer> _streamBuffers = new();

        public SpeechToTextLogic(IConnectionMultiplexer redis) => _redis = redis;

        public async Task ProcessTaskResponseAsync(
            TaskResponse taskResponse,
            TaskRequest taskRequest
        )
        {
            // Obter a resposta, que pode ser de diferentes tipos
            STTChunk stt;
            
            // Verificar o tipo da resposta e converter se necessário
            if (taskResponse.Data.Response is STTChunk sttr)
            {
                stt = sttr;
            }
            else
            {
                // Se não for do tipo esperado, converter via serialização
                var json = JsonSerializer.Serialize(taskResponse.Data.Response);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                stt = JsonSerializer.Deserialize<STTChunk>(json, options);
            }
            
            Console.WriteLine(
                $"Processing task response for request : {JsonSerializer.Serialize(taskResponse)}"
            );
            if (stt == null)
                return;

            if (
                taskRequest.PrivateArgs.TryGetValue("start", out var startObj)
                && double.TryParse(startObj?.ToString(), out var baseStart)
                && stt.Chunks != null
            )
            {
                foreach (var chunk in stt.Chunks)
                {
                    chunk.Timestamp[0] += baseStart;
                    chunk.Timestamp[1] += baseStart;
                }
            }

            var sttChunksList = new List<STTChunk> { stt };

            bool hasParent = taskRequest.PrivateArgs.TryGetValue("parent", out var parentObj);
            string requestId =
                hasParent && parentObj != null ? parentObj.ToString()! : taskRequest.Id.ToString();

            // Determinar se é uma requisição de streaming
            bool isStream =
                taskRequest.Kwargs.TryGetValue("stream", out var streamObj)
                && bool.TryParse(streamObj?.ToString(), out var s)
                && s;
                
            // Se for streaming, registramos no dicionário de streamBuffers para referência futura
            if (isStream && !_streamBuffers.ContainsKey(requestId))
            {
                _streamBuffers.TryAdd(requestId, new StreamBuffer());
            }
                
            bool isLast =
                taskRequest.PrivateArgs.TryGetValue("last", out var lastObj)
                && bool.TryParse(lastObj?.ToString(), out var l)
                && l;

            if (!hasParent)
            {
                Console.WriteLine($"Single request (no parent) for {requestId}, stream: {isStream}");
                
                // Caso específico: Single request com stream
                if (isStream)
                {
                    Console.WriteLine($"Sending single chunk with stream flag for {requestId}");
                    
                    // Para streaming, enviamos o chunk e depois o status de completed
                    // O StreamTaskResultAsync vai capturar o status e encerrar o stream
                    await DispatchBatchAsync(
                        requestId,
                        sttChunksList,
                        sendCompletion: true  // Importante: enviamos completed para sinalizar o fim
                    );
                }
                else
                {
                    // Para requisições não-streaming, enviar o chunk e a mensagem de completed
                    await DispatchBatchAsync(
                        requestId,
                        sttChunksList,
                        sendCompletion: true
                    );
                }
                return;
            }

            if (isStream)
            {
                Console.WriteLine($"Processing streaming request: {requestId}, isLast: {isLast}, hasParent: {hasParent}");
                
                if (
                    taskRequest.PrivateArgs.TryGetValue("order", out var ordObj)
                    && int.TryParse(ordObj?.ToString(), out var order)
                )
                {
                    Console.WriteLine($"Streaming with order: {order}");
                    var buf = _streamBuffers.GetOrAdd(requestId, _ => new StreamBuffer());
                    List<STTChunk> toSend = new();
                    bool sendCompletion = false;

                    lock (buf)
                    {
                        if (isLast)
                        {
                            Console.WriteLine($"Setting LastOrder to {order} for {requestId}");
                            buf.LastOrder = order;
                        }

                        if (!buf.Pending.TryGetValue(order, out var list))
                        {
                            list = new List<STTChunk>();
                            buf.Pending[order] = list;
                        }
                        list.Add(stt);

                        while (buf.Pending.TryGetValue(buf.NextExpected, out var ready))
                        {
                            toSend.AddRange(ready);
                            buf.Pending.Remove(buf.NextExpected);
                            buf.NextExpected++;
                        }

                        if (buf.LastOrder.HasValue && buf.NextExpected > buf.LastOrder.Value)
                        {
                            Console.WriteLine($"All chunks processed for {requestId}, setting completion flag");
                            sendCompletion = true;
                            _streamBuffers.TryRemove(requestId, out _);
                        }
                    }

                    // Para streaming com ordem, enviamos os chunks e, se for o último, 
                    // enviamos o sinal de completado para encerrar o stream
                    if (toSend.Count > 0)
                    {
                        Console.WriteLine($"Dispatching {toSend.Count} chunks for stream {requestId}");
                        await DispatchBatchAsync(requestId, toSend, sendCompletion: sendCompletion);
                    }
                    else if (sendCompletion)
                    {
                        // Mesmo sem chunks, precisamos enviar o status de completado
                        Console.WriteLine($"Stream {requestId} completed, sending completion status");
                        await DispatchBatchAsync(requestId, new List<STTChunk>(), sendCompletion: true);
                    }
                }
                else
                {
                    Console.WriteLine($"Simple streaming for {requestId}, isLast: {isLast}");
                    // Para streaming simples, enviamos o chunk e o status de completado se for o último
                    await DispatchBatchAsync(requestId, sttChunksList, sendCompletion: isLast);
                    
                    // Se foi o último chunk, remover do registro de streamBuffers
                    if (isLast)
                    {
                        Console.WriteLine($"Removing streamBuffer for {requestId} (isLast)");
                        _streamBuffers.TryRemove(requestId, out _);
                    }
                }

                return;
            }

            // Non-streaming case
            Console.WriteLine($"Non-streaming batch for {requestId}, isLast: {isLast}");
            var buffer = _buffers.GetOrAdd(requestId, _ => new List<STTChunk>());
            lock (buffer)
            {
                buffer.AddRange(sttChunksList);
            }

            if (isLast)
            {
                Console.WriteLine($"Processing final batch for non-streaming request {requestId}");
                List<STTChunk> toSend;
                lock (buffer)
                {
                    toSend = buffer
                        .OrderBy(c =>
                            c.Chunks != null && c.Chunks.Count > 0
                                ? c.Chunks[0].Timestamp[0]
                                : double.MaxValue
                        )
                        .ToList();
                    buffer.Clear();
                }
                _buffers.TryRemove(requestId, out _);
                
                // Para requisições não-streaming, enviamos a mensagem de completado
                await DispatchBatchAsync(requestId, toSend, sendCompletion: true);
            }
        }

        private async Task DispatchBatchAsync(
            string requestId,
            IEnumerable<STTChunk> chunks,
            bool sendCompletion
        )
        {
            var subscriber = _redis.GetSubscriber();
            var queueName = $"result_queue_{requestId}";

            if (chunks != null && chunks.Any())
            {
                var payload = JsonSerializer.Serialize(chunks);
                Console.WriteLine($"Sending chunks to {queueName}, count: {chunks.Count()}");
                await subscriber.PublishAsync(RedisChannel.Literal(queueName), payload);
            }

            if (sendCompletion)
            {
                var completion = JsonSerializer.Serialize(new { Status = "Completed" });
                Console.WriteLine($"Sending completion status to {queueName}");
                await subscriber.PublishAsync(RedisChannel.Literal(queueName), completion);
            }
        }
    }
}
