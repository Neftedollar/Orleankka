﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IOPath = System.IO.Path;

using Orleankka;
using Orleankka.Behaviors;
using Orleankka.Facets;
using Orleankka.Meta;
using Orleankka.Services;
using Orleans.Core;

namespace ProcessManager
{
    /* external commands */
    [Serializable] public class Start    : Command<ICopier> {}
    [Serializable] public class Suspend  : Command<ICopier> {}
    [Serializable] public class Continue : Command<ICopier> {}
    [Serializable] public class Cancel   : Command<ICopier> {}
    [Serializable] public class Restart  : Command<ICopier> {}

    /* external events (pushed via 'copier-notifications' stream) */
    [Serializable] public abstract class JobEvent : Event
    {
        public string Id { get; set; }
    }

    [Serializable] public class StateChanged : JobEvent
    {
        public string Current { get; set; }
        public string Previous { get; set; }
        public bool Active { get; set; }

        public override string ToString() => $"#{Id}: State changed from {Previous} to {Current}. Active: {(Active ? "yes" : "no")}";
    }

    [Serializable] public class ProgressChanged : JobEvent
    {
        public double Progress { get; set; }
         
        public override string ToString() => $"#{Id}: ProgressChanged to {(int)(Progress * 100)}%";
    }

    [Serializable] public class Error : JobEvent
    {
        public string Type { get; set; }
        public string Message { get; set; }
        public string StackTrace { get; set; }

        public override string ToString() => $"#{Id}: Error -> {Type}:{Message}";
    }

    public class CopierState
    {
        public string Current { get; set; }
        public string Previous { get; set; }
        public int LineTotal { get; set; }
        public int LastCopiedLine { get; set; }
    }

    public interface ICopier : IActorGrain {}
    
    public class Copier : ActorGrain, ICopier
    {
        /* internal events */
        class Prepared : Event
        {
            public int Lines;
        }

        class Copied : Event
        {
            public int Count;
        }

        // durable state
        CopierState State => storage.State;

        readonly IStorage<CopierState> storage;
        readonly Behavior behavior;
        StreamRef notifications;

        // injected storage provider
        public Copier([UseStorageProvider("copier")]
                      IStorage<CopierState> storage)
        {
            this.storage = storage;
            
            var supervision = new Supervision(this);
            
            var fsm = new StateMachine()
                .State(Active, supervision.On)
                    .Substate(Preparing,   Trait.Of(Restartable), Durable)
                    .Substate(Copying,     Trait.Of(Restartable, Cancellable), Durable)
                    .Substate(Restarting,  Durable)
                    .Substate(Suspended)                    
                .State(Inactive, supervision.Off)
                    .Substate(Initial)
                    .Substate(Completed,   Trait.Of(Restartable), Durable)
                    .Substate(Canceled,    Trait.Of(Restartable), Durable);

            behavior = new Behavior(fsm);            
        }

        void RunJob(Func<BackgroundJobToken, Task> job) => Jobs.Run(job.Method.Name, job);
        void TerminateJob(Func<BackgroundJobToken, Task> job) => Jobs.Terminate(job.Method.Name);

        bool TerminateAllJobs()
        {
            var activeJobs = Jobs.Active();
            if (!activeJobs.Any())
                return true;

            Array.ForEach(activeJobs, job => job.Terminate());
            return false;
        }

        async Task Fire(object message) => await Self.Tell(message);

        public override async Task<object> Receive(object message)
        {
            if (message is Activate _)
            {
                // state is automatically loaded by storage provider
                // before Activate message is sent to the actor
                var state = State.Current ?? nameof(Initial);
                behavior.Initial(state);

                // grab reference to notifications stream
                notifications = System.StreamOf("notifications", "copier");
            }

            // route all received messages via behavior
            return await behavior.Receive(message);
        }

        Task<object> Become(Receive other) => Do(()=> Switch(() => behavior.Become(other)));
        Task<object> BecomeStacked(Receive other) => Do(()=> Switch(() => behavior.BecomeStacked(other)));
        Task<object> Unbecome() => Do(() => Switch(() => behavior.Unbecome()));

        async Task Switch(Func<Task> func)
        {
            try
            {
                await func();
                await NotifyStateChanged();
            }
            catch (Exception ex)
            {
                // if an unhandled error occured during switching behavior
                // deactivate an actor as state is indeterminate
                // it will be either reactivated back by supervision (reminder)
                // or by the user command, continuing from where it previously crashed
                await NotifyError(ex);
                Activation.DeactivateOnIdle();
                throw;
            }

            async Task NotifyStateChanged() => await Notify(new StateChanged
            {
                Current = State.Current,
                Previous = State.Previous,

                // this is where modeling superstates is also handy
                Active = behavior.Current.IsSubstateOf(nameof(Active))
            });

            async Task NotifyError(Exception ex) => await Notify(new Error
            {
                Message = ex.Message,
                Type = ex.Message,
                StackTrace = ex.StackTrace
            });
        }

