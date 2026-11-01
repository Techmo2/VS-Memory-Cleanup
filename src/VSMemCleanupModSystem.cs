using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace VSMemCleanup;

/// <summary>
/// Lets a closed world be freed. Mods that leave a handler on something that outlives the world -
/// the game window, or a static event or field - keep that world's whole client or server in
/// memory, a few GB per world loaded since the game started. VSImGui 1.2.7 and ConfigLib 1.13.1 do.
///
/// Once a world has closed, this looks through every static field of mod code and every event on
/// the game window for references that lead back to that world (its API, its world, or one of its
/// mod systems), removes those handlers and clears those fields, then hands the freed memory back
/// to the system. The game's own code and fields are never changed.
/// </summary>
public class VSMemCleanupModSystem : ModSystem
{
    private const string ModId = "vsmemcleanup";
    private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags AnyStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    /// <summary>
    /// How many references from a handler's object to look for the closed world. A handler and
    /// the object it calls count as one.
    /// </summary>
    private const int HandlerDepth = 2;

    /// <summary>The same for a plain static field, which is cleared rather than unsubscribed, so kept close.</summary>
    private const int FieldDepth = 1;

    /// <summary>After the last side of a world closes; a single player server stops after the client.</summary>
    private static readonly TimeSpan SweepDelay = TimeSpan.FromSeconds(5);
    private static readonly object SweepLock = new();
    private static readonly List<ClosedWorld> Pending = new();
    private static readonly ConcurrentDictionary<Type, FieldInfo[]> FieldCache = new();
    private static Timer _sweepTimer;

    /// <summary>
    /// A closed world that is still alive after the sweep is usually held by work still running,
    /// such as a worldgen task another mod started before the world closed. It is checked again
    /// at this interval, up to the limit, and its memory returned once it is gone.
    /// </summary>
    private static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(20);
    private const int RecheckLimit = 15;
    private static readonly List<WeakReference> Watched = new();
    private static ILogger _watchLogger;
    private static int _rechecks;

    private ICoreAPI _api;

    /// <summary>Disposed after every other mod, so their own cleanup still finds their fields.</summary>
    public override double ExecuteOrder() => 1_000_000;

    public override bool ShouldLoad(EnumAppSide side) => true;

    public override void StartPre(ICoreAPI api)
    {
        _api = api;
        // A world started before the last one was swept: sweep it now, without the collection.
        Sweep(collect: false);
        lock (SweepLock)
        {
            Watched.Clear();
            _watchLogger = null;
        }
    }

