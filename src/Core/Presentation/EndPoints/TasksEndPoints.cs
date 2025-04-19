using System.Text;
using System.Text.Json;
using Application.Logic;
using Contracts.Constants;
using Domain.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Presentation.EndPoints;

public static class TasksEndPoints
{
    public static void AddTasksEndPoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("api/v1/");

        group.WithOpenApi();

        group.MapPost("{task}", handleTask).RequireAuthorization().RequireRateLimiting("fixed");
    }

    public static async Task handleTask(
        string task,
        HttpContext context,
        ITaskBusinessLogic logic,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var userId =
                context.User.Claims.FirstOrDefault()?.Value
                ?? throw new Exception("User not found");
            if (string.IsNullOrEmpty(userId))
            {
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync(
                    JsonSerializer.Serialize(new { error = "Unauthorized access." })
                );
                return;
            }

            var form = await context.Request.ReadFormAsync();
            var request = await TaskRequest.Create(form, AvailableModels.SpeechToText);

            if (request == null)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(
                    JsonSerializer.Serialize(new { error = "Invalid request." })
                );
                return;
            }

            if (!request.IsValidTask())
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(
                    JsonSerializer.Serialize(new { error = "Invalid task." })
                );
                return;
            }
            var (result, error) = request.IsValidFields();
            if (!result)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync(JsonSerializer.Serialize(new { error = error }));
                return;
            }
            request.RequesterId = Guid.Parse(userId);

            if (!await logic.PublishPreProcessingQueueAsync(request))
            {
                context.Response.StatusCode = 503;
                await context.Response.WriteAsync(
                    JsonSerializer.Serialize(
                        new
                        {
                            error = "Unable to process request due to queue service unavailability",
                        }
                    )
                );
                return;
            }

            bool isStreaming =
                request.Kwargs.ContainsKey("stream")
                && request.Kwargs["stream"].ToString().ToLower() == "true";

            if (isStreaming)
            {
                context.Response.ContentType = "text/plain";
                context.Response.Headers.Add("X-Content-Type-Options", "nosniff");
                context.Response.Headers.Add("Cache-Control", "no-cache");

                await foreach (
                    var message in logic.StreamTaskResultAsync(request, cancellationToken)
                )
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes(message + "\n");
                    await context.Response.Body.WriteAsync(bytes, 0, bytes.Length);
                    await context.Response.Body.FlushAsync();
                }
            }
            else
            {
                var response = await logic.AwaitTaskResultAsync(request);
                if (!string.IsNullOrEmpty(response))
                {
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(response);
                }
            }
            return;
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            context.Response.StatusCode = 500;
            await context.Response.WriteAsync(
                JsonSerializer.Serialize(new { error = "internal error" })
            );
            return;
        }
    }
}
