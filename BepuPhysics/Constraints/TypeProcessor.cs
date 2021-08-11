﻿using BepuUtilities.Memory;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using BepuUtilities.Collections;
using BepuUtilities;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace BepuPhysics.Constraints
{


    /// <summary>
    /// Superclass of constraint type batch processors. Responsible for interpreting raw type batches for the purposes of bookkeeping and solving.
    /// </summary>
    /// <remarks>
    /// <para>This class holds no actual state of its own. A solver creates a unique type processor for each registered constraint type, and all instances are held in untyped memory.
    /// Splitting the functionality from the data allows for far fewer GC-tracked instances and allows the raw data layout to be shared more easily.</para>
    /// <para>For example, sleeping simulation islands store type batches, but they are created and used differently- and for convenience, they are stored on a per-island basis.
    /// Using the same system but with reference type TypeBatches, tens of thousands of inactive islands would imply tens of thousands of GC-tracked objects.</para>
    /// That's not acceptable, so here we are. 
    /// <para>Conceptually, you can think of the solver's array of TypeProcessors like C function pointers.</para>
    /// </remarks>
    public abstract class TypeProcessor
    {
        //TODO: Having this in the base class actually complicates the implementation of some special constraint types. Consider an 'articulation' subsolver that involves
        //N bodies, for N > Vector<float>.Count * 2. You may want to do SIMD internally in such a case, so there would be no 'bundles' at this level. Worry about that later.

        //We cache type id and bodies per constraint to avoid virtual calls.
        protected int typeId;
        protected int bodiesPerConstraint;
        public int TypeId { get { return typeId; } }
        /// <summary>
        /// Gets the number of bodies associated with each constraint in this type processor.
        /// </summary>
        public int BodiesPerConstraint { get { return bodiesPerConstraint; } }
        /// <summary>
        /// Gets the number of degrees of freedom that each constraint in this type processor constrains. Equal to the number of entries in the accumulated impulses.
        /// </summary>
        public int ConstrainedDegreesOfFreedom { get; private set; }
        protected abstract int InternalBodiesPerConstraint { get; }
        protected abstract int InternalConstrainedDegreesOfFreedom { get; }

        public void Initialize(int typeId)
        {
            this.typeId = typeId;
            this.bodiesPerConstraint = InternalBodiesPerConstraint;
            this.ConstrainedDegreesOfFreedom = InternalConstrainedDegreesOfFreedom;
        }

        /// <summary>
        /// Allocates a slot in the batch.
        /// </summary>
        /// <param name="typeBatch">Type batch to allocate in.</param>
        /// <param name="handle">Handle of the constraint to allocate. Establishes a link from the allocated constraint to its handle.</param>
        /// <param name="bodyIndices">Pointer to a list of body indices (not handles!) with count equal to the type batch's expected number of involved bodies.</param>
        /// <param name="pool">Allocation provider to use if the type batch has to be resized.</param>
        /// <returns>Index of the slot in the batch.</returns>
        public unsafe abstract int Allocate(ref TypeBatch typeBatch, ConstraintHandle handle, int* bodyIndices, BufferPool pool);
        public abstract void Remove(ref TypeBatch typeBatch, int index, ref Buffer<ConstraintLocation> handlesToConstraints);

        /// <summary>
        /// Moves a constraint from one ConstraintBatch's TypeBatch to another ConstraintBatch's TypeBatch of the same type.
        /// </summary>
        /// <param name="sourceBatchIndex">Index of the batch that owns the type batch that is the source of the constraint transfer.</param>
        /// <param name="indexInTypeBatch">Index of the constraint to move in the current type batch.</param>
        /// <param name="solver">Solver that owns the batches.</param>
        /// <param name="bodies">Bodies set that owns all the constraint's bodies.</param>
        /// <param name="targetBatchIndex">Index of the ConstraintBatch in the solver to copy the constraint into.</param>
        public unsafe abstract void TransferConstraint(ref TypeBatch typeBatch, int sourceBatchIndex, int indexInTypeBatch, Solver solver, Bodies bodies, int targetBatchIndex);

        public abstract void EnumerateConnectedBodyIndices<TEnumerator>(ref TypeBatch typeBatch, int indexInTypeBatch, ref TEnumerator enumerator) where TEnumerator : IForEach<int>;
        [Conditional("DEBUG")]
        protected abstract void ValidateAccumulatedImpulsesSizeInBytes(int sizeInBytes);
        public unsafe void EnumerateAccumulatedImpulses<TEnumerator>(ref TypeBatch typeBatch, int indexInTypeBatch, ref TEnumerator enumerator) where TEnumerator : IForEach<float>
        {
            BundleIndexing.GetBundleIndices(indexInTypeBatch, out var bundleIndex, out var innerIndex);
            var bundleSizeInFloats = ConstrainedDegreesOfFreedom * Vector<float>.Count;
            ValidateAccumulatedImpulsesSizeInBytes(bundleSizeInFloats * 4);
            var impulseAddress = (float*)typeBatch.AccumulatedImpulses.Memory + (bundleIndex * bundleSizeInFloats + innerIndex);
            enumerator.LoopBody(*impulseAddress);
            for (int i = 1; i < ConstrainedDegreesOfFreedom; ++i)
            {
                impulseAddress += Vector<float>.Count;
                enumerator.LoopBody(*impulseAddress);
            }
        }
        public abstract void ScaleAccumulatedImpulses(ref TypeBatch typeBatch, float scale);
        public abstract void UpdateForBodyMemoryMove(ref TypeBatch typeBatch, int indexInTypeBatch, int bodyIndexInConstraint, int newBodyLocation);

        public abstract void Scramble(ref TypeBatch typeBatch, Random random, ref Buffer<ConstraintLocation> handlesToConstraints);

        internal abstract void GetBundleTypeSizes(out int bodyReferencesBundleSize, out int prestepBundleSize, out int accumulatedImpulseBundleSize);

        internal abstract void GenerateSortKeysAndCopyReferences(
            ref TypeBatch typeBatch,
            int bundleStart, int localBundleStart, int bundleCount,
            int constraintStart, int localConstraintStart, int constraintCount,
            ref int firstSortKey, ref int firstSourceIndex, ref RawBuffer bodyReferencesCache);

        internal abstract void CopyToCache(
            ref TypeBatch typeBatch,
            int bundleStart, int localBundleStart, int bundleCount,
            int constraintStart, int localConstraintStart, int constraintCount,
            ref Buffer<ConstraintHandle> indexToHandleCache, ref RawBuffer prestepCache, ref RawBuffer accumulatedImpulsesCache);

        internal abstract void Regather(
            ref TypeBatch typeBatch,
            int constraintStart, int constraintCount, ref int firstSourceIndex,
            ref Buffer<ConstraintHandle> indexToHandleCache, ref RawBuffer bodyReferencesCache, ref RawBuffer prestepCache, ref RawBuffer accumulatedImpulsesCache,
            ref Buffer<ConstraintLocation> handlesToConstraints);

        internal unsafe abstract void GatherActiveConstraints(Bodies bodies, Solver solver, ref QuickList<ConstraintHandle> sourceHandles, int startIndex, int endIndex, ref TypeBatch targetTypeBatch);

        internal unsafe abstract void CopySleepingToActive(
            int sourceSet, int sourceBatchIndex, int sourceTypeBatchIndex, int targetBatchIndex, int targetTypeBatchIndex,
            int sourceStart, int targetStart, int count, Bodies bodies, Solver solver);


        internal unsafe abstract void AddWakingBodyHandlesToBatchReferences(ref TypeBatch typeBatch, ref IndexSet targetBatchReferencedHandles);

        [Conditional("DEBUG")]
        internal abstract void VerifySortRegion(ref TypeBatch typeBatch, int bundleStartIndex, int constraintCount, ref Buffer<int> sortedKeys, ref Buffer<int> sortedSourceIndices);
        internal abstract int GetBodyReferenceCount(ref TypeBatch typeBatch, int body);

        public abstract void Initialize(ref TypeBatch typeBatch, int initialCapacity, BufferPool pool);
        public abstract void Resize(ref TypeBatch typeBatch, int newCapacity, BufferPool pool);

        public abstract void Prestep(ref TypeBatch typeBatch, Bodies bodies, float dt, float inverseDt, int startBundle, int exclusiveEndBundle);
        public abstract void WarmStart(ref TypeBatch typeBatch, Bodies bodies, int startBundle, int exclusiveEndBundle);
        public abstract void SolveIteration(ref TypeBatch typeBatch, Bodies bodies, int startBundle, int exclusiveEndBundle);

        public abstract void JacobiPrestep(ref TypeBatch typeBatch, Bodies bodies, ref FallbackBatch jacobiBatch, float dt, float inverseDt, int startBundle, int exclusiveEndBundle);
        public abstract void JacobiWarmStart(ref TypeBatch typeBatch, Bodies bodies, ref FallbackTypeBatchResults jacobiResults, int startBundle, int exclusiveEndBundle);
        public abstract void JacobiSolveIteration(ref TypeBatch typeBatch, Bodies bodies, ref FallbackTypeBatchResults jacobiResults, int startBundle, int exclusiveEndBundle);

        public abstract void SolveStep(ref TypeBatch typeBatch, Bodies bodies, float dt, float inverseDt, int startBundle, int exclusiveEndBundle);
        public abstract void JacobiSolveStep(ref TypeBatch typeBatch, Bodies bodies, ref FallbackBatch jacobiBatch, ref FallbackTypeBatchResults jacobiResults, float dt, float inverseDt, int startBundle, int exclusiveEndBundle);


        public abstract void WarmStart2<TIntegratorCallbacks, TBatchIntegrationMode, TAllowPoseIntegration>(ref TypeBatch typeBatch, ref Buffer<IndexSet> integrationFlags, Bodies bodies, ref TIntegratorCallbacks poseIntegratorCallbacks,
            float dt, float inverseDt, int startBundle, int exclusiveEndBundle, int workerIndex)
            where TIntegratorCallbacks : struct, IPoseIntegratorCallbacks
            where TBatchIntegrationMode : unmanaged, IBatchIntegrationMode
            where TAllowPoseIntegration : unmanaged, IBatchPoseIntegrationAllowed;
        public abstract void SolveStep2(ref TypeBatch typeBatch, Bodies bodies, float dt, float inverseDt, int startBundle, int exclusiveEndBundle);


        public abstract void Prestep2(ref TypeBatch typeBatch, Bodies bodies, float dt, float inverseDt, int startBundle, int exclusiveEndBundle);
        public abstract void JacobiPrestep2(ref TypeBatch typeBatch, Bodies bodies, ref FallbackBatch jacobiBatch, ref FallbackTypeBatchResults jacobiResults, float dt, float inverseDt, int startBundle, int exclusiveEndBundle);

        public virtual void IncrementallyUpdateContactData(ref TypeBatch typeBatch, Bodies bodies, float dt, float inverseDt, int startBundle, int end)
        {
            Debug.Fail("A contact data update was scheduled for a type batch that does not have a contact data update implementation.");
        }
    }

    /// <summary>
    /// Defines a function that creates a sort key from body references in a type batch. Used by constraint layout optimization.
    /// </summary>
    public interface ISortKeyGenerator<TBodyReferences> where TBodyReferences : unmanaged
    {
        int GetSortKey(int constraintIndex, ref Buffer<TBodyReferences> bodyReferences);
    }

    //Note that the only reason to have generics at the type level here is to avoid the need to specify them for each individual function. It's functionally equivalent, but this just
    //cuts down on the syntax noise a little bit. 
    //Really, you could use a bunch of composed static generic helpers.
    public abstract class TypeProcessor<TBodyReferences, TPrestepData, TProjection, TAccumulatedImpulse> : TypeProcessor where TBodyReferences : unmanaged where TPrestepData : unmanaged where TProjection : unmanaged where TAccumulatedImpulse : unmanaged
    {
        protected override int InternalConstrainedDegreesOfFreedom
        {
            get
            {
                //We're making an assumption about the layout of memory here. It's not guaranteed to be valid, but it does happen to be for all existing and planned constraints.
                var dofCount = Unsafe.SizeOf<TAccumulatedImpulse>() / (4 * Vector<float>.Count);
                Debug.Assert(dofCount * 4 * Vector<float>.Count == Unsafe.SizeOf<TAccumulatedImpulse>(), "One of the assumptions of this DOF calculator is broken. Fix this!");
                return dofCount;
            }
        }
        protected override void ValidateAccumulatedImpulsesSizeInBytes(int sizeInBytes)
        {
            Debug.Assert(sizeInBytes == Unsafe.SizeOf<TAccumulatedImpulse>(), "Your assumptions about memory layout and size are wrong for this type! Fix it!");
        }

        public override unsafe void ScaleAccumulatedImpulses(ref TypeBatch typeBatch, float scale)
        {
            var dofCount = Unsafe.SizeOf<TAccumulatedImpulse>() / Unsafe.SizeOf<Vector<float>>();
            var broadcastedScale = new Vector<float>(scale);
            ref var impulsesBase = ref Unsafe.AsRef<Vector<float>>(typeBatch.AccumulatedImpulses.Memory);
            for (int i = 0; i < dofCount; ++i)
            {
                Unsafe.Add(ref impulsesBase, i) *= broadcastedScale;
            }
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe static void AddBodyReferencesLane(ref TBodyReferences bundle, int innerIndex, int* bodyIndices)
        {
            //The jit should be able to fold almost all of the size-related calculations and address fiddling.
            ref var start = ref Unsafe.As<TBodyReferences, int>(ref bundle);
            ref var targetLane = ref Unsafe.Add(ref start, innerIndex);
            var stride = Vector<int>.Count;
            //We assume that the body references struct is organized in memory like Bundle0, Inner0, ... BundleN, InnerN, Count
            //Assuming contiguous storage, Count is then located at start + stride * BodyCount.
            var bodyCount = Unsafe.SizeOf<TBodyReferences>() / (stride * sizeof(int));
            targetLane = *bodyIndices;
            for (int i = 1; i < bodyCount; ++i)
            {
                Unsafe.Add(ref targetLane, i * stride) = bodyIndices[i];
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected static int GetCountInBundle(ref TypeBatch typeBatch, int bundleStartIndex)
        {
            //TODO: May want to check codegen on this. Min vs explicit branch. Theoretically, it could do this branchlessly...
            return Math.Min(Vector<float>.Count, typeBatch.ConstraintCount - (bundleStartIndex << BundleIndexing.VectorShift));
        }


        public unsafe sealed override int Allocate(ref TypeBatch typeBatch, ConstraintHandle handle, int* bodyIndices, BufferPool pool)
        {
            Debug.Assert(typeBatch.BodyReferences.Allocated, "Should initialize the batch before allocating anything from it.");
            if (typeBatch.ConstraintCount == typeBatch.IndexToHandle.Length)
            {
                InternalResize(ref typeBatch, pool, typeBatch.ConstraintCount * 2);
            }
            var index = typeBatch.ConstraintCount++;
            typeBatch.IndexToHandle[index] = handle;
            BundleIndexing.GetBundleIndices(index, out var bundleIndex, out var innerIndex);
            ref var bundle = ref Buffer<TBodyReferences>.Get(ref typeBatch.BodyReferences, bundleIndex);
            AddBodyReferencesLane(ref bundle, innerIndex, bodyIndices);
            //Clear the slot's accumulated impulse. The backing memory could be initialized to any value.
            GatherScatter.ClearLane<TAccumulatedImpulse, float>(ref Buffer<TAccumulatedImpulse>.Get(ref typeBatch.AccumulatedImpulses, bundleIndex), innerIndex);
            var bundleCount = typeBatch.BundleCount;
            Debug.Assert(typeBatch.PrestepData.Length >= bundleCount * Unsafe.SizeOf<TPrestepData>());
            Debug.Assert(typeBatch.Projection.Length >= bundleCount * Unsafe.SizeOf<TProjection>());
            Debug.Assert(typeBatch.BodyReferences.Length >= bundleCount * Unsafe.SizeOf<TBodyReferences>());
            Debug.Assert(typeBatch.AccumulatedImpulses.Length >= bundleCount * Unsafe.SizeOf<TAccumulatedImpulse>());
            Debug.Assert(typeBatch.IndexToHandle.Length >= typeBatch.ConstraintCount);
            return index;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected static void CopyConstraintData(
             ref TBodyReferences sourceReferencesBundle, ref TPrestepData sourcePrestepBundle, ref TAccumulatedImpulse sourceAccumulatedBundle, int sourceInner,
             ref TBodyReferences targetReferencesBundle, ref TPrestepData targetPrestepBundle, ref TAccumulatedImpulse targetAccumulatedBundle, int targetInner)
        {
            //Note that we do NOT copy the iteration data. It is regenerated each frame from scratch. 
            GatherScatter.CopyLane(ref sourceReferencesBundle, sourceInner, ref targetReferencesBundle, targetInner);
            GatherScatter.CopyLane(ref sourcePrestepBundle, sourceInner, ref targetPrestepBundle, targetInner);
            GatherScatter.CopyLane(ref sourceAccumulatedBundle, sourceInner, ref targetAccumulatedBundle, targetInner);
        }
        /// <summary>
        /// Overwrites all the data in the target constraint slot with source data.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected static void Move(
            ref TBodyReferences sourceReferencesBundle, ref TPrestepData sourcePrestepBundle, ref TAccumulatedImpulse sourceAccumulatedBundle, ConstraintHandle sourceHandle,
            int sourceInner,
            ref TBodyReferences targetReferencesBundle, ref TPrestepData targetPrestepBundle, ref TAccumulatedImpulse targetAccumulatedBundle, ref ConstraintHandle targetIndexToHandle,
            int targetInner, int targetIndex, ref Buffer<ConstraintLocation> handlesToConstraints)
        {
            CopyConstraintData(
                ref sourceReferencesBundle, ref sourcePrestepBundle, ref sourceAccumulatedBundle, sourceInner,
                ref targetReferencesBundle, ref targetPrestepBundle, ref targetAccumulatedBundle, targetInner);
            targetIndexToHandle = sourceHandle;
            handlesToConstraints[sourceHandle.Value].IndexInTypeBatch = targetIndex;
        }



        public sealed override unsafe void Scramble(ref TypeBatch typeBatch, Random random, ref Buffer<ConstraintLocation> handlesToConstraints)
        {
            //This is a pure debug function used to compare cache optimization strategies. Performance doesn't matter. 
            TPrestepData aPrestep = default;
            TAccumulatedImpulse aAccumulated = default;
            TBodyReferences aBodyReferences = default;
            ConstraintHandle aHandle;

            var prestepData = typeBatch.PrestepData.As<TPrestepData>();
            var accumulatedImpulses = typeBatch.AccumulatedImpulses.As<TAccumulatedImpulse>();
            var bodyReferences = typeBatch.BodyReferences.As<TBodyReferences>();

            for (int a = typeBatch.ConstraintCount - 1; a >= 1; --a)
            {
                BundleIndexing.GetBundleIndices(a, out var aBundle, out var aInner);
                GatherScatter.CopyLane(ref bodyReferences[aBundle], aInner, ref aBodyReferences, 0);
                GatherScatter.CopyLane(ref prestepData[aBundle], aInner, ref aPrestep, 0);
                GatherScatter.CopyLane(ref accumulatedImpulses[aBundle], aInner, ref aAccumulated, 0);
                aHandle = typeBatch.IndexToHandle[a];

                var b = random.Next(a);
                BundleIndexing.GetBundleIndices(b, out var bBundle, out var bInner);
                //Move b into a's slot.
                Move(
                    ref bodyReferences[bBundle], ref prestepData[bBundle], ref accumulatedImpulses[bBundle], typeBatch.IndexToHandle[b], bInner,
                    ref bodyReferences[aBundle], ref prestepData[aBundle], ref accumulatedImpulses[aBundle], ref typeBatch.IndexToHandle[a], aInner, a, ref handlesToConstraints);
                //Move cached a into b's slot.
                Move(
                    ref aBodyReferences, ref aPrestep, ref aAccumulated, aHandle, 0,
                    ref bodyReferences[bBundle], ref prestepData[bBundle], ref accumulatedImpulses[bBundle], ref typeBatch.IndexToHandle[b], bInner, b, ref handlesToConstraints);
            }
        }

        /// <summary>
        /// Removes a constraint from the batch.
        /// </summary>
        /// <param name="index">Index of the constraint to remove.</param>
        /// <param name="handlesToConstraints">The handle to constraint mapping used by the solver that could be modified by a swap on removal.</param>
        public override unsafe void Remove(ref TypeBatch typeBatch, int index, ref Buffer<ConstraintLocation> handlesToConstraints)
        {
            Debug.Assert(index >= 0 && index < typeBatch.ConstraintCount, "Can only remove elements that are actually in the batch!");
            var lastIndex = typeBatch.ConstraintCount - 1;
            typeBatch.ConstraintCount = lastIndex;
            BundleIndexing.GetBundleIndices(lastIndex, out var sourceBundleIndex, out var sourceInnerIndex);

            ref var bodyReferences = ref Unsafe.As<byte, TBodyReferences>(ref *typeBatch.BodyReferences.Memory);
            if (index < lastIndex)
            {
                //Need to swap.
                ref var prestepData = ref Unsafe.As<byte, TPrestepData>(ref *typeBatch.PrestepData.Memory);
                ref var accumulatedImpulses = ref Unsafe.As<byte, TAccumulatedImpulse>(ref *typeBatch.AccumulatedImpulses.Memory);
                BundleIndexing.GetBundleIndices(index, out var targetBundleIndex, out var targetInnerIndex);
                Move(
                    ref Unsafe.Add(ref bodyReferences, sourceBundleIndex), ref Unsafe.Add(ref prestepData, sourceBundleIndex), ref Unsafe.Add(ref accumulatedImpulses, sourceBundleIndex),
                    typeBatch.IndexToHandle[lastIndex], sourceInnerIndex,
                    ref Unsafe.Add(ref bodyReferences, targetBundleIndex), ref Unsafe.Add(ref prestepData, targetBundleIndex), ref Unsafe.Add(ref accumulatedImpulses, targetBundleIndex),
                    ref typeBatch.IndexToHandle[index], targetInnerIndex, index,
                    ref handlesToConstraints);
            }
        }

        /// <summary>
        /// Moves a constraint from one ConstraintBatch's TypeBatch to another ConstraintBatch's TypeBatch of the same type.
        /// </summary>
        /// <param name="sourceBatchIndex">Index of the batch that owns the type batch that is the source of the constraint transfer.</param>
        /// <param name="indexInTypeBatch">Index of the constraint to move in the current type batch.</param>
        /// <param name="solver">Solver that owns the batches.</param>
        /// <param name="bodies">Bodies set that owns all the constraint's bodies.</param>
        /// <param name="targetBatchIndex">Index of the ConstraintBatch in the solver to copy the constraint into.</param>
        public unsafe override void TransferConstraint(ref TypeBatch typeBatch, int sourceBatchIndex, int indexInTypeBatch, Solver solver, Bodies bodies, int targetBatchIndex)
        {
            //Note that the following does some redundant work. It's technically possible to do better than this, but it requires bypassing a lot of bookkeeping.
            //It's not exactly trivial to keep everything straight, especially over time- it becomes a maintenance nightmare.
            //So instead, given that compressions should generally be extremely rare (relatively speaking) and highly deferrable, we'll accept some minor overhead.
            int bodiesPerConstraint = InternalBodiesPerConstraint;
            var bodyHandles = stackalloc int[bodiesPerConstraint];
            var bodyHandleCollector = new ActiveConstraintBodyHandleCollector(bodies, bodyHandles);
            EnumerateConnectedBodyIndices(ref typeBatch, indexInTypeBatch, ref bodyHandleCollector);
            Debug.Assert(targetBatchIndex <= solver.FallbackBatchThreshold,
                "Constraint transfers should never target the fallback batch. It doesn't have any body handles so attempting to allocate in the same way wouldn't turn out well.");
            //Allocate a spot in the new batch. Note that it does not change the Handle->Constraint mapping in the Solver; that's important when we call Solver.Remove below.
            var constraintHandle = typeBatch.IndexToHandle[indexInTypeBatch];
            solver.AllocateInBatch(targetBatchIndex, constraintHandle, new Span<BodyHandle>(bodyHandles, bodiesPerConstraint), typeId, out var targetReference);

            BundleIndexing.GetBundleIndices(targetReference.IndexInTypeBatch, out var targetBundle, out var targetInner);
            BundleIndexing.GetBundleIndices(indexInTypeBatch, out var sourceBundle, out var sourceInner);
            //We don't pull a description or anything from the old constraint. That would require having a unique mapping from constraint to 'full description'. 
            //Instead, we just directly copy from lane to lane.
            //Note that we leave out the runtime generated bits- they'll just get regenerated.
            CopyConstraintData(
                ref Buffer<TBodyReferences>.Get(ref typeBatch.BodyReferences, sourceBundle),
                ref Buffer<TPrestepData>.Get(ref typeBatch.PrestepData, sourceBundle),
                ref Buffer<TAccumulatedImpulse>.Get(ref typeBatch.AccumulatedImpulses, sourceBundle),
                sourceInner,
                ref Buffer<TBodyReferences>.Get(ref targetReference.TypeBatch.BodyReferences, targetBundle),
                ref Buffer<TPrestepData>.Get(ref targetReference.TypeBatch.PrestepData, targetBundle),
                ref Buffer<TAccumulatedImpulse>.Get(ref targetReference.TypeBatch.AccumulatedImpulses, targetBundle),
                targetInner);

            //Now we can get rid of the old allocation.
            //Note the use of RemoveFromBatch instead of Remove. Solver.Remove returns the handle to the pool, which we do not want!
            //It may look a bit odd to use a solver-level function here, given that we are operating on batches and handling the solver state directly for the most part. 
            //However, removes can result in empty batches that require resource reclamation. 
            //Rather than reimplementing that we just reuse the solver's version. 
            //That sort of resource cleanup isn't required on add- everything that is needed already exists, and nothing is going away.
            solver.RemoveFromBatch(constraintHandle, sourceBatchIndex, typeId, indexInTypeBatch);

            //Don't forget to keep the solver's pointers consistent! We bypassed the usual add procedure, so the solver hasn't been notified yet.
            ref var constraintLocation = ref solver.HandleToConstraint[constraintHandle.Value];
            constraintLocation.BatchIndex = targetBatchIndex;
            constraintLocation.IndexInTypeBatch = targetReference.IndexInTypeBatch;
            constraintLocation.TypeId = typeId;

        }

        void InternalResize(ref TypeBatch typeBatch, BufferPool pool, int constraintCapacity)
        {
            Debug.Assert(constraintCapacity >= 0, "The constraint capacity should have already been validated.");
            pool.ResizeToAtLeast(ref typeBatch.IndexToHandle, constraintCapacity, typeBatch.ConstraintCount);
            //Note that we construct the bundle capacity from the resized constraint capacity. This means we only have to check the IndexToHandle capacity
            //before allocating, which simplifies things a little bit at the cost of some memory. Could revisit this if memory use is actually a concern.
            var bundleCapacity = BundleIndexing.GetBundleCount(typeBatch.IndexToHandle.Length);
            //Note that the projection is not copied over. It is ephemeral data. (In the same vein as above, if memory is an issue, we could just allocate projections on demand.)
            var bundleCount = typeBatch.BundleCount;
            pool.ResizeToAtLeast(ref typeBatch.Projection, bundleCapacity * Unsafe.SizeOf<TProjection>(), 0);
            pool.ResizeToAtLeast(ref typeBatch.BodyReferences, bundleCapacity * Unsafe.SizeOf<TBodyReferences>(), bundleCount * Unsafe.SizeOf<TBodyReferences>());
            pool.ResizeToAtLeast(ref typeBatch.PrestepData, bundleCapacity * Unsafe.SizeOf<TPrestepData>(), bundleCount * Unsafe.SizeOf<TPrestepData>());
            pool.ResizeToAtLeast(ref typeBatch.AccumulatedImpulses, bundleCapacity * Unsafe.SizeOf<TAccumulatedImpulse>(), bundleCount * Unsafe.SizeOf<TAccumulatedImpulse>());
        }

        public override void Initialize(ref TypeBatch typeBatch, int initialCapacity, BufferPool pool)
        {
            //We default-initialize the type batch because the resize checks existing allocation status when deciding how much to copy over. 
            //Technically we could use an initialization-specific version, but it doesn't matter.
            typeBatch = new TypeBatch();
            typeBatch.TypeId = TypeId;
            InternalResize(ref typeBatch, pool, initialCapacity);
        }

        public override void Resize(ref TypeBatch typeBatch, int desiredCapacity, BufferPool pool)
        {
            var desiredConstraintCapacity = BufferPool.GetCapacityForCount<int>(desiredCapacity);
            if (desiredConstraintCapacity != typeBatch.IndexToHandle.Length)
            {
                InternalResize(ref typeBatch, pool, desiredConstraintCapacity);
            }
        }


        internal sealed override void GetBundleTypeSizes(out int bodyReferencesBundleSize, out int prestepBundleSize, out int accumulatedImpulseBundleSize)
        {
            bodyReferencesBundleSize = Unsafe.SizeOf<TBodyReferences>();
            prestepBundleSize = Unsafe.SizeOf<TPrestepData>();
            accumulatedImpulseBundleSize = Unsafe.SizeOf<TAccumulatedImpulse>();
        }


        public sealed override void UpdateForBodyMemoryMove(ref TypeBatch typeBatch, int indexInTypeBatch, int bodyIndexInConstraint, int newBodyLocation)
        {
            BundleIndexing.GetBundleIndices(indexInTypeBatch, out var constraintBundleIndex, out var constraintInnerIndex);
            //Note that this relies on the bodyreferences memory layout. It uses the stride of vectors to skip to the next body based on the bodyIndexInConstraint.
            ref var bundle = ref Unsafe.As<TBodyReferences, Vector<int>>(ref Buffer<TBodyReferences>.Get(ref typeBatch.BodyReferences, constraintBundleIndex));
            GatherScatter.Get(ref bundle, constraintInnerIndex + bodyIndexInConstraint * Vector<int>.Count) = newBodyLocation;
        }

        //Note that these next two sort key users require a generic sort key implementation; this avoids virtual dispatch on a per-object level while still sharing the bulk of the logic.
        //Technically, we could force the TSortKeyGenerator to be defined at the generic type level, but this seems a little less... extreme.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void GenerateSortKeysAndCopyReferences<TSortKeyGenerator>(
           ref TypeBatch typeBatch,
           int bundleStart, int localBundleStart, int bundleCount,
           int constraintStart, int localConstraintStart, int constraintCount,
           ref int firstSortKey, ref int firstSourceIndex, ref RawBuffer bodyReferencesCache)
            where TSortKeyGenerator : struct, ISortKeyGenerator<TBodyReferences>
        {
            var sortKeyGenerator = default(TSortKeyGenerator);
            var bodyReferences = typeBatch.BodyReferences.As<TBodyReferences>();
            for (int i = 0; i < constraintCount; ++i)
            {
                Unsafe.Add(ref firstSourceIndex, i) = localConstraintStart + i;
                Unsafe.Add(ref firstSortKey, i) = sortKeyGenerator.GetSortKey(constraintStart + i, ref bodyReferences);
            }
            var typedBodyReferencesCache = bodyReferencesCache.As<TBodyReferences>();
            bodyReferences.CopyTo(bundleStart, typedBodyReferencesCache, localBundleStart, bundleCount);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected void VerifySortRegion<TSortKeyGenerator>(ref TypeBatch typeBatch, int bundleStartIndex, int constraintCount, ref Buffer<int> sortedKeys, ref Buffer<int> sortedSourceIndices)
            where TSortKeyGenerator : struct, ISortKeyGenerator<TBodyReferences>
        {
            var sortKeyGenerator = default(TSortKeyGenerator);
            var previousKey = -1;
            var baseIndex = bundleStartIndex << BundleIndexing.VectorShift;
            var bodyReferences = typeBatch.BodyReferences.As<TBodyReferences>();
            for (int i = 0; i < constraintCount; ++i)
            {
                var sourceIndex = sortedSourceIndices[i];
                var targetIndex = baseIndex + i;
                var key = sortKeyGenerator.GetSortKey(baseIndex + i, ref bodyReferences);
                //Note that this assert uses >= and not >; in a synchronized constraint batch, it's impossible for body references to be duplicated, but fallback batches CAN have duplicates.
                Debug.Assert(key >= previousKey, "After the sort and swap completes, all constraints should be in order.");
                Debug.Assert(key == sortedKeys[i], "After the swap goes through, the rederived sort keys should match the previously sorted ones.");
                previousKey = key;

            }
        }

        internal unsafe sealed override void CopyToCache(
            ref TypeBatch typeBatch,
            int bundleStart, int localBundleStart, int bundleCount,
            int constraintStart, int localConstraintStart, int constraintCount,
            ref Buffer<ConstraintHandle> indexToHandleCache, ref RawBuffer prestepCache, ref RawBuffer accumulatedImpulsesCache)
        {
            typeBatch.IndexToHandle.CopyTo(constraintStart, indexToHandleCache, localConstraintStart, constraintCount);
            Unsafe.CopyBlockUnaligned(
                prestepCache.Memory + Unsafe.SizeOf<TPrestepData>() * localBundleStart,
                typeBatch.PrestepData.Memory + Unsafe.SizeOf<TPrestepData>() * bundleStart,
                (uint)(Unsafe.SizeOf<TPrestepData>() * bundleCount));
            Unsafe.CopyBlockUnaligned(
                accumulatedImpulsesCache.Memory + Unsafe.SizeOf<TAccumulatedImpulse>() * localBundleStart,
                typeBatch.AccumulatedImpulses.Memory + Unsafe.SizeOf<TAccumulatedImpulse>() * bundleStart,
                (uint)(Unsafe.SizeOf<TAccumulatedImpulse>() * bundleCount));
        }
        internal sealed override void Regather(
            ref TypeBatch typeBatch,
            int constraintStart, int constraintCount, ref int firstSourceIndex,
            ref Buffer<ConstraintHandle> indexToHandleCache, ref RawBuffer bodyReferencesCache, ref RawBuffer prestepCache, ref RawBuffer accumulatedImpulsesCache,
            ref Buffer<ConstraintLocation> handlesToConstraints)
        {
            var typedBodyReferencesCache = bodyReferencesCache.As<TBodyReferences>();
            var typedPrestepCache = prestepCache.As<TPrestepData>();
            var typedAccumulatedImpulsesCache = accumulatedImpulsesCache.As<TAccumulatedImpulse>();

            var typedBodyReferencesTarget = typeBatch.BodyReferences.As<TBodyReferences>();
            var typedPrestepTarget = typeBatch.PrestepData.As<TPrestepData>();
            var typedAccumulatedImpulsesTarget = typeBatch.AccumulatedImpulses.As<TAccumulatedImpulse>();
            for (int i = 0; i < constraintCount; ++i)
            {
                var sourceIndex = Unsafe.Add(ref firstSourceIndex, i);
                var targetIndex = constraintStart + i;
                //Note that we do not bother checking whether the source and target are the same.
                //The cost of the branch is large enough in comparison to the frequency of its usefulness that it only helps in practically static situations.
                //Also, its maximum benefit is quite small.
                BundleIndexing.GetBundleIndices(sourceIndex, out var sourceBundle, out var sourceInner);
                BundleIndexing.GetBundleIndices(targetIndex, out var targetBundle, out var targetInner);

                Move(
                    ref typedBodyReferencesCache[sourceBundle], ref typedPrestepCache[sourceBundle], ref typedAccumulatedImpulsesCache[sourceBundle],
                    indexToHandleCache[sourceIndex], sourceInner,
                    ref typedBodyReferencesTarget[targetBundle], ref typedPrestepTarget[targetBundle], ref typedAccumulatedImpulsesTarget[targetBundle],
                    ref typeBatch.IndexToHandle[targetIndex], targetInner, targetIndex, ref handlesToConstraints);

            }
        }

        internal unsafe sealed override void GatherActiveConstraints(Bodies bodies, Solver solver, ref QuickList<ConstraintHandle> sourceHandles, int startIndex, int endIndex, ref TypeBatch targetTypeBatch)
        {
            ref var activeConstraintSet = ref solver.ActiveSet;
            ref var activeBodySet = ref bodies.ActiveSet;
            for (int i = startIndex; i < endIndex; ++i)
            {
                var sourceHandle = sourceHandles[i];
                targetTypeBatch.IndexToHandle[i] = sourceHandle;
                ref var location = ref solver.HandleToConstraint[sourceHandle.Value];
                Debug.Assert(targetTypeBatch.TypeId == location.TypeId, "Can only gather from batches of the same type.");
                Debug.Assert(location.SetIndex == 0, "Can only gather from the active set.");

                ref var sourceBatch = ref activeConstraintSet.Batches[location.BatchIndex];
                ref var sourceTypeBatch = ref sourceBatch.TypeBatches[sourceBatch.TypeIndexToTypeBatchIndex[location.TypeId]];
                BundleIndexing.GetBundleIndices(location.IndexInTypeBatch, out var sourceBundle, out var sourceInner);
                BundleIndexing.GetBundleIndices(i, out var targetBundle, out var targetInner);

                //Note that we don't directly copy body references or projection information. Projection information is ephemeral and unnecessary for sleeping constraints.
                //Body references get turned into handles so that awakening can easily track down the bodies' readded locations.
                GatherScatter.CopyLane(
                    ref Buffer<TPrestepData>.Get(ref sourceTypeBatch.PrestepData, sourceBundle), sourceInner,
                    ref Buffer<TPrestepData>.Get(ref targetTypeBatch.PrestepData, targetBundle), targetInner);
                GatherScatter.CopyLane(
                    ref Buffer<TAccumulatedImpulse>.Get(ref sourceTypeBatch.AccumulatedImpulses, sourceBundle), sourceInner,
                    ref Buffer<TAccumulatedImpulse>.Get(ref targetTypeBatch.AccumulatedImpulses, targetBundle), targetInner);
                ref var sourceReferencesLaneStart = ref Unsafe.Add(ref Unsafe.As<TBodyReferences, int>(ref Buffer<TBodyReferences>.Get(ref sourceTypeBatch.BodyReferences, sourceBundle)), sourceInner);
                ref var targetReferencesLaneStart = ref Unsafe.Add(ref Unsafe.As<TBodyReferences, int>(ref Buffer<TBodyReferences>.Get(ref targetTypeBatch.BodyReferences, targetBundle)), targetInner);
                var offset = 0;
                for (int j = 0; j < bodiesPerConstraint; ++j)
                {
                    Unsafe.Add(ref targetReferencesLaneStart, offset) = activeBodySet.IndexToHandle[Unsafe.Add(ref sourceReferencesLaneStart, offset)].Value;
                    offset += Vector<int>.Count;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void CopyIncompleteBundle(int sourceStart, int targetStart, int count, ref TypeBatch sourceTypeBatch, ref TypeBatch targetTypeBatch)
        {
            for (int i = 0; i < count; ++i)
            {
                //Note that this implementation allows two threads to access a single bundle. That would be a pretty bad case of false sharing if it happens, but
                //it won't cause correctness problems.
                var sourceIndex = sourceStart + i;
                var targetIndex = targetStart + i;
                BundleIndexing.GetBundleIndices(sourceIndex, out var sourceBundle, out var sourceInner);
                BundleIndexing.GetBundleIndices(targetIndex, out var targetBundle, out var targetInner);
                GatherScatter.CopyLane(
                    ref Buffer<TPrestepData>.Get(ref sourceTypeBatch.PrestepData, sourceBundle), sourceInner,
                    ref Buffer<TPrestepData>.Get(ref targetTypeBatch.PrestepData, targetBundle), targetInner);
                GatherScatter.CopyLane(
                    ref Buffer<TAccumulatedImpulse>.Get(ref sourceTypeBatch.AccumulatedImpulses, sourceBundle), sourceInner,
                    ref Buffer<TAccumulatedImpulse>.Get(ref targetTypeBatch.AccumulatedImpulses, targetBundle), targetInner);
            }
        }


        internal unsafe sealed override void CopySleepingToActive(
            int sourceSet, int sourceBatchIndex, int sourceTypeBatchIndex, int targetBatchIndex, int targetTypeBatchIndex,
            int sourceStart, int targetStart, int count, Bodies bodies, Solver solver)
        {
            ref var sourceTypeBatch = ref solver.Sets[sourceSet].Batches[sourceBatchIndex].TypeBatches[sourceTypeBatchIndex];
            ref var targetTypeBatch = ref solver.ActiveSet.Batches[targetBatchIndex].TypeBatches[targetTypeBatchIndex];
            Debug.Assert(sourceStart >= 0 && sourceStart + count <= sourceTypeBatch.ConstraintCount);
            Debug.Assert(targetStart >= 0 && targetStart + count <= targetTypeBatch.ConstraintCount,
                "This function should only be used when a region has been preallocated within the type batch.");
            Debug.Assert(sourceTypeBatch.TypeId == targetTypeBatch.TypeId);
            //TODO: Note that we give up on a bulk copy very easily here.
            //If you see this showing up in profiling to a meaningful extent, consider doing incomplete copies to allow a central bulk copy.
            //The only reasons that such a thing isn't already implemented are simplicity and time.
            if ((targetStart & BundleIndexing.VectorMask) == 0 &&
                (sourceStart & BundleIndexing.VectorMask) == 0 &&
                ((count & BundleIndexing.VectorMask) == 0 || count == targetTypeBatch.ConstraintCount))
            {
                //We can use a simple bulk copy here.      
                Debug.Assert(count > 0);
                var bundleCount = BundleIndexing.GetBundleCount(count);
                var sourcePrestepData = sourceTypeBatch.PrestepData.As<TPrestepData>();
                var sourceAccumulatedImpulses = sourceTypeBatch.AccumulatedImpulses.As<TAccumulatedImpulse>();
                var targetPrestepData = targetTypeBatch.PrestepData.As<TPrestepData>();
                var targetAccumulatedImpulses = targetTypeBatch.AccumulatedImpulses.As<TAccumulatedImpulse>();
                var sourceBundleStart = sourceStart >> BundleIndexing.VectorShift;
                var targetBundleStart = targetStart >> BundleIndexing.VectorShift;
                sourcePrestepData.CopyTo(sourceBundleStart, targetPrestepData, targetBundleStart, bundleCount);
                sourceAccumulatedImpulses.CopyTo(sourceBundleStart, targetAccumulatedImpulses, targetBundleStart, bundleCount);
            }
            else
            {
                CopyIncompleteBundle(sourceStart, targetStart, count, ref sourceTypeBatch, ref targetTypeBatch);
            }
            //Note that body reference copies cannot be done in bulk because inactive constraints refer to body handles while active constraints refer to body indices.
            for (int i = 0; i < count; ++i)
            {
                var sourceIndex = sourceStart + i;
                var targetIndex = targetStart + i;
                BundleIndexing.GetBundleIndices(sourceIndex, out var sourceBundle, out var sourceInner);
                BundleIndexing.GetBundleIndices(targetIndex, out var targetBundle, out var targetInner);

                ref var sourceReferencesLaneStart = ref Unsafe.Add(ref Unsafe.As<TBodyReferences, int>(ref Buffer<TBodyReferences>.Get(ref sourceTypeBatch.BodyReferences, sourceBundle)), sourceInner);
                ref var targetReferencesLaneStart = ref Unsafe.Add(ref Unsafe.As<TBodyReferences, int>(ref Buffer<TBodyReferences>.Get(ref targetTypeBatch.BodyReferences, targetBundle)), targetInner);
                var offset = 0;
                for (int j = 0; j < bodiesPerConstraint; ++j)
                {
                    Unsafe.Add(ref targetReferencesLaneStart, offset) = bodies.HandleToLocation[Unsafe.Add(ref sourceReferencesLaneStart, offset)].Index;
                    offset += Vector<int>.Count;
                }
                var constraintHandle = sourceTypeBatch.IndexToHandle[sourceIndex];
                ref var location = ref solver.HandleToConstraint[constraintHandle.Value];
                Debug.Assert(location.SetIndex == sourceSet);
                location.SetIndex = 0;
                location.BatchIndex = targetBatchIndex;
                Debug.Assert(sourceTypeBatch.TypeId == location.TypeId);
                location.IndexInTypeBatch = targetIndex;
                //This could be done with a bulk copy, but eh! We already touched the memory.
                targetTypeBatch.IndexToHandle[targetIndex] = constraintHandle;
            }
        }


        internal unsafe sealed override void AddWakingBodyHandlesToBatchReferences(ref TypeBatch typeBatch, ref IndexSet targetBatchReferencedHandles)
        {
            for (int i = 0; i < typeBatch.ConstraintCount; ++i)
            {
                BundleIndexing.GetBundleIndices(i, out var sourceBundle, out var sourceInner);
                ref var sourceHandlesStart = ref Unsafe.Add(ref Unsafe.As<TBodyReferences, int>(ref Buffer<TBodyReferences>.Get(ref typeBatch.BodyReferences, sourceBundle)), sourceInner);
                var offset = 0;
                for (int j = 0; j < bodiesPerConstraint; ++j)
                {
                    var bodyHandle = Unsafe.Add(ref sourceHandlesStart, offset);
                    Debug.Assert(!targetBatchReferencedHandles.Contains(bodyHandle),
                        "It should be impossible for a batch in the active set to already contain a reference to a body that is being woken up.");
                    //Given that we're only adding references to bodies that already exist, and therefore were at some point in the active set, it should never be necessary
                    //to resize the batch referenced handles structure.
                    targetBatchReferencedHandles.AddUnsafely(bodyHandle);
                    offset += Vector<int>.Count;
                }
            }
        }

        internal override int GetBodyReferenceCount(ref TypeBatch typeBatch, int bodyToFind)
        {
            //This is a pure debug function; performance does not matter.
            //Note that this function is used across both active and inactive sets. In the active set, the body references refer to *indices* in the Bodies.ActiveSet.
            //For inactive constraint sets, the body references are instead body *handles*. The user of this function is expected to appreciate the difference.
            var bundleCount = typeBatch.BundleCount;
            var bodyReferences = typeBatch.BodyReferences.As<TBodyReferences>();
            int count = 0;
            var bodiesPerConstraint = InternalBodiesPerConstraint;
            for (int bundleIndex = 0; bundleIndex < bundleCount; ++bundleIndex)
            {
                var bundleSize = Math.Min(Vector<float>.Count, typeBatch.ConstraintCount - (bundleIndex << BundleIndexing.VectorShift));
                ref var bundleBase = ref Unsafe.As<TBodyReferences, Vector<int>>(ref bodyReferences[bundleIndex]);
                for (int constraintBodyIndex = 0; constraintBodyIndex < bodiesPerConstraint; ++constraintBodyIndex)
                {
                    ref var bodyVectorBase = ref Unsafe.As<Vector<int>, int>(ref Unsafe.Add(ref bundleBase, constraintBodyIndex));
                    for (int innerIndex = 0; innerIndex < bundleSize; ++innerIndex)
                    {
                        if (Unsafe.Add(ref bodyVectorBase, innerIndex) == bodyToFind)
                            ++count;
                        Debug.Assert(count <= 1);
                    }
                }
            }
            return count;
        }


        public enum BundleIntegrationMode
        {
            None = 0,
            Partial = 1,
            All = 2
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static BundleIntegrationMode BundleShouldIntegrate(int bundleIndex, in IndexSet integrationFlags, out Vector<int> integrationMask)
        {
            Debug.Assert(Vector<float>.Count <= 32, "Wait, what? The integration mask isn't big enough to handle a vector this big.");
            var constraintStartIndex = bundleIndex * Vector<float>.Count;
            var flagBundleIndex = constraintStartIndex >> 6;
            var flagInnerIndex = constraintStartIndex - (flagBundleIndex << 6);
            var flagMask = (1 << Vector<float>.Count) - 1;
            var scalarIntegrationMask = ((int)(integrationFlags.Flags[flagBundleIndex] >> flagInnerIndex)) & flagMask;
            if (scalarIntegrationMask == flagMask)
            {
                //No need to carefully expand a bitstring into a vector mask if we know that a single broadcast will suffice.
                integrationMask = new Vector<int>(-1);
                return BundleIntegrationMode.All;
            }
            else if (scalarIntegrationMask > 0)
            {
                if (Vector<int>.Count == 4 || Vector<int>.Count == 8)
                {
                    Vector<int> selectors;
                    if (Vector<int>.Count == 8)
                    {
                        selectors = Vector256.Create(1, 2, 4, 8, 16, 32, 64, 128).AsVector();
                    }
                    else
                    {
                        selectors = Vector128.Create(1, 2, 4, 8).AsVector();
                    }
                    var scalarBroadcast = new Vector<int>(scalarIntegrationMask);
                    var selected = Vector.BitwiseAnd(selectors, scalarBroadcast);
                    integrationMask = Vector.Equals(selected, selectors);
                }
                else
                {
                    //This is not a good implementation, but I don't know of any target platforms that will hit this.
                    //TODO: AVX512 being enabled by the runtime could force this path to be taken; it'll require an update!
                    Debug.Assert(Vector<int>.Count <= 8, "The vector path assumes that AVX512 is not supported, so this is hitting a fallback path.");
                    Span<int> mask = stackalloc int[Vector<int>.Count];
                    for (int i = 0; i < Vector<int>.Count; ++i)
                    {
                        mask[i] = (scalarIntegrationMask & (1 << i)) > 0 ? -1 : 0;
                    }
                    integrationMask = new Vector<int>(mask);
                }
                return BundleIntegrationMode.Partial;
            }
            integrationMask = default;
            return BundleIntegrationMode.None;
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe void IntegratePoseAndVelocity<TIntegratorCallbacks>(
            ref TIntegratorCallbacks integratorCallbacks, ref Vector<int> bodyIndices, int count, in BodyInertiaWide localInertia, float dt, in Vector<int> integrationMask,
            ref Vector3Wide position, ref QuaternionWide orientation, ref BodyVelocityWide velocity,
            int workerIndex,
            out BodyInertiaWide inertia)
            where TIntegratorCallbacks : struct, IPoseIntegratorCallbacks
        {
            //Note that we integrate pose, then velocity.
            //We only use this function where we can guarantee that the external-to-timestep view of velocities and poses looks like the frame starts on a velocity integration and ends on a pose integration.
            //This ensures that velocities set externally are still solved before being integrated.
            //So, the solver runs velocity integration alone on the first substep. All later substeps then run pose + velocity, and then after the last substep, a final pose integration.
            //This is equivalent in ordering to running each substep as velocity, warmstart, solve, pose integration, but just shifting the execution context.
            var dtWide = new Vector<float>(dt);
            var newPosition = position + velocity.Linear * dtWide;
            //Note that we only take results for slots which actually need integration. Reintegration would be an error.
            Vector3Wide.ConditionalSelect(integrationMask, newPosition, position, out position);
            QuaternionWide newOrientation;
            inertia.InverseMass = localInertia.InverseMass;
            var previousVelocity = velocity;
            if (integratorCallbacks.AngularIntegrationMode == AngularIntegrationMode.ConserveMomentum)
            {
                var previousOrientation = orientation;
                PoseIntegration.Integrate(orientation, velocity.Angular, dtWide * new Vector<float>(0.5f), out newOrientation);
                QuaternionWide.ConditionalSelect(integrationMask, newOrientation, orientation, out orientation);
                PoseIntegration.RotateInverseInertia(localInertia.InverseInertiaTensor, orientation, out inertia.InverseInertiaTensor);
                PoseIntegration.IntegrateAngularVelocityConserveMomentum(previousOrientation, localInertia.InverseInertiaTensor, inertia.InverseInertiaTensor, ref velocity.Angular);
            }
            else if (integratorCallbacks.AngularIntegrationMode == AngularIntegrationMode.ConserveMomentumWithGyroscopicTorque)
            {
                PoseIntegration.Integrate(orientation, velocity.Angular, dtWide * new Vector<float>(0.5f), out newOrientation);
                QuaternionWide.ConditionalSelect(integrationMask, newOrientation, orientation, out orientation);
                PoseIntegration.RotateInverseInertia(localInertia.InverseInertiaTensor, orientation, out inertia.InverseInertiaTensor);
                PoseIntegration.IntegrateAngularVelocityConserveMomentumWithGyroscopicTorque(orientation, localInertia.InverseInertiaTensor, ref velocity.Angular, dtWide);
            }
            else
            {
                PoseIntegration.Integrate(orientation, velocity.Angular, dtWide * new Vector<float>(0.5f), out newOrientation);
                QuaternionWide.ConditionalSelect(integrationMask, newOrientation, orientation, out orientation);
                PoseIntegration.RotateInverseInertia(localInertia.InverseInertiaTensor, orientation, out inertia.InverseInertiaTensor);
            }
            integratorCallbacks.IntegrateVelocity(new ReadOnlySpan<int>(Unsafe.AsPointer(ref bodyIndices), count), position, orientation, localInertia, integrationMask, workerIndex, new Vector<float>(dt), ref velocity);
            //It would be annoying to make the user handle masking velocity writes to inactive lanes, so we handle it internally.
            Vector3Wide.ConditionalSelect(integrationMask, velocity.Linear, previousVelocity.Linear, out velocity.Linear);
            Vector3Wide.ConditionalSelect(integrationMask, velocity.Angular, previousVelocity.Angular, out velocity.Angular);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe void IntegratePoseAndVelocity<TIntegratorCallbacks>(
            ref TIntegratorCallbacks integratorCallbacks, ref Vector<int> bodyIndices, int count, in BodyInertiaWide localInertia, float dt,
            ref Vector3Wide position, ref QuaternionWide orientation, ref BodyVelocityWide velocity,
            int workerIndex,
            out BodyInertiaWide inertia)
            where TIntegratorCallbacks : struct, IPoseIntegratorCallbacks
        {
            //This is identical to the other IntegratePoseAndVelocity, but it avoids any masking because we know ahead of time that the entire bundle is integrating.
            var dtWide = new Vector<float>(dt);
            position += velocity.Linear * dtWide;
            inertia.InverseMass = localInertia.InverseMass;
            if (integratorCallbacks.AngularIntegrationMode == AngularIntegrationMode.ConserveMomentum)
            {
                var previousOrientation = orientation;
                PoseIntegration.Integrate(orientation, velocity.Angular, dtWide * new Vector<float>(0.5f), out orientation);
                PoseIntegration.RotateInverseInertia(localInertia.InverseInertiaTensor, orientation, out inertia.InverseInertiaTensor);
                PoseIntegration.IntegrateAngularVelocityConserveMomentum(previousOrientation, localInertia.InverseInertiaTensor, inertia.InverseInertiaTensor, ref velocity.Angular);
            }
            else if (integratorCallbacks.AngularIntegrationMode == AngularIntegrationMode.ConserveMomentumWithGyroscopicTorque)
            {
                PoseIntegration.Integrate(orientation, velocity.Angular, dtWide * new Vector<float>(0.5f), out orientation);
                PoseIntegration.RotateInverseInertia(localInertia.InverseInertiaTensor, orientation, out inertia.InverseInertiaTensor);
                PoseIntegration.IntegrateAngularVelocityConserveMomentumWithGyroscopicTorque(orientation, localInertia.InverseInertiaTensor, ref velocity.Angular, dtWide);
            }
            else
            {
                PoseIntegration.Integrate(orientation, velocity.Angular, dtWide * new Vector<float>(0.5f), out orientation);
                PoseIntegration.RotateInverseInertia(localInertia.InverseInertiaTensor, orientation, out inertia.InverseInertiaTensor);
            }
            integratorCallbacks.IntegrateVelocity(new ReadOnlySpan<int>(Unsafe.AsPointer(ref bodyIndices), count), position, orientation, localInertia, new Vector<int>(-1), workerIndex, new Vector<float>(dt), ref velocity);
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe void IntegrateVelocity<TIntegratorCallbacks, TBatchIntegrationMode>(
            ref TIntegratorCallbacks integratorCallbacks, ref Vector<int> bodyIndices, int count, in BodyInertiaWide localInertia, float dt, in Vector<int> integrationMask,
            in Vector3Wide position, in QuaternionWide orientation, ref BodyVelocityWide velocity,
            int workerIndex,
            out BodyInertiaWide inertia)
            where TIntegratorCallbacks : struct, IPoseIntegratorCallbacks
            where TBatchIntegrationMode : unmanaged, IBatchIntegrationMode
        {
            inertia.InverseMass = localInertia.InverseMass;
            PoseIntegration.RotateInverseInertia(localInertia.InverseInertiaTensor, orientation, out inertia.InverseInertiaTensor);
            if (integratorCallbacks.AngularIntegrationMode == AngularIntegrationMode.ConserveMomentum)
            {
                //Yes, that's integrating backwards to get a previous orientation to convert to momentum. Yup, that's a bit janky.
                PoseIntegration.Integrate(orientation, velocity.Angular, new Vector<float>(dt * -0.5f), out var previousOrientation);
                PoseIntegration.IntegrateAngularVelocityConserveMomentum(previousOrientation, localInertia.InverseInertiaTensor, inertia.InverseInertiaTensor, ref velocity.Angular);
            }
            else if (integratorCallbacks.AngularIntegrationMode == AngularIntegrationMode.ConserveMomentumWithGyroscopicTorque)
            {
                PoseIntegration.IntegrateAngularVelocityConserveMomentumWithGyroscopicTorque(orientation, localInertia.InverseInertiaTensor, ref velocity.Angular, new Vector<float>(dt));
            }
            if (typeof(TBatchIntegrationMode) == typeof(BatchShouldConditionallyIntegrate))
            {
                var previousVelocity = velocity;
                integratorCallbacks.IntegrateVelocity(new ReadOnlySpan<int>(Unsafe.AsPointer(ref bodyIndices), count), position, orientation, localInertia, integrationMask, workerIndex, new Vector<float>(dt), ref velocity);
                //It would be annoying to make the user handle masking velocity writes to inactive lanes, so we handle it internally.
                Vector3Wide.ConditionalSelect(integrationMask, velocity.Linear, previousVelocity.Linear, out velocity.Linear);
                Vector3Wide.ConditionalSelect(integrationMask, velocity.Angular, previousVelocity.Angular, out velocity.Angular);
            }
            else
            {
                integratorCallbacks.IntegrateVelocity(new ReadOnlySpan<int>(Unsafe.AsPointer(ref bodyIndices), count), position, orientation, localInertia, integrationMask, workerIndex, new Vector<float>(dt), ref velocity);
            }
        }

        //[MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static unsafe void GatherAndIntegrate<TIntegratorCallbacks, TBatchIntegrationMode, TAccessFilter, TShouldIntegratePoses>(
            Bodies bodies, ref TIntegratorCallbacks integratorCallbacks, ref Buffer<IndexSet> integrationFlags, int bodyIndexInConstraint, float dt, int workerIndex, int bundleIndex,
            ref Vector<int> bodyIndices, int count, out Vector3Wide position, out QuaternionWide orientation, out BodyVelocityWide velocity, out BodyInertiaWide inertia)
            where TIntegratorCallbacks : struct, IPoseIntegratorCallbacks
            where TBatchIntegrationMode : unmanaged, IBatchIntegrationMode
            where TAccessFilter : unmanaged, IBodyAccessFilter
            where TShouldIntegratePoses : unmanaged, IBatchPoseIntegrationAllowed
        {
            //These type tests are compile time constants and will be specialized.
            if (typeof(TShouldIntegratePoses) == typeof(AllowPoseIntegration))
            {
                if (typeof(TBatchIntegrationMode) == typeof(BatchShouldAlwaysIntegrate))
                {
                    var integrationMask = BundleIndexing.CreateMaskForCountInBundle(count);
                    bodies.GatherState<AccessAll>(ref bodyIndices, count, false, out position, out orientation, out velocity, out var localInertia);
                    IntegratePoseAndVelocity(ref integratorCallbacks, ref bodyIndices, count, localInertia, dt, ref position, ref orientation, ref velocity, workerIndex, out inertia);
                    bodies.ScatterPose(ref position, ref orientation, ref bodyIndices, ref integrationMask);
                    bodies.ScatterInertia(ref inertia, ref bodyIndices, ref integrationMask);
                }
                else if (typeof(TBatchIntegrationMode) == typeof(BatchShouldNeverIntegrate))
                {
                    bodies.GatherState<TAccessFilter>(ref bodyIndices, count, true, out position, out orientation, out velocity, out inertia);
                }
                else
                {
                    Debug.Assert(typeof(TBatchIntegrationMode) == typeof(BatchShouldConditionallyIntegrate));
                    //This executes in warmstart, and warmstarts are typically quite simple from an instruction stream perspective.
                    //Having a dynamically chosen codepath is unlikely to cause instruction fetching issues.
                    var bundleIntegrationMode = BundleShouldIntegrate(bundleIndex, integrationFlags[bodyIndexInConstraint], out var integrationMask);
                    //Note that this will gather world inertia if there is no integration in the bundle, but that it is guaranteed to load all motion state information.
                    //This avoids complexity around later velocity scattering- we don't have to condition on whether the bundle is integrating.
                    //In practice, since the access filters are only reducing instruction counts and not memory bandwidth,
                    //the slightly increased unnecessary gathering is no worse than the more complex scatter condition in performance, and remains simpler.
                    bodies.GatherState<AccessAll>(ref bodyIndices, count, bundleIntegrationMode == BundleIntegrationMode.None, out position, out orientation, out velocity, out var gatheredInertia);
                    if (bundleIntegrationMode != BundleIntegrationMode.None)
                    {
                        //Note that if we take this codepath, the integration routine will reconstruct the world inertias from local inertia given the current pose.
                        //The changes to pose and velocity for integration inactive lanes will be masked out, so it'll just be identical to the world inertia if we had gathered it.
                        //Given that we're running the instructions in a bundle to build it, there's no reason to go out of our way to gather the world inertia.
                        IntegratePoseAndVelocity(ref integratorCallbacks, ref bodyIndices, count, gatheredInertia, dt, integrationMask, ref position, ref orientation, ref velocity, workerIndex, out inertia);
                        bodies.ScatterPose(ref position, ref orientation, ref bodyIndices, ref integrationMask);
                        bodies.ScatterInertia(ref inertia, ref bodyIndices, ref integrationMask);
                    }
                    else
                    {
                        inertia = gatheredInertia;
                    }
                }
            }
            else
            {
                Debug.Assert(typeof(TShouldIntegratePoses) == typeof(DisallowPoseIntegration));
                //There is no need to integrate poses; this is the first substep. 
                //Note that the full loop for constrained bodies with 3 substeps looks like:
                //(velocity -> solve) -> (pose -> velocity -> solve) -> (pose -> velocity -> solve) -> pose
                //For unconstrained bodies, it's a tight loop of just:
                //(velocity -> pose) -> (velocity -> pose) -> (velocity -> pose)
                //So we're maintaining the same order.
                //Note that world inertia is still scattered as a part of velocity integration; we need the updated value since we can't trust the cached value across frames.
                if (typeof(TBatchIntegrationMode) == typeof(BatchShouldAlwaysIntegrate))
                {
                    var integrationMask = BundleIndexing.CreateMaskForCountInBundle(count);
                    bodies.GatherState<AccessAll>(ref bodyIndices, count, false, out position, out orientation, out velocity, out var localInertia);
                    IntegrateVelocity<TIntegratorCallbacks, TBatchIntegrationMode>(ref integratorCallbacks, ref bodyIndices, count, localInertia, dt, integrationMask, position, orientation, ref velocity, workerIndex, out inertia);
                    bodies.ScatterInertia(ref inertia, ref bodyIndices, ref integrationMask);
                }
                else if (typeof(TBatchIntegrationMode) == typeof(BatchShouldNeverIntegrate))
                {
                    bodies.GatherState<TAccessFilter>(ref bodyIndices, count, true, out position, out orientation, out velocity, out inertia);
                }
                else
                {
                    Debug.Assert(typeof(TBatchIntegrationMode) == typeof(BatchShouldConditionallyIntegrate));
                    var bundleIntegrationMode = BundleShouldIntegrate(bundleIndex, integrationFlags[bodyIndexInConstraint], out var integrationMask);
                    bodies.GatherState<AccessAll>(ref bodyIndices, count, bundleIntegrationMode == BundleIntegrationMode.None, out position, out orientation, out velocity, out var gatheredInertia);
                    if (bundleIntegrationMode != BundleIntegrationMode.None)
                    {
                        IntegrateVelocity<TIntegratorCallbacks, TBatchIntegrationMode>(ref integratorCallbacks, ref bodyIndices, count, gatheredInertia, dt, integrationMask, position, orientation, ref velocity, workerIndex, out inertia);
                        bodies.ScatterInertia(ref inertia, ref bodyIndices, ref integrationMask);
                    }
                    else
                    {
                        inertia = gatheredInertia;
                    }
                }
            }
        }

    }


}