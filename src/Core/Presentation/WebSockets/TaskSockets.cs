using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Application.Logic;
using Domain.Contracts;
using Domain.Entities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Queue;

namespace Presentation.Websockets;

public static class TaskSockets
{
    public static void AddTaskSockets(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("ws/");

        group.WithOpenApi();

        group.Map("{id}", ReceiveAsync);
    }

    public static async Task<IResult> ReceiveAsync(
        HttpContext context,
        IMessagePublisher publisher,
        WebSocketNodesQueue webSocketNodesQueue,
        string id
    )
    {
        if (string.IsNullOrEmpty(id))
        {
            return Results.NotFound();
        }

        var webSocket = await context.WebSockets.AcceptWebSocketAsync();

        // Adicionar o WebSocket ao gerenciador de conexões
        await webSocketNodesQueue.AddWebsocketInQueueAsync(id, webSocket);

        var buffer = new byte[1024];
        WebSocketReceiveResult result;

        try
        {
            do
            {
                string data = string.Empty;
                do
                {
                    result = await webSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        CancellationToken.None
                    );
                    data += Encoding.UTF8.GetString(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                // Processar a mensagem recebida
                if (!string.IsNullOrEmpty(data))
                {
                    // Publicar a mensagem recebida para processamento
                    await publisher.PublishAsync(
                        "message_received",
                        new { ClientId = id, Message = data }
                    );
                }
            } while (!result.CloseStatus.HasValue);
        }
        catch (WebSocketException)
        {
            await webSocket.CloseAsync(
                WebSocketCloseStatus.InternalServerError,
                "WebSocket error occurred.",
                CancellationToken.None
            );
        }
        finally
        {
            // Remover o WebSocket do gerenciador de conexões quando a conexão for fechada
            await webSocketNodesQueue.RemoveClientAsync(id);
        }

        return Results.Ok();
    }
}
