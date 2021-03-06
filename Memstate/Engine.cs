﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Memstate
{
    public class Engine<TModel> where TModel : class
    {
        private readonly ILogger _logger;

        private readonly Kernel _kernel;

        private readonly MemstateSettings _settings;

        private readonly IJournalWriter _journalWriter;

        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<object>> _pendingLocalCommands;

        private readonly IDisposable _commandSubscription;

        private readonly AutoResetEvent _pendingCommandsChanged = new AutoResetEvent(false);

        private readonly EngineMetrics _metrics;

        private volatile bool _stopped;

        /// <summary>
        /// Last record number applied to the model, -1 if initial model
        /// Numbering starts from 0!
        /// </summary>
        private long _lastRecordNumber;

        public event CommandExecutedDelegate CommandExecuted = delegate { };

        public Engine(
            MemstateSettings settings,
            TModel model,
            IJournalSubscriptionSource subscriptionSource,
            IJournalWriter journalWriter,
            long nextRecord)
        {
            _lastRecordNumber = nextRecord - 1;
            _logger = settings.CreateLogger<Engine<TModel>>();
            _kernel = new Kernel(settings, model);
            _settings = settings;
            _journalWriter = journalWriter;
            _pendingLocalCommands = new ConcurrentDictionary<Guid, TaskCompletionSource<object>>();
            _commandSubscription = subscriptionSource.Subscribe(nextRecord, ApplyRecord);
            _metrics = new EngineMetrics(settings);
        }

        public long LastRecordNumber => Interlocked.Read(ref _lastRecordNumber);

        public async Task<TResult> ExecuteAsync<TResult>(Command<TModel, TResult> command)
        {
            EnsureOperational();

            var completionSource = new TaskCompletionSource<object>();

            _pendingLocalCommands[command.Id] = completionSource;

            _metrics.PendingLocalCommands(_pendingLocalCommands.Count);

            using (_metrics.MeasureCommandExecution())
            {
                _journalWriter.Send(command);

                return (TResult) await completionSource.Task.ConfigureAwait(false);
            }
        }

        public Task ExecuteAsync(Command<TModel> command)
        {
            EnsureOperational();

            var completionSource = new TaskCompletionSource<object>();

            _pendingLocalCommands[command.Id] = completionSource;

            _metrics.PendingLocalCommands(_pendingLocalCommands.Count);

            using (_metrics.MeasureCommandExecution())
            {
                _journalWriter.Send(command);
                return completionSource.Task;
            }
        }

        public async Task<TResult> ExecuteAsync<TResult>(Query<TModel, TResult> query)
        {
            EnsureOperational();

            return await Task.Run(() => Execute(query)).ConfigureAwait(false);
        }

        public TResult Execute<TResult>(Command<TModel, TResult> command)
        {
            EnsureOperational();

            return ExecuteAsync(command).Result;
        }

        public void Execute(Command<TModel> command)
        {
            EnsureOperational();

            ExecuteAsync(command).Wait();
        }

        public TResult Execute<TResult>(Query<TModel, TResult> query)
        {
            return (TResult) Execute((Query) query);
        }

        public async Task DisposeAsync()
        {
            _logger.LogDebug("Begin Dispose");

            await _journalWriter.DisposeAsync().ConfigureAwait(false);

            while (!_pendingLocalCommands.IsEmpty)
            {
                _pendingCommandsChanged.WaitOne();
            }

            _commandSubscription.Dispose();

            _logger.LogDebug("End Dispose");
        }

        public Task EnsureAsync(long recordNumber)
        {
            EnsureOperational();

            var completionSource = new TaskCompletionSource<object>();

            CommandExecuted += (journalRecord, isLocal, events) =>
            {
                if (journalRecord.RecordNumber >= recordNumber)
                {
                    completionSource.SetResult(null);
                }
            };

            return Interlocked.Read(ref _lastRecordNumber) >= recordNumber
                ? Task.CompletedTask
                : completionSource.Task;
        }

        internal object Execute(Query query)
        {
            EnsureOperational();

            using (_metrics.MeasureQueryExecution())
            {
                try
                {
                    var result = _kernel.Execute(query);

                    _metrics.QueryExecuted();

                    return result;
                }
                catch (Exception)
                {
                    _metrics.QueryFailed();

                    throw;
                }
            }
        }

        internal object Execute(Command command)
        {
            EnsureOperational();

            var completionSource = new TaskCompletionSource<object>();

            _pendingLocalCommands[command.Id] = completionSource;

            _metrics.PendingLocalCommands(_pendingLocalCommands.Count);

            using (_metrics.MeasureCommandExecution())
            {
                _journalWriter.Send(command);

                return completionSource.Task.ConfigureAwait(false).GetAwaiter().GetResult();
            }
        }

        /// <summary>
        /// Handler for records obtained through the subscription
        /// </summary>
        private void ApplyRecord(JournalRecord record)
        {
            TaskCompletionSource<object> completion = null;

            try
            {
                var command = record.Command;

                var isLocalCommand = _pendingLocalCommands.TryRemove(command.Id, out completion);

                if (isLocalCommand)
                {
                    _metrics.PendingLocalCommands(_pendingLocalCommands.Count);

                    _pendingCommandsChanged.Set();
                }

                _logger.LogDebug("ApplyRecord: {0}/{1}, isLocal: {2}", record.RecordNumber, command.GetType().Name, isLocalCommand);

                var expected = Interlocked.Increment(ref _lastRecordNumber);

                if (expected != record.RecordNumber)
                {
                    if (!_settings.AllowBrokenSequence)
                    {
                        _stopped = true;

                        throw new Exception($"Broken sequence, expected {expected}, got {record.RecordNumber}");
                    }

                    _logger.LogWarning(
                        "ApplyRecord: RecordNumber out of order. Expected {0}, got {1}",
                        expected,
                        record.RecordNumber);
                }

                var events = new List<Event>();

                var result = _kernel.Execute(record.Command, events.Add);

                NotifyCommandExecuted(record, isLocalCommand, events);

                completion?.SetResult(result);

                _metrics.CommandExecuted();
            }
            catch (Exception ex)
            {
                _metrics.CommandFailed();

                _logger.LogError(default(EventId), ex, "ApplyRecord failed: {0}/{1}", record.RecordNumber, record.Command.GetType().Name);

                completion?.SetException(ex);
            }
        }

        private void EnsureOperational()
        {
            if (_stopped)
            {
                throw new Exception("Engine has stopped due to a failure condition");
            }
        }

        private void NotifyCommandExecuted(JournalRecord journalRecord, bool isLocal, IEnumerable<Event> events)
        {
            try
            {
                CommandExecuted.Invoke(journalRecord, isLocal, events);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Exception thrown in CommandExecuted handler.");
            }
        }
    }
}