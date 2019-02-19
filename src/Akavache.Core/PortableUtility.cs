﻿// Copyright (c) 2019 .NET Foundation and Contributors. All rights reserved.
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System;
using System.Reactive.Subjects;

namespace Akavache
{
    internal static class PortableExtensions
    {
        public static T Retry<T>(this Func<T> block, int retries = 3)
        {
            while (true)
            {
                try
                {
                    T ret = block();
                    return ret;
                }
                catch (Exception)
                {
                    retries--;
                    if (retries == 0)
                    {
                        throw;
                    }
                }
            }
        }

        internal static IObservable<T> PermaRef<T>(this IConnectableObservable<T> observable)
        {
            observable.Connect();
            return observable;
        }
    }
}
