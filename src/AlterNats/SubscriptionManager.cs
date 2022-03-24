﻿using AlterNats.Internal;
using System.Buffers;
using System.Collections.Concurrent;

namespace AlterNats;

internal sealed class SubscriptionManager : IDisposable
{
    internal readonly object gate = new object(); // lock for add/remove, publish can avoid lock.
    readonly ConcurrentDictionary<int, RefCountSubscription> bySubscriptionId = new();
    readonly ConcurrentDictionary<string, RefCountSubscription> byStringKey = new();

    internal readonly NatsConnection connection;

    int subscriptionId = 0; // unique alphanumeric subscription ID, generated by the client(per connection).

    public SubscriptionManager(NatsConnection connection)
    {
        this.connection = connection;
    }

    public IDisposable Add<T>(string key, Action<T> handler)
    {
        lock (gate)
        {
            if (byStringKey.TryGetValue(key, out var subscription))
            {
                if (subscription.ElementType != typeof(T))
                {
                    throw new InvalidOperationException($"Register different type on same key. RegisteredType:{subscription.ElementType.FullName} NewType:{typeof(T).FullName}");
                }

                var handlerId = subscription.AddHandler(handler);
                return new Subscription(subscription, handlerId);
            }
            else
            {
                var sid = Interlocked.Increment(ref subscriptionId);

                subscription = new RefCountSubscription(this, sid, key, typeof(T));
                var handlerId = subscription.AddHandler(handler);
                bySubscriptionId[sid] = subscription;
                byStringKey[key] = subscription;

                connection.PostSubscribe(sid, key);
                return new Subscription(subscription, handlerId);
            }
        }
    }

    internal void Remove(string key, int subscriptionId)
    {
        // inside lock from RefCountSubscription.RemoveHandler
        byStringKey.Remove(key, out _);
        bySubscriptionId.Remove(subscriptionId, out _);
    }

    public void PublishToClientHandlers(int subscriptionId, ReadOnlySequence<byte> buffer)
    {
        if (bySubscriptionId.TryGetValue(subscriptionId, out var subscription))
        {
            var list = subscription.Handlers.GetValues();
            if (list.Length != 0)
            {
                var item = PublishCallbackThreadPoolWorkItemFactory.Create(subscription.ElementType, connection.Options, buffer, list);
                ThreadPool.UnsafeQueueUserWorkItem(item, preferLocal: false);
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            // remove all references.
            foreach (var item in bySubscriptionId)
            {
                item.Value.Handlers.Dispose();
            }

            bySubscriptionId.Clear();
            byStringKey.Clear();
        }
    }

    sealed class Subscription : IDisposable
    {
        readonly RefCountSubscription rootSubscription;
        readonly int handlerId;
        bool isDisposed;

        public Subscription(RefCountSubscription rootSubscription, int handlerId)
        {
            this.rootSubscription = rootSubscription;
            this.handlerId = handlerId;
        }

        void IDisposable.Dispose()
        {
            if (!isDisposed)
            {
                isDisposed = true;
                rootSubscription.RemoveHandler(handlerId);
            }
        }
    }
}

internal sealed class RefCountSubscription
{
    // All operation exclude Handlers.GetValues inside in lock

    readonly SubscriptionManager manager;

    public int SubscriptionId { get; }
    public string Key { get; }
    public int ReferenceCount { get; private set; }
    public Type ElementType { get; }
    public FreeList<object> Handlers { get; }

    public RefCountSubscription(SubscriptionManager manager, int subscriptionId, string key, Type elementType)
    {
        this.manager = manager;
        SubscriptionId = subscriptionId;
        Key = key;
        ReferenceCount = 0;
        ElementType = elementType;
        Handlers = new FreeList<object>();
    }

    public int AddHandler(object handler)
    {
        var id = Handlers.Add(handler);
        ReferenceCount++;
        return id;
    }

    public void RemoveHandler(int handlerId)
    {
        lock (manager.gate)
        {
            ReferenceCount--;
            Handlers.Remove(handlerId, false);
            if (ReferenceCount == 0)
            {
                manager.Remove(Key, SubscriptionId);
                Handlers.Dispose();
                manager.connection.PostUnsubscribe(SubscriptionId);
            }
        }
    }
}