        async Task Notify(JobEvent e)
        {
            e.Id = Id;
            await notifications.Push(e);
        }

        static Task<object> Inactive(object message) => TaskResult.Unhandled;

        Task<object> Active(object message) => message switch
        {   
            Suspend _ => BecomeStacked(Suspended),
            _ => TaskResult.Unhandled
        };

        Receive Durable(Receive next) => async message =>
        {
            var result = await next(message);
            if (message is Become)
            {
                State.Current = behavior.Current;
                State.Previous = behavior.Previous;
                await storage.WriteStateAsync();
            }
            return result;
        };

        Task<object> Initial(object message) => message switch
        {
            Start _ => Become(Preparing),
            _ => TaskResult.Unhandled
        };

        Task<object> Preparing(object message)
        {
            return message switch
            {
                Activate _ => Do(() => RunJob(Prepare)),
                Prepared x => Do(async () =>
                {
                    State.LineTotal = x.Lines;
                    await Become(Copying);
                }),
                _ => TaskResult.Unhandled
            };

            async Task Prepare(BackgroundJobToken _)
            {
                var source = SourceFileName();
                var target = TargetFileName();
                
                source.DeleteFileIfExists();
                target.DeleteFileIfExists();

                const int lines = 50000;
                await File.WriteAllLinesAsync(source, Enumerable.Range(0, lines).Select(x => x.ToString("00000")));
                await Task.Delay(TimeSpan.FromSeconds(10)); // artificial delay for demo purposes

                // since timer callback runs in interleaved mode
                // mutating state within it is unsafe, to synchronize
                // we need to send message to self
                await Fire(new Prepared {Lines = lines});
            }
        }

        string SourceFileName() => IOPath.Combine(IOPath.GetTempPath(), "orleankka_durable_fsm_example", $"{Id}.txt");
        string TargetFileName() => IOPath.Combine(IOPath.GetTempPath(), "orleankka_durable_fsm_example", $"{Id}-copy.txt");

        Task<object> Copying(object message)
        {
            const int maxChunkSize = 500;

            return message switch
            {
                Activate _      => Do(() => RunJob(Copy)),
                Deactivate _    => Do(() => TerminateJob(Copy)),
                Copied x        => Do(async () =>
                {
                    State.LastCopiedLine += x.Count;
                    await storage.WriteStateAsync(); // checkpoint
                    
                    // notify progress
                    await NotifyProgressChanged((double) State.LastCopiedLine / State.LineTotal);
                    
                    // are we done?
                    if (x.Count < maxChunkSize)
                        await Become(Completed);
                }),
                _ => TaskResult.Unhandled
            };

            async Task Copy(BackgroundJobToken job)
            {
                var buffer = new List<char>();
                
                using (var source = new FileStream(SourceFileName(), FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var target = new FileStream(TargetFileName(), FileMode.Append, FileAccess.Write, FileShare.None))
                using (var reader = new StreamReader(source))
                using (var writer = new StreamWriter(target))
                {
                    var position = State.LastCopiedLine;
                    source.Seek(position * (5 + 2), SeekOrigin.Begin);

                    int read;
                    do
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(500)); // artificial delay for demo purposes

                        read = 0;
                        buffer.Clear();

                        while (read < maxChunkSize)
                        {
                            var line = await reader.ReadLineAsync();
                            if (line == null)
                                break;

                            read++;
                            buffer.AddRange(line.ToCharArray());
                        }
                        await writer.WriteAsync(buffer.ToArray());

                        // send message to self to checkpoint current progress
                        await Fire(new Copied {Count = read});
                    }
                    while (read == maxChunkSize && !job.IsTerminationRequested);
                }
            }

            async Task NotifyProgressChanged(double progress) => await Notify(new ProgressChanged {Progress = progress});
        }

        Task<object> Restartable(object message) => message switch
        {
            Restart _ => Become(Restarting), // switch to intermediary state
            _ =>  TaskResult.Unhandled
        };

        async Task<object> Restarting(object message)
        {
            // we can't directly switch to Preparing upon receive of Restart message,
            // since in Preparing step we delete/create the files which are used
            // in Copying state and so we need to wait until all background jobs
            // are *actually* terminated (or failed)
            
            switch (message)
            {
                case Activate _:
                    if (TerminateAllJobs())
                        await Become(Preparing);
                    break;
                case BackgroundJobFailed _:
                case BackgroundJobTerminated _:
                    if (!Jobs.Active().Any())
                        await Become(Preparing);
                    break;
                default: 
                    return Unhandled;
            }

            return Done;
        }
 
        Task<object> Suspended(object message) => message switch
        {
            Continue _ => Unbecome(),
            _ => TaskResult.Unhandled
        };
        
        Task<object> Cancellable(object message) => message switch
        {
            Cancel _ => Become(Canceled),
            _ => TaskResult.Unhandled
        };

        static Task<object> Canceled(object message) => TaskResult.Unhandled;
        static Task<object> Completed(object message) => TaskResult.Unhandled;
    }
}