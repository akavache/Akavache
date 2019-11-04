// Copyright (c) 2019 .NET Foundation and Contributors. All rights reserved.
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading;
using Akavache.Sqlite3.Internal;
using Newtonsoft.Json;
using Newtonsoft.Json.Bson;
using Splat;

namespace Akavache.Sqlite3
{
    /// <summary>
    /// This class represents an IBlobCache backed by a SQLite3 database, and
    /// it is the default (and best!) implementation.
    /// </summary>
    public class SqlRawPersistentBlobCache : IObjectBlobCache, IEnableLogger, IObjectBulkBlobCache
    {
        private static readonly object DisposeGate = 42;
        private readonly IObservable<Unit> _initializer;
        [SuppressMessage("Design", "CA2213: Dispose field", Justification = "Used to indicate disposal.")]
        private readonly AsyncSubject<Unit> _shutdown = new AsyncSubject<Unit>();
        private SqliteOperationQueue _opQueue;
        private IDisposable _queueThread;
        private DateTimeKind? _dateTimeKind;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="SqlRawPersistentBlobCache"/> class.
        /// </summary>
        /// <param name="databaseFile">The location of the database file.</param>
        /// <param name="scheduler">The scheduler to perform operations on.</param>
        public SqlRawPersistentBlobCache(string databaseFile, IScheduler scheduler = null)
        {
            Scheduler = scheduler ?? BlobCache.TaskpoolScheduler;

            BlobCache.EnsureInitialized();

            Connection = new SQLiteConnection(databaseFile, storeDateTimeAsTicks: true);
            _initializer = Initialize();
        }

        /// <inheritdoc />
        public DateTimeKind? ForcedDateTimeKind
        {
            get => _dateTimeKind ?? BlobCache.ForcedDateTimeKind;
            set => _dateTimeKind = value;
        }

        /// <inheritdoc />
        public IScheduler Scheduler { get; }

        /// <summary>
        /// Gets the connection to the sqlite file.
        /// </summary>
        public SQLiteConnection Connection { get; }

        /// <inheritdoc />
        public IObservable<Unit> Shutdown => _shutdown;

        /// <inheritdoc />
        public IObservable<Unit> Insert(string key, byte[] data, DateTimeOffset? absoluteExpiration = null)
        {
            if (_disposed)
            {
                return ExceptionHelper.ObservableThrowObjectDisposedException<Unit>("SqlitePersistentBlobCache");
            }

            if (key == null)
            {
                return Observable.Throw<Unit>(new ArgumentNullException(nameof(key)));
            }

            if (data == null)
            {
                return Observable.Throw<Unit>(new ArgumentNullException(nameof(data)));
            }

            var exp = (absoluteExpiration ?? DateTimeOffset.MaxValue).UtcDateTime;
            var createdAt = Scheduler.Now.UtcDateTime;

            return _initializer
                .SelectMany(_ => BeforeWriteToDiskFilter(data, Scheduler))
                .SelectMany(encData => _opQueue.Insert(new[]
                {
                    new CacheElement
                    {
                        Key = key,
                        Value = encData,
                        CreatedAt = createdAt,
                        Expiration = exp,
                    },
                }))
                .PublishLast().PermaRef();
        }

        /// <inheritdoc />
        public IObservable<byte[]> Get(string key)
        {
            if (_disposed)
            {
                return ExceptionHelper.ObservableThrowObjectDisposedException<byte[]>("SqlitePersistentBlobCache");
            }

            if (key == null)
            {
                return Observable.Throw<byte[]>(new ArgumentNullException(nameof(key)));
            }

            return _initializer.SelectMany(_ => _opQueue.Select(new[] { key }))
                .SelectMany(x =>
                {
                    var cacheElements = x.ToList();
                    return cacheElements.Count == 1
                            ? Observable.Return(cacheElements.First().Value)
                            : ExceptionHelper.ObservableThrowKeyNotFoundException<byte[]>(key);
                })
                .SelectMany(x => AfterReadFromDiskFilter(x, Scheduler))
                .PublishLast().PermaRef();
        }

