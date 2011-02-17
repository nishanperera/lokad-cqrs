#region (c) 2010 Lokad Open Source - New BSD License 

// Copyright (c) Lokad 2010, http://www.lokad.com
// This code is released as Open Source under the terms of the New BSD Licence

#endregion

using System;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Lokad.Cqrs.Consume;
using Lokad.Cqrs.Queue;

namespace Lokad.Cqrs.Transport
{
	
	public sealed class ConsumingProcess : IEngineProcess
	{
		readonly AzureQueueFactory _factory;
		readonly IMessageDispatcher _dispatcher;
		readonly ILog _log;
		readonly string[] _queueNames;
		readonly AzureReadQueue[] _queues;
		readonly Func<uint, TimeSpan> _threadSleepInterval;

		public ConsumingProcess(
			AzureQueueTransportConfig config,
			ILogProvider logProvider,
			AzureQueueFactory factory, 
			IMessageDispatcher dispatcher)
		{
			_factory = factory;
			_dispatcher = dispatcher;
			_queueNames = config.QueueNames;
			_log = logProvider.Get(typeof (ConsumingProcess).Name + "." + _queueNames.Join("|"));
			_threadSleepInterval = config.SleepWhenNoMessages;
			_queues = new AzureReadQueue[_queueNames.Length];
		}
		
		public void Dispose()
		{
			_disposal.Dispose();
		}

		public void Initialize()
		{
			for (int i = 0; i < _queueNames.Length; i++)
			{
				_queues[i] = _factory.GetReadQueue(_queueNames[i]);
			}
		}

		readonly CancellationTokenSource _disposal = new CancellationTokenSource();

		public Task Start(CancellationToken token)
		{
			_log.DebugFormat("Starting transport for {0}", _queueNames.Join(";"));
			return Task.Factory.StartNew(() => ReceiveMessages(token), token);
		}
	

		Maybe<Exception> GetProcessingFailure(AzureReadQueue queue, UnpackedMessage message)
		{
			try
			{
				MessageContext.OverrideContext(message);
				_dispatcher.DispatchMessage(message);
			}
			catch (Exception ex)
			{
				var text = string.Format("Failed to consume '{0}' from '{1}'", message, queue.Name);
				_log.Error(ex, text);
				return ex;
			}
			finally
			{
				MessageContext.ClearContext();
			}
			return Maybe<Exception>.Empty;
		}

		void MessageHandlingProblem(UnpackedMessage message, Exception ex)
		{
			// notify all subscribers
			// do nothing. Message will show up in the queue with the increased enqueue count.
		}

		static void FinalizeSuccess(AzureReadQueue queue, UnpackedMessage message)
		{
			queue.AckMessage(message);
			//tx.Complete();
		}

		void ReceiveMessages(CancellationToken outer)
		{
			uint beenIdleFor = 0;

			using (var source = CancellationTokenSource.CreateLinkedTokenSource(_disposal.Token, outer))
			{
				var token = source.Token;

				while (!token.IsCancellationRequested)
				{
					var messageFound = false;
					foreach (var messageQueue in _queues)
					{
						if (token.IsCancellationRequested)
							return;

						// selector policy goes here
						if (ProcessQueueForMessage(messageQueue) == QueueProcessingResult.MoreWork)
						{
							messageFound = true;
						}
					}

					if (messageFound)
					{
						beenIdleFor = 0;
					}
					else
					{
						beenIdleFor += 1;
						var sleepInterval = _threadSleepInterval(beenIdleFor);
						token.WaitHandle.WaitOne(sleepInterval);
					}
				}
			}
		}

		QueueProcessingResult ProcessQueueForMessage(AzureReadQueue queue)
		{
			
			try
			{
				var result = queue.GetMessage();

				switch (result.State)
				{
					case GetMessageResultState.Success:
						GetProcessingFailure(queue, result.Message)
							.Apply(ex => MessageHandlingProblem(result.Message, ex))
							.Handle(() => FinalizeSuccess(queue, result.Message));
						return QueueProcessingResult.MoreWork;

					case GetMessageResultState.Wait:
						return QueueProcessingResult.Sleep;

					case GetMessageResultState.Exception:
						_log.DebugFormat(result.Exception, "Exception, while trying to get message");
						return QueueProcessingResult.MoreWork;

					case GetMessageResultState.Retry:
						//tx.Complete();
						// retry immediately
						return QueueProcessingResult.MoreWork;
					default:
						throw new ArgumentOutOfRangeException();
				}
			}
			catch (TransactionAbortedException ex)
			{
				_log.Error(ex, "Aborting transaction");
				// do nothing);
				return QueueProcessingResult.MoreWork;
			}
		}

		enum QueueProcessingResult
		{
			MoreWork,
			Sleep
		}
	}
}