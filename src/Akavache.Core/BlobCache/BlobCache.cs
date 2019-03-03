// Copyright (c) 2019 .NET Foundation and Contributors. All rights reserved.
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Threading.Tasks;
using Newtonsoft.Json.Bson;
using Splat;

namespace Akavache
{
    /// <summary>
    /// A class which represents a blobbed cache.
    /// </summary>
    public static class BlobCache
    {
        private static string _applicationName;
        private static IBlobCache _localMachine;
        private static IBlobCache _userAccount;
        private static ISecureBlobCache _secure;
        private static bool _shutdownRequested;

        private static IScheduler _taskPoolOverride;

        [ThreadStatic]
        private static IBlobCache _unitTestLocalMachine;

        [ThreadStatic]
        private static IBlobCache _unitTestUserAccount;

        [ThreadStatic]
        private static ISecureBlobCache _unitTestSecure;

        static BlobCache()
        {
            Locator.RegisterResolverCallbackChanged(() =>
            {
                if (Locator.CurrentMutable == null)
                {
                    return;
                }

                Locator.CurrentMutable.InitializeAkavache();
            });

            InMemory = new InMemoryBlobCache(Scheduler.Default);
        }

        /// <summary>
        /// Gets or sets your application's name. Set this at startup, this defines where
        /// your data will be stored (usually at %AppData%\[ApplicationName]).
        /// </summary>
        [SuppressMessage("Design", "CA1065: Properties should not fire exceptions.", Justification = "Extreme non standard case.")]
        public static string ApplicationName
        {
            get
            {
                if (_applicationName == null)
                {
                    throw new Exception("Make sure to set BlobCache.ApplicationName on startup");
                }

                return _applicationName;
            }

            set => _applicationName = value;
        }

        /// <summary>
        /// Gets or sets the local machine cache. Store data here that is unrelated to the
        /// user account or shouldn't be uploaded to other machines (i.e.
        /// image cache data).
        /// </summary>
        public static IBlobCache LocalMachine
        {
            get => _unitTestLocalMachine ?? _localMachine ?? (_shutdownRequested ? new ShutdownBlobCache() : null) ?? Locator.Current.GetService<IBlobCache>("LocalMachine");
            set
            {
                if (ModeDetector.InUnitTestRunner())
                {
                    _unitTestLocalMachine = value;
                    _localMachine = _localMachine ?? value;
                }
                else
                {
                    _localMachine = value;
                }
            }
        }

        /// <summary>
        /// Gets or sets the user account cache. Store data here that is associated with
        /// the user; in large organizations, this data will be synced to all
        /// machines via NT Roaming Profiles.
        /// </summary>
        public static IBlobCache UserAccount
        {
            get => _unitTestUserAccount ?? _userAccount ?? (_shutdownRequested ? new ShutdownBlobCache() : null) ?? Locator.Current.GetService<IBlobCache>("UserAccount");
            set
            {
                if (ModeDetector.InUnitTestRunner())
                {
                    _unitTestUserAccount = value;
                    _userAccount = _userAccount ?? value;
                }
                else
                {
                    _userAccount = value;
                }
            }
        }

        /// <summary>
        /// Gets or sets an IBlobCache that is encrypted - store sensitive data in this
        /// cache such as login information.
        /// </summary>
        public static ISecureBlobCache Secure
        {
            get => _unitTestSecure ?? _secure ?? (_shutdownRequested ? new ShutdownBlobCache() : null) ?? Locator.Current.GetService<ISecureBlobCache>();
            set
            {
                if (ModeDetector.InUnitTestRunner())
                {
                    _unitTestSecure = value;
                    _secure = _secure ?? value;
                }
                else
                {
                    _secure = value;
                }
            }
        }

        /// <summary>
        /// Gets or sets an IBlobCache that simply stores data in memory. Data stored in
        /// this cache will be lost when the application restarts.
        /// </summary>
        public static ISecureBlobCache InMemory { get; set; }