        /// <inheritdoc />
        public IObservable<IEnumerable<string>> GetAllKeys()
        {
            if (_disposed)
            {
                return ExceptionHelper.ObservableThrowObjectDisposedException<List<string>>("SqlitePersistentBlobCache");
            }

            return _initializer.SelectMany(_ => _opQueue.GetAllKeys())
                .PublishLast().PermaRef();
        }

        /// <inheritdoc />
        public IObservable<DateTimeOffset?> GetCreatedAt(string key)
        {
            if (_disposed)
            {
                return ExceptionHelper.ObservableThrowObjectDisposedException<DateTimeOffset?>("SqlitePersistentBlobCache");
            }

            if (key == null)
            {
                return Observable.Throw<DateTimeOffset?>(new ArgumentNullException(nameof(key)));
            }

            return _initializer.SelectMany(_ => _opQueue.Select(new[] { key }))
                .Select(x =>
                {
                    var cacheElements = x.ToList();
                    return cacheElements.Count == 1
                            ? (DateTimeOffset?)new DateTimeOffset(cacheElements[0].CreatedAt, TimeSpan.Zero)
                            : default(DateTimeOffset?);
                })
                .PublishLast().PermaRef();
        }

        /// <inheritdoc />
        public IObservable<DateTimeOffset?> GetObjectCreatedAt<T>(string key)
        {
            return GetCreatedAt(key);
        }

        /// <inheritdoc />
        public IObservable<Unit> Flush()
        {
            if (_disposed)
            {
                return ExceptionHelper.ObservableThrowObjectDisposedException<Unit>("SqlitePersistentBlobCache");
            }

            return _initializer.SelectMany(_ => _opQueue.Flush())
                .PublishLast().PermaRef();
        }

        /// <inheritdoc />
        public IObservable<Unit> Invalidate(string key)
        {
            if (_disposed)
            {
                return ExceptionHelper.ObservableThrowObjectDisposedException<Unit>("SqlitePersistentBlobCache");
            }

            return _initializer.SelectMany(_ => _opQueue.Invalidate(new[] { key }))
                .PublishLast().PermaRef();
        }

        /// <inheritdoc />
        public IObservable<Unit> InvalidateAll()
        {
            if (_disposed)
            {
                return ExceptionHelper.ObservableThrowObjectDisposedException<Unit>("SqlitePersistentBlobCache");
            }

            return _initializer.SelectMany(_ => _opQueue.InvalidateAll())
                .PublishLast().PermaRef();
        }

        /// <inheritdoc />
        public IObservable<Unit> InsertObject<T>(string key, T value, DateTimeOffset? absoluteExpiration = null)
        {
            if (_disposed)
            {
                return ExceptionHelper.ObservableThrowObjectDisposedException<Unit>("SqlitePersistentBlobCache");
            }

            if (key == null)
            {
                return Observable.Throw<Unit>(new ArgumentNullException(nameof(key)));
            }

            var data = SerializeObject(value);
            var exp = (absoluteExpiration ?? DateTimeOffset.MaxValue).UtcDateTime;
            var createdAt = Scheduler.Now.UtcDateTime;

            return _initializer
                .SelectMany(_ => BeforeWriteToDiskFilter(data, Scheduler))
                .SelectMany(encData => _opQueue.Insert(new[]
                {
                    new CacheElement
                    {
                        Key = key,
                        TypeName = typeof(T).FullName,
                        Value = encData,
                        CreatedAt = createdAt,
                        Expiration = exp,
                    },
                }))
                .PublishLast().PermaRef();
        }

