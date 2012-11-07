﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text;
using Newtonsoft.Json;
#if WP7
using ReactiveUI;
#endif

namespace Akavache
{
    public static class JsonSerializationMixin
    {
        static readonly ConcurrentDictionary<string, object> inflightFetchRequests = new ConcurrentDictionary<string, object>(); 

        /// <summary>
        /// Insert an object into the cache, via the JSON serializer.
        /// </summary>
        /// <param name="key">The key to associate with the object.</param>
        /// <param name="value">The object to serialize.</param>
        /// <param name="absoluteExpiration">An optional expiration date.</param>
        public static IObservable<Unit> InsertObject<T>(this IBlobCache This, string key, T value, DateTimeOffset? absoluteExpiration = null)
        {
            var bytes = SerializeObject(value);
            return This.Insert(GetTypePrefixedKey(key, typeof(T)), bytes, absoluteExpiration);
        }

        /// <summary>
        /// Get an object from the cache and deserialize it via the JSON
        /// serializer.
        /// </summary>
        /// <param name="key">The key to look up in the cache.</param>
        /// <param name="noTypePrefix">Use the exact key name instead of a
        /// modified key name. If this is true, GetAllObjects will not find this object.</param>
        /// <returns>A Future result representing the object in the cache.</returns>
        public static IObservable<T> GetObjectAsync<T>(this IBlobCache This, string key, bool noTypePrefix = false)
        {
            return This.GetAsync(noTypePrefix ? key : GetTypePrefixedKey(key, typeof(T)))
                       .SelectMany(DeserializeObject<T>);
        }

        /// <summary>
        /// Return all objects of a specific Type in the cache.
        /// </summary>
        /// <returns>A Future result representing all objects in the cache
        /// with the specified Type.</returns>
        public static IObservable<IEnumerable<T>> GetAllObjects<T>(this IBlobCache This)
        {
            // NB: This isn't exactly thread-safe, but it's Close Enough(tm)
            // We make up for the fact that the keys could get kicked out
            // from under us via the Catch below
            var matchingKeys = This.GetAllKeys()
                                   .Where(x => x.StartsWith(GetTypePrefixedKey("", typeof(T))))
                                   .ToArray();

            return matchingKeys.ToObservable()
                               .SelectMany(x => This.GetObjectAsync<T>(x, true).Catch(Observable.Empty<T>()))
                               .ToList()
                               .Select(x => (IEnumerable<T>) x);
        }

        /// <summary>
        /// Attempt to return an object from the cache. If the item doesn't
        /// exist or returns an error, call a Func to return the latest
        /// version of an object and insert the result in the cache.
        ///
        /// For most Internet applications, this method is the best method to
        /// call to fetch static data (i.e. images) from the network.
        /// </summary>
        /// <param name="key">The key to associate with the object.</param>
        /// <param name="fetchFunc">A Func which will asynchronously return
        /// the latest value for the object should the cache not contain the
        /// key. 
        ///
        /// Observable.Start is the most straightforward way (though not the
        /// most efficient!) to implement this Func.</param>
        /// <param name="absoluteExpiration">An optional expiration date.</param>
        /// <returns>A Future result representing the deserialized object from
        /// the cache.</returns>
        public static IObservable<T> GetOrFetchObject<T>(this IBlobCache This, string key, Func<IObservable<T>> fetchFunc, DateTimeOffset? absoluteExpiration = null)
        {
            return This.GetObjectAsync<T>(key).Catch<T, Exception>(_ =>
            {
                object dontcare;
                return ((IObservable<T>)inflightFetchRequests.GetOrAdd(key, __ => (object)fetchFunc()))
                    .Do(x => This.InsertObject(key, x, absoluteExpiration))
                    .Finally(() => inflightFetchRequests.TryRemove(key, out dontcare))
                    .Multicast(new AsyncSubject<T>()).RefCount();
            });
        }

        /// <summary>
        /// This method attempts to returned a cached value, while
        /// simultaneously calling a Func to return the latest value. When the
        /// latest data comes back, it replaces what was previously in the
        /// cache.
        ///
        /// This method is best suited for loading dynamic data from the
        /// Internet, while still showing the user earlier data.
        ///
        /// This method returns an IObservable that may return *two* results
        /// (first the cached data, then the latest data). Therefore, it's
        /// important for UI applications that in your Subscribe method, you
        /// write the code to merge the second result when it comes in.
        /// </summary>
        /// <param name="key">The key to store the returned result under.</param>
        /// <param name="fetchFunc"></param>
        /// <param name="fetchPredicate">An optional Func to determine whether
        /// the updated item should be fetched. If the cached version isn't found,
        /// this parameter is ignored and the item is always fetched.</param>
        /// <param name="absoluteExpiration">An optional expiration date.</param>
        /// <returns>An Observable stream containing either one or two
        /// results (possibly a cached version, then the latest version)</returns>
        public static IObservable<T> GetAndFetchLatest<T>(this IBlobCache This, 
            string key, 
            Func<IObservable<T>> fetchFunc, 
            Func<DateTimeOffset, bool> fetchPredicate = null,
            DateTimeOffset? absoluteExpiration = null)
        {
            bool foundItemInCache;
            var fail = Observable.Defer(() => This.GetCreatedAt(key))
                                 .Select(x => fetchPredicate != null && x != null ? fetchPredicate(x.Value) : true)
                                 .Where(x => x != false)
                                 .SelectMany(_ => fetchFunc())
                                 .Finally(() => This.Invalidate(key))
                                 .Do(x => This.InsertObject(key, x, absoluteExpiration));

            var result = This.GetObjectAsync<T>(key).Select(x => new Tuple<T, bool>(x, true))
                             .Catch(Observable.Return(new Tuple<T, bool>(default(T), false)));

            return result.SelectMany(x =>
            {
                foundItemInCache = x.Item2;
                return x.Item2 ?
                    Observable.Return(x.Item1) :
                    Observable.Empty<T>();
            }).Concat(fail).Multicast(new ReplaySubject<T>()).RefCount();
        }

        public static void InvalidateObject<T>(this IBlobCache This, string key)
        {
            This.Invalidate(GetTypePrefixedKey(key, typeof(T)));
        }

        public static void InvalidateAllObjects<T>(this IBlobCache This)
        {
            foreach(var key in This.GetAllKeys().Where(x => x.StartsWith(GetTypePrefixedKey("", typeof(T)))))
            {
                This.Invalidate(key);
            }
        }

        static Lazy<JsonSerializer> serializer = new Lazy<JsonSerializer>(
            () => JsonSerializer.Create(new JsonSerializerSettings()));
        
        internal static byte[] SerializeObject(object value)
        {
            return SerializeObject<object>(value);
        }

        internal static byte[] SerializeObject<T>(T value)
        {
            return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value));
        }

        static IObservable<T> DeserializeObject<T>(byte[] x)
        {
            try
            {
                var bytes = Encoding.UTF8.GetString(x, 0, x.Length);
                var ret = BlobCache.ObjectFactory == null ? JsonConvert.DeserializeObject<T>(bytes) : JsonConvert.DeserializeObject<T>(bytes, BlobCache.JsonObjectConverter);
                return Observable.Return(ret);
            } 
            catch (Exception ex)
            {
                return Observable.Throw<T>(ex);
            }
        }

        internal static string GetTypePrefixedKey(string key, Type type)
        {
            return type.FullName + "___" + key;
        }
    }
}