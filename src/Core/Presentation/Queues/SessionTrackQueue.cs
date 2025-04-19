using System.Collections.Concurrent;
using System.Text.Json;
using Application.Logic;
using Domain.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;

namespace Background;

public sealed class SessionTrackQueue(IServiceScopeFactory serviceScopeFactory) : BackgroundService
{
    // Dicionário para rastrear tarefas em andamento com seus respectivos timers
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _taskTimers = new();

    // Dicionário para rastrear o número de tentativas de cada tarefa
    private readonly ConcurrentDictionary<Guid, int> _taskAttempts = new();
    private const int TASK_TIMEOUT_MS = 120000; // 2 minutos
    private const int MAX_RETRY_ATTEMPTS = 3; // Limite máximo de tentativas

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = serviceScopeFactory.CreateScope();
                IConnectionMultiplexer redis =
                    scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();
                var db = redis.GetDatabase();
                var subscriber = redis.GetSubscriber();

                // Inscrever-se no canal de rastreamento de sessão
                var sessionTrackChannel = await subscriber.SubscribeAsync("sesion_tracking_queue");

                // Inscrever-se no canal de resultados para cancelar timers quando tarefas forem concluídas
                var taskCompletionChannel = await subscriber.SubscribeAsync("task_completion");

                sessionTrackChannel.OnMessage(message =>
                {
                    try
                    {
                        var messageStr = message.Message.ToString();
                        if (string.IsNullOrEmpty(messageStr))
                            return;

                        var taskRequest = JsonSerializer.Deserialize<TaskRequest>(messageStr);
                        if (taskRequest == null)
                            return;

                        // Registrar a tarefa e iniciar o timer de timeout
                        StartTaskTracking(taskRequest.Id, db, subscriber);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(
                            $"Error processing session tracking message: {ex.Message}"
                        );
                    }
                });

                taskCompletionChannel.OnMessage(message =>
                {
                    try
                    {
                        var messageStr = message.Message.ToString();
                        if (string.IsNullOrEmpty(messageStr))
                            return;

                        var completionData = JsonSerializer.Deserialize<TaskCompletionData>(
                            messageStr
                        );
                        if (completionData == null)
                            return;

                        // Cancelar o timer da tarefa concluída
                        CancelTaskTracking(completionData.TaskRequestId);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(
                            $"Error processing task completion message: {ex.Message}"
                        );
                    }
                });

                // Keep the connection alive
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error in session tracking queue: {e.Message}");
                // Add delay before retry
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    private void StartTaskTracking(Guid taskId, IDatabase db, ISubscriber subscriber)
    {
        // Criar um novo token de cancelamento para esta tarefa
        var cts = new CancellationTokenSource();
        _taskTimers[taskId] = cts;

        // Incrementar o contador de tentativas ou inicializá-lo se for a primeira tentativa
        _taskAttempts.AddOrUpdate(taskId, 1, (_, currentAttempts) => currentAttempts + 1);

        // Iniciar um timer para verificar o timeout
        Task.Run(async () =>
        {
            try
            {
                // Aguardar o tempo de timeout
                await Task.Delay(TASK_TIMEOUT_MS, cts.Token);

                // Se chegou aqui, o timeout ocorreu
                if (_taskTimers.TryRemove(taskId, out _))
                {
                    Console.WriteLine(
                        $"Task {taskId} timed out after {TASK_TIMEOUT_MS / 1000} seconds"
                    );

                    // Redistribuir a tarefa ou notificar erro
                    var taskData = await db.StringGetAsync($"task:{taskId}");
                    if (!taskData.IsNull)
                    {
                        // Verificar o número de tentativas
                        int attempts = _taskAttempts.GetOrAdd(taskId, 1);

                        if (attempts < MAX_RETRY_ATTEMPTS)
                        {
                            // Publicar a tarefa novamente para redistribuição
                            await subscriber.PublishAsync("distribute_queue", taskData);
                            Console.WriteLine(
                                $"Task {taskId} redistributed due to timeout (attempt {attempts} of {MAX_RETRY_ATTEMPTS})"
                            );
                        }
                        else
                        {
                            // Marcar a tarefa como falha após 3 tentativas
                            Console.WriteLine(
                                $"Task {taskId} failed after {MAX_RETRY_ATTEMPTS} attempts"
                            );

                            // Remover a tarefa do dicionário de tentativas
                            _taskAttempts.TryRemove(taskId, out _);

                            // Publicar mensagem de falha
                            var failureMessage = JsonSerializer.Serialize(
                                new TaskCompletionData { TaskRequestId = taskId, Status = "failed" }
                            );

                            //emit error
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Timer foi cancelado, tarefa foi concluída normalmente
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error in task timeout handler: {ex.Message}");
            }
        });
    }

    private void CancelTaskTracking(Guid taskId)
    {
        // Remover e cancelar o timer da tarefa
        if (_taskTimers.TryRemove(taskId, out var cts))
        {
            cts.Cancel();
            cts.Dispose();
            Console.WriteLine($"Task {taskId} tracking canceled - task completed successfully");

            // Remover a tarefa do dicionário de tentativas
            _taskAttempts.TryRemove(taskId, out _);
        }
    }
}

// Classe para deserializar dados de conclusão de tarefa
public class TaskCompletionData
{
    public Guid TaskRequestId { get; set; }
    public string Status { get; set; }
}
