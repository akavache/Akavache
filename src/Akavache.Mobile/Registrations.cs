﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using ReactiveUI;
using Splat;

namespace Akavache.Mobile
{
    public class Registrations : IWantsToRegisterStuff
    {
        public void Register(IMutableDependencyResolver resolver)
        {
            resolver.Register(() => new JsonSerializerSettings()
            {
                ObjectCreationHandling = ObjectCreationHandling.Replace,
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                TypeNameHandling = TypeNameHandling.All,
            }, typeof(JsonSerializerSettings), null);

            var akavacheDriver = new AkavacheDriver();
            resolver.Register(() => akavacheDriver, typeof(ISuspensionDriver), null);

            // NB: These correspond to the hacks in Akavache.Http's registrations
            resolver.Register(() => resolver.GetService<ISuspensionHost>().ShouldPersistState,
                typeof(IObservable<IDisposable>), "ShouldPersistState");

            resolver.Register(() => resolver.GetService<ISuspensionHost>().IsUnpausing,
                typeof(IObservable<Unit>), "IsUnpausing");

            resolver.Register(() => RxApp.TaskpoolScheduler, typeof(IScheduler), "Taskpool");
        }
    }
}
