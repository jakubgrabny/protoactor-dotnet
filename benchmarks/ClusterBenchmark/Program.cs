﻿// -----------------------------------------------------------------------
// <copyright file="Program.cs" company="Asynkron AB">
//      Copyright (C) 2015-2020 Asynkron AB All rights reserved
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ClusterExperiment1.Messages;
using Microsoft.Extensions.Logging;
using Proto;
using Proto.Cluster;
using Proto.Cluster.Partition;
using Proto.Utils;

namespace ClusterExperiment1
{
    public static class Program
    {

        private static TaskCompletionSource<bool> ts = null!;
        private static int actorCount;
        private static int memberCount;
        private static int killTimeoutSeconds;

        private static int requestCount;
        private static int failureCount;
        private static int successCount;

        private static object Request = null!;

        public static async Task Main(string[] args)
        {
            AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
        //    ThreadPool.SetMinThreads(500, 500);
            Request = new HelloRequest();

            if (args.Length > 0)
            {
                // InteractiveOutput = args[0] == "1";

                var worker = await Configuration.SpawnMember();
                AppDomain.CurrentDomain.ProcessExit += (sender, args) => { worker.ShutdownAsync().Wait(); };
                Thread.Sleep(Timeout.Infinite);
                return;
            }

            Configuration.SetupLogger();

            ts = new TaskCompletionSource<bool>();

            // _ = DockerSupport.Run(ts.Task);

            Console.WriteLine("Proto.Cluster chaos benchmark");
            Console.WriteLine();
            Console.WriteLine("Explanation:");
            Console.WriteLine(". = 10 000 successful requests");
            // Console.WriteLine("# = activation of a virtual actor");
            // Console.WriteLine("+ = (deliberate) deactivation of virtual actor");
            Console.WriteLine("X = NULL response, e.g. requests retried but got no response");
            Console.WriteLine();
            // Console.WriteLine("1) Run with interactive output");
            // Console.WriteLine("2) Run silent");
            //
            // var res0 = Console.ReadLine();
            // InteractiveOutput = res0 == "1";

            Console.WriteLine("1) Run single process - graceful exit");
            Console.WriteLine("2) Run single process");
            Console.WriteLine("3) Run multi process - graceful exit");
            Console.WriteLine("4) Run multi process");

            var memberRunStrategy = Console.ReadLine();
            
            Console.WriteLine("1) Protobuf serializer");
            Console.WriteLine("2) Json serializer");

            if (Console.ReadLine() == "2")
            {
                Request = new HelloRequestPoco();
            }
            

            Console.WriteLine("1) Run single request client");
            Console.WriteLine("2) Run batch requests client");
            Console.WriteLine("3) Run fire and forget client");

            var clientStrategy = Console.ReadLine();

            var batchSize = 0;

            if (clientStrategy == "2")
            {
                Console.WriteLine("Batch size? default is 50");

                if (!int.TryParse(Console.ReadLine(), out batchSize)) batchSize = 50;

                Console.WriteLine($"Using batch size {batchSize}");
            }

            Console.WriteLine("Number of virtual actors? default 10000");
            if (!int.TryParse(Console.ReadLine(), out actorCount)) actorCount = 10_000;
            Console.WriteLine($"Using {actorCount} actors");

            Console.WriteLine("Number of cluster members? default is 8");
            if (!int.TryParse(Console.ReadLine(), out memberCount)) memberCount = 8;
            Console.WriteLine($"Using {memberCount} members");

            Console.WriteLine("Seconds to run before stopping members? default is 30");
            if (!int.TryParse(Console.ReadLine(), out killTimeoutSeconds)) killTimeoutSeconds = 30;
            Console.WriteLine($"Using {killTimeoutSeconds} seconds");


            Action run = clientStrategy switch
            {
                "1" => () => RunClient(),
                "2" => () => RunBatchClient(batchSize),
                "3" => () => RunFireForgetClient(),
                _   => throw new ArgumentOutOfRangeException()
            };
            
            var elapsed = await (memberRunStrategy switch
            {
                "1" => RunWorkers(() => new RunMemberInProcGraceful(), run),
                "2" => RunWorkers(() => new RunMemberInProc(), run),
                "3" => RunWorkers(() => new RunMemberExternalProcGraceful(), run),
                "4" => RunWorkers(() => new RunMemberExternalProc(), run),
                _   => throw new ArgumentOutOfRangeException()
            });

            var tps = requestCount / elapsed.TotalMilliseconds * 1000;
            Console.WriteLine();
            Console.WriteLine($"Requests:\t{requestCount:N0}");
            Console.WriteLine($"Successful:\t{successCount:N0}");
            Console.WriteLine($"Failures:\t{failureCount:N0}");
            Console.WriteLine($"Throughput:\t{tps:N0} requests/sec -> {(tps*2):N0} msg/sec");
        }

