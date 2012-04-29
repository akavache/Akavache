using System;
using System.IO;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reflection;
using System.Security.Cryptography;
using ReactiveUI;

namespace Akavache
{
    public abstract class EncryptedBlobCache : PersistentBlobCache, ISecureBlobCache
    {
        static Lazy<ISecureBlobCache> _Current = new Lazy<ISecureBlobCache>(() => new CEncryptedBlobCache(GetDefaultCacheDirectory()));
        public static ISecureBlobCache Current
        {
            get { return _Current.Value; }
        }

        protected EncryptedBlobCache(string cacheDirectory = null, IFilesystemProvider filesystemProvider = null, IScheduler scheduler = null) : base(cacheDirectory, filesystemProvider, scheduler)
        {
        }

        class CEncryptedBlobCache : EncryptedBlobCache {
            public CEncryptedBlobCache(string cacheDirectory) : base(cacheDirectory, null, RxApp.TaskpoolScheduler) { }
        }

        protected override IObservable<byte[]> BeforeWriteToDiskFilter(byte[] data, IScheduler scheduler)
        {
            try
            {
                var ret = Observable.Return(ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser));

                // NB: MemoizedRequests will be null as we're disposing
                if (MemoizedRequests != null)
                {
                    lock(MemoizedRequests) MemoizedRequests.InvalidateAll();
                }
                return ret;
            } 
            catch(Exception ex)
            {
                return Observable.Throw<byte[]>(ex);
            }
            
        }

        protected override IObservable<byte[]> AfterReadFromDiskFilter(byte[] data, IScheduler scheduler)
        {
            try
            {
                string dontcare;
                var ret = Observable.Return(ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser));

                // NB: MemoizedRequests will be null as we're disposing
                if (MemoizedRequests != null)
                {
                    lock(MemoizedRequests) MemoizedRequests.InvalidateAll();
                }
                return ret;
            } 
            catch(Exception ex)
            {
                return Observable.Throw<byte[]>(ex);
            }
        }

        protected static string GetDefaultCacheDirectory()
        {
            return RxApp.InUnitTestRunner() ?
                Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "SecretCache") :
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), BlobCache.ApplicationName, "SecretCache");
        }
    }
}