﻿using System;
using System.IO;
using Lokad.Cqrs.Lmf;
using Microsoft.WindowsAzure;
using Microsoft.WindowsAzure.StorageClient;

namespace Lokad.Cqrs.Queue
{
	public sealed class AzureWriteQueue
	{
		public void SendMessages(object[] messages)
		{
			if (messages.Length == 0)
				return;
			foreach (var message in messages)
			{
				var packed = PackNewMessage(message);
				_queue.AddMessage(packed);
			}
		}

		//http://abdullin.com/journal/2010/6/4/azure-queue-messages-cannot-be-larger-than-8192-bytes.html
		const int CloudQueueLimit = 6144;


		CloudQueueMessage PackNewMessage(object message)
		{
			var messageId = Guid.NewGuid();
			var created = DateTime.UtcNow;

			var messageType = message.GetType();
			var contract = _serializer
				.GetContractNameByType(messageType)
				.ExposeException(() => NoContractNameOnSend(messageType, _serializer));
			var referenceId = created.ToString(DateFormatInBlobName) + "-" + messageId;

			var buffer = MessageUtil.SaveDataMessage(messageId, contract, _queue.Uri, _serializer, message);
			if (buffer.Length < CloudQueueLimit)
			{
				// write message to queue
				return new CloudQueueMessage(buffer);
			}
			// ok, we didn't fit, so create reference message

			_cloudBlob
					.GetBlobReference(referenceId)
					.UploadByteArray(buffer);

			var reference = new MessageReference(messageId.ToString(), _cloudBlob.Uri.ToString(), referenceId);
			var blob = MessageUtil.SaveReferenceMessage(reference);
			return new CloudQueueMessage(blob);
		}

		public AzureWriteQueue(IMessageSerializer serializer, CloudStorageAccount account, string queueName)
		{
			_serializer = serializer;
			var blobClient = account.CreateCloudBlobClient();
			blobClient.RetryPolicy = RetryPolicies.NoRetry();

			_cloudBlob = blobClient.GetContainerReference(queueName);

			var queueClient = account.CreateCloudQueueClient();
			queueClient.RetryPolicy = RetryPolicies.NoRetry();
			_queue = queueClient.GetQueueReference(queueName);

		}

		public void Init()
		{
			_queue.CreateIfNotExist();
			_cloudBlob.CreateIfNotExist();
		}


		const string DateFormatInBlobName = "yyyy-MM-dd-HH-mm-ss-ffff";
		readonly IMessageSerializer _serializer;
		readonly CloudBlobContainer _cloudBlob;
		readonly CloudQueue _queue;

		public static Exception NoContractNameOnSend(Type messageType, IMessageSerializer serializer)
		{
			return Errors.InvalidOperation(
				"Can't find contract name to serialize message: '{0}'. Make sure that your message types are loaded by domain and are compatible with '{1}'.",
				messageType, serializer.GetType().Name);
		}
	}
}