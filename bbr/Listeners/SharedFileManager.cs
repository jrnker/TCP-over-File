﻿using bbr.Commands;
using bbr.Listeners;
using bbrelay.Utilities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Mail;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace bbr.Streams
{
    public class SharedFileManager : StreamEstablisher
    {
        readonly Dictionary<int, BlockingCollection<byte[]>> ReceiveQueue = new();
        readonly BlockingCollection<Command> SendQueue = new();
        readonly HashSet<int> ConnectionIds = new();

        public SharedFileManager(string readFromFilename, string writeToFilename)
        {
            ReadFromFilename = readFromFilename;
            WriteToFilename = writeToFilename;

            if (!string.IsNullOrEmpty(readFromFilename))
            {
                Task.Factory.StartNew(() =>
                {
                    try
                    {
                        ReceivePump();
                    }
                    catch (Exception ex)
                    {
                        Program.Log($"ReceivePump: {ex}");
                        Environment.Exit(1);
                    }
                });
            }

            if (!string.IsNullOrEmpty(writeToFilename))
            {
                Task.Factory.StartNew(() =>
                {
                    try
                    {
                        SendPump();
                    }
                    catch (Exception ex)
                    {
                        Program.Log($"SendPump: {ex}");
                        Environment.Exit(1);
                    }
                });
            }
        }

        public byte[] Read(int connectionId)
        {
            if (!ReceiveQueue.ContainsKey(connectionId))
            {
                ReceiveQueue.Add(connectionId, new BlockingCollection<byte[]>());
            }

            var queue = ReceiveQueue[connectionId];

            var result = queue.Take(cancellationTokenSource.Token);
            return result;
        }

        public void Connect(int connectionId)
        {
            var connectCommand = new Connect(connectionId);
            SendQueue.Add(connectCommand);
        }

        public void Write(int connectionId, byte[] data)
        {
            var forwardCommand = new Forward(connectionId, data);
            SendQueue.Add(forwardCommand);
        }

        public void TearDown(int connectionId)
        {
            var teardownCommand = new TearDown(connectionId);
            SendQueue.Add(teardownCommand);
        }

        public const long PURGE_SIZE_BYTES = 1 * 1024 * 1024;

        public void SendPump()
        {
            FileStream fileStream = null;
            BinaryWriter writer = null;

            while (!cancellationTokenSource.IsCancellationRequested)
            {
                try
                {
                    foreach (var toSend in SendQueue.GetConsumingEnumerable(cancellationTokenSource.Token))
                    {
                        //FileOptions.DeleteOnClose causes access issues, and FileOptions.WriteThrough causes significant slowdown
                        fileStream ??= new FileStream(WriteToFilename, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                        writer ??= new BinaryWriter(fileStream);

                        if (toSend is Forward fwd && !ConnectionIds.Contains(fwd.ConnectionId))
                        {

                        }

                        toSend.Serialise(writer);
                        writer.Flush();

                        if (fileStream.Length > PURGE_SIZE_BYTES)
                        {
                            if (toSend is Forward forward)
                            {
                                //tell the other side to purge the file
                                var purgeCommand = new Purge(forward.ConnectionId);
                                purgeCommand.Serialise(writer);
                                writer.Flush();

                                //wait until the receiver has processed this message (signified by the file being truncated)

                                while (true)
                                {
                                    Program.Log($"Waiting for file to be purged: {WriteToFilename}");

                                    try
                                    {
                                        if (fileStream.Length == 0)
                                        {
                                            break;
                                        }

                                        Thread.Sleep(10);
                                    }
                                    catch (Exception ex)
                                    {
                                        Program.Log($"Waiting for file to be purged: {ex}");
                                        break;
                                    }
                                }

                                fileStream.Position = 0;

                                //fileStream.Close();
                                //writer.Close();

                                Program.Log($"File purge is complete: {WriteToFilename}");
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {

                }
                catch (Exception ex)
                {
                    Program.Log($"CopyTo: {ex}");
                }
            }
        }

        readonly CancellationTokenSource cancellationTokenSource = new();
        public void ReceivePump()
        {
            File.Delete(ReadFromFilename);

            while (!IOUtils.FileExists(ReadFromFilename))
            {
                Program.Log($"Waiting for file to be created: {ReadFromFilename}");
                Thread.Sleep(1000);
            }

            FileStream fileStream = null;
            BinaryReader binaryReader = null;

            while (true)
            {
                fileStream ??= new FileStream(ReadFromFilename, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
                binaryReader ??= new BinaryReader(fileStream);

                if (fileStream.Position == fileStream.Length)
                {
                    //Program.Log($"Waiting for content: {ReadFromFilename}");
                    //Thread.Sleep(100);
                    continue;
                }

                var command = Command.Deserialise(binaryReader);

                if (command is Connect connect)
                {
                    if (!ReceiveQueue.ContainsKey(connect.ConnectionId))
                    {
                        ReceiveQueue.Add(connect.ConnectionId, new BlockingCollection<byte[]>());

                        var sharedFileStream = new SharedFileStream(this, connect.ConnectionId);
                        StreamEstablished?.Invoke(this, sharedFileStream);
                    }
                }

                if (command is Forward forward)
                {
                    var connectionReceiveQueue = ReceiveQueue[forward.ConnectionId];
                    connectionReceiveQueue.Add(forward.Payload);
                }

                if (command is Purge)
                {
                    Program.Log($"Was asked to purge {ReadFromFilename}");

                    //let's truncate the file, so that it doesn't get too big and to signify to the other side that we've processed it.
                    //FPS 30/11/2023: Occasionally, this doesn't seem to clear the file

                    /*
                    fileStream.Position = 0;
                    fileStream.SetLength(0);
                    fileStream.Flush(true);
                    */

                    binaryReader.Close();
                    fileStream.Close();

                    binaryReader = null;
                    fileStream = null;

                    using (var fs = new FileStream(ReadFromFilename, new FileStreamOptions()
                    {
                        Mode = FileMode.Open,
                        Access = FileAccess.ReadWrite,
                        Share = FileShare.ReadWrite | FileShare.Delete
                    }))
                    {
                        fs.SetLength(0);
                    }

                    Program.Log($"Purge complete: {ReadFromFilename}");
                }

                if (command is TearDown teardown)
                {
                    Program.Log($"Was asked to tear down {teardown.CommandId}");
                    //var connectionReceiveQueue = ReceiveQueue[teardown.CommandId];
                    //connectionReceiveQueue.CompleteAdding();
                }
            }
        }

        public override void Stop()
        {
            /*
            ConnectionIds
                .ForEach(connectionId =>
                {
                    var teardownCommand = new TearDown(connectionId);
                    SendQueue.Add(teardownCommand);
                });

            cancellationTokenSource.Cancel();
            receiveTask.Wait();
            sendTask.Wait();

            try
            {
                Program.Log($"Deleting {ReadFromFilename}");
                File.Delete(ReadFromFilename);
            }
            catch { }

            try
            {
                Program.Log($"Deleting {WriteToFilename}");
                File.Delete(WriteToFilename);
            }
            catch { }
            */
        }

        public string WriteToFilename { get; }
        public string ReadFromFilename { get; }
    }
}