        /// <summary>
        /// Gets or sets the DateTimeKind handling for BSON readers to be forced.
        /// </summary>
        /// <remarks>
        /// <para>
        /// By default, <see cref="BsonReader"/> uses a <see cref="DateTimeKind"/> of <see cref="DateTimeKind.Local"/> and <see cref="BsonWriter"/>
        /// uses <see cref="DateTimeKind.Utc"/>. Thus, DateTimes are serialized as UTC but deserialized as local time. To force BSON readers to
        /// use some other <c>DateTimeKind</c>, you can set this value.
        /// </para>
        /// </remarks>
        public static DateTimeKind? ForcedDateTimeKind { get; set; }

#if PORTABLE
        /// <summary>
        /// Gets or sets the Scheduler used for task pools.
        /// </summary>
        /// <exception cref="Exception">If the task pool scheduler can't be found.</exception>
        [SuppressMessage("Design", "CA1065: Properties should not fire exceptions.", Justification = "Extreme non standard case.")]
        public static IScheduler TaskpoolScheduler
        {
            get
            {
                var ret = _taskPoolOverride ?? Locator.Current.GetService<IScheduler>("Taskpool");
                if (ret == null)
                {
                    throw new Exception("Can't find a TaskPoolScheduler. You probably accidentally linked to the PCL Akavache in your app.");
                }

                return ret;
            }
            set => _taskPoolOverride = value;
        }
#else
        /// <summary>
        /// Gets or sets the Scheduler used for task pools.
        /// </summary>
        public static IScheduler TaskpoolScheduler
        {
            get => _taskPoolOverride ?? Locator.Current.GetService<IScheduler>("Taskpool") ?? System.Reactive.Concurrency.TaskPoolScheduler.Default;
            set => _taskPoolOverride = value;
        }
#endif

        /// <summary>
        /// Makes sure that the system has been initialized.
        /// </summary>
        public static void EnsureInitialized()
        {
            // NB: This method doesn't actually do anything, it just ensures
            // that the static constructor runs
            LogHost.Default.Debug("Initializing Akavache");
        }

        /// <summary>
        /// This method shuts down all of the blob caches. Make sure call it
        /// on app exit and await / Wait() on it.
        /// </summary>
        /// <returns>A Task representing when all caches have finished shutting
        /// down.</returns>
        public static Task Shutdown()
        {
            _shutdownRequested = true;
            var toDispose = new[] { LocalMachine, UserAccount, Secure, InMemory, };

            var ret = toDispose.Select(x =>
            {
                x.Dispose();
                return x.Shutdown;
            }).Merge().ToList().Select(_ => Unit.Default);

            return ret.ToTask();
        }

        private class ShutdownBlobCache : ISecureBlobCache
        {
            IObservable<Unit> IBlobCache.Shutdown => Observable.Return(Unit.Default);

            public IScheduler Scheduler => System.Reactive.Concurrency.Scheduler.Immediate;

            /// <inheritdoc />
            public DateTimeKind? ForcedDateTimeKind
            {
                get => null;
                set { }
            }

            public void Dispose()
            {
            }

            public IObservable<Unit> Insert(string key, byte[] data, DateTimeOffset? absoluteExpiration = null)
            {
                return null;
            }

            public IObservable<byte[]> Get(string key)
            {
                return null;
            }

            public IObservable<IEnumerable<string>> GetAllKeys()
            {
                return null;
            }

            public IObservable<DateTimeOffset?> GetCreatedAt(string key)
            {
                return null;
            }

            public IObservable<Unit> Flush()
            {
                return null;
            }

            public IObservable<Unit> Invalidate(string key)
            {
                return null;
            }

            public IObservable<Unit> InvalidateAll()
            {
                return null;
            }

            public IObservable<Unit> Vacuum()
            {
                return null;
            }
        }
    }
}