        /// <inheritdoc />
        public IObservable<T> GetObject<T>(string key)
        {
            if (_disposed)
            {
                return ExceptionHelper.ObservableThrowObjectDisposedException<T>("SqlitePersistentBlobCache");
            }

            if (key == null)
            {
                return Observable.Throw<T>(new ArgumentNullException(nameof(key)));
            }

            return _initializer.SelectMany(_ => _opQueue.Select(new[] { key }))
                .SelectMany(x =>
                {
                    var cacheElements = x.ToList();
                    return cacheElements.Count == 1
                            ? Observable.Return(cacheElements.First().Value)
                            : ExceptionHelper.ObservableThrowKeyNotFoundException<byte[]>(key);
                })
                .SelectMany(x => AfterReadFromDiskFilter(x, Scheduler))
                .SelectMany(DeserializeObject<T>)
                .PublishLast().PermaRef();
        }

        /// <inheritdoc />
        public IObservable<IEnumerable<T>> GetAllObjects<T>()
        {
            if (_disposed)
            {
                return ExceptionHelper.ObservableThrowObjectDisposedException<IEnumerable<T>>("SqlitePersistentBlobCache");
            }

            return _initializer.SelectMany(_ => _opQueue.SelectTypes(new[] { typeof(T).FullName })
                    .SelectMany(x => x.ToObservable()
                        .SelectMany(y => AfterReadFromDiskFilter(y.Value, Scheduler))
                        .SelectMany(DeserializeObject<T>)
                        .ToList()))
                .PublishLast().PermaRef();
        }

        /// <inheritdoc />
        public IObservable<Unit> InvalidateObject<T>(string key)
        {
            if (_disposed)
            {
                return ExceptionHelper.ObservableThrowObjectDisposedException<Unit>("SqlitePersistentBlobCache");
            }

            return Invalidate(key);
        }

        /// <inheritdoc />
        public IObservable<Unit> InvalidateAllObjects<T>()
        {
            if (_disposed)
            {
                return ExceptionHelper.ObservableThrowObjectDisposedException<Unit>("SqlitePersistentBlobCache");
            }

            return _initializer.SelectMany(_ => _opQueue.InvalidateTypes(new[] { typeof(T).FullName }))
                .PublishLast().PermaRef();
        }

        /// <inheritdoc />
        public IObservable<Unit> Vacuum()
        {
            if (_disposed)
            {
                return ExceptionHelper.ObservableThrowObjectDisposedException<Unit>("SqlitePersistentBlobCache");
            }

            return _initializer.SelectMany(_ => _opQueue.Vacuum())
                .PublishLast().PermaRef();
        }

        /// <inheritdoc />
        public IObservable<Unit> Insert(IDictionary<string, byte[]> keyValuePairs, DateTimeOffset? absoluteExpiration = null)
        {
            if (_disposed)
            {
                return ExceptionHelper.ObservableThrowObjectDisposedException<Unit>("SqlitePersistentBlobCache");
            }

            if (keyValuePairs == null)
            {
                return Observable.Throw<Unit>(new ArgumentNullException(nameof(keyValuePairs)));
            }

            var exp = (absoluteExpiration ?? DateTimeOffset.MaxValue).UtcDateTime;
            var createdAt = Scheduler.Now.UtcDateTime;

            return _initializer
                .SelectMany(_ => keyValuePairs.Select(x => BeforeWriteToDiskFilter(x.Value, Scheduler).Select(data => (key: x.Key, data: data))))
                .Merge().ToList()
                .SelectMany(list => _opQueue.Insert(list.Select(data =>
                        new CacheElement
                        {
                            Key = data.key,
                            Value = data.data,
                            CreatedAt = createdAt,
                            Expiration = exp,
                        })))
                .PublishLast().PermaRef();
        }

        /// <inheritdoc />
        public IObservable<IDictionary<string, byte[]>> Get(IEnumerable<string> keys)
        {
            if (_disposed)
            {
                return ExceptionHelper.ObservableThrowObjectDisposedException<IDictionary<string, byte[]>>("SqlitePersistentBlobCache");
            }

            if (keys == null)
            {
                return Observable.Throw<IDictionary<string, byte[]>>(new ArgumentNullException(nameof(keys)));
            }

            return _initializer
                .SelectMany(_ => _opQueue.Select(keys))
                .SelectMany(x =>
                {
                    var cacheElements = x.ToList();
                    return Observable.Return(cacheElements.ToDictionary(element => element.Key, element => element.Value));
                })
                .SelectMany(dict => dict.Select(x => AfterReadFromDiskFilter(x.Value, Scheduler).Select(data => (key: x.Key, data: data))))
                .Merge()
                .ToDictionary(x => x.key, x => x.data)
                .PublishLast().PermaRef();
        }