        private static void RunFireForgetClient()
        {
            var logger = Log.CreateLogger(nameof(Program));

            _ = SafeTask.Run(async () => {
                    var semaphore = new AsyncSemaphore(50);
                    var cluster = await Configuration.SpawnClient();
                    var rnd = new Random();

                    while (true)
                    {
                        var id = "myactor" + rnd.Next(0, actorCount);
                        semaphore.Wait(() => SendRequest(cluster, id, CancellationTokens.WithTimeout(20_000)));
                    }
                }
            );
        }

        private static async Task SendRequest(Cluster cluster, string id, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requestCount);

            try
            {
                var x = await cluster.RequestAsync<object>(id, "hello", Request, cancellationToken);

                if (x != null)
                {
                    var res = Interlocked.Increment(ref successCount);

                    if (res % 10000 == 0)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGreen;
                        Console.Write(".");
                        Console.ResetColor();
                    }

                    return;
                }

                OnError();
            }
            catch
            {
                OnError();
            }

            void OnError()
            {
                Interlocked.Increment(ref failureCount);

                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write("X");
                Console.ResetColor();

                var il = cluster.Config.IdentityLookup as PartitionIdentityLookup;

                il?.DumpState(ClusterIdentity.Create(id, "hello"));
            }
        }


        private static void RunBatchClient(int batchSize)
        {
            var logger = Log.CreateLogger(nameof(Program));

            _ = SafeTask.Run(async () => {
                    var cluster = await Configuration.SpawnClient();
                    var rnd = new Random();
                    var semaphore = new AsyncSemaphore(5);
                    
                    while (true)
                    {
                        semaphore.Wait(() => RunBatch(rnd, cluster) );
                    }
                }
            );

            async Task RunBatch(Random? rnd, Cluster cluster)
            {

                var requests = new List<Task>();

                try
                {
                    var ct = CancellationTokens.FromSeconds(20);

                    for (var i = 0; i < batchSize; i++)
                    {
                        var id = "myactor" + rnd.Next(0, actorCount);
                        var request = SendRequest(cluster, id, ct);

                        requests.Add(request);
                    }

                    await Task.WhenAll(requests);
                }
                catch (Exception x)
                {
                    logger.LogError(x, "Error...");
                }
            }
        }

        private static void RunClient()
        {
            var logger = Log.CreateLogger(nameof(Program));

            _ = SafeTask.Run(async () => {
                    var cluster = await Configuration.SpawnClient();
                    var rnd = new Random();

                    while (true)
                    {
                        var id = "myactor" + rnd.Next(0, actorCount);
                        var ct = CancellationTokens.WithTimeout(20_000);
                        await SendRequest(cluster, id, ct);
                    }
                }
            );
        }

        private static async Task<TimeSpan> RunWorkers(Func<IRunMember> memberFactory, Action startClient)
        {
            var followers = new List<IRunMember>();

            for (var i = 0; i < memberCount; i++)
            {
                var p = memberFactory();
                await p.Start();
                followers.Add(p);
            }
            
            await Task.Delay(8000);

            startClient();
            Console.WriteLine("Client started...");

            var sw = Stopwatch.StartNew();

            await Task.Delay(killTimeoutSeconds * 1000);
            bool first = true;
            foreach (var t in followers)
            {
                if (first)
                {
                    first = false;
                }
                else
                {
                    await Task.Delay(killTimeoutSeconds * 1000);
                }

                Console.WriteLine("Stopping node...");
                _ = t.Kill();
                
            }

            sw.Stop();
            return sw.Elapsed;
        }
    }
}