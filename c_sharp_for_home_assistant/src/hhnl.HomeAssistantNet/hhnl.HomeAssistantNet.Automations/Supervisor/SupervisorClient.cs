﻿using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using hhnl.HomeAssistantNet.Automations.Automation;
using hhnl.HomeAssistantNet.Shared.Configuration;
using hhnl.HomeAssistantNet.Shared.Supervisor;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace hhnl.HomeAssistantNet.Automations.Supervisor
{
    public class SupervisorClient : IHostedService, IManagementClient
    {
        private readonly IAutomationRegistry _automationRegistry;
        private readonly IAutomationService _automationService;
        private readonly HubConnection? _hubConnection;
        private readonly ILogger<SupervisorClient> _logger;

        public SupervisorClient(
            IAutomationRegistry automationRegistry,
            IAutomationService automationService,
            ILogger<SupervisorClient> logger,
            IOptions<AutomationsConfig> config,
            IOptions<HomeAssistantConfig> haConfig)
        {
            _automationRegistry = automationRegistry;
            _automationService = automationService;
            _logger = logger;

            if (config.Value.SupervisorUrl is null)
            {
                _logger.LogInformation("Supervisor not configured.");
                return;
            }

            _logger.LogInformation($"Setup supervisor client Url '{config.Value.SupervisorUrl}' Token '{haConfig.Value.Token.Substring(0, 10)}...'");
            
            var connectUri = new Uri(new Uri(config.Value.SupervisorUrl), "/api/client-management");

            _hubConnection = new HubConnectionBuilder()
                .WithUrl(connectUri, options => options.AccessTokenProvider = () => Task.FromResult(haConfig.Value.Token))
                .WithAutomaticReconnect()
                .Build();
            _hubConnection.On<long>(nameof(IManagementClient.GetAutomationsAsync), GetAutomationsAsync);
            _hubConnection.On<long, string>(nameof(IManagementClient.StartAutomationAsync), StartAutomationAsync);
            _hubConnection.On<long, string>(nameof(IManagementClient.StopAutomationAsync), StopAutomationAsync);
            _hubConnection.On(nameof(IManagementClient.Shutdown), Shutdown);
            _hubConnection.On<long>(nameof(IManagementClient.GetProcessId), GetProcessId);
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_hubConnection is null)
                return;
            
            await _hubConnection.StartAsync(cancellationToken);
            _logger.LogInformation("Supervisor client started");
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            if (_hubConnection is null)
                return Task.CompletedTask;

            _logger.LogInformation("Stopping supervisor client ...");
            return _hubConnection.StopAsync(cancellationToken);
        }

        public async Task StartAutomationAsync(long messageId, string name)
        {
            if (!_automationRegistry.Automations.TryGetValue(name, out var automation))
            {
                await _hubConnection.SendAsync("AutomationStarted", messageId, null);
                return;
            }

            await _automationService.EnqueueAutomationForManualStartAsync(automation);
            await _hubConnection.SendAsync("AutomationStarted", messageId, ToDto(automation));
        }

        public async Task StopAutomationAsync(long messageId, string name)
        {
            if (!_automationRegistry.Automations.TryGetValue(name, out var automation))
            {
                await _hubConnection.SendAsync("AutomationStopped", messageId, null);
                return;
            }

            await _automationService.StopAutomationAsync(automation);
            await _hubConnection.SendAsync("AutomationStopped", messageId, ToDto(automation));
        }

        public Task GetAutomationsAsync(long messageId)
        {
            var automations = _automationRegistry.Automations.Values.Select(ToDto).ToArray();
            return _hubConnection.SendAsync("AutomationsGot", messageId, automations);
        }

        public Task Shutdown()
        {
            Environment.Exit(0);
            return Task.CompletedTask;
        }

        public Task GetProcessId(long messageId)
        {
            return _hubConnection.SendAsync("ProcessIdGot", messageId, Process.GetCurrentProcess().Id);
        }

        private static AutomationInfoDto ToDto(AutomationEntry entry)
        {
            return new AutomationInfoDto
            {
                Info = entry.Info,
                Runs = entry.Runs
            };
        }
    }
}