        /// <inheritdoc />
        public IObservable<IDictionary<string, DateTimeOffset?>> GetCreatedAt(IEnumerable<string> keys)
        {
            if (_disposed)
            {
                return ExceptionHelper.ObservableThrowObjectDisposedException<IDictionary<string, DateTimeOffset?>>("SqlitePersistentBlobCache");
            }

            if (keys == null)
            {
                return Observable.Throw<IDictionary<string, DateTimeOffset?>>(new ArgumentNullException(nameof(keys)));
            }

            return _initializer.SelectMany(_ => _opQueue.Select(keys))
                .Select(x =>
                {
                    var cacheElements = x.ToList();
                    return cacheElements.ToDictionary(element => element.Key, element => (DateTimeOffset?)new DateTimeOffset(element.CreatedAt, TimeSpan.Zero));
                })
                .PublishLast().PermaRef();
        }

        /// <inheritdoc />
        public IObservable<Unit> Invalidate(IEnumerable<string> keys)
        {
            if (_disposed)
            {
                return ExceptionHelper.ObservableThrowObjectDisposedException<Unit>("SqlitePersistentBlobCache");
            }

            return _initializer.SelectMany(_ => _opQueue.Invalidate(keys))
                .PublishLast().PermaRef();
        }

        /// <inheritdoc />
        public IObservable<Unit> InsertObjects<T>(IDictionary<string, T> keyValuePairs, DateTimeOffset? absoluteExpiration = null)
        {
            if (_disposed)
            {
                return ExceptionHelper.ObservableThrowObjectDisposedException<Unit>("SqlitePersistentBlobCache");
            }

            if (keyValuePairs == null)
            {
                return Observable.Throw<Unit>(new ArgumentNullException(nameof(keyValuePairs)));
            }

            var dataToAdd = keyValuePairs.Select(x => (key: x.Key, value: SerializeObject(x.Value)));
            var exp = (absoluteExpiration ?? DateTimeOffset.MaxValue).UtcDateTime;
            var createdAt = Scheduler.Now.UtcDateTime;

            return _initializer
                .SelectMany(_ => dataToAdd.Select(x => BeforeWriteToDiskFilter(x.value, Scheduler).Select(data => (key: x.key, data: data))))
                .Merge().ToList()
                .SelectMany(list => _opQueue.Insert(list.Select(data =>
                    new CacheElement
                    {
                        Key = data.key,
                        TypeName = typeof(T).FullName,
                        Value = data.data,
                        CreatedAt = createdAt,
                        Expiration = exp,
                    })))
                .PublishLast().PermaRef();
        }

        /// <inheritdoc />
        public IObservable<IDictionary<string, T>> GetObjects<T>(IEnumerable<string> keys)
        {
            if (_disposed)
            {
                return ExceptionHelper.ObservableThrowObjectDisposedException<IDictionary<string, T>>("SqlitePersistentBlobCache");
            }

            if (keys == null)
            {
                return Observable.Throw<IDictionary<string, T>>(new ArgumentNullException(nameof(keys)));
            }

            return _initializer.SelectMany(_ => _opQueue.Select(keys))
                .SelectMany(
                    x =>
                    {
                        var cacheElements = x.ToList();
                        return Observable.Return(cacheElements.ToDictionary(element => element.Key, element => element.Value));
                    })
                .SelectMany(dict => dict.Select(x => AfterReadFromDiskFilter(x.Value, Scheduler).Select(data => (key: x.Key, data: data))))
                .Merge()
                .SelectMany(x => DeserializeObject<T>(x.data).Select(obj => (key: x.key, data: obj)))
                .ToDictionary(x => x.key, x => x.data)
                .PublishLast().PermaRef();
        }

