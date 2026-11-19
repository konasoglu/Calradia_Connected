using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace CalradiaConnected.Engine.Events
{
    /// <summary>
    /// Decoupled internal event bus (Message Broker) connecting game patches and UI hooks
    /// to network and campaign synchronization services without direct coupling.
    /// </summary>
    public interface IEventBus
    {
        void Subscribe<TEvent>(Action<TEvent> handler) where TEvent : class;
        void Unsubscribe<TEvent>(Action<TEvent> handler) where TEvent : class;
        void Publish<TEvent>(TEvent evt) where TEvent : class;
        void Clear();
    }

    public class EventBus : IEventBus
    {
        private readonly ConcurrentDictionary<Type, List<Delegate>> _subscribers = new ConcurrentDictionary<Type, List<Delegate>>();
        private readonly object _syncRoot = new object();

        public void Subscribe<TEvent>(Action<TEvent> handler) where TEvent : class
        {
            if (handler == null) return;
            var eventType = typeof(TEvent);

            lock (_syncRoot)
            {
                if (!_subscribers.TryGetValue(eventType, out var list))
                {
                    list = new List<Delegate>();
                    _subscribers[eventType] = list;
                }

                if (!list.Contains(handler))
                {
                    list.Add(handler);
                }
            }
        }

        public void Unsubscribe<TEvent>(Action<TEvent> handler) where TEvent : class
        {
            if (handler == null) return;
            var eventType = typeof(TEvent);

            lock (_syncRoot)
            {
                if (_subscribers.TryGetValue(eventType, out var list))
                {
                    list.Remove(handler);
                }
            }
        }

        public void Publish<TEvent>(TEvent evt) where TEvent : class
        {
            if (evt == null) return;
            var eventType = typeof(TEvent);

            Delegate[]? snapshot = null;
            lock (_syncRoot)
            {
                if (_subscribers.TryGetValue(eventType, out var list) && list.Count > 0)
                {
                    snapshot = list.ToArray();
                }
            }

            if (snapshot == null) return;

            for (int i = 0; i < snapshot.Length; i++)
            {
                try
                {
                    if (snapshot[i] is Action<TEvent> typedHandler)
                    {
                        typedHandler(evt);
                    }
                }
                catch (Exception ex)
                {
                    TaleWorlds.Library.Debug.PrintError($"[Calradia-Connected] EventBus handler error ({eventType.Name}): {ex.Message}");
                }
            }
        }

        public void Clear()
        {
            lock (_syncRoot)
            {
                _subscribers.Clear();
            }
        }
    }
}
