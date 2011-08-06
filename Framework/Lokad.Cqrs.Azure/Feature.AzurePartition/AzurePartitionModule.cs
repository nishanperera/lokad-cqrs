#region (c) 2010-2011 Lokad CQRS - New BSD License 

// Copyright (c) Lokad SAS 2010-2011 (http://www.lokad.com)
// This code is released as Open Source under the terms of the New BSD Licence
// Homepage: http://lokad.github.com/lokad-cqrs/

#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using Funq;
using Lokad.Cqrs.Build.Engine;
using Lokad.Cqrs.Core;
using Lokad.Cqrs.Core.Dispatch;
using Lokad.Cqrs.Core.Outbox;
using Lokad.Cqrs.Evil;
using Lokad.Cqrs.Feature.AzurePartition.Inbox;

// ReSharper disable UnusedMember.Global
// ReSharper disable MemberCanBePrivate.Global

namespace Lokad.Cqrs.Feature.AzurePartition
{
    public sealed class AzurePartitionModule : HideObjectMembersFromIntelliSense, IAdvancedDispatchBuilder
    {
        readonly HashSet<string> _queueNames = new HashSet<string>();
        TimeSpan _queueVisibilityTimeout;
        Func<Container, IEnvelopeQuarantine> _quarantineFactory;

        Func<uint, TimeSpan> _decayPolicy;

        readonly IAzureStorageConfig _config;

        Func<Container, ISingleThreadMessageDispatcher> _dispatcher;


        public AzurePartitionModule(IAzureStorageConfig config, string[] queueNames)
        {
            QueueVisibility(30000);

            _config = config;
            _queueNames = new HashSet<string>(queueNames);

            Quarantine(c => new MemoryQuarantine());
            DecayPolicy(TimeSpan.FromSeconds(2));
        }


        public void DispatcherIs(Func<Container, ISingleThreadMessageDispatcher> factory)
        {
            _dispatcher = factory;
        }

        /// <summary>
        /// Allows to specify custom <see cref="IEnvelopeQuarantine"/> optionally resolving 
        /// additional instances from the container
        /// </summary>
        /// <param name="factory">The factory method to specify custom <see cref="IEnvelopeQuarantine"/>.</param>
        public void Quarantine(Func<Container, IEnvelopeQuarantine> factory)
        {
            _quarantineFactory = factory;
        }

        /// <summary>
        /// Sets the custom decay policy used to throttle Azure queue checks, when there are no messages for some time.
        /// This overload eventually slows down requests till the max of <paramref name="maxInterval"/>.
        /// </summary>
        /// <param name="maxInterval">The maximum interval to keep between checks, when there are no messages in the queue.</param>
        public void DecayPolicy(TimeSpan maxInterval)
        {
            _decayPolicy = DecayEvil.BuildExponentialDecay(maxInterval);
        }

        /// <summary>
        /// Sets the custom decay policy used to throttle Azure queue checks, when there are no messages for some time.
        /// </summary>
        /// <param name="decayPolicy">The decay policy, which is function that returns time to sleep after Nth empty check.</param>
        public void DecayPolicy(Func<uint, TimeSpan> decayPolicy)
        {
            _decayPolicy = decayPolicy;
        }

        /// <summary>
        /// Defines dispatcher as lambda method that is resolved against the container
        /// </summary>
        /// <param name="factory">The factory.</param>
        public void DispatcherIsLambda(HandlerFactory factory)
        {
            _dispatcher = context =>
                {
                    var lambda = factory(context);
                    return new ActionDispatcher(lambda);
                };
        }


        public void DispatchToRoute(Func<ImmutableEnvelope, string> route)
        {
            DispatcherIs((ctx) => new DispatchMessagesToRoute(ctx.Resolve<QueueWriterRegistry>(), route));
        }

        IEngineProcess BuildConsumingProcess(Container context)
        {
            var log = context.Resolve<ISystemObserver>();

            var dispatcher = _dispatcher(context);
            dispatcher.Init();

            var streamer = context.Resolve<IEnvelopeStreamer>();

            var factory = new AzurePartitionFactory(streamer, log, _config, _queueVisibilityTimeout, _decayPolicy);

            var notifier = factory.GetNotifier(_queueNames.ToArray());
            var quarantine = _quarantineFactory(context);
            var manager = context.Resolve<MessageDuplicationManager>();
            var transport = new DispatcherProcess(log, dispatcher, notifier, quarantine, manager);
            return transport;
        }

        /// <summary>
        /// Specifies queue visibility timeout for Azure Queues.
        /// </summary>
        /// <param name="timeoutMilliseconds">The timeout milliseconds.</param>
        public void QueueVisibility(int timeoutMilliseconds)
        {
            _queueVisibilityTimeout = TimeSpan.FromMilliseconds(timeoutMilliseconds);
        }

        /// <summary>
        /// Specifies queue visibility timeout for Azure Queues.
        /// </summary>
        /// <param name="timespan">The timespan.</param>
        public void QueueVisibility(TimeSpan timespan)
        {
            _queueVisibilityTimeout = timespan;
        }

        public void Configure(Container container)
        {
            if (null == _dispatcher)
            {
                throw new InvalidOperationException(@"No message dispatcher configured, please supply one.

You can use either 'DispatcherIsLambda' or reference Lokad.CQRS.Composite and 
use Command/Event dispatchers. If you are migrating from v2.0, that's what you 
should do.");
            }
            var setup = container.Resolve<EngineSetup>();
            var process = BuildConsumingProcess(container);
            setup.AddProcess(process);
        }
    }
}