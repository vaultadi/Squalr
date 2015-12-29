﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Binarysharp.MemoryManagement;
using Binarysharp.MemoryManagement.Memory;

namespace Anathema
{
    class FilterChunkScan : IFilterChunkScanModel
    {
        
        // Variables
        private List<MemoryChunkRoots> ChunkRoots;
        private Snapshot InitialSnapshot;
        private Int32 ChunkSize;
        private Int32 MinChanges;

        public FilterChunkScan()
        {

        }

        public override void SetChunkSize(Int32 ChunkSize)
        {
            this.ChunkSize = ChunkSize;
        }

        public override void SetMinChanges(Int32 MinChanges)
        {
            this.MinChanges = MinChanges;
        }

        public override void BeginScan()
        {
            InitialSnapshot = SnapshotManager.GetInstance().GetActiveSnapshot();
            this.ChunkRoots = new List<MemoryChunkRoots>();

            // Initialize filter tree roots
            List<RemoteRegion> MemoryRegions = InitialSnapshot.GetMemoryRegions();
            for (int PageIndex = 0; PageIndex < MemoryRegions.Count; PageIndex++)
                ChunkRoots.Add(new MemoryChunkRoots(MemoryRegions[PageIndex], ChunkSize));
            
            base.BeginScan();
        }

        protected override void UpdateScan()
        {
            InitialSnapshot.ReadAllMemory();

            Parallel.ForEach(ChunkRoots, (ChunkRoot) =>
            {
                Byte[] PageData = InitialSnapshot.GetCurrentMemoryValues()[ChunkRoots.IndexOf(ChunkRoot)];

                // Process the changes that have occurred since the last sampling for this memory page
                if (PageData != null)
                {
                    ChunkRoot.ProcessChanges(PageData);
                }
                // Error reading this page -- kill it (may have been deallocated)
                else
                {
                    ChunkRoot.KillRegion();
                }
            });
        }

        public override void EndScan()
        {
            // Wait for the filter to finish
            base.EndScan();

            // Collect the pages that have changed
            List<RemoteRegion> ChangedRegions = new List<RemoteRegion>();
            for (Int32 Index = 0; Index < ChunkRoots.Count; Index++)
                ChunkRoots[Index].GetChangedRegions(ChangedRegions, MinChanges);
            ChunkRoots = null;

            // Convert trees to a list of memory regions
            List<RemoteRegion> FilteredRegions = ChangedRegions.ConvertAll(Page => (RemoteRegion)Page);

            // Create snapshot with results
            Snapshot FilteredSnapshot = new Snapshot(FilteredRegions);

            // Grow regions by the size of the largest standard variable and mask this with the original memory list.
            FilteredSnapshot.GrowRegions(sizeof(UInt64));
            FilteredSnapshot.MaskRegions(InitialSnapshot);

            // Send the size of the filtered memory to the GUI
            FilterChunksEventArgs Args = new FilterChunksEventArgs();
            Args.FilterResultSize = FilteredSnapshot.GetSize();
            OnEventUpdateMemorySize(Args);

            // Save result
            SnapshotManager.GetInstance().SaveSnapshot(FilteredSnapshot);
        }

        public class MemoryChunkRoots : RemoteRegion
        {
            private Boolean Dead;
            private RemoteRegion[] Chunks;
            private UInt16[] ChangeCounts;
            private UInt32?[] Checksums;

            public MemoryChunkRoots(RemoteRegion Region, Int32 ChunkSize) : base(null, Region.BaseAddress, Region.RegionSize)
            {
                // Initialize state variables
                Int32 ChunkCount = RegionSize / ChunkSize + 1;
                IntPtr CurrentBase = Region.BaseAddress;
                Chunks = new RemoteRegion[ChunkCount];
                ChangeCounts = new UInt16[ChunkCount];
                Checksums = new UInt32?[ChunkCount];
                Dead = false;

                // Initialize all chunks and checksums
                for (Int32 Index = 0; Index < ChunkCount; Index++)
                {
                    Int32 ChunkRegionSize = ChunkSize;

                    if (Index == ChunkCount - 1)
                        ChunkRegionSize = RegionSize % ChunkSize;

                    Chunks[Index] = new RemoteRegion(null, CurrentBase, ChunkRegionSize);
                    ChangeCounts[Index] = 0;
                    Checksums[Index] = 0;

                    CurrentBase += ChunkSize;
                }
            }

            public void GetChangedRegions(List<RemoteRegion> AcceptedRegions, Int32 MinChanges)
            {
                if (IsDead())
                    return;

                for (Int32 Index = 0; Index < Chunks.Length; Index++)
                {
                    if (ChangeCounts[Index] >= MinChanges)
                        AcceptedRegions.Add(Chunks[Index]);
                }
            }

            public void KillRegion()
            {
                Dead = true;
            }

            public Boolean IsDead()
            {
                return Dead;
            }

            public void ProcessChanges(Byte[] Data)
            {
                for (Int32 Index = 0; Index < Chunks.Length; Index++)
                {
                    UInt32 NewChecksum = Hashing.ComputeCheckSum(Data, 
                        (UInt32)((UInt64)Chunks[Index].BaseAddress - (UInt64)this.BaseAddress),
                        (UInt32)((UInt64)Chunks[Index].EndAddress - (UInt64)this.BaseAddress));

                    if (Checksums[Index].HasValue && Checksums[Index] != NewChecksum)
                        ChangeCounts[Index]++;

                    Checksums[Index] = NewChecksum;
                }
            }

        } // End class

    } // End class

} // End namespace