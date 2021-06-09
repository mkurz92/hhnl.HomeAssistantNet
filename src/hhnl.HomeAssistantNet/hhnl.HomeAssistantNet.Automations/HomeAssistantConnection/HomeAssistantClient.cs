﻿using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using hhnl.HomeAssistantNet.Automations.Utils;
using hhnl.HomeAssistantNet.Shared.Configuration;
using hhnl.HomeAssistantNet.Shared.HomeAssistantConnection;
using MediatR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace hhnl.HomeAssistantNet.Automations.HomeAssistantConnection
{
    public class HomeAssistantClient : IHostedService, IDisposable, IHomeAssistantClient
    {
        private readonly ConcurrentDictionary<long, TaskCompletionSource<WebsocketApiMessage>> _callResults = new();
        private readonly IOptions<HomeAssistantConfig> _haConfig;
        private readonly ILogger<HomeAssistantClient> _logger;
        private readonly IMediator _mediator;
        private readonly Channel<byte[]> _messagesToSend;
        private CancellationTokenSource? _cancellationTokenSource;
        private long _id;
        private Task? _receiveTask;
        private Task? _sendTask;
        private ClientWebSocket? _webSocket;

        public HomeAssistantClient(
            ILogger<HomeAssistantClient> logger,
            IOptions<HomeAssistantConfig> haConfig,
            IMediator mediator)
        {
            _logger = logger;
            _haConfig = haConfig;
            _mediator = mediator;
            _messagesToSend = Channel.CreateBounded<byte[]>(10);
        }

        public void Dispose()
        {
            _cancellationTokenSource?.Dispose();
            _webSocket?.Dispose();
        }

        public async Task<JsonElement> FetchStatesAsync(CancellationToken cancellationToken = default)
        {
            var response = await SendRequestAsync(id => new
                {
                    id,
                    type = "get_states"
                },
                cancellationToken);

            return response.Result;
        }

        public async Task<JsonElement> CallServiceAsync(
            string domain,
            string service,
            dynamic? serviceData = null,
            CancellationToken cancellationToken = default)
        {
            var response = await SendRequestAsync(id => new
                {
                    id,
                    type = "call_service",
                    domain,
                    service,
                    service_data = serviceData
                },
                cancellationToken);


            return response.Result;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _cancellationTokenSource = new CancellationTokenSource();
            _webSocket = new ClientWebSocket();
            _id = 1;

            var uriBuilder = new UriBuilder(new Uri(_haConfig.Value.Instance));
            uriBuilder.Path += "api/websocket";
            uriBuilder.Scheme = uriBuilder.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";

            await _webSocket.ConnectAsync(uriBuilder.Uri, cancellationToken);

            _logger.LogInformation("Connected to home assistant websocket api.");

            _receiveTask = Task.Run(ReceiveLoopAsync);
            _sendTask = Task.Run(SendLoopAsync);
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            _cancellationTokenSource?.Cancel();

            if (_receiveTask != null)
                await _receiveTask;

            if (_sendTask != null)
                await _sendTask;
        }

        private async Task ReceiveLoopAsync()
        {
            while (!_cancellationTokenSource?.IsCancellationRequested ?? false)
            {
                var received = await ReadNextEventAsync();

                // We start a new task here so who ever handles the message can't block the receiving of new messages.
                // This prevents deadlocks where the waiting of a result of a request blocks the result from being processed.
                // TODO: This should probably be revised. Once the automation execution runs on a different thread, this can be removed.
                Task.Run(async () =>
                {
                    try
                    {
                        var receiveTask = HandleMessageAsync(received);

                        await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromSeconds(10)));

                        if (!receiveTask.IsCompleted)
                            throw new TimeoutException("Receive timeout exceeded.");
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "Error occured while handling message from home assistant.");

#if DEBUG
                        Debugger.Break();
#endif

                        throw;
                    }
                });
            }
        }

        private async Task SendLoopAsync()
        {
            while (!_cancellationTokenSource?.IsCancellationRequested ?? false)
            {
                var bytes = await _messagesToSend.Reader.ReadAsync();

                await _webSocket!.SendAsync(bytes,
                    WebSocketMessageType.Text,
                    true,
                    _cancellationTokenSource?.Token ?? CancellationToken.None);
            }
        }

        private async Task HandleMessageAsync(WebsocketApiMessage apiMessage)
        {
            switch (apiMessage.Type)
            {
                case "auth_required":

                    _logger.LogDebug("Got auth_required; sending token.");

                    await SendAsync(
                        new
                        {
                            type = "auth",
                            access_token = _haConfig.Value.Token
                        });

                    break;
                case "auth_ok":

                    _logger.LogDebug("Got auth_ok; Init complete.");

                    Initialization.HomeAssistantConnected();

                    await SendAsync(
                        new
                        {
                            id = Interlocked.Increment(ref _id),
                            type = "subscribe_events",
                            event_type = "state_changed"
                        });

                    break;
                case "result":


                    if (_callResults.TryGetValue(apiMessage.Id, out var tsc))
                        tsc.SetResult(apiMessage);

                    break;
                case "event":
                    var apiEvent = await apiMessage.Event.ToObjectAsync<WebsocketApiEvent>();

                    if (apiEvent is null)
                        return;

                    await HandleEventAsync(apiEvent);

                    break;
            }
        }


        private async Task HandleEventAsync(WebsocketApiEvent apiEvent)
        {
            switch (apiEvent.EventType)
            {
                case "state_changed":
                    var eventData = await apiEvent.Data.ToObjectAsync<StateChangedNotification>();

                    if (eventData is null)
                        return;

                    await _mediator.Publish(eventData);
                    break;
            }
        }

        private async Task<WebsocketApiMessage> ReadNextEventAsync()
        {
            var buffer = new ArraySegment<byte>(new byte[8192]);
            WebSocketReceiveResult? result;

            await using var ms = new MemoryStream();

            do
            {
                result = await _webSocket!.ReceiveAsync(buffer, _cancellationTokenSource!.Token);
                ms.Write(buffer.Array!, buffer.Offset, result.Count);
            } while (!result.EndOfMessage);

            ms.Seek(0, SeekOrigin.Begin);

            if (_logger.IsEnabled(LogLevel.Trace))
            {
                using var sr = new StreamReader(ms, Encoding.UTF8, false, 1024, true);
                var json = await sr.ReadToEndAsync();

                _logger.LogTrace($"Received message: \r\n {json}");

                ms.Seek(0, SeekOrigin.Begin);
            }

            return await JsonSerializer.DeserializeAsync<WebsocketApiMessage>(
                       ms,
                       cancellationToken: _cancellationTokenSource.Token) ??
                   throw new InvalidOperationException("Message expected but got null.");
        }

        private async Task<WebsocketApiMessage> SendRequestAsync<T>(
            Func<long, T> requestFactory,
            CancellationToken cancellationToken)
        {
            // Wait for init
            await Initialization.WaitForHomeAssistantConnectionAsync();

            var id = Interlocked.Increment(ref _id);
            var request = requestFactory(id);
            var tcs = _callResults.GetOrAdd(id, i => new TaskCompletionSource<WebsocketApiMessage>(cancellationToken));

            try
            {
                await SendAsync(request);

                var result = await tcs.Task;

                if (result.Success == false)
                {
                    var ex = new HomeAssistantCallFailedException(result.Error.Code!, result.Error.Message!);
                    _logger.LogError(ex, "Home assistant api doesn't indicate success.");
                    throw ex;
                }

                return result;
            }
            finally
            {
                _callResults.TryRemove(id, out _);
            }
        }


        private async Task SendAsync<T>(T response)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(response);

            await _messagesToSend.Writer.WriteAsync(bytes);
        }

        private class WebsocketApiMessage
        {
            [JsonPropertyName("id")] public long Id { get; set; }

            [JsonPropertyName("type")] public string? Type { get; set; }

            [JsonPropertyName("success")] public bool? Success { get; set; }

            [JsonPropertyName("event")] public JsonElement Event { get; set; }

            [JsonPropertyName("result")] public JsonElement Result { get; set; }

            [JsonPropertyName("error")] public WebsocketApiMessageError Error { get; set; }
        }

        private class WebsocketApiMessageError
        {
            [JsonPropertyName("code")] public string? Code { get; set; }

            [JsonPropertyName("message")] public string? Message { get; set; }
        }

        private class WebsocketApiEvent
        {
            [JsonPropertyName("time_fired")] public DateTimeOffset TimeFired { get; set; }

            [JsonPropertyName("event_type")] public string EventType { get; set; }

            [JsonPropertyName("origin")] public string Origin { get; set; }

            [JsonPropertyName("data")] public JsonElement Data { get; set; }
        }

        public class HomeAssistantCallFailedException : Exception
        {
            public HomeAssistantCallFailedException(string code, string message)
                : base(message)
            {
                Code = code;
            }

            public string Code { get; }
        }

        public class StateChangedNotification : INotification
        {
            [JsonPropertyName("entity_id")] public string EntityId { get; set; }

            [JsonPropertyName("new_state")] public JsonElement NewState { get; set; }
        }
    }
}