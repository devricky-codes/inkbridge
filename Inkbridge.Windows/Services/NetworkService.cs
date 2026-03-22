using System;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Makaretu.Dns;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Inkbridge.Windows.Services;

public class NetworkService : BackgroundService
{
    private readonly ILogger<NetworkService> _logger;
    private readonly TextInjector _textInjector;
    private readonly PointerInjector _pointerInjector;
    private ServiceDiscovery _serviceDiscovery;
    private HttpListener _httpListener;
    private const int Port = 8765;

    public NetworkService(ILogger<NetworkService> logger, TextInjector textInjector, PointerInjector pointerInjector)
    {
        _logger = logger;
        _textInjector = textInjector;
        _pointerInjector = pointerInjector;
    }

    public Action<string>? OnWhiteboardMessage { get; set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Start mDNS
        _serviceDiscovery = new ServiceDiscovery();
        var serviceProfile = new ServiceProfile("InkbridgePC", "_inkbridge._tcp", (ushort)Port);
        _serviceDiscovery.Advertise(serviceProfile);
        _logger.LogInformation($"mDNS Advertising _inkbridge._tcp on port {Port}");

        // Start WebSocket Server
        _httpListener = new HttpListener();
        _httpListener.Prefixes.Add($"http://+:{Port}/");
        _httpListener.Start();
        _logger.LogInformation($"WebSocket Server listening on port {Port}");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                /* 
                 * In the background service context, we accept incoming connections 
                 * without blocking the cancellation. 
                 */
                var getContextTask = _httpListener.GetContextAsync();
                
                // Yield to cancellation if requested before context arrives
                var tcs = new TaskCompletionSource();
                using var registration = stoppingToken.Register(() => tcs.TrySetResult());
                
                var completedTask = await Task.WhenAny(getContextTask, tcs.Task);
                
                if (completedTask == tcs.Task)
                {
                    break;
                }

                var context = await getContextTask;
                if (context.Request.IsWebSocketRequest)
                {
                    _ = ProcessWebSocketRequestAsync(context);
                }
                else
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                }
            }
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "HTTP Listener error");
        }
    }

    private readonly List<WebSocket> _clients = new();

    public async Task BroadcastJsonAsync(string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var memory = new ReadOnlyMemory<byte>(bytes);
        WebSocket[] targets;
        lock (_clients) targets = _clients.ToArray();
        foreach (var c in targets)
        {
            if (c.State == WebSocketState.Open)
            {
                try { await c.SendAsync(memory, WebSocketMessageType.Text, true, CancellationToken.None); } catch { }
            }
        }
    }

    public async Task BroadcastFrameAsync(byte[] payload)
    {
        var memory = new ReadOnlyMemory<byte>(payload);
        WebSocket[] targets;
        lock (_clients) targets = _clients.ToArray();
        foreach (var c in targets)
        {
            if (c.State == WebSocketState.Open)
            {
                try { await c.SendAsync(memory, WebSocketMessageType.Binary, true, CancellationToken.None); } catch { }
            }
        }
    }

    private async Task ProcessWebSocketRequestAsync(HttpListenerContext context)
    {
        WebSocketContext webSocketContext = null;
        try
        {
            webSocketContext = await context.AcceptWebSocketAsync(null);
            var webSocket = webSocketContext.WebSocket;
            lock (_clients) _clients.Add(webSocket);
            _logger.LogInformation("WebSocket client connected.");

            var buffer = new byte[8192];
            while (webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                }
                else if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    _logger.LogInformation($"Received JSON: {message}");
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(message);
                        if (doc.RootElement.TryGetProperty("type", out var typeEl))
                        {
                            var type = typeEl.GetString();
                            if (type == "inject" && doc.RootElement.TryGetProperty("text", out var textEl))
                            {
                                var text = textEl.GetString();
                                var method = await _textInjector.InjectTextAsync(text);
                                var ack = System.Text.Json.JsonSerializer.Serialize(new { type = "ack", method = method, chars = text.Length });
                                await BroadcastJsonAsync(ack);
                            }
                            else if (type == "undo")
                            {
                                _textInjector.Undo();
                            }
                            else if (type != null && type.StartsWith("wb-"))
                            {
                                OnWhiteboardMessage?.Invoke(message);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Failed to parse JSON: {ex.Message}");
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Binary)
                {
                    if (result.Count == 21)
                    {
                        byte phase = buffer[0];
                        float nx = BitConverter.ToSingle(buffer, 1);
                        float ny = BitConverter.ToSingle(buffer, 5);
                        float pressure = BitConverter.ToSingle(buffer, 9);
                        long ts = BitConverter.ToInt64(buffer, 13);
                        _pointerInjector.InjectStroke(phase, nx, ny, pressure, ts);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WebSocket error");
        }
        finally
        {
            if (webSocketContext?.WebSocket != null)
            {
                lock (_clients) _clients.Remove(webSocketContext.WebSocket);
                webSocketContext.WebSocket.Dispose();
            }
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        _serviceDiscovery?.Dispose();
        _httpListener?.Stop();
        return base.StopAsync(cancellationToken);
    }
}
