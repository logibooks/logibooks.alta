// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter

using System.Collections.Specialized;
using Logibooks.Alta.Configuration;
using Quartz;
using Quartz.Impl;

namespace Logibooks.Alta.Monitoring;

internal sealed class QuartzMonitoringSchedule(
    TimeProvider clock, Func<CancellationToken, Task> runCycle, CancellationToken stoppingToken) : IAsyncDisposable
{
    private const string ContextKey = "Alta monitoring";
    private readonly object _gate = new();
    private IScheduler? _scheduler;
    private DateTimeOffset _lastCompletedAt;
    private bool _running;
    private bool _stopped;

    public async Task StartAsync(ServiceOptions settings)
    {
        var factory = new StdSchedulerFactory(new NameValueCollection
        {
            ["quartz.scheduler.instanceName"] = $"Alta monitoring {Guid.NewGuid():N}",
            ["quartz.threadPool.maxConcurrency"] = "2",
            ["quartz.jobStore.type"] = "Quartz.Simpl.RAMJobStore, Quartz"
        });
        _scheduler = await factory.GetScheduler(stoppingToken);
        _scheduler.Context[ContextKey] = this;
        var job = JobBuilder.Create<MonitoringJob>().WithIdentity("alta-poll").Build();
        _lastCompletedAt = clock.GetUtcNow();
        var trigger = TriggerBuilder.Create().WithIdentity("alta-cron")
            .WithCronSchedule(settings.MonitoringCron, cron => cron
                .InTimeZone(TimeZoneInfo.FindSystemTimeZoneById(settings.MonitoringTimeZone))
                .WithMisfireHandlingInstructionDoNothing())
            .Build();
        await _scheduler.ScheduleJob(job, trigger, stoppingToken);
        await _scheduler.Start(stoppingToken);
    }

    internal async Task RunAsync(DateTimeOffset scheduledAt)
    {
        lock (_gate)
        {
            // Quartz can dispatch a firing late, after the cycle it overlapped has finished.
            if (_stopped || stoppingToken.IsCancellationRequested || _running || scheduledAt < _lastCompletedAt)
                return;
            _running = true;
        }

        try
        {
            await runCycle(stoppingToken);
        }
        finally
        {
            lock (_gate)
            {
                _lastCompletedAt = clock.GetUtcNow();
                _running = false;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
            _stopped = true;
        if (_scheduler is not null)
            await _scheduler.Shutdown(waitForJobsToComplete: true);
    }

    public sealed class MonitoringJob : IJob
    {
        public async Task Execute(IJobExecutionContext context)
        {
            var schedule = (QuartzMonitoringSchedule)context.Scheduler.Context[ContextKey]!;
            if (context.ScheduledFireTimeUtc is { } scheduledAt)
                await schedule.RunAsync(scheduledAt);
        }
    }
}
