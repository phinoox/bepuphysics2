﻿using BepuUtilities;
using BepuUtilities.Collections;
using BepuUtilities.Memory;
using BepuPhysics.Constraints;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Runtime.Intrinsics.X86;
using System.Numerics;
using System.Runtime.Intrinsics;

namespace BepuPhysics
{
    public partial class Solver
    {
        public virtual void PrepareConstraintIntegrationResponsibilities(IThreadDispatcher threadDispatcher = null)
        {
        }
        public virtual void DisposeConstraintIntegrationResponsibilities()
        {
        }

        public virtual void SolveStep2(float dt, IThreadDispatcher threadDispatcher = null)
        {

        }
    }
    public class Solver<TIntegrationCallbacks> : Solver where TIntegrationCallbacks : struct, IPoseIntegratorCallbacks
    {
        public Solver(Bodies bodies, BufferPool pool, int iterationCount, int fallbackBatchThreshold,
            int initialCapacity,
            int initialIslandCapacity,
            int minimumCapacityPerTypeBatch, PoseIntegrator<TIntegrationCallbacks> poseIntegrator)
            : base(bodies, pool, iterationCount, fallbackBatchThreshold, initialCapacity, initialIslandCapacity, minimumCapacityPerTypeBatch)
        {
            PoseIntegrator = poseIntegrator;
            solveStep2Worker = SolveStep2Worker;
            constraintIntegrationResponsibilitiesWorker = ConstraintIntegrationResponsibilitiesWorker;
        }

        public PoseIntegrator<TIntegrationCallbacks> PoseIntegrator { get; private set; }

        //Split the solve process into a warmstart and solve, where warmstart doesn't try to store out anything. It just computes jacobians and modifies velocities according to the accumulated impulse.
        //The solve step then *recomputes* jacobians from prestep data and pose information.
        //Why? Memory bandwidth. Redoing the calculation is cheaper than storing it out.
        struct WarmStartStep2StageFunction : IStageFunction
        {
            public float Dt;
            public float InverseDt;
            public Solver<TIntegrationCallbacks> solver;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Execute(Solver solver, int blockIndex, int workerIndex)
            {
                ref var block = ref this.solver.context.ConstraintBlocks.Blocks[blockIndex];
                ref var typeBatch = ref solver.ActiveSet.Batches[block.BatchIndex].TypeBatches[block.TypeBatchIndex];
                var typeProcessor = solver.TypeProcessors[typeBatch.TypeId];
                if (block.BatchIndex == 0)
                {
                    Buffer<IndexSet> noFlagsRequired = default;
                    typeProcessor.WarmStart2<TIntegrationCallbacks, BatchShouldAlwaysIntegrate>(
                        ref typeBatch, ref noFlagsRequired, this.solver.bodies, ref this.solver.PoseIntegrator.Callbacks,
                        Dt, InverseDt, block.StartBundle, block.End, workerIndex);
                }
                else
                {
                    if (this.solver.coarseBatchIntegrationResponsibilities[block.BatchIndex][block.TypeBatchIndex])
                    {
                        typeProcessor.WarmStart2<TIntegrationCallbacks, BatchShouldConditionallyIntegrate>(
                            ref typeBatch, ref this.solver.integrationFlags[block.BatchIndex][block.TypeBatchIndex], this.solver.bodies, ref this.solver.PoseIntegrator.Callbacks,
                            Dt, InverseDt, block.StartBundle, block.End, workerIndex);
                    }
                    else
                    {
                        typeProcessor.WarmStart2<TIntegrationCallbacks, BatchShouldNeverIntegrate>(
                            ref typeBatch, ref this.solver.integrationFlags[block.BatchIndex][block.TypeBatchIndex], this.solver.bodies, ref this.solver.PoseIntegrator.Callbacks,
                            Dt, InverseDt, block.StartBundle, block.End, workerIndex);
                    }
                }
            }
        }
        //no fallback warmstart; the last constraint batch is always handled by the solve instead, and if the fallback batch exists, it's guaranteed to be the last batch.

        struct SolveStep2StageFunction : IStageFunction
        {
            public float Dt;
            public float InverseDt;
            public Solver<TIntegrationCallbacks> solver;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public void Execute(Solver solver, int blockIndex, int workerIndex)
            {
                ref var block = ref this.solver.context.ConstraintBlocks.Blocks[blockIndex];
                ref var typeBatch = ref solver.ActiveSet.Batches[block.BatchIndex].TypeBatches[block.TypeBatchIndex];
                var typeProcessor = solver.TypeProcessors[typeBatch.TypeId];
                typeProcessor.SolveStep2(ref typeBatch, solver.bodies, Dt, InverseDt, block.StartBundle, block.End);
            }
        }

        //struct FallbackSolveStep2StageFunction : IStageFunction
        //{
        //    public float Dt;
        //    public float InverseDt;

