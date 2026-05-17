using System.Collections.Concurrent;

namespace ValheimHighCap.Util
{
    /// <summary>
    /// Thread-safe pool for <see cref="ZPackage"/> instances.
    ///
    /// ZPackages are allocated frequently in <c>ZRoutedRpc.RouteRPC</c> and
    /// <c>ZDOMan.SendZDOs</c>; a pool eliminates repeated heap allocations and
    /// reduces GC pressure during high-player-count send cycles.
    ///
    /// Usage:
    ///   var pkg = ZPackagePool.Rent();
    ///   try   { pkg.Write(someData); peer.m_rpc.Invoke("RoutedRPC", pkg); }
    ///   finally { ZPackagePool.Return(pkg); }
    ///
    /// Safety notes:
    ///   • <see cref="ZPackage.Clear"/> resets the internal MemoryStream position and
    ///     length to zero — the object is safe to reuse after <see cref="Return"/>.
    ///   • Do NOT hold a rented ZPackage across await or yield boundaries.
    ///   • Return() is a no-op when the pool is at capacity; the package is discarded.
    /// </summary>
    public static class ZPackagePool
    {
        private static readonly ConcurrentQueue<ZPackage> s_pool = new ConcurrentQueue<ZPackage>();
        private const int MaxPoolSize = 512;

        /// <summary>
        /// Retrieve a cleared ZPackage from the pool, or allocate a new one.
        /// </summary>
        public static ZPackage Rent()
        {
            if (s_pool.TryDequeue(out ZPackage pkg))
            {
                pkg.Clear();
                return pkg;
            }
            return new ZPackage();
        }

        /// <summary>
        /// Return a ZPackage to the pool for later reuse.
        /// The caller must not access <paramref name="pkg"/> after calling this.
        /// </summary>
        public static void Return(ZPackage pkg)
        {
            // Guard against pool overflow; discard surplus packages silently.
            if (s_pool.Count < MaxPoolSize)
                s_pool.Enqueue(pkg);
        }

        /// <summary>
        /// Number of packages currently available in the pool.
        /// Exposed for the metrics monitor.
        /// </summary>
        public static int PoolSize => s_pool.Count;
    }
}
