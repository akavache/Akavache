﻿// Copyright (c) 2019 .NET Foundation and Contributors. All rights reserved.
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using System.Reactive.Windows.Foundation;
using Splat;
using Windows.Storage;

namespace Akavache
{
    /// <summary>
    /// A file system provider for the WinRT system.
    /// </summary>
    [SuppressMessage("FxCop.Analyzer", "CA1307: The behavior of 'string.Replace(string, string)' could vary based on the current user's locale settings", Justification = "Not all platforms allow locale.")]
    public class WinRTFilesystemProvider : IFilesystemProvider, IEnableLogger
    {
        /// <inheritdoc />
        public IObservable<Stream> OpenFileForReadAsync(string path, IScheduler scheduler)
        {
            var folder = Path.GetDirectoryName(path);
            var name = Path.GetFileName(path);

            return StorageFolder.GetFolderFromPathAsync(folder).ToObservable()
                .SelectMany(x => x.GetFileAsync(name).ToObservable())
                .SelectMany(x => x.OpenStreamForReadAsync().ToObservable());
        }

        /// <inheritdoc />
        public IObservable<Stream> OpenFileForWriteAsync(string path, IScheduler scheduler)
        {
            var folder = Path.GetDirectoryName(path);
            var name = Path.GetFileName(path);

            return StorageFolder.GetFolderFromPathAsync(folder).ToObservable()
                .SelectMany(x => x.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting).ToObservable())
                .SelectMany(x => x.OpenStreamForWriteAsync().ToObservable());
        }

        /// <inheritdoc />
        public IObservable<Unit> CreateRecursive(string path)
        {
            var paths = path.Split('\\');

            var firstFolderThatExists = Observable.Range(0, paths.Length - 1)
                .Select(x =>
                    StorageFolder.GetFolderFromPathAsync(string.Join("\\", paths.Take(paths.Length - x)))
                    .ToObservable()
                    .Catch(Observable.Empty<StorageFolder>()))
                .Concat()
                .Take(1);

            return firstFolderThatExists
                .Select(x =>
                {
                    if (x.Path == path)
                    {
                        return null;
                    }

                    return new { Root = x, Paths = path.Replace(x.Path + "\\", string.Empty).Split('\\') };
                })
                .SelectMany(x =>
                {
                    if (x == null)
                    {
                        return Observable.Return(default(StorageFolder));
                    }

                    #pragma warning disable CS0618 // Observable.First<TSource>(IObservable<TSource>)' is obsolete -- need to find a better solution
                    return x.Paths.ToObservable().Aggregate(x.Root, (acc, y) => acc.CreateFolderAsync(y).ToObservable().First());
                    #pragma warning restore CS0618
                })
                .Select(_ => Unit.Default);
        }

        /// <inheritdoc />
        public IObservable<Unit> Delete(string path)
        {
            return StorageFile.GetFileFromPathAsync(path).ToObservable()
                .SelectMany(x => x.DeleteAsync().ToObservable());
        }

        /// <inheritdoc />
        public string GetDefaultRoamingCacheDirectory()
        {
            return Path.Combine(Windows.Storage.ApplicationData.Current.RoamingFolder.Path, "BlobCache");
        }

        /// <inheritdoc />
        public string GetDefaultLocalMachineCacheDirectory()
        {
            return Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "BlobCache");
        }

        /// <inheritdoc />
        public string GetDefaultSecretCacheDirectory()
        {
            return Path.Combine(Windows.Storage.ApplicationData.Current.RoamingFolder.Path, "SecretCache");
        }
    }
}
