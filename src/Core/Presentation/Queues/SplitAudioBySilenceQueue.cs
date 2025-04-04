using System.IO;
using Application.Logic;
using Domain.Utilities;
using Domain.WebServices;
using Infrastructure.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Background;

public sealed class PreProcessingQueue(IServiceScopeFactory serviceScopeFactory) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessQueueAsync();

                // Keep the connection alive
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (Exception e)
            {
                Console.WriteLine($"Error in preprocessing queue: {e.Message}");
                // Add delay before retry
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    private async Task ProcessQueueAsync()
    {
        using var scope = serviceScopeFactory.CreateScope();
        IConnectionMultiplexer redis =
            scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();
        var db = redis.GetDatabase();
        var channel = await db.SubscribeAsync("split_audio_by_silence_queue");

        channel.OnMessage(message =>
        {
            try
            {
                await ProccessMessageAsync(message);
            }
            catch (Exception e)
            {
                //emit error
                Console.WriteLine($"Error in preprocessing queue: {e.Message}");
            }
        });
    }

    private async Task ProccessMessageAsync(RedisValue message)
    {
        var request = System.Text.Json.JsonSerializer.Deserialize<TaskRequest>(message.Message);
        if (request == null)
            return;

        var filePath = request.Kwargs["input"];
        var extension = Path.GetExtension(filePath);

        using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);

        string wavFilePath = filePath;

        if (!AudioValidation.IsWav(extension))
        {
            wavFilePath = await FFmpegManager.ConvertToWavAsync(filePath);
        }

        var duration = FFmpegManager.GetDuration(wavFilePath);
        if (duration <= 0)
            throw new Exception("Invalid duration");

        if (duration < 25)
        {
            request.Kwargs["input"] = wavFilePath;
            request.PrivateArgs["start"] = "0";
            request.PrivateArgs["end"] = duration.ToString();
            request.PrivateArgs["order"] = "1";
            await db.ListRightPushAsync("distribute_queue", JsonSerializer.Serialize(request));
            return;
        }

        await foreach (var segment in FFmpegManager.BreakAudioFile(tempFilePath))
        {
            request.Kwargs["input"] = segment.FilePath;
            request.PrivateArgs["start"] = segment.StartTime.ToString();
            request.PrivateArgs["end"] = segment.EndTime.ToString();
            request.PrivateArgs["order"] = segment.Order.ToString();
            request.PrivateArgs["parent"] = request.Id.ToString();
            request.Id = Guid.NewGuid();
            await db.ListRightPushAsync("distribute_queue", JsonSerializer.Serialize(request));
        }

        //update taskSession with count of segments waiting to be processed
    }
}
