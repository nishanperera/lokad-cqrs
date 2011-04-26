﻿#region (c) 2010-2011 Lokad - CQRS for Windows Azure - New BSD License 

// Copyright (c) Lokad 2010-2011, http://www.lokad.com
// This code is released as Open Source under the terms of the New BSD Licence

#endregion

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using Lokad.Cqrs.Build.Engine;
using Lokad.Cqrs.Core.Directory.Default;
using NUnit.Framework;

namespace Lokad.Cqrs
{
    public abstract class EngineFixture
    {
        CancellationTokenSource _source;
        CloudEngineHost _host;
        Subject<ISystemEvent> _events;

        public IObservable<ISystemEvent> Events
        {
            get { return _events; }
        }

        Action<CloudEngineBuilder> _whenConfiguring;

        [DataContract]
        public sealed class StringCommand : Define.Command
        {
            [DataMember]
            public string Data { get; set; }
        }

        public sealed class StringHandler : Define.Handler<StringCommand>
        {
            readonly Action<string> _action;


            public StringHandler(Action<string> action)
            {
                _action = action;
            }

            public void Consume(StringCommand message)
            {
                _action(message.Data);
            }
        }

        public bool TestCompleted { get; set; }

        public void StopAndComplete()
        {
            Trace.WriteLine("completing test");
            TestCompleted = true;
            _source.Cancel();
        }

        protected IMessageSender Sender
        {
            get { return _host.Resolve<IMessageSender>(); }
        }

        protected void HandleString(Action<string> data)
        {
            _whenConfiguring += builder => builder.RegisterInstance(data);
        }

        protected void SendString(string data)
        {
            Sender.Send(new StringCommand {Data = data});
        }

        [SetUp]
        public void SetUp()
        {
            _source = new CancellationTokenSource();
            _events = new Subject<ISystemEvent>();
            _whenConfiguring = builder => { };
            TestCompleted = false;
        }

        public void RunEngineTillStopped(Action whenStarted)
        {
            var identifyNested =
                new[] {typeof (EngineFixture), GetType()}
                    .SelectMany(t => t.GetNestedTypes())
                    .Where(t => typeof (IMessage).IsAssignableFrom(t))
                    .Where(t => !t.IsAbstract)
                    .ToArray();


            var engine = new CloudEngineBuilder()
                .RegisterInstance<IObserver<ISystemEvent>>(_events)
                .RegisterInstance(this)
                .AddMessageClient("memory:in")
                .AddMemoryPartition("in")
                .DomainIs(d => d.WhereMessages(t => identifyNested.Contains(t)).InCurrentAssembly());

            _whenConfiguring(engine);

            using (_host = engine.Build())
            using (var t = _source)
            {
                _host.Start(t.Token);

                whenStarted();

                if (!t.Token.WaitHandle.WaitOne(5000))
                {
                    t.Cancel();
                }
            }
            _source = null;
            _host = null;
        }
    }
}