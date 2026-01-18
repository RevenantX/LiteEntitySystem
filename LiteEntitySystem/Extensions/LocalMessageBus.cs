using System;
using System.Collections.Generic;
using LiteEntitySystem;
using LiteEntitySystem.Internal;

namespace LiteEntitySystem.Extensions
{
    /// <summary>
    /// Represents a subscription to a message bus channel that can be disposed to unsubscribe.
    /// </summary>
    public readonly struct BusSubscription : IDisposable
    {
        private readonly Action _dispose;
        public BusSubscription(Action dispose) => _dispose = dispose;
        
        /// <summary>
        /// Disposes the subscription, unsubscribing the handler from the message bus.
        /// </summary>
        public void Dispose() => _dispose?.Invoke();
    }

    /// <summary>
    /// Marker interface for message bus messages. TSender declares the expected sender type for the message.
    /// </summary>
    /// <typeparam name="TSender">The type of the entity that sends this message.</typeparam>
    public interface IBusMessage<TSender>
    {
    }

    /// <summary>
    /// A local message bus for publishing and subscribing to typed messages within a local singleton context.
    /// </summary>
    public sealed class LocalMessageBus : ILocalSingleton
    {
        /// <summary>
        /// If true, all published messages will be logged for debugging purposes.
        /// </summary>
        public bool DebugToLog;

        private interface IChannel : IDisposable { }

        private interface IChannel<TSender, TMessage> : IChannel
            where TMessage : struct, IBusMessage<TSender>
        {
            BusSubscription Subscribe(Action<TSender, TMessage> handler);
            void Publish(TSender sender, in TMessage msg, bool debugToLog = false);
        }

        // Null-object channel used after the bus is disposed to avoid null checks and NREs
        private sealed class NullChannel<TSender, TMessage> : IChannel<TSender, TMessage>
            where TMessage : struct, IBusMessage<TSender>
        {
            public static readonly NullChannel<TSender, TMessage> Instance = new();
            public BusSubscription Subscribe(Action<TSender, TMessage> handler) => default;
            public void Publish(TSender sender, in TMessage msg, bool debugToLog = false) { }
            public void Dispose() { }
        }

        private sealed class Channel<TSender, TMessage> : IChannel<TSender, TMessage>
            where TMessage : struct, IBusMessage<TSender>
        {
            private const int PreallocatedSize = 8;
            private Action<TSender, TMessage>[] _buffer;

            private readonly List<Action<TSender, TMessage>> _handlers = new();
            private bool _disposed;

            public BusSubscription Subscribe(Action<TSender, TMessage> handler)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(Channel<TSender, TMessage>));
                _handlers.Add(handler);
                return new BusSubscription(() => Unsubscribe(handler));
            }

            private void Unsubscribe(Action<TSender, TMessage> handler)
            {
                _handlers.Remove(handler);
            }

            public void Publish(TSender sender, in TMessage msg, bool debugToLog = false)
            {
                if (_disposed || _handlers == null || _handlers.Count == 0) 
                    return;

                var count = _handlers.Count;
                if (_buffer == null || _buffer.Length < count)
                    _buffer = new Action<TSender, TMessage>[Math.Max(count, PreallocatedSize)];

                // Fast copy handlers into buffer
                _handlers.CopyTo(0, _buffer, 0, count);

                for (int i = 0; i < count; i++)
                {
                    if (debugToLog)
                    {
                        Logger.Log($"MsgBus: {typeof(TSender)} -> {_buffer[i].Target}, msg: {msg}");
                    }
                    _buffer[i](sender, msg);
                }
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                _handlers.Clear();
            }
        }

        private readonly Dictionary<Type, IChannel> _channels = new();
        private bool _disposed;

        private IChannel<TSender, TMessage> Get<TSender, TMessage>()
            where TMessage : struct, IBusMessage<TSender>
        {
            if (_disposed) 
                return NullChannel<TSender, TMessage>.Instance;
        
            var t = typeof(TMessage);
            if (_channels.TryGetValue(t, out var ch))
                return (IChannel<TSender, TMessage>)ch;

            var created = new Channel<TSender, TMessage>();
            _channels.Add(t, created);
            return created;
        }

        /// <summary>
        /// Subscribes a handler to receive messages of a specific type.
        /// </summary>
        /// <typeparam name="TSender">The type of the sender of the message.</typeparam>
        /// <typeparam name="TMessage">The type of message to subscribe to.</typeparam>
        /// <param name="handler">The handler delegate to invoke when messages are published.</param>
        /// <returns>A subscription token that can be disposed to unsubscribe.</returns>
        public BusSubscription Subscribe<TSender, TMessage>(Action<TSender, TMessage> handler)
            where TMessage : struct, IBusMessage<TSender>
            => Get<TSender, TMessage>().Subscribe(handler);

        /// <summary>
        /// Publishes a message to all subscribers.
        /// </summary>
        /// <typeparam name="TSender">The type of the sender of the message.</typeparam>
        /// <typeparam name="TMessage">The type of message to publish.</typeparam>
        /// <param name="sender">The entity sending the message.</param>
        /// <param name="msg">The message to publish.</param>
        public void Send<TSender, TMessage>(TSender sender, in TMessage msg)
            where TMessage : struct, IBusMessage<TSender>
            => Get<TSender, TMessage>().Publish(sender, in msg, DebugToLog);

        /// <summary>
        /// Destroys the message bus, disposing all channels and unsubscribing all handlers.
        /// </summary>
        public void Destroy()
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var ch in _channels.Values)
                ch.Dispose();
            _channels.Clear();
        }
    }

    /// <summary>
    /// Extension methods for accessing the local message bus from entities.
    /// </summary>
    public static class LocalMessageBusExtensions
    {
        /// <summary>
        /// Gets or creates a local message bus singleton for the entity manager.
        /// </summary>
        /// <param name="ent">The entity to get the message bus from.</param>
        /// <returns>The local message bus instance, creating it if it doesn't exist.</returns>
        public static LocalMessageBus GetOrCreateLocalMessageBus(this InternalEntity ent)
        {
            var msgBus = ent.EntityManager.GetLocalSingleton<LocalMessageBus>();
            if(msgBus == null)
            {
                msgBus = new LocalMessageBus();
                ent.EntityManager.AddLocalSingleton<LocalMessageBus>(msgBus);
            }
            return msgBus;
        }

        /// <summary>
        /// Gets the local message bus singleton from the entity manager.
        /// </summary>
        /// <param name="ent">The entity to get the message bus from.</param>
        /// <returns>The local message bus instance, or null if not yet created.</returns>
        public static LocalMessageBus GetLocalMessageBus(this InternalEntity ent) =>
            ent.EntityManager.GetLocalSingleton<LocalMessageBus>();
    }
}