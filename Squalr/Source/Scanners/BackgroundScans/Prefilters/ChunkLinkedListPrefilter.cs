﻿namespace Squalr.Source.Scanners.BackgroundScans.Prefilters
{
    using ActionScheduler;
    using Output;
    using Snapshots;
    using Squalr.Properties;
    using SqualrCore.Source.Engine;
    using SqualrCore.Source.Engine.OperatingSystems;
    using SqualrCore.Source.Engine.Processes;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Utils;
    using Utils.Extensions;

    /// <summary>
    /// <para>
    ///     SnapshotPrefilter is a heuristic process that drastically improves scan speed. It capitalizes on the fact that
    ///     > 90% of memory remains constant in most processes, at least for a short time span. Users will
    ///     likely be hunting variables that are in the remaining 10%.
    /// </para>
    /// <para>
    ///     This is a heuristic because it assumes that the variable, or a variable in the same chunk on the same page,
    ///     has changed before the user requests a snapshot of the target processes memory.
    /// </para>
    /// <para>
    ///     Steps are as follows:
    ///     1) Update a queue of chunks to process, based on timestamp since last edit. Add or remove chunks that
    ///         become allocated or deallocated.
    ///     2) Cycle through x chunks (to be determined how many is reasonable), computing the hash of each. Skip chunks
    ///         that have already changed. We can indefinitely mark them as a dynamic region.
    ///     3) On snapshot request, we can do a grow+mask operation of current chunks against the current virtual pages.
    /// </para>
    /// </summary>
    internal class ChunkLinkedListPrefilter : ScheduledTask, ISnapshotPrefilter, IProcessObserver
    {
        /// <summary>
        /// The maximum number of chunks to process in a given update cycle.
        /// </summary>
        private const Int32 ChunkLimit = 0x2000;

        /// <summary>
        /// The maximum number of chunks to process in a given update cycle when ramping up.
        /// </summary>
        private const Int32 RampUpChunkLimit = 0x4000;

        /// <summary>
        /// The size of a chunk.
        /// </summary>
        private const Int32 ChunkSize = 0x2000;

        /// <summary>
        /// The time between each update cycle.
        /// </summary>
        private const Int32 RescanTime = 800;

        /// <summary>
        /// The time between each update cycle when ramping up.
        /// </summary>
        private const Int32 RampUpRescanTime = 200;

        /// <summary>
        /// Singleton instance of the <see cref="ChunkLinkedListPrefilter"/> class.
        /// </summary>
        private static Lazy<ISnapshotPrefilter> snapshotPrefilterInstance = new Lazy<ISnapshotPrefilter>(
            () => { return new ChunkLinkedListPrefilter(); },
            LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>
        /// Prevents a default instance of the <see cref="ChunkLinkedListPrefilter" /> class from being created.
        /// </summary>
        private ChunkLinkedListPrefilter() : base("Prefilter", isRepeated: true, trackProgress: true)
        {
            this.ChunkList = new LinkedList<RegionProperties>();
            this.ChunkLock = new Object();
            this.ElementLock = new Object();

            // Subscribe to process events (async call as to avoid locking on GetInstance() if engine is being constructed)
            Task.Run(() => { EngineCore.GetInstance().Processes.Subscribe(this); });
        }

        /// <summary>
        /// Gets or sets the current list of tracked chunks.
        /// </summary>
        private LinkedList<RegionProperties> ChunkList { get; set; }

        /// <summary>
        /// Gets or sets the access lock for our chunk list.
        /// </summary>
        private Object ChunkLock { get; set; }

        /// <summary>
        /// Gets or sets the access lock for individual chunks in the chunk list.
        /// </summary>
        private Object ElementLock { get; set; }

        /// <summary>
        /// Gets a singleton instance of the <see cref="ChunkLinkedListPrefilter"/> class.
        /// </summary>
        /// <returns>A singleton instance of the class.</returns>
        public static ISnapshotPrefilter GetInstance()
        {
            return ChunkLinkedListPrefilter.snapshotPrefilterInstance.Value;
        }

        /// <summary>
        /// Starts the update cycle for this prefilter.
        /// </summary>
        public void BeginPrefilter()
        {
            this.Schedule();
        }

        /// <summary>
        /// Gets the snapshot generated by the prefilter.
        /// </summary>
        /// <returns>The snapshot generated by the prefilter.</returns>
        public Snapshot GetPrefilteredSnapshot()
        {
            List<SnapshotRegion> regions = new List<SnapshotRegion>();

            lock (this.ChunkLock)
            {
                foreach (RegionProperties virtualPage in this.ChunkList)
                {
                    if (!virtualPage.HasChanged)
                    {
                        continue;
                    }

                    SnapshotRegion newRegion = new SnapshotRegion(virtualPage);
                    regions.Add(newRegion);
                }
            }

            // Create snapshot from valid regions, do standard expand/mask operations to catch lost bytes for larger data types
            Snapshot prefilteredSnapshot = new Snapshot(regions);
            prefilteredSnapshot.ExpandAllRegions((PrimitiveTypes.GetLargestPrimitiveSize() - 1).ToUInt64());

            return prefilteredSnapshot;
        }

        /// <summary>
        /// Recieves a process update.
        /// </summary>
        /// <param name="process">The newly selected process.</param>>
        public void Update(NormalizedProcess process)
        {
            lock (this.ChunkLock)
            {
                if (this.ChunkList.Count > 0)
                {
                    this.ChunkList.Clear();
                    OutputViewModel.GetInstance().Log(OutputViewModel.LogLevel.Info, "Prefilter cleared");
                }
            }
        }

        /// <summary>
        /// Starts the prefilter.
        /// </summary>
        protected override void OnBegin()
        {
            this.UpdateInterval = ChunkLinkedListPrefilter.RampUpRescanTime;

            base.OnBegin();
        }

        /// <summary>
        /// Updates the prefilter.
        /// </summary>
        protected override void OnUpdate()
        {
            this.ProcessPages();

            lock (this.ChunkLock)
            {
                this.UpdateProgress(this.ChunkList.Where(x => x.IsProcessed()).Count(), this.ChunkList.Count());
                this.IsTaskComplete = this.IsProgressComplete;
            }

            // Set rescan time based on whether or not we have already cycled through all the pages
            if (this.IsTaskComplete)
            {
                this.UpdateInterval = ChunkLinkedListPrefilter.RescanTime;
            }
            else
            {
                this.UpdateInterval = ChunkLinkedListPrefilter.RampUpRescanTime;
            }

            base.OnUpdate();
        }

        protected override void OnEnd()
        {
            base.OnEnd();
        }

        /// <summary>
        /// Queries virtual pages from the OS to dertermine if any allocations or deallocations have happened.
        /// </summary>
        /// <returns>The collected pages.</returns>
        private IEnumerable<RegionProperties> CollectNewPages()
        {
            List<RegionProperties> newRegions = new List<RegionProperties>();

            // Gather current regions from the target process
            IEnumerable<NormalizedRegion> queriedVirtualRegions = SnapshotManager.GetInstance().CreateSnapshotFromSettings().GetSnapshotRegions();
            List<NormalizedRegion> queriedChunkedRegions = new List<NormalizedRegion>();

            // Chunk all virtual regions into a standardized size
            queriedVirtualRegions.ForEach(x => queriedChunkedRegions.AddRange(x.ChunkNormalizedRegion(ChunkLinkedListPrefilter.ChunkSize)));

            // Sort our lists (descending)
            IOrderedEnumerable<NormalizedRegion> queriedRegionsSorted = queriedChunkedRegions.OrderByDescending(x => x.BaseAddress.ToUInt64());
            IOrderedEnumerable<RegionProperties> currentRegionsSorted;

            lock (this.ChunkLock)
            {
                currentRegionsSorted = this.ChunkList.OrderByDescending(x => x.BaseAddress.ToUInt64());
            }

            // Create comparison stacks
            Stack<RegionProperties> currentRegionStack = new Stack<RegionProperties>();
            Stack<NormalizedRegion> queriedRegionStack = new Stack<NormalizedRegion>();

            currentRegionsSorted.ForEach(x => currentRegionStack.Push(x));
            queriedRegionsSorted.ForEach(x => queriedRegionStack.Push(x));

            // Begin stack based comparison algorithm to resolve our chunk processing queue
            NormalizedRegion queriedRegion = queriedRegionStack.Count == 0 ? null : queriedRegionStack.Pop();
            RegionProperties currentRegion = currentRegionStack.Count == 0 ? null : currentRegionStack.Pop();

            while (queriedRegionStack.Count > 0 && currentRegionStack.Count > 0)
            {
                if (queriedRegion < currentRegion)
                {
                    // New region we have not seen yet
                    newRegions.Add(new RegionProperties(queriedRegion));
                    queriedRegion = queriedRegionStack.Pop();
                }
                else if (queriedRegion > currentRegion)
                {
                    // Region that went missing (deallocated)
                    currentRegion = currentRegionStack.Pop();
                }
                else
                {
                    // Region we have already seen
                    queriedRegion = queriedRegionStack.Pop();
                    currentRegion = currentRegionStack.Pop();
                }
            }

            // Add remaining queried regions
            while (queriedRegionStack.Count > 0)
            {
                newRegions.Add(new RegionProperties(queriedRegion));
                queriedRegion = queriedRegionStack.Pop();
            }

            return newRegions;
        }

        /// <summary>
        /// Processes all pages, computing checksums to determine chunks of virtual pages that have changed
        /// </summary>
        private void ProcessPages()
        {
            // Check for newly allocated pages
            lock (this.ChunkLock)
            {
                foreach (RegionProperties newPage in this.CollectNewPages())
                {
                    this.ChunkList.AddFirst(newPage);
                }
            }

            Int32 chunkLimit;

            if (this.IsTaskComplete)
            {
                chunkLimit = ChunkLinkedListPrefilter.ChunkLimit;
            }
            else
            {
                chunkLimit = ChunkLinkedListPrefilter.RampUpChunkLimit;
            }

            lock (this.ChunkLock)
            {
                // Process the allowed amount of chunks from the priority queue
                Parallel.For(
                    0,
                    Math.Min(this.ChunkList.Count, chunkLimit),
                    SettingsViewModel.GetInstance().ParallelSettingsFast,
                    index =>
                {
                    RegionProperties chunk;
                    Boolean success = false;

                    // Grab next available element
                    lock (this.ElementLock)
                    {
                        chunk = this.ChunkList.FirstOrDefault();

                        if (chunk == null)
                        {
                            return;
                        }

                        this.ChunkList.RemoveFirst();

                        // Do not process chunks that have been marked as changed
                        if (chunk.HasChanged)
                        {
                            this.ChunkList.AddLast(chunk);
                            return;
                        }
                    }

                    // Read current page data for chunk
                    Byte[] pageData = EngineCore.GetInstance().OperatingSystem?.ReadBytes(chunk.BaseAddress, chunk.RegionSize.ToInt32(), out success);

                    // Read failed; Deallocated page
                    if (!success)
                    {
                        return;
                    }

                    // Update chunk
                    chunk.Update(pageData);

                    // Recycle it
                    lock (this.ElementLock)
                    {
                        ChunkList.AddLast(chunk);
                    }
                });
            }
        }

        /// <summary>
        /// A class to keep track of state within a tracked chunk of memory.
        /// </summary>
        internal class RegionProperties : NormalizedRegion
        {
            /// <summary>
            /// Initializes a new instance of the <see cref="RegionProperties" /> class.
            /// </summary>
            /// <param name="region">The region that this chunk spans.</param>
            public RegionProperties(NormalizedRegion region) : this(region.BaseAddress, region.RegionSize)
            {
            }

            /// <summary>
            /// Initializes a new instance of the <see cref="RegionProperties" /> class.
            /// </summary>
            /// <param name="baseAddress">The start address of this chunk.</param>
            /// <param name="regionSize">The size of this chunk.</param>
            public RegionProperties(IntPtr baseAddress, UInt64 regionSize) : base(baseAddress, regionSize)
            {
                this.Checksum = null;
                this.HasChanged = false;
            }

            /// <summary>
            /// Gets a value indicating whether there has been an observed change in this region.
            /// </summary>
            public Boolean HasChanged { get; private set; }

            /// <summary>
            /// Gets or sets the last computed checksum of this chunk.
            /// </summary>
            private UInt64? Checksum { get; set; }

            /// <summary>
            /// Determines if a checksum has ever been computed for this chunk.
            /// </summary>
            /// <returns>True if a checksum has been computed, otherwise false.</returns>
            public Boolean IsProcessed()
            {
                if (this.Checksum == null)
                {
                    return false;
                }

                return true;
            }

            /// <summary>
            /// Processes the provided bytes associated with this chunk to compute the checksum and determine if there are changes.
            /// </summary>
            /// <param name="memory">The bytes read from memory in this chunk.</param>
            public void Update(Byte[] memory)
            {
                UInt64 currentChecksum;

                currentChecksum = Hashing.ComputeCheckSum(memory);

                if (this.Checksum == null)
                {
                    this.Checksum = currentChecksum;
                    return;
                }

                this.HasChanged = this.Checksum != currentChecksum;
            }
        }
        //// End class
    }
    //// End class
}
//// End namespace