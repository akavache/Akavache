﻿// Copyright (c) 2019 .NET Foundation and Contributors. All rights reserved.
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Newtonsoft.Json;
using Newtonsoft.Json.Bson;
using Splat;

namespace Akavache
{
    /// <summary>
    /// This class is an IBlobCache backed by a simple in-memory Dictionary.
    /// Use it for testing / mocking purposes.
    /// </summary>
    public class InMemoryBlobCache : ISecureBlobCache, IObjectBlobCache, IEnableLogger
    {
        private readonly AsyncSubject<Unit> _shutdown = new AsyncSubject<Unit>();
        private readonly IDisposable _inner;
        private Dictionary<string, CacheEntry> _cache = new Dictionary<string, CacheEntry>();
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="InMemoryBlobCache"/> class.
        /// </summary>
        public InMemoryBlobCache()
            : this(null, null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="InMemoryBlobCache"/> class.
        /// </summary>
        /// <param name="scheduler">The scheduler to use for Observable based operations.</param>
        public InMemoryBlobCache(IScheduler scheduler)
            : this(scheduler, null)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="InMemoryBlobCache"/> class.
        /// </summary>
        /// <param name="initialContents">The initial contents of the cache.</param>
        public InMemoryBlobCache(IEnumerable<KeyValuePair<string, byte[]>> initialContents)
            : this(null, initialContents)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="InMemoryBlobCache"/> class.
        /// </summary>
        /// <param name="scheduler">The scheduler to use for Observable based operations.</param>
        /// <param name="initialContents">The initial contents of the cache.</param>
        public InMemoryBlobCache(IScheduler scheduler, IEnumerable<KeyValuePair<string, byte[]>> initialContents)
        {
            Scheduler = scheduler ?? CurrentThreadScheduler.Instance;
            foreach (var item in initialContents ?? Enumerable.Empty<KeyValuePair<string, byte[]>>())
            {
                _cache[item.Key] = new CacheEntry(null, item.Value, Scheduler.Now, null);
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="InMemoryBlobCache"/> class.
        /// </summary>
        /// <param name="disposer">A action that is called to dispose contents.</param>
        /// <param name="scheduler">The scheduler to use for Observable based operations.</param>
        /// <param name="initialContents">The initial contents of the cache.</param>
        internal InMemoryBlobCache(
            Action disposer,
            IScheduler scheduler,
            IEnumerable<KeyValuePair<string, byte[]>> initialContents)
            : this(scheduler, initialContents)
        {
            _inner = Disposable.Create(disposer);
        }

        /// <summary>
        /// Gets or sets the scheduler to dispatch observable operations on.
        /// </summary>
        public IScheduler Scheduler { get; protected set; }

        /// <summary>
        /// Gets a observable that signals when a shutdown is going to happen.
        /// </summary>
        public IObservable<Unit> Shutdown => _shutdown;

        /// <summary>
        /// Overrides the global registrations with specified values.
        /// </summary>
        /// <param name="scheduler">The default scheduler to use.</param>
        /// <param name="initialContents">The default inner contents to use.</param>
        /// <returns>A generated cache.</returns>
        public static InMemoryBlobCache OverrideGlobals(IScheduler scheduler = null, params KeyValuePair<string, byte[]>[] initialContents)
        {
            var local = BlobCache.LocalMachine;
            var user = BlobCache.UserAccount;
            var sec = BlobCache.Secure;

            var resetBlobCache = new Action(() =>
            {
                BlobCache.LocalMachine = local;
                BlobCache.Secure = sec;
                BlobCache.UserAccount = user;
            });

            var testCache = new InMemoryBlobCache(resetBlobCache, scheduler, initialContents);
            BlobCache.LocalMachine = testCache;
            BlobCache.Secure = testCache;
            BlobCache.UserAccount = testCache;

            return testCache;
        }

        /// <summary>
        /// Overrides the global registrations with specified values.
        /// </summary>
        /// <param name="initialContents">The default inner contents to use.</param>
        /// <param name="scheduler">The default scheduler to use.</param>
        /// <returns>A generated cache.</returns>
        public static InMemoryBlobCache OverrideGlobals(IDictionary<string, byte[]> initialContents, IScheduler scheduler = null)
        {
            return OverrideGlobals(scheduler, initialContents.ToArray());
        }

        /// <summary>
        /// Overrides the global registrations with specified values.
        /// </summary>
        /// <param name="initialContents">The default inner contents to use.</param>
        /// <param name="scheduler">The default scheduler to use.</param>
        /// <returns>A generated cache.</returns>
        public static InMemoryBlobCache OverrideGlobals(IDictionary<string, object> initialContents, IScheduler scheduler = null)
        {
            var initialSerializedContents = initialContents
                .Select(item => new KeyValuePair<string, byte[]>(item.Key, JsonSerializationMixin.SerializeObject(item.Value)))
                .ToArray();

            return OverrideGlobals(scheduler, initialSerializedContents);
        }

        /// <inheritdoc />
        public IObservable<Unit> Insert(string key, byte[] data, DateTimeOffset? absoluteExpiration = null)
        {
            if (_disposed)
            {
                return ExceptionHelper.ObservableThrowObjectDisposedException<Unit>("InMemoryBlobCache");
            }

            lock (_cache)
            {
                _cache[key] = new CacheEntry(null, data, Scheduler.Now, absoluteExpiration);
            }

            return Observable.Return(Unit.Default);
        }

        /// <inheritdoc />
        public IObservable<Unit> Flush()
        {
            if (_disposed)
            {
                return ExceptionHelper.ObservableThrowObjectDisposedException<Unit>("InMemoryBlobCache");
            }

            return Observable.Return(Unit.Default);
        }

        /// <inheritdoc />
        public IObservable<byte[]> Get(string key)
        {
            if (_disposed)
            {
                return ExceptionHelper.ObservableThrowObjectDisposedException<byte[]>("InMemoryBlobCache");
            }

            CacheEntry entry;
            lock (_cache)
            {
                if (!_cache.TryGetValue(key, out entry))
                {
                    return ExceptionHelper.ObservableThrowKeyNotFoundException<byte[]>(key);
                }
            }

            if (entry.ExpiresAt != null && Scheduler.Now > entry.ExpiresAt.Value)
            {
                lock (_cache)
                {
                    _cache.Remove(key);
                }

                return ExceptionHelper.ObservableThrowKeyNotFoundException<byte[]>(key);
            }

            return Observable.Return(entry.Value, Scheduler);
        }

        /// <inheritdoc />
        public IObservable<DateTimeOffset?> GetCreatedAt(string key)
        {
            if (_disposed)
            {
                return ExceptionHelper.ObservableThrowObjectDisposedException<DateTimeOffset?>("InMemoryBlobCache");
            }

            CacheEntry entry;
            lock (_cache)
            {
                if (!_cache.TryGetValue(key, out entry))
                {
                    return Observable.Return<DateTimeOffset?>(null);
                }
            }

            return Observable.Return<DateTimeOffset?>(entry.CreatedAt, Scheduler);
        }

        /// <inheritdoc />
        public IObservable<IEnumerable<string>> GetAllKeys()
        {
            if (_disposed)
            {
                return ExceptionHelper.ObservableThrowObjectDisposedException<List<string>>("InMemoryBlobCache");
            }

            lock (_cache)
            {
                return Observable.Return(_cache
                    .Where(x => x.Value.ExpiresAt == null || x.Value.ExpiresAt >= Scheduler.Now)
                    .Select(x => x.Key)
                    .ToList());
            }
        }

        /// <inheritdoc />
        public IObservable<Unit> Invalidate(string key)
        {
            if (_disposed)
            {
                return ExceptionHelper.ObservableThrowObjectDisposedException<Unit>("InMemoryBlobCache");
            }

            lock (_cache)
            {
                _cache.Remove(key);
            }

            return Observable.Return(Unit.Default);
        }

        /// <inheritdoc />
        public IObservable<Unit> InvalidateAll()
        {
            if (_disposed)
            {
                return ExceptionHelper.ObservableThrowObjectDisposedException<Unit>("InMemoryBlobCache");
            }

            lock (_cache)
            {
                _cache.Clear();
            }

            return Observable.Return(Unit.Default);
        }

        /// <inheritdoc />
        public IObservable<Unit> InsertObject<T>(string key, T value, DateTimeOffset? absoluteExpiration = null)
        {
            if (_disposed)
            {
                return ExceptionHelper.ObservableThrowObjectDisposedException<Unit>("InMemoryBlobCache");
            }

            var data = SerializeObject(value);

            lock (_cache)
            {
                _cache[key] = new CacheEntry(typeof(T).FullName, data, Scheduler.Now, absoluteExpiration);
            }

            return Observable.Return(Unit.Default);
        }

        /// <inheritdoc />
        public IObservable<T> GetObject<T>(string key)
        {
            if (_disposed)
            {
                return ExceptionHelper.ObservableThrowObjectDisposedException<T>("InMemoryBlobCache");
            }

            CacheEntry entry;
            lock (_cache)
            {
                if (!_cache.TryGetValue(key, out entry))
                {
                    return ExceptionHelper.ObservableThrowKeyNotFoundException<T>(key);
                }
            }

            if (entry.ExpiresAt != null && Scheduler.Now > entry.ExpiresAt.Value)
            {
                lock (_cache)
                {
                    _cache.Remove(key);
                }

                return ExceptionHelper.ObservableThrowKeyNotFoundException<T>(key);
            }

            T obj = DeserializeObject<T>(entry.Value);

            return Observable.Return(obj, Scheduler);
        }

        /// <inheritdoc />
        public IObservable<DateTimeOffset?> GetObjectCreatedAt<T>(string key)
        {
            return GetCreatedAt(key);
        }

        /// <inheritdoc />
        public IObservable<IEnumerable<T>> GetAllObjects<T>()
        {
            if (_disposed)
            {
                return ExceptionHelper.ObservableThrowObjectDisposedException<IEnumerable<T>>("InMemoryBlobCache");
            }

            lock (_cache)
            {
                return Observable.Return(
                    _cache
                        .Where(x => x.Value.TypeName == typeof(T).FullName && (x.Value.ExpiresAt == null || x.Value.ExpiresAt >= Scheduler.Now))
                        .Select(x => DeserializeObject<T>(x.Value.Value))
                        .ToList(), Scheduler);
            }
        }

        /// <inheritdoc />
        public IObservable<Unit> InvalidateObject<T>(string key)
        {
            if (_disposed)
            {
                return ExceptionHelper.ObservableThrowObjectDisposedException<Unit>("InMemoryBlobCache");
            }

            return Invalidate(key);
        }

        /// <inheritdoc />
        public IObservable<Unit> InvalidateAllObjects<T>()
        {
            if (_disposed)
            {
                return ExceptionHelper.ObservableThrowObjectDisposedException<Unit>("InMemoryBlobCache");
            }

            lock (_cache)
            {
                var toDelete = _cache.Where(x => x.Value.TypeName == typeof(T).FullName).ToArray();
                foreach (var obj in toDelete)
                {
                    _cache.Remove(obj.Key);
                }
            }

            return Observable.Return(Unit.Default);
        }

        /// <inheritdoc />
        public IObservable<Unit> Vacuum()
        {
            if (_disposed)
            {
                return ExceptionHelper.ObservableThrowObjectDisposedException<Unit>("InMemoryBlobCache");
            }

            lock (_cache)
            {
                var toDelete = _cache.Where(x => x.Value.ExpiresAt >= Scheduler.Now);
                foreach (var kvp in toDelete)
                {
                    _cache.Remove(kvp.Key);
                }
            }

            return Observable.Return(Unit.Default);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Disposes of the managed memory inside the class.
        /// </summary>
        /// <param name="isDisposing">If this is being called by the Dispose method.</param>
        protected virtual void Dispose(bool isDisposing)
        {
            if (_disposed)
            {
                return;
            }

            if (isDisposing)
            {
                Scheduler = null;
                lock (_cache)
                {
                    _cache = null;
                }

                _inner?.Dispose();

                _shutdown.OnNext(Unit.Default);
                _shutdown.OnCompleted();
            }

            _disposed = true;
        }

        private byte[] SerializeObject<T>(T value)
        {
            var settings = Locator.Current.GetService<JsonSerializerSettings>() ?? new JsonSerializerSettings();
            settings.ContractResolver = new JsonDateTimeContractResolver(settings?.ContractResolver); // This will make us use ticks instead of json ticks for DateTime.
            var ms = new MemoryStream();
            var serializer = JsonSerializer.Create(settings);
            var writer = new BsonDataWriter(ms);

            serializer.Serialize(writer, new ObjectWrapper<T> { Value = value });
            return ms.ToArray();
        }

        private T DeserializeObject<T>(byte[] data)
        {
            var settings = Locator.Current.GetService<JsonSerializerSettings>() ?? new JsonSerializerSettings();
            settings.ContractResolver = new JsonDateTimeContractResolver(settings?.ContractResolver); // This will make us use ticks instead of json ticks for DateTime.
            var serializer = JsonSerializer.Create(settings);
            var reader = new BsonDataReader(new MemoryStream(data));
            var forcedDateTimeKind = BlobCache.ForcedDateTimeKind;

            if (forcedDateTimeKind.HasValue)
            {
                reader.DateTimeKindHandling = forcedDateTimeKind.Value;
            }

            try
            {
                return serializer.Deserialize<ObjectWrapper<T>>(reader).Value;
            }
            catch (Exception ex)
            {
                this.Log().WarnException("Failed to deserialize data as boxed, we may be migrating from an old Akavache", ex);
            }

            return serializer.Deserialize<T>(reader);
        }
    }
}
