﻿// Copyright (c) 2019 .NET Foundation and Contributors. All rights reserved.
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;

namespace Akavache.Sqlite3
{
    internal class ObjectWrapper<T> : IObjectWrapper
    {
        public T Value { get; set; }
    }
}
