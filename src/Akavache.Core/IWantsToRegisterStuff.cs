// Copyright (c) 2019 .NET Foundation and Contributors. All rights reserved.
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using Splat;

namespace Akavache
{
    /// <summary>
    /// This class is derived for different assemblies to provide registrations in Splat.
    /// </summary>
    internal interface IWantsToRegisterStuff
    {
        /// <summary>
        /// Register required items in the provided dependency resolver.
        /// </summary>
        /// <param name="resolverToUse">The dependency resolver to register items against.</param>
        void Register(IMutableDependencyResolver resolverToUse);
    }
}