        //    [MethodImpl(MethodImplOptions.AggressiveInlining)]
        //    public void Execute(Solver solver, int blockIndex)
        //    {
        //        ref var block = ref solver.context.ConstraintBlocks.Blocks[blockIndex];
        //        ref var typeBatch = ref solver.ActiveSet.Batches[block.BatchIndex].TypeBatches[block.TypeBatchIndex];
        //        var typeProcessor = solver.TypeProcessors[typeBatch.TypeId];
        //        typeProcessor.JacobiSolveStep2(ref typeBatch, solver.bodies, ref solver.ActiveSet.Fallback, ref solver.context.FallbackResults[block.TypeBatchIndex], Dt, InverseDt, block.StartBundle, block.End);
        //    }
        //}

        Action<int> solveStep2Worker;
        void SolveStep2Worker(int workerIndex)
        {
            int fallbackStart = GetUniformlyDistributedStart(workerIndex, context.FallbackBlocks.Blocks.Count, context.WorkerCount, 0);
            Buffer<int> batchStarts;
            ref var activeSet = ref ActiveSet;
            unsafe
            {
                var batchStartsData = stackalloc int[activeSet.Batches.Count];
                batchStarts = new Buffer<int>(batchStartsData, activeSet.Batches.Count);
            }
            for (int batchIndex = 0; batchIndex < activeSet.Batches.Count; ++batchIndex)
            {
                var batchOffset = batchIndex > 0 ? context.BatchBoundaries[batchIndex - 1] : 0;
                var batchCount = context.BatchBoundaries[batchIndex] - batchOffset;
                batchStarts[batchIndex] = GetUniformlyDistributedStart(workerIndex, batchCount, context.WorkerCount, batchOffset);
            }

            int syncStage = 0;
            //The claimed and unclaimed state swap after every usage of both pingpong claims buffers.
            int claimedState = 1;
            int unclaimedState = 0;
            var bounds = context.WorkerBoundsA;
            var boundsBackBuffer = context.WorkerBoundsB;
            //Note that every batch has a different start position. Each covers a different subset of constraints, so they require different start locations.
            //The same concept applies to the prestep- the prestep covers all constraints at once, rather than batch by batch.
            Debug.Assert(activeSet.Batches.Count > 0, "Don't dispatch if there are no constraints.");
            GetSynchronizedBatchCount(out var synchronizedBatchCount, out var fallbackExists);

            var warmstartStage = new WarmStartStep2StageFunction
            {
                Dt = context.Dt,
                InverseDt = 1f / context.Dt,
                solver = this
            };
            for (int batchIndex = 0; batchIndex < synchronizedBatchCount; ++batchIndex)
            {
                var batchOffset = batchIndex > 0 ? context.BatchBoundaries[batchIndex - 1] : 0;
                ExecuteStage(ref warmstartStage, ref context.ConstraintBlocks, ref bounds, ref boundsBackBuffer, workerIndex, batchOffset, context.BatchBoundaries[batchIndex],
                    ref batchStarts[batchIndex], ref syncStage, claimedState, unclaimedState);
            }
            claimedState ^= 1;
            unclaimedState ^= 1;
            var solveStage = new SolveStep2StageFunction
            {
                Dt = context.Dt,
                InverseDt = 1f / context.Dt,
                solver = this
            };
            for (int batchIndex = 0; batchIndex < synchronizedBatchCount; ++batchIndex)
            {
                var batchOffset = batchIndex > 0 ? context.BatchBoundaries[batchIndex - 1] : 0;
                ExecuteStage(ref solveStage, ref context.ConstraintBlocks, ref bounds, ref boundsBackBuffer, workerIndex, batchOffset, context.BatchBoundaries[batchIndex],
                    ref batchStarts[batchIndex], ref syncStage, claimedState, unclaimedState);
            }
            //if (fallbackExists)
            //{
            //    var solveFallbackStage = new FallbackSolveStepStageFunction();
            //    var fallbackScatterStage = new FallbackScatterStageFunction();
            //    var batchOffset = FallbackBatchThreshold > 0 ? context.BatchBoundaries[FallbackBatchThreshold - 1] : 0;
            //    ExecuteStage(ref solveFallbackStage, ref context.ConstraintBlocks, ref bounds, ref boundsBackBuffer, workerIndex, batchOffset, context.BatchBoundaries[FallbackBatchThreshold],
            //        ref batchStarts[FallbackBatchThreshold], ref syncStage, claimedState, unclaimedState);
            //    ExecuteStage(ref fallbackScatterStage, ref context.FallbackBlocks, ref bounds, ref boundsBackBuffer,
            //        workerIndex, 0, context.FallbackBlocks.Blocks.Count, ref fallbackStart, ref syncStage, claimedState, unclaimedState);
            //}
            claimedState ^= 1;
            unclaimedState ^= 1;
        }