        /// <inheritdoc />
        public IObservable<Unit> InvalidateObjects<T>(IEnumerable<string> keys)
        {
            if (_disposed)
            {
                return ExceptionHelper.ObservableThrowObjectDisposedException<Unit>("SqlitePersistentBlobCache");
            }

            return Invalidate(keys);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        internal void ReplaceOperationQueue(SqliteOperationQueue queue)
        {
            _initializer.Wait();

            _opQueue.Dispose();

            _opQueue = queue;
            _opQueue.Start();
        }

        /// <summary>
        /// Disposes of any managed objects that are IDisposable.
        /// </summary>
        /// <param name="isDisposing">If the method is being called by the Dispose() method.</param>
        protected virtual void Dispose(bool isDisposing)
        {
            if (_disposed)
            {
                return;
            }

            if (isDisposing)
            {
                var disp = Interlocked.Exchange(ref _queueThread, null);
                if (disp == null)
                {
                    return;
                }

                var cleanup = Observable.Start(
                    () =>
                    {
                        // NB: While we intentionally dispose the operation queue
                        // from a background thread so that we don't park the UI
                        // while we're waiting for background operations to
                        // complete, we must serialize calls to sqlite3_close or
                        // else SQLite3 will start throwing back "busy" at us.
                        //
                        // We intentionally serialize even the shutdown of the
                        // background queue to be extra paranoid about not getting
                        // 'busy' while cleaning up.
                        lock (DisposeGate)
                        {
                            disp.Dispose();
                            _opQueue.Dispose();
                            Connection.Dispose();
                        }
                    }, Scheduler);

                cleanup.Multicast(_shutdown).PermaRef();
            }

            _disposed = true;
        }

        /// <summary>
        /// Initializes the cache.
        /// </summary>
        /// <returns>An observable that signals when the initialization is complete.</returns>
        protected IObservable<Unit> Initialize()
        {
            var ret = Observable.Create<Unit>(subj =>
            {
                // NB: This is in its own try block because depending on the
                // platform, we may not have a modern SQLite3, where these
                // PRAGMAs are supported. These aren't critical, so let them
                // fail silently
                try
                {
                    // NB: Setting journal_mode returns a row, nfi
                    Connection.ExecuteScalar<int>("PRAGMA journal_mode=WAL");
                    Connection.Execute("PRAGMA temp_store=MEMORY");
                    Connection.Execute("PRAGMA synchronous=OFF");
                }
                catch (SQLiteException)
                {
                }

                try
                {
                    Connection.CreateTable<CacheElement>();

                    var schemaVersion = GetSchemaVersion();

                    if (schemaVersion < 2)
                    {
                        Connection.Execute("ALTER TABLE CacheElement RENAME TO VersionOneCacheElement;");
                        Connection.CreateTable<CacheElement>();

                        var sql = "INSERT INTO CacheElement SELECT Key,TypeName,Value,Expiration,\"{0}\" AS CreatedAt FROM VersionOneCacheElement;";
                        Connection.Execute(string.Format(CultureInfo.InvariantCulture, sql, BlobCache.TaskpoolScheduler.Now.UtcDateTime.Ticks));
                        Connection.Execute("DROP TABLE VersionOneCacheElement;");

                        Connection.Insert(new SchemaInfo { Version = 2, });
                    }

                    // NB: We have to do this here because you can't prepare
                    // statements until you've got the backing table
                    _opQueue = new SqliteOperationQueue(Connection, Scheduler);
                    _queueThread = _opQueue.Start();

                    subj.OnNext(Unit.Default);
                    subj.OnCompleted();
                }
                catch (Exception ex)
                {
                    subj.OnError(ex);
                }

                return Disposable.Empty;
            });

            return ret.PublishLast().PermaRef();
        }

        /// <summary>
        /// Gets the current version of schema being used.
        /// </summary>
        /// <returns>The version of the schema.</returns>
        protected int GetSchemaVersion()
        {
            bool shouldCreateSchemaTable = false;
            int versionNumber = 0;

            try
            {
                versionNumber = Connection.ExecuteScalar<int>("SELECT Version from SchemaInfo ORDER BY Version DESC LIMIT 1");
            }
            catch (Exception)
            {
                shouldCreateSchemaTable = true;
            }

            if (shouldCreateSchemaTable)
            {
                Connection.CreateTable<SchemaInfo>();
                versionNumber = 1;
            }

            return versionNumber;
        }

        /// <summary>
        /// This method is called immediately before writing any data to disk.
        /// Override this in encrypting data stores in order to encrypt the
        /// data.
        /// </summary>
        /// <param name="data">The byte data about to be written to disk.</param>
        /// <param name="scheduler">The scheduler to use if an operation has
        /// to be deferred. If the operation can be done immediately, use
        /// Observable.Return and ignore this parameter.</param>
        /// <returns>A Future result representing the encrypted data.</returns>
        protected virtual IObservable<byte[]> BeforeWriteToDiskFilter(byte[] data, IScheduler scheduler)
        {
            if (_disposed)
            {
                return ExceptionHelper.ObservableThrowObjectDisposedException<byte[]>("SqlitePersistentBlobCache");
            }

            return Observable.Return(data, scheduler);
        }

        /// <summary>
        /// This method is called immediately after reading any data to disk.
        /// Override this in encrypting data stores in order to decrypt the
        /// data.
        /// </summary>
        /// <param name="data">The byte data that has just been read from
        /// disk.</param>
        /// <param name="scheduler">The scheduler to use if an operation has
        /// to be deferred. If the operation can be done immediately, use
        /// Observable.Return and ignore this parameter.</param>
        /// <returns>A Future result representing the decrypted data.</returns>
        protected virtual IObservable<byte[]> AfterReadFromDiskFilter(byte[] data, IScheduler scheduler)
        {
            if (_disposed)
            {
                return ExceptionHelper.ObservableThrowObjectDisposedException<byte[]>("SqlitePersistentBlobCache");
            }

            return Observable.Return(data, scheduler);
        }

        private byte[] SerializeObject<T>(T value)
        {
            var settings = Locator.Current.GetService<JsonSerializerSettings>() ?? new JsonSerializerSettings();
            settings.ContractResolver = new JsonDateTimeContractResolver(settings.ContractResolver, ForcedDateTimeKind); // This will make us use ticks instead of json ticks for DateTime.
            var serializer = JsonSerializer.Create(settings);
            using (var ms = new MemoryStream())
            {
                using (var writer = new BsonDataWriter(ms))
                {
                    serializer.Serialize(writer, new ObjectWrapper<T> { Value = value });
                    return ms.ToArray();
                }
            }
        }

        private IObservable<T> DeserializeObject<T>(byte[] data)
        {
            var settings = Locator.Current.GetService<JsonSerializerSettings>() ?? new JsonSerializerSettings();
            settings.ContractResolver = new JsonDateTimeContractResolver(settings.ContractResolver, ForcedDateTimeKind); // This will make us use ticks instead of json ticks for DateTime.
            var serializer = JsonSerializer.Create(settings);
            using (var reader = new BsonDataReader(new MemoryStream(data)))
            {
                var forcedDateTimeKind = BlobCache.ForcedDateTimeKind;

                if (forcedDateTimeKind.HasValue)
                {
                    reader.DateTimeKindHandling = forcedDateTimeKind.Value;
                }

                try
                {
                    try
                    {
                        var boxedVal = serializer.Deserialize<ObjectWrapper<T>>(reader).Value;
                        return Observable.Return(boxedVal);
                    }
                    catch (Exception ex)
                    {
                        this.Log().Warn(ex, "Failed to deserialize data as boxed, we may be migrating from an old Akavache");
                    }

                    var rawVal = serializer.Deserialize<T>(reader);
                    return Observable.Return(rawVal);
                }
                catch (Exception ex)
                {
                    return Observable.Throw<T>(ex);
                }
            }
        }
    }
}