    public override void Dispose()
    {
        ICoreAPI api = _api;
        _api = null;
        if (api == null) return;

        var world = new ClosedWorld(api);
        lock (SweepLock)
        {
            Pending.Add(world);
            _sweepTimer?.Dispose();
            _sweepTimer = new Timer(_ =>
            {
                ILogger logger = Sweep(collect: true);
                if (logger != null) Collect(logger);
            }, null, SweepDelay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>What identifies one side of a closed world, captured while it is still intact.</summary>
    private sealed class ClosedWorld
    {
        public readonly ICoreAPI Api;
        public readonly HashSet<object> Roots = new(ReferenceEqualityComparer.Instance);
        public readonly HashSet<Type> SystemTypes = new();
        public readonly object Window;
        public readonly bool Dedicated;

        public ClosedWorld(ICoreAPI api)
        {
            Api = api;
            Dedicated = api is ICoreServerAPI { Server.IsDedicated: true };
            Roots.Add(api);
            if (api.World != null) Roots.Add(api.World);
            foreach (ModSystem system in api.ModLoader.Systems)
            {
                Roots.Add(system);
                for (Type type = system.GetType(); type != null && type != typeof(ModSystem); type = type.BaseType)
                    SystemTypes.Add(type);
            }

            if (api is ICoreClientAPI)
            {
                object platform = Field(api.World, "Platform");
                Window = Field(platform, "window");
            }
        }
    }

    /// <summary>
    /// Releases what the pending closed worlds left behind. Returns the logger to report the
    /// collection to, or null when none is due. Kept out of <see cref="Collect"/> so nothing on this
    /// stack still holds a closed world while it is collected.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ILogger Sweep(bool collect)
    {
        ClosedWorld[] worlds;
        lock (SweepLock)
        {
            _sweepTimer?.Dispose();
            _sweepTimer = null;
            worlds = Pending.ToArray();
            Pending.Clear();
        }
        if (worlds.Length == 0) return null;

        // The client's logger outlives the world; a single player server's goes with it.
        ILogger log = (worlds.FirstOrDefault(w => w.Api.Side == EnumAppSide.Client) ?? worlds[0]).Api.Logger;
        foreach (ClosedWorld world in worlds)
        {
            ILogger logger = log;
            string side = world.Api.Side == EnumAppSide.Client ? "client" : "server";
            try
            {
                var released = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                if (world.Window != null) ReleaseHandlersOn(world.Window, world, released, logger);
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly != typeof(VSMemCleanupModSystem).Assembly && !IsFramework(assembly))
                        ReleaseStatics(assembly, world, released, logger);
                }
                if (released.Count > 0)
                {
                    Log(logger, l => l.Notification("[{0}] Released {1} references that kept the closed world's {2} in memory: {3}.",
                        ModId, released.Values.Sum(), side, string.Join(", ", released.Select(kv => $"{kv.Key} {kv.Value}"))));
                }
            }
            catch (Exception e)
            {
                Log(logger, l => l.Warning("[{0}] Could not release the closed world: {1}", ModId, e));
            }
        }

        if (!collect || worlds.All(w => w.Dedicated)) return null;
        lock (SweepLock)
        {
            Watched.AddRange(worlds.Select(w => new WeakReference(w.Api)));
            _watchLogger = log;
            _rechecks = 0;
        }
        return log;
    }

    /// <summary>
    /// The runtime keeps the memory a world used even once it is garbage, so the main menu would
    /// still hold what the last world peaked at. One compacting collection returns it.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Collect(ILogger log)
    {
        long before = Environment.WorkingSet;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        long after = Environment.WorkingSet;

        int alive;
        lock (SweepLock)
        {
            Watched.RemoveAll(w => !w.IsAlive);
            alive = Watched.Count;
            if (alive > 0 && _rechecks++ < RecheckLimit)
                _sweepTimer = new Timer(_ => Recheck(), null, RecheckInterval, Timeout.InfiniteTimeSpan);
            else
            {
                Watched.Clear();
                _watchLogger = null;
            }
        }

        if (alive == 0)
        {
            Log(log, l => l.Notification("[{0}] Freed memory after the world closed: the game now uses {1:F1} GB, down from {2:F1} GB.",
                ModId, after / 1e9, before / 1e9));
        }
        else if (_rechecks == 1)
        {
            Log(log, l => l.Notification("[{0}] The closed world is still in use, probably by a task that hasn't finished; " +
                "its memory is returned once it is freed. The game uses {1:F1} GB for now.", ModId, after / 1e9));
        }
    }

    /// <summary>Collects only once the closed world is gone, so the main menu doesn't pause for nothing.</summary>
    private static void Recheck()
    {
        ILogger log;
        lock (SweepLock)
        {
            _sweepTimer?.Dispose();
            _sweepTimer = null;
            log = _watchLogger;
            if (log == null) return;
        }

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: false);
        bool freed;
        lock (SweepLock) freed = Watched.All(w => !w.IsAlive);
        if (freed || _rechecks >= RecheckLimit) Collect(log);
        else lock (SweepLock) _sweepTimer = new Timer(_ => Recheck(), null, RecheckInterval, Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// The game window lasts the whole game. VSImGui's controllers subscribe to its MouseWheel and
    /// TextInput and never unsubscribe. Handlers of the game's own code stay.
    /// </summary>
    private static void ReleaseHandlersOn(object window, ClosedWorld world, IDictionary<string, int> released, ILogger logger)
    {
        for (Type type = window.GetType(); type != null; type = type.BaseType)
        {
            foreach (EventInfo evt in type.GetEvents(AnyInstance | BindingFlags.DeclaredOnly))
            {
                FieldInfo backing = type.GetField(evt.Name, AnyInstance | BindingFlags.DeclaredOnly);
                if (backing == null || !typeof(Delegate).IsAssignableFrom(backing.FieldType)) continue;
                ReleaseDelegateField(backing, window, world, released, logger);
            }
        }
    }

    /// <summary>
    /// Static fields of every type in a mod assembly or the game's: handlers that lead to the closed
    /// world are removed, wherever the event is. Plain fields are cleared only in mod code.
    /// </summary>
    private static void ReleaseStatics(Assembly assembly, ClosedWorld world, IDictionary<string, int> released, ILogger logger)
    {
        bool gameCode = IsGame(assembly);
        Type[] types;
        try { types = assembly.GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }

        foreach (Type type in types)
        {
            // Reading a static field runs the type's static constructor if it hasn't run yet, which
            // could fail at the main menu and break the type for good. Safe: types without one; the
            // closed world's own mod systems, whose constructor has run; and in mod code, types
            // whose constructor only sets field initializers, which rarely depend on a world.
            if (type.ContainsGenericParameters) continue;
            if (type.TypeInitializer != null && !world.SystemTypes.Contains(type) &&
                (gameCode || (type.Attributes & TypeAttributes.BeforeFieldInit) == 0)) continue;

            FieldInfo[] fields;
            try { fields = type.GetFields(AnyStatic); }
            catch (Exception) { continue; }

            foreach (FieldInfo field in fields)
            {
                if (field.IsLiteral || field.FieldType.IsValueType || field.FieldType.IsPointer) continue;
                try
                {
                    if (typeof(Delegate).IsAssignableFrom(field.FieldType))
                    {
                        ReleaseDelegateField(field, null, world, released, logger);
                    }
                    else if (!gameCode && !field.IsInitOnly)
                    {
                        object value = field.GetValue(null);
                        if (value == null || value is string || value is Array || value is ICollection) continue;
                        if (!LeadsTo(value, world, FieldDepth, new HashSet<object>(ReferenceEqualityComparer.Instance))) continue;
                        field.SetValue(null, null);
                        Count(field, released, logger);
                    }
                }
                catch (Exception)
                {
                    // A field that can't be read or written is left alone.
                }
            }
        }
    }

    /// <summary>Removes the handlers of a delegate field that belong to mod code and lead to the closed world.</summary>
    private static void ReleaseDelegateField(FieldInfo field, object instance, ClosedWorld world, IDictionary<string, int> released, ILogger logger)
    {
        if (field.GetValue(instance) is not Delegate handlers) return;

        Delegate[] list = handlers.GetInvocationList();
        Delegate[] kept = list.Where(handler =>
            handler.Target == null || IsGame(handler.Target.GetType().Assembly) ||
            !LeadsTo(handler.Target, world, HandlerDepth, new HashSet<object>(ReferenceEqualityComparer.Instance))).ToArray();
        if (kept.Length == list.Length) return;

        field.SetValue(instance, Delegate.Combine(kept));
        for (int i = kept.Length; i < list.Length; i++) Count(field, released, logger);
    }

    /// <summary>Whether an object is part of the closed world, or reaches it within the given number of references.</summary>
    private static bool LeadsTo(object value, ClosedWorld world, int depth, HashSet<object> seen)
    {
        if (value == null) return false;
        if (world.Roots.Contains(value)) return true;
        if (value is Delegate handlers)
        {
            foreach (Delegate handler in handlers.GetInvocationList())
                if (LeadsTo(handler.Target, world, depth, seen)) return true;
            return false;
        }

        if (depth <= 0 || !seen.Add(value)) return false;
        Type type = value.GetType();
        if (value is string || type.IsArray || value is ICollection || value is MemberInfo || value is Assembly) return false;
        foreach (FieldInfo field in FieldCache.GetOrAdd(type, InstanceReferenceFields))
        {
            object child;
            try { child = field.GetValue(value); }
            catch (Exception) { continue; }
            if (LeadsTo(child, world, depth - 1, seen)) return true;
        }
        return false;
    }

    private static FieldInfo[] InstanceReferenceFields(Type type)
    {
        var fields = new List<FieldInfo>();
        for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
            fields.AddRange(t.GetFields(AnyInstance | BindingFlags.DeclaredOnly).Where(f => !f.FieldType.IsValueType && !f.FieldType.IsPointer));
        return fields.ToArray();
    }

    private static void Count(FieldInfo field, IDictionary<string, int> released, ILogger logger)
    {
        string assembly = field.DeclaringType?.Assembly.GetName().Name ?? "?";
        released[assembly] = released.TryGetValue(assembly, out int n) ? n + 1 : 1;
        Log(logger, l => l.VerboseDebug("[{0}] Released {1}.{2}", ModId, field.DeclaringType?.FullName, field.Name));
    }

    private static readonly string GameDirectory = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
    private static readonly string RuntimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location) ?? "";

    /// <summary>The game's own code: its install folder, Lib and the bundled Mods (survival, essentials, creative).</summary>
    private static bool IsGame(Assembly assembly)
    {
        if (assembly.IsDynamic || IsFramework(assembly)) return true;
        string location = assembly.Location;
        if (string.IsNullOrEmpty(location)) return false;
        string directory = Path.GetDirectoryName(Path.GetFullPath(location))?.TrimEnd(Path.DirectorySeparatorChar) ?? "";
        return directory == GameDirectory ||
               directory == Path.Combine(GameDirectory, "Lib") ||
               directory == Path.Combine(GameDirectory, "Mods");
    }

    private static bool IsFramework(Assembly assembly)
    {
        if (assembly.IsDynamic) return true;
        string location = assembly.Location;
        return !string.IsNullOrEmpty(location) && !string.IsNullOrEmpty(RuntimeDirectory) &&
               location.StartsWith(RuntimeDirectory, StringComparison.Ordinal);
    }

    private static object Field(object instance, string name)
    {
        for (Type type = instance?.GetType(); type != null; type = type.BaseType)
        {
            FieldInfo field = type.GetField(name, AnyInstance | BindingFlags.DeclaredOnly);
            if (field != null) return field.GetValue(instance);
        }
        return null;
    }

    private static void Log(ILogger logger, Action<ILogger> write)
    {
        try { write(logger); }
        catch (Exception) { /* The logger may already be closed. */ }
    }
}