        unsafe bool ComputeIntegrationResponsibilitiesForConstraintRegion(int batchIndex, int typeBatchIndex, int constraintStart, int exclusiveConstraintEnd)
        {
            ref var firstObservedForBatch = ref bodiesFirstObservedInBatches[batchIndex];
            ref var integrationFlagsForTypeBatch = ref integrationFlags[batchIndex][typeBatchIndex];
            ref var typeBatch = ref ActiveSet.Batches[batchIndex].TypeBatches[typeBatchIndex];
            var typeBatchBodyReferences = typeBatch.BodyReferences.As<int>();
            var bodiesPerConstraintInTypeBatch = TypeProcessors[typeBatch.TypeId].BodiesPerConstraint;
            var intsPerBundle = Vector<int>.Count * bodiesPerConstraintInTypeBatch;
            var bundleStartIndex = constraintStart / Vector<float>.Count;
            var bundleEndIndex = (exclusiveConstraintEnd + Vector<float>.Count - 1) / Vector<float>.Count;
            Debug.Assert(bundleStartIndex >= 0 && bundleEndIndex <= typeBatch.BundleCount);

            for (int bundleIndex = bundleStartIndex; bundleIndex < bundleEndIndex; ++bundleIndex)
            {
                int bundleStartIndexInConstraints = bundleIndex * Vector<int>.Count;
                int countInBundle = Math.Min(Vector<float>.Count, typeBatch.ConstraintCount - bundleStartIndexInConstraints);
                //Body references are stored in AOSOA layout.
                var bundleBodyReferencesStart = typeBatchBodyReferences.Memory + bundleIndex * intsPerBundle;
                for (int bodyIndexInConstraint = 0; bodyIndexInConstraint < bodiesPerConstraintInTypeBatch; ++bodyIndexInConstraint)
                {
                    ref var integrationFlagsForBodyInConstraint = ref integrationFlagsForTypeBatch[bodyIndexInConstraint];
                    var bundleStart = bundleBodyReferencesStart + bodyIndexInConstraint * Vector<int>.Count;
                    for (int bundleInnerIndex = 0; bundleInnerIndex < countInBundle; ++bundleInnerIndex)
                    {
                        //Constraints refer to bodies by index when they're in the active set, so we need to transform to handle to look up our merged batch results.
                        var bodyHandle = bodies.ActiveSet.IndexToHandle[bundleStart[bundleInnerIndex]].Value;
                        if (firstObservedForBatch.Contains(bodyHandle))
                        {
                            integrationFlagsForBodyInConstraint.AddUnsafely(bundleStartIndexInConstraints + bundleInnerIndex);
                        }
                    }
                }
            }
            //Precompute which type batches have *any* integration responsibilities, allowing us to use a all-or-nothing test before dispatching a workblock.
            var flagBundleCount = IndexSet.GetBundleCapacity(typeBatch.ConstraintCount);
            ulong mergedFlagBundles = 0;
            for (int bodyIndexInConstraint = 0; bodyIndexInConstraint < bodiesPerConstraintInTypeBatch; ++bodyIndexInConstraint)
            {
                ref var integrationFlagsForBodyInTypeBatch = ref integrationFlagsForTypeBatch[bodyIndexInConstraint];
                for (int i = 0; i < flagBundleCount; ++i)
                {
                    mergedFlagBundles |= integrationFlagsForBodyInTypeBatch.Flags[i];
                }
            }
            return mergedFlagBundles != 0;
        }

        void ConstraintIntegrationResponsibilitiesWorker(int workerIndex)
        {
            int jobIndex;
            while ((jobIndex = Interlocked.Increment(ref nextConstraintIntegrationResponsibilityJobIndex) - 1) < integrationResponsibilityPrepassJobs.Count)
            {
                ref var job = ref integrationResponsibilityPrepassJobs[jobIndex];
                jobAlignedIntegrationResponsibilities[jobIndex] = ComputeIntegrationResponsibilitiesForConstraintRegion(job.batch, job.typeBatch, job.start, job.end);
            }
        }

        int nextConstraintIntegrationResponsibilityJobIndex;
        QuickList<(int batch, int typeBatch, int start, int end)> integrationResponsibilityPrepassJobs;
        Buffer<bool> jobAlignedIntegrationResponsibilities;
        Buffer<IndexSet> bodiesFirstObservedInBatches;
        Buffer<Buffer<Buffer<IndexSet>>> integrationFlags;
        /// <summary>
        /// Caches a single bool for whether type batches within batches have constraints with any integration responsibilities.
        /// Type batches with no integration responsibilities can use a codepath with no integration checks at all.
        /// </summary>
        Buffer<Buffer<bool>> coarseBatchIntegrationResponsibilities;
        Action<int> constraintIntegrationResponsibilitiesWorker;

