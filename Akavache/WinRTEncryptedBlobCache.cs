﻿using System;
using System.IO;
using System.Reactive.Concurrency;
using System.Reactive.Windows.Foundation;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Reactive.Linq;
using System.Reflection;
using ReactiveUI;
using Windows.Security.Cryptography.DataProtection;
using Windows.Storage;

namespace Akavache
{
    public abstract class EncryptedBlobCache : PersistentBlobCache, ISecureBlobCache
    {
        static Lazy<ISecureBlobCache> _Current = new Lazy<ISecureBlobCache>(() => new CEncryptedBlobCache(GetDefaultCacheDirectory()));
        public static ISecureBlobCache Current
        {
            get { return _Current.Value; }
        }

        protected EncryptedBlobCache(string cacheDirectory = null, IFileSystemProvider filesystemProvider = null, IScheduler scheduler = null)
            : base(cacheDirectory, filesystemProvider, scheduler)
        {
        }

        class CEncryptedBlobCache : EncryptedBlobCache
        {
            public CEncryptedBlobCache(string cacheDirectory) : base(cacheDirectory, null, RxApp.TaskpoolScheduler) { }
        }

        protected override IObservable<byte[]> BeforeWriteToDiskFilter(byte[] data, IScheduler scheduler)
        {
            if (data.Length == 0) 
            {
                return Observable.Return(data);
            }

            var dpapi = new DataProtectionProvider("LOCAL=user");
            return dpapi.ProtectAsync(data.AsBuffer()).ToObservable()
                .Select(x => x.ToArray());
        }

        protected override IObservable<byte[]> AfterReadFromDiskFilter(byte[] data, IScheduler scheduler)
        {
            if (data.Length == 0) 
            {
                return Observable.Return(data);
            }

            var dpapi = new DataProtectionProvider();
            return dpapi.UnprotectAsync(data.AsBuffer()).ToObservable()
                .Select(x => x.ToArray());
        }

        protected static string GetDefaultCacheDirectory()
        {
            return Path.Combine(ApplicationData.Current.RoamingFolder.Path, "SecretCache");
        }
    }
}