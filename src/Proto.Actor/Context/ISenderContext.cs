// -----------------------------------------------------------------------
// <copyright file="ISenderContext.cs" company="Asynkron AB">
//      Copyright (C) 2015-2020 Asynkron AB All rights reserved
// </copyright>
// -----------------------------------------------------------------------
using System;
using System.Threading;
using System.Threading.Tasks;
using Proto.Future;

// ReSharper disable once CheckNamespace
namespace Proto
{
    public interface ISenderContext : IInfoContext
    {
        /// <summary>
        ///     MessageHeaders of the Context
        /// </summary>
        MessageHeader Headers { get; }

        //TODO: should the current message of the actor be exposed to sender middleware?
        object? Message { get; }

        /// <summary>
        ///     Send a message to a given PID target
        /// </summary>
        /// <param name="target">The target PID</param>
        /// <param name="message">The message to send</param>
        void Send(PID target, object message);

        /// <summary>
        ///     Sends a message together with a Sender PID, this allows the target to respond async to the Sender
        /// </summary>
        /// <param name="target">The target PID</param>
        /// <param name="message">The message to send</param>
        /// <param name="sender">Message sender</param>
        void Request(PID target, object message, PID? sender);

        /// <summary>
        ///     Sends a message together with a Sender PID, this allows the target to respond async to the Sender.
        ///     This operation can be awaited.
        /// </summary>
        /// <param name="target">The target PID</param>
        /// <param name="message">The message to send</param>
        /// <param name="cancellationToken">Cancellation token for the request</param>
        /// <typeparam name="T">Expected return message type</typeparam>
        /// <returns>A Task that completes once the Target Responds back to the Sender</returns>
        Task<T> RequestAsync<T>(PID target, object message, CancellationToken cancellationToken);
    }

    public static class SenderContextExtensions
    {

        /// <summary>
        ///     Sends a message together with a Sender PID, this allows the target to respond async to the Sender
        /// </summary>
        /// <param name="target">The target PID</param>
        /// <param name="message">The message to send</param>
        public static void Request(this ISenderContext self, PID target, object message) =>
            self.Request(target, message, self.Self);

        /// <summary>
        ///     Sends a message together with a Sender PID, this allows the target to respond async to the Sender.
        ///     This operation can be awaited.
        /// </summary>
        /// <param name="target">The target PID</param>
        /// <param name="message">The message to send</param>
        /// <typeparam name="T">Expected return message type</typeparam>
        /// <returns>A Task that completes once the Target Responds back to the Sender</returns>
        public static Task<T> RequestAsync<T>(this ISenderContext self, PID target, object message) =>
            self.RequestAsync<T>(target, message, CancellationToken.None);

        /// <summary>
        ///     Sends a message together with a Sender PID, this allows the target to respond async to the Sender.
        ///     This operation can be awaited.
        /// </summary>
        /// <param name="target">The target PID</param>
        /// <param name="message">The message to send</param>
        /// <param name="timeout">Timeout for the request</param>
        /// <typeparam name="T">Expected return message type</typeparam>
        /// <returns>A Task that completes once the Target Responds back to the Sender</returns>
        public static Task<T> RequestAsync<T>(this ISenderContext self, PID target, object message, TimeSpan timeout)
            => self.RequestAsync<T>(target, message, CancellationTokens.WithTimeout(timeout));

        internal static async Task<T> RequestAsync<T>(this ISenderContext self,  PID target, object message, CancellationToken cancellationToken)
        {
            using var future = new FutureProcess(self.System);
            var messageEnvelope = new MessageEnvelope(message, future.Pid);
            self.Send(target, messageEnvelope);
            var result = await future.GetTask(cancellationToken);

            switch (result)
            {
                case DeadLetterResponse:
                    throw new DeadLetterException(target);
                case null:
                case T:
                    return (T) result!;
                default:
                    throw new InvalidOperationException(
                        $"Unexpected message. Was type {result.GetType()} but expected {typeof(T)}"
                    );
            }
        }
    }
}