        public override unsafe void PrepareConstraintIntegrationResponsibilities(IThreadDispatcher threadDispatcher = null)
        {
            //var start = Stopwatch.GetTimestamp();
            pool.Take(ActiveSet.Batches.Count, out integrationFlags);
            integrationFlags[0] = default;
            pool.Take(ActiveSet.Batches.Count, out coarseBatchIntegrationResponsibilities);
            for (int batchIndex = 1; batchIndex < integrationFlags.Length; ++batchIndex)
            {
                ref var batch = ref ActiveSet.Batches[batchIndex];
                ref var flagsForBatch = ref integrationFlags[batchIndex];
                pool.Take(batch.TypeBatches.Count, out flagsForBatch);
                pool.Take(batch.TypeBatches.Count, out coarseBatchIntegrationResponsibilities[batchIndex]);
                for (int typeBatchIndex = 0; typeBatchIndex < flagsForBatch.Length; ++typeBatchIndex)
                {
                    ref var flagsForTypeBatch = ref flagsForBatch[typeBatchIndex];
                    ref var typeBatch = ref batch.TypeBatches[typeBatchIndex];
                    var bodiesPerConstraint = TypeProcessors[typeBatch.TypeId].BodiesPerConstraint;
                    pool.Take(bodiesPerConstraint, out flagsForTypeBatch);
                    for (int bodyIndexInConstraint = 0; bodyIndexInConstraint < bodiesPerConstraint; ++bodyIndexInConstraint)
                    {
                        flagsForTypeBatch[bodyIndexInConstraint] = new IndexSet(pool, typeBatch.ConstraintCount);
                    }
                }
            }
            //for (int i = 0; i < bodies.ActiveSet.Count; ++i)
            //{
            //    ref var constraints = ref bodies.ActiveSet.Constraints[i];
            //    ConstraintHandle minimumConstraint;
            //    minimumConstraint.Value = -1;
            //    int minimumBatchIndex = int.MaxValue;
            //    int minimumIndexInConstraint = -1;
            //    for (int j = 0; j < constraints.Count; ++j)
            //    {
            //        ref var constraint = ref constraints[j];
            //        var batchIndex = HandleToConstraint[constraint.ConnectingConstraintHandle.Value].BatchIndex;
            //        if (batchIndex < minimumBatchIndex)
            //        {
            //            minimumBatchIndex = batchIndex;
            //            minimumIndexInConstraint = constraint.BodyIndexInConstraint;
            //            minimumConstraint = constraint.ConnectingConstraintHandle;
            //        }
            //    }
            //    if (minimumConstraint.Value >= 0)
            //    {
            //        ref var location = ref HandleToConstraint[minimumConstraint.Value];
            //        var typeBatchIndex = ActiveSet.Batches[location.BatchIndex].TypeIndexToTypeBatchIndex[location.TypeId];
            //        ref var indexSet = ref integrationFlags[location.BatchIndex][typeBatchIndex][minimumIndexInConstraint];
            //        indexSet.AddUnsafely(location.IndexInTypeBatch);
            //    }
            //}
            //Console.WriteLine($"body count: {bodies.ActiveSet.Count}, constraint count: {CountConstraints()}");
            //var end = Stopwatch.GetTimestamp();
            //Console.WriteLine($"Brute force time (ms): {(end - start) * 1e3 / Stopwatch.Frequency}");

            pool.Take(batchReferencedHandles.Count, out bodiesFirstObservedInBatches);
            IndexSet merged;
            //We don't have to consider the first batch, since we know ahead of time that the first batch will be the first time we see any bodies in it.
            //Just copy directly from the first batch into the merged to initialize it.
            pool.Take((bodies.HandlePool.HighestPossiblyClaimedId + 63) / 64, out merged.Flags);
            var copyLength = Math.Min(merged.Flags.Length, batchReferencedHandles[0].Flags.Length);
            batchReferencedHandles[0].Flags.CopyTo(0, merged.Flags, 0, copyLength);
            batchReferencedHandles[0].Flags.Clear(copyLength, batchReferencedHandles[0].Flags.Length - copyLength);

            //Yup, we're just leaving the first slot unallocated to avoid having to offset indices all over the place. Slight wonk, but not a big deal.
            bodiesFirstObservedInBatches[0] = default;
            pool.Take<bool>(batchReferencedHandles.Count, out var batchHasAnyIntegrationResponsibilities);
            for (int batchIndex = 1; batchIndex < bodiesFirstObservedInBatches.Length; ++batchIndex)
            {
                ref var batchHandles = ref batchReferencedHandles[batchIndex];
                var bundleCount = Math.Min(merged.Flags.Length, batchHandles.Flags.Length);
                //Note that we bypass the constructor to avoid zeroing unnecessarily. Every bundle will be fully assigned.
                pool.Take(bundleCount, out bodiesFirstObservedInBatches[batchIndex].Flags);
            }
            //Note that we are not multithreading the batch merging phase. This typically takes a handful of microseconds.
            //You'd likely need millions of bodies before you'd see any substantial benefit from multithreading this.
            for (int batchIndex = 1; batchIndex < ActiveSet.Batches.Count; ++batchIndex)
            {
                ref var batchHandles = ref batchReferencedHandles[batchIndex];
                ref var firstObservedInBatch = ref bodiesFirstObservedInBatches[batchIndex];
                var flagBundleCount = Math.Min(merged.Flags.Length, batchHandles.Flags.Length);
                if (Avx2.IsSupported)
                {
                    var avxBundleCount = flagBundleCount / 4;
                    var horizontalAvxMerge = Vector256<ulong>.Zero;
                    for (int avxBundleIndex = 0; avxBundleIndex < avxBundleCount; ++avxBundleIndex)
                    {
                        var mergeBundle = ((Vector256<ulong>*)merged.Flags.Memory)[avxBundleIndex];
                        var batchBundle = ((Vector256<ulong>*)batchHandles.Flags.Memory)[avxBundleIndex];
                        ((Vector256<ulong>*)merged.Flags.Memory)[avxBundleIndex] = Avx2.Or(mergeBundle, batchBundle);
                        //If this batch contains a body, and the merged set does not, then it's the first batch that sees a body and it will have integration responsibility.
                        var firstObservedBundle = Avx2.AndNot(mergeBundle, batchBundle);
                        horizontalAvxMerge = Avx2.Or(firstObservedBundle, horizontalAvxMerge);
                        ((Vector256<ulong>*)firstObservedInBatch.Flags.Memory)[avxBundleIndex] = firstObservedBundle;
                    }
                    var notEqual = Avx2.CompareNotEqual(horizontalAvxMerge.AsDouble(), Vector256<double>.Zero);
                    ulong horizontalMerge = (ulong)Avx.MoveMask(notEqual);

                    //Cleanup loop.
                    for (int flagBundleIndex = avxBundleCount * 4; flagBundleIndex < flagBundleCount; ++flagBundleIndex)
                    {
                        var mergeBundle = merged.Flags[flagBundleIndex];
                        var batchBundle = batchHandles.Flags[flagBundleIndex];
                        merged.Flags[flagBundleIndex] = mergeBundle | batchBundle;
                        //If this batch contains a body, and the merged set does not, then it's the first batch that sees a body and it will have integration responsibility.
                        var firstObservedBundle = ~mergeBundle & batchBundle;
                        horizontalMerge |= firstObservedBundle;
                        firstObservedInBatch.Flags[flagBundleIndex] = firstObservedBundle;
                    }
                    batchHasAnyIntegrationResponsibilities[batchIndex] = horizontalMerge != 0;
                }
                else
                {
                    ulong horizontalMerge = 0;
                    for (int flagBundleIndex = 0; flagBundleIndex < flagBundleCount; ++flagBundleIndex)
                    {
                        var mergeBundle = merged.Flags[flagBundleIndex];
                        var batchBundle = batchHandles.Flags[flagBundleIndex];
                        merged.Flags[flagBundleIndex] = mergeBundle | batchBundle;
                        //If this batch contains a body, and the merged set does not, then it's the first batch that sees a body and it will have integration responsibility.
                        var firstObservedBundle = ~mergeBundle & batchBundle;
                        horizontalMerge |= firstObservedBundle;
                        firstObservedInBatch.Flags[flagBundleIndex] = firstObservedBundle;
                    }
                    batchHasAnyIntegrationResponsibilities[batchIndex] = horizontalMerge != 0;
                }
            }
            var start = Stopwatch.GetTimestamp();
            //We now have index sets representing the first time each body handle is observed in a batch.
            //This process is significantly more expensive than the batch merging phase and can benefit from multithreading.
            //It is still fairly cheap, though- we can't use really fine grained jobs or the cost of swapping jobs will exceed productive work.

            //Note that we arbitrarily use single threaded execution if the job is small enough. Dispatching isn't free.
            bool useSingleThreadedPath = true;
            if (threadDispatcher != null && threadDispatcher.ThreadCount > 1)
            {
                integrationResponsibilityPrepassJobs = new(128, pool);
                int constraintCount = 0;
                const int targetJobSize = 2048;
                Debug.Assert(targetJobSize % 64 == 0, "Target job size must be a multiple of the index set bundles to avoid threads working on the same flag bundle.");
                for (int batchIndex = 1; batchIndex < bodiesFirstObservedInBatches.Length; ++batchIndex)
                {
                    if (!batchHasAnyIntegrationResponsibilities[batchIndex])
                        continue;
                    ref var batch = ref ActiveSet.Batches[batchIndex];
                    for (int typeBatchIndex = 0; typeBatchIndex < batch.TypeBatches.Count; ++typeBatchIndex)
                    {
                        ref var typeBatch = ref batch.TypeBatches[typeBatchIndex];
                        constraintCount += typeBatch.ConstraintCount;
                        int jobCountForTypeBatch = (typeBatch.ConstraintCount + targetJobSize - 1) / targetJobSize;
                        for (int i = 0; i < jobCountForTypeBatch; ++i)
                        {
                            var jobStart = i * targetJobSize;
                            var jobEnd = Math.Min(jobStart + targetJobSize, typeBatch.ConstraintCount);
                            integrationResponsibilityPrepassJobs.Allocate(pool) = (batchIndex, typeBatchIndex, jobStart, jobEnd);
                        }
                    }
                }
                if (constraintCount > 4096 + threadDispatcher.ThreadCount * 1024)
                {
                    nextConstraintIntegrationResponsibilityJobIndex = 0;
                    useSingleThreadedPath = false;
                    pool.Take(integrationResponsibilityPrepassJobs.Count, out jobAlignedIntegrationResponsibilities);
                    //for (int i = 0; i < integrationResponsibilityPrepassJobs.Count; ++i)
                    //{
                    //    ref var job = ref integrationResponsibilityPrepassJobs[i];
                    //    jobAlignedIntegrationResponsibilities[i] = ComputeIntegrationResponsibilitiesForConstraintRegion(job.batch, job.typeBatch, job.start, job.end);
                    //}
                    threadDispatcher.DispatchWorkers(constraintIntegrationResponsibilitiesWorker);

                    //Coarse batch integration responsibilities start uninitialized. Possible to have multiple jobs per type batch in multithreaded case, so we need to init to merge.
                    for (int i = 1; i < ActiveSet.Batches.Count; ++i)
                    {
                        ref var batch = ref ActiveSet.Batches[i];
                        for (int j = 0; j < batch.TypeBatches.Count; ++j)
                        {
                            coarseBatchIntegrationResponsibilities[i][j] = false;
                        }
                    }
                    for (int i = 0; i < integrationResponsibilityPrepassJobs.Count; ++i)
                    {
                        ref var job = ref integrationResponsibilityPrepassJobs[i];
                        coarseBatchIntegrationResponsibilities[job.batch][job.typeBatch] |= jobAlignedIntegrationResponsibilities[i];
                    }
                    pool.Return(ref jobAlignedIntegrationResponsibilities);
                }
                integrationResponsibilityPrepassJobs.Dispose(pool);
            }
            if (useSingleThreadedPath)
            {
                for (int i = 1; i < ActiveSet.Batches.Count; ++i)
                {
                    if (!batchHasAnyIntegrationResponsibilities[i])
                        continue;
                    ref var batch = ref ActiveSet.Batches[i];
                    for (int j = 0; j < batch.TypeBatches.Count; ++j)
                    {
                        ref var typeBatch = ref batch.TypeBatches[j];
                        coarseBatchIntegrationResponsibilities[i][j] = ComputeIntegrationResponsibilitiesForConstraintRegion(i, j, 0, typeBatch.ConstraintCount);
                    }
                }


            }

            //for (int batchIndex = 1; batchIndex < bodiesFirstObservedInBatches.Length; ++batchIndex)
            //{
            //    if (!batchHasAnyIntegrationResponsibilities[batchIndex])
            //        continue;
            //    ref var integrationFlagsForBatch = ref integrationFlags[batchIndex];
            //    ref var firstObservedForBatch = ref bodiesFirstObservedInBatches[batchIndex];
            //    ref var batch = ref ActiveSet.Batches[batchIndex];

            //    //ulong totalConstraintCount = 0;
            //    //ulong integratingConstraintCount = 0;
            //    //ulong totalLanes = 0;
            //    //ulong integratingLanes = 0;

            //    for (int typeBatchIndex = 0; typeBatchIndex < batch.TypeBatches.Count; ++typeBatchIndex)
            //    {
            //        ref var integrationFlagsForTypeBatch = ref integrationFlagsForBatch[typeBatchIndex];
            //        ref var typeBatch = ref batch.TypeBatches[typeBatchIndex];
            //        var typeBatchBodyReferences = typeBatch.BodyReferences.As<int>();
            //        var bodiesPerConstraintInTypeBatch = TypeProcessors[typeBatch.TypeId].BodiesPerConstraint;
            //        var intsPerBundle = Vector<int>.Count * bodiesPerConstraintInTypeBatch;
            //        for (int bundleIndex = 0; bundleIndex < typeBatch.BundleCount; ++bundleIndex)
            //        {
            //            int bundleStartIndexInConstraints = bundleIndex * Vector<int>.Count;
            //            int countInBundle = Math.Min(Vector<float>.Count, typeBatch.ConstraintCount - bundleStartIndexInConstraints);
            //            //Body references are stored in AOSOA layout.
            //            var bundleBodyReferencesStart = typeBatchBodyReferences.Memory + bundleIndex * intsPerBundle;
            //            for (int bodyIndexInConstraint = 0; bodyIndexInConstraint < bodiesPerConstraintInTypeBatch; ++bodyIndexInConstraint)
            //            {
            //                ref var integrationFlagsForBodyInConstraint = ref integrationFlagsForTypeBatch[bodyIndexInConstraint];
            //                var bundleStart = bundleBodyReferencesStart + bodyIndexInConstraint * Vector<int>.Count;
            //                for (int bundleInnerIndex = 0; bundleInnerIndex < countInBundle; ++bundleInnerIndex)
            //                {
            //                    //Constraints refer to bodies by index when they're in the active set, so we need to transform to handle to look up our merged batch results.
            //                    var bodyHandle = bodies.ActiveSet.IndexToHandle[bundleStart[bundleInnerIndex]].Value;
            //                    if (firstObservedForBatch.Contains(bodyHandle))
            //                    {
            //                        integrationFlagsForBodyInConstraint.AddUnsafely(bundleStartIndexInConstraints + bundleInnerIndex);
            //                    }
            //                }
            //            }
            //        }
            //        //Precompute which type batches have *any* integration responsibilities, allowing us to use a all-or-nothing test before dispatching a workblock.
            //        var flagBundleCount = IndexSet.GetBundleCapacity(typeBatch.ConstraintCount);
            //        ulong mergedFlagBundles = 0;
            //        for (int bodyIndexInConstraint = 0; bodyIndexInConstraint < bodiesPerConstraintInTypeBatch; ++bodyIndexInConstraint)
            //        {
            //            ref var integrationFlagsForBodyInTypeBatch = ref integrationFlagsForTypeBatch[bodyIndexInConstraint];
            //            for (int i = 0; i < flagBundleCount; ++i)
            //            {
            //                mergedFlagBundles |= integrationFlagsForBodyInTypeBatch.Flags[i];
            //            }
            //        }
            //        coarseBatchIntegrationResponsibilities[batchIndex][typeBatchIndex] = mergedFlagBundles != 0;

            //        //for (int i = 0; i < flagBundleCount; ++i)
            //        //{
            //        //    ulong countMerge = 0;
            //        //    for (int bodyIndexInConstraint = 0; bodyIndexInConstraint < bodiesPerConstraintInTypeBatch; ++bodyIndexInConstraint)
            //        //    {
            //        //        var flagsForBody = integrationFlagsForTypeBatch[bodyIndexInConstraint].Flags[i];
            //        //        countMerge |= flagsForBody;
            //        //        integratingLanes += Popcnt.X64.PopCount(flagsForBody);
            //        //        totalLanes += (ulong)Math.Min(64, typeBatch.ConstraintCount - i * 64);
            //        //    }
            //        //    integratingConstraintCount += Popcnt.X64.PopCount(countMerge);
            //        //    totalConstraintCount += (ulong)Math.Min(64, typeBatch.ConstraintCount - i * 64);
            //        //}
            //    }
            //    //Console.WriteLine($"Batch {batchIndex} integrating constraints: {integratingConstraintCount} over {totalConstraintCount}, {integratingConstraintCount / (double)totalConstraintCount}");
            //    //Console.WriteLine($"Batch {batchIndex} integrating lanes:       {integratingLanes} over {totalLanes}, {integratingLanes / (double)totalLanes}");
            //}
            pool.Return(ref batchHasAnyIntegrationResponsibilities);
            //for (int batchIndex = 1; batchIndex < bodiesFirstObservedInBatches.Length; ++batchIndex)
            //{
            //    ref var integrationFlagsForBatch = ref integrationFlags[batchIndex];
            //    ref var firstObservedForBatch = ref bodiesFirstObservedInBatches[batchIndex];
            //    ref var batch = ref ActiveSet.Batches[batchIndex];
            //    for (int typeBatchIndex = 0; typeBatchIndex < batch.TypeBatches.Count; ++typeBatchIndex)
            //    {
            //        ref var integrationFlagsForTypeBatch = ref integrationFlagsForBatch[typeBatchIndex];
            //        ref var typeBatch = ref batch.TypeBatches[typeBatchIndex];
            //        var typeBatchBodyReferences = typeBatch.BodyReferences.As<int>();
            //        var bodiesPerConstraintInTypeBatch = TypeProcessors[typeBatch.TypeId].BodiesPerConstraint;
            //        var intsPerBundle = Vector<int>.Count * bodiesPerConstraintInTypeBatch;
            //        //We process over one strip of bodies in the constraint in each pass, constructing contiguous flag sequences as we go.
            //        for (int bodyIndexInConstraint = 0; bodyIndexInConstraint < bodiesPerConstraintInTypeBatch; ++bodyIndexInConstraint)
            //        {
            //            ref var integrationFlagsForBodyInConstraint = ref integrationFlagsForTypeBatch[bodyIndexInConstraint];
            //            for (int flagBundleIndex = 0; flagBundleIndex < integrationFlagsForBodyInConstraint.Flags.Length; ++flagBundleIndex)
            //            {
            //                ulong flagBundle = 0;
            //                var constraintBundleStartIndexInConstraints = flagBundleIndex * 64;
            //                int countInFlagBundle = Math.Min(64, typeBatch.ConstraintCount - constraintBundleStartIndexInConstraints);
            //                for (int indexInFlagBundle = 0; indexInFlagBundle < countInFlagBundle; ++indexInFlagBundle)
            //                {
            //                    var constraintIndex = constraintBundleStartIndexInConstraints + indexInFlagBundle;
            //                    BundleIndexing.GetBundleIndices(constraintIndex, out var constraintBundleIndex, out var constraintIndexInBundle);
            //                    var bodyHandle = bodies.ActiveSet.IndexToHandle[typeBatchBodyReferences.Memory[constraintBundleIndex * intsPerBundle + Vector<int>.Count * bodyIndexInConstraint]].Value;
            //                    if (firstObservedForBatch.Contains(bodyHandle))
            //                    {
            //                        integrationFlagsForBodyInConstraint.AddUnsafely(constraintIndex);
            //                    }
            //                }
            //                integrationFlagsForBodyInConstraint.Flags[flagBundleIndex] = flagBundle;
            //            }
            //        }
            //    }
            //}
            var end = Stopwatch.GetTimestamp();
            //Console.WriteLine($"Time (ms): {(end - start) * 1e3 / Stopwatch.Frequency}");
            //for (int i = 0; i < bodies.ActiveSet.Count; ++i)
            //{
            //    ref var constraints = ref bodies.ActiveSet.Constraints[i];
            //    ConstraintHandle minimumConstraint;
            //    minimumConstraint.Value = -1;
            //    int minimumBatchIndex = int.MaxValue;
            //    int minimumIndexInConstraint = -1;
            //    for (int j = 0; j < constraints.Count; ++j)
            //    {
            //        ref var constraint = ref constraints[j];
            //        var batchIndex = HandleToConstraint[constraint.ConnectingConstraintHandle.Value].BatchIndex;
            //        if (batchIndex < minimumBatchIndex)
            //        {
            //            minimumBatchIndex = batchIndex;
            //            minimumIndexInConstraint = constraint.BodyIndexInConstraint;
            //            minimumConstraint = constraint.ConnectingConstraintHandle;
            //        }
            //    }
            //    if (minimumConstraint.Value >= 0)
            //    {
            //        ref var location = ref HandleToConstraint[minimumConstraint.Value];
            //        var typeBatchIndex = ActiveSet.Batches[location.BatchIndex].TypeIndexToTypeBatchIndex[location.TypeId];
            //        if (location.BatchIndex > 0)
            //        {
            //            ref var indexSet = ref integrationFlags[location.BatchIndex][typeBatchIndex][minimumIndexInConstraint];
            //            Debug.Assert(indexSet.Contains(location.IndexInTypeBatch));
            //        }
            //    }
            //}

            merged.Dispose(pool);
            Debug.Assert(!bodiesFirstObservedInBatches[0].Flags.Allocated, "Remember, we're assuming we're just leaving the first batch's slot empty to avoid indexing complexity.");
            for (int batchIndex = 1; batchIndex < bodiesFirstObservedInBatches.Length; ++batchIndex)
            {
                bodiesFirstObservedInBatches[batchIndex].Dispose(pool);
            }
            pool.Return(ref bodiesFirstObservedInBatches);
        }
        public override void DisposeConstraintIntegrationResponsibilities()
        {
            Debug.Assert(!integrationFlags[0].Allocated, "Remember, we're assuming we're just leaving the first batch's slot empty to avoid indexing complexity.");
            for (int batchIndex = 1; batchIndex < integrationFlags.Length; ++batchIndex)
            {
                ref var flagsForBatch = ref integrationFlags[batchIndex];
                for (int typeBatchIndex = 0; typeBatchIndex < flagsForBatch.Length; ++typeBatchIndex)
                {
                    ref var flagsForTypeBatch = ref flagsForBatch[typeBatchIndex];
                    for (int bodyIndexInConstraint = 0; bodyIndexInConstraint < flagsForTypeBatch.Length; ++bodyIndexInConstraint)
                    {
                        flagsForTypeBatch[bodyIndexInConstraint].Dispose(pool);
                    }
                    pool.Return(ref flagsForTypeBatch);
                }
                pool.Return(ref flagsForBatch);
                pool.Return(ref coarseBatchIntegrationResponsibilities[batchIndex]);
            }
            pool.Return(ref integrationFlags);
            pool.Return(ref coarseBatchIntegrationResponsibilities);
        }

        public override void SolveStep2(float dt, IThreadDispatcher threadDispatcher = null)
        {
            if (threadDispatcher == null)
            {
                var inverseDt = 1f / dt;
                ref var activeSet = ref ActiveSet;
                GetSynchronizedBatchCount(out var synchronizedBatchCount, out var fallbackExists);
                Debug.Assert(!fallbackExists, "Not handling this yet.");

                for (int i = 0; i < synchronizedBatchCount; ++i)
                {
                    ref var batch = ref activeSet.Batches[i];
                    ref var integrationFlagsForBatch = ref integrationFlags[i];
                    for (int j = 0; j < batch.TypeBatches.Count; ++j)
                    {
                        ref var typeBatch = ref batch.TypeBatches[j];
                        if (i == 0)
                        {
                            Buffer<IndexSet> noFlagsRequired = default;
                            TypeProcessors[typeBatch.TypeId].WarmStart2<TIntegrationCallbacks, BatchShouldAlwaysIntegrate>(ref typeBatch, ref noFlagsRequired, bodies, ref PoseIntegrator.Callbacks,
                                dt, inverseDt, 0, typeBatch.BundleCount, 0);
                        }
                        else
                        {
                            if (coarseBatchIntegrationResponsibilities[i][j])
                            {
                                TypeProcessors[typeBatch.TypeId].WarmStart2<TIntegrationCallbacks, BatchShouldConditionallyIntegrate>(ref typeBatch, ref integrationFlagsForBatch[j], bodies, ref PoseIntegrator.Callbacks,
                                    dt, inverseDt, 0, typeBatch.BundleCount, 0);
                            }
                            else
                            {
                                TypeProcessors[typeBatch.TypeId].WarmStart2<TIntegrationCallbacks, BatchShouldNeverIntegrate>(ref typeBatch, ref integrationFlagsForBatch[j], bodies, ref PoseIntegrator.Callbacks,
                                    dt, inverseDt, 0, typeBatch.BundleCount, 0);
                            }
                        }
                    }
                }

                for (int i = 0; i < synchronizedBatchCount; ++i)
                {
                    ref var batch = ref activeSet.Batches[i];
                    for (int j = 0; j < batch.TypeBatches.Count; ++j)
                    {
                        ref var typeBatch = ref batch.TypeBatches[j];
                        TypeProcessors[typeBatch.TypeId].SolveStep2(ref typeBatch, bodies, dt, inverseDt, 0, typeBatch.BundleCount);
                    }
                }
            }
            else
            {
                ExecuteMultithreaded<MainSolveFilter>(dt, threadDispatcher, solveStep2Worker);
            }
        }
    }
}
