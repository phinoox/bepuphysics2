﻿using BepuUtilities;
using BepuUtilities.Collections;
using BepuUtilities.Memory;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace BepuPhysics.Constraints
{
    /// <summary>
    /// A constraint's body references. Stored separately from the iteration data since it is accessed by both the prestep and solve.
    /// </summary>
    public struct ThreeBodyReferences
    {
        public Vector<int> IndexA;
        public Vector<int> IndexB;
        public Vector<int> IndexC;
    }

    /// <summary>
    /// Prestep, warm start and solve iteration functions for a three body constraint type.
    /// </summary>
    /// <typeparam name="TPrestepData">Type of the prestep data used by the constraint.</typeparam>
    /// <typeparam name="TAccumulatedImpulse">Type of the accumulated impulses used by the constraint.</typeparam>
    /// <typeparam name="TProjection">Type of the projection to input.</typeparam>
    public interface IThreeBodyConstraintFunctions<TPrestepData, TProjection, TAccumulatedImpulse>
    {
        void Prestep(in QuaternionWide orientationA, in BodyInertiaWide inertiaA, in Vector3Wide ab, in QuaternionWide orientationB, in BodyInertiaWide inertiaB, in Vector3Wide ac, in QuaternionWide orientationC, in BodyInertiaWide inertiaC,
            float dt, float inverseDt, ref TPrestepData prestepData, out TProjection projection);
        void WarmStart(ref BodyVelocityWide velocityA, ref BodyVelocityWide velocityB, ref BodyVelocityWide velocityC, ref TProjection projection, ref TAccumulatedImpulse accumulatedImpulse);
        void Solve(ref BodyVelocityWide velocityA, ref BodyVelocityWide velocityB, ref BodyVelocityWide velocityC, ref TProjection projection, ref TAccumulatedImpulse accumulatedImpulse);
    }

    /// <summary>
    /// Shared implementation across all three body constraints.
    /// </summary>
    public abstract class ThreeBodyTypeProcessor<TPrestepData, TProjection, TAccumulatedImpulse, TConstraintFunctions>
        : TypeProcessor<ThreeBodyReferences, TPrestepData, TProjection, TAccumulatedImpulse>
        where TPrestepData : unmanaged where TProjection : unmanaged where TAccumulatedImpulse : unmanaged
        where TConstraintFunctions : unmanaged, IThreeBodyConstraintFunctions<TPrestepData, TProjection, TAccumulatedImpulse>
    {
        protected sealed override int InternalBodiesPerConstraint => 3;

        public sealed unsafe override void EnumerateConnectedBodyIndices<TEnumerator>(ref TypeBatch typeBatch, int indexInTypeBatch, ref TEnumerator enumerator)
        {
            BundleIndexing.GetBundleIndices(indexInTypeBatch, out var constraintBundleIndex, out var constraintInnerIndex);
            ref var indices = ref GatherScatter.GetOffsetInstance(ref Buffer<ThreeBodyReferences>.Get(typeBatch.BodyReferences.Memory, constraintBundleIndex), constraintInnerIndex);
            //Note that we hold a reference to the indices. That's important if the loop body mutates indices.
            enumerator.LoopBody(GatherScatter.GetFirst(ref indices.IndexA));
            enumerator.LoopBody(GatherScatter.GetFirst(ref indices.IndexB));
            enumerator.LoopBody(GatherScatter.GetFirst(ref indices.IndexC));
        }
        struct ThreeBodySortKeyGenerator : ISortKeyGenerator<ThreeBodyReferences>
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int GetSortKey(int constraintIndex, ref Buffer<ThreeBodyReferences> bodyReferences)
            {
                BundleIndexing.GetBundleIndices(constraintIndex, out var bundleIndex, out var innerIndex);
                ref var bundleReferences = ref bodyReferences[bundleIndex];
                //We sort based on the body references within the constraint. 
                //Sort based on the smallest body index in a constraint. Note that it is impossible for there to be two references to the same body within a constraint batch, 
                //so there's no need to worry about the case where the comparison is equal.
                ref var bundle = ref bodyReferences[bundleIndex];
                //Avoiding some branches and scalar work. Could do better with platform intrinsics on some architectures.
                var bundleMin = Vector.Min(Vector.Min(bundle.IndexA, bundle.IndexB), bundle.IndexC);
                return GatherScatter.Get(ref bundleMin, innerIndex);
                //TODO: Note that we could do quite a bit better by generating sort keys in a fully vectorized way. Would require some changes in the caller, but it's doable-
                //these sorts are done across contiguous regions, after all.
                //Not a huge deal regardless.
            }
        }


        internal sealed override void GenerateSortKeysAndCopyReferences(
            ref TypeBatch typeBatch,
            int bundleStart, int localBundleStart, int bundleCount,
            int constraintStart, int localConstraintStart, int constraintCount,
            ref int firstSortKey, ref int firstSourceIndex, ref RawBuffer bodyReferencesCache)
        {
            GenerateSortKeysAndCopyReferences<ThreeBodySortKeyGenerator>(
                ref typeBatch,
                bundleStart, localBundleStart, bundleCount,
                constraintStart, localConstraintStart, constraintCount,
                ref firstSortKey, ref firstSourceIndex, ref bodyReferencesCache);
        }

        internal sealed override void VerifySortRegion(ref TypeBatch typeBatch, int bundleStartIndex, int constraintCount, ref Buffer<int> sortedKeys, ref Buffer<int> sortedSourceIndices)
        {
            VerifySortRegion<ThreeBodySortKeyGenerator>(ref typeBatch, bundleStartIndex, constraintCount, ref sortedKeys, ref sortedSourceIndices);
        }

        public unsafe override void Prestep(ref TypeBatch typeBatch, Bodies bodies, float dt, float inverseDt, int startBundle, int exclusiveEndBundle)
        {
            ref var prestepBase = ref Unsafe.AsRef<TPrestepData>(typeBatch.PrestepData.Memory);
            ref var bodyReferencesBase = ref Unsafe.AsRef<ThreeBodyReferences>(typeBatch.BodyReferences.Memory);
            ref var projectionBase = ref Unsafe.AsRef<TProjection>(typeBatch.Projection.Memory);
            var function = default(TConstraintFunctions);
            for (int i = startBundle; i < exclusiveEndBundle; ++i)
            {
                ref var prestep = ref Unsafe.Add(ref prestepBase, i);
                ref var projection = ref Unsafe.Add(ref projectionBase, i);
                ref var references = ref Unsafe.Add(ref bodyReferencesBase, i);
                var count = GetCountInBundle(ref typeBatch, i);
                bodies.GatherState(ref references, count,
                    out var orientationA, out var wsvA, out var inertiaA,
                    out var ab, out var orientationB, out var wsvB, out var inertiaB,
                    out var ac, out var orientationC, out var wsvC, out var inertiaC);
                function.Prestep(orientationA, inertiaA, ab, orientationB, inertiaB, ac, orientationC, inertiaC, dt, inverseDt, ref prestep, out projection);
            }
        }

        public unsafe override void WarmStart(ref TypeBatch typeBatch, Bodies bodies, int startBundle, int exclusiveEndBundle)
        {
            ref var bodyReferencesBase = ref Unsafe.AsRef<ThreeBodyReferences>(typeBatch.BodyReferences.Memory);
            ref var accumulatedImpulsesBase = ref Unsafe.AsRef<TAccumulatedImpulse>(typeBatch.AccumulatedImpulses.Memory);
            ref var projectionBase = ref Unsafe.AsRef<TProjection>(typeBatch.Projection.Memory);
            var function = default(TConstraintFunctions);
            for (int i = startBundle; i < exclusiveEndBundle; ++i)
            {
                ref var projection = ref Unsafe.Add(ref projectionBase, i);
                ref var accumulatedImpulses = ref Unsafe.Add(ref accumulatedImpulsesBase, i);
                ref var bodyReferences = ref Unsafe.Add(ref bodyReferencesBase, i);
                int count = GetCountInBundle(ref typeBatch, i);
                bodies.GatherState(ref bodyReferences, count,
                    out var orientationA, out var wsvA, out var inertiaA,
                    out var ab, out var orientationB, out var wsvB, out var inertiaB,
                    out var ac, out var orientationC, out var wsvC, out var inertiaC);
                function.WarmStart(ref wsvA, ref wsvB, ref wsvC, ref projection, ref accumulatedImpulses);
                bodies.ScatterVelocities(ref wsvA, ref wsvB, ref wsvC, ref bodyReferences, count);

            }
        }

        public unsafe override void SolveIteration(ref TypeBatch typeBatch, Bodies bodies, int startBundle, int exclusiveEndBundle)
        {
            ref var bodyReferencesBase = ref Unsafe.AsRef<ThreeBodyReferences>(typeBatch.BodyReferences.Memory);
            ref var accumulatedImpulsesBase = ref Unsafe.AsRef<TAccumulatedImpulse>(typeBatch.AccumulatedImpulses.Memory);
            ref var projectionBase = ref Unsafe.AsRef<TProjection>(typeBatch.Projection.Memory);
            var function = default(TConstraintFunctions);
            for (int i = startBundle; i < exclusiveEndBundle; ++i)
            {
                ref var projection = ref Unsafe.Add(ref projectionBase, i);
                ref var accumulatedImpulses = ref Unsafe.Add(ref accumulatedImpulsesBase, i);
                ref var bodyReferences = ref Unsafe.Add(ref bodyReferencesBase, i);
                int count = GetCountInBundle(ref typeBatch, i);
                bodies.GatherState(ref bodyReferences, count,
                    out var orientationA, out var wsvA, out var inertiaA,
                    out var ab, out var orientationB, out var wsvB, out var inertiaB,
                    out var ac, out var orientationC, out var wsvC, out var inertiaC);
                function.Solve(ref wsvA, ref wsvB, ref wsvC, ref projection, ref accumulatedImpulses);
                bodies.ScatterVelocities(ref wsvA, ref wsvB, ref wsvC, ref bodyReferences, count);
            }
        }

        public unsafe override void JacobiPrestep(ref TypeBatch typeBatch, Bodies bodies, ref FallbackBatch jacobiBatch, float dt, float inverseDt, int startBundle, int exclusiveEndBundle)
        {
            ref var prestepBase = ref Unsafe.AsRef<TPrestepData>(typeBatch.PrestepData.Memory);
            ref var bodyReferencesBase = ref Unsafe.AsRef<ThreeBodyReferences>(typeBatch.BodyReferences.Memory);
            ref var projectionBase = ref Unsafe.AsRef<TProjection>(typeBatch.Projection.Memory);
            var function = default(TConstraintFunctions);
            for (int i = startBundle; i < exclusiveEndBundle; ++i)
            {
                ref var prestep = ref Unsafe.Add(ref prestepBase, i);
                ref var projection = ref Unsafe.Add(ref projectionBase, i);
                ref var references = ref Unsafe.Add(ref bodyReferencesBase, i);
                var count = GetCountInBundle(ref typeBatch, i);
                bodies.GatherState(ref references, count,
                    out var orientationA, out var wsvA, out var inertiaA,
                    out var ab, out var orientationB, out var wsvB, out var inertiaB,
                    out var ac, out var orientationC, out var wsvC, out var inertiaC);
                //Jacobi batches split affected bodies into multiple pieces to guarantee convergence.
                jacobiBatch.GetJacobiScaleForBodies(ref references, count, out var jacobiScaleA, out var jacobiScaleB, out var jacobiScaleC);
                Symmetric3x3Wide.Scale(inertiaA.InverseInertiaTensor, jacobiScaleA, out inertiaA.InverseInertiaTensor);
                inertiaA.InverseMass *= jacobiScaleA;
                Symmetric3x3Wide.Scale(inertiaB.InverseInertiaTensor, jacobiScaleB, out inertiaB.InverseInertiaTensor);
                inertiaB.InverseMass *= jacobiScaleB;
                Symmetric3x3Wide.Scale(inertiaC.InverseInertiaTensor, jacobiScaleC, out inertiaC.InverseInertiaTensor);
                inertiaC.InverseMass *= jacobiScaleC;
                function.Prestep(orientationA, inertiaA, ab, orientationB, inertiaB, ac, orientationC, inertiaC, dt, inverseDt, ref prestep, out projection);
            }
        }
        public unsafe override void JacobiWarmStart(ref TypeBatch typeBatch, Bodies bodies, ref FallbackTypeBatchResults jacobiResults, int startBundle, int exclusiveEndBundle)
        {
            ref var bodyReferencesBase = ref Unsafe.AsRef<ThreeBodyReferences>(typeBatch.BodyReferences.Memory);
            ref var accumulatedImpulsesBase = ref Unsafe.AsRef<TAccumulatedImpulse>(typeBatch.AccumulatedImpulses.Memory);
            ref var projectionBase = ref Unsafe.AsRef<TProjection>(typeBatch.Projection.Memory);
            var function = default(TConstraintFunctions);
            ref var jacobiResultsBundlesA = ref jacobiResults.GetVelocitiesForBody(0);
            ref var jacobiResultsBundlesB = ref jacobiResults.GetVelocitiesForBody(1);
            ref var jacobiResultsBundlesC = ref jacobiResults.GetVelocitiesForBody(2);
            for (int i = startBundle; i < exclusiveEndBundle; ++i)
            {
                ref var projection = ref Unsafe.Add(ref projectionBase, i);
                ref var accumulatedImpulses = ref Unsafe.Add(ref accumulatedImpulsesBase, i);
                ref var bodyReferences = ref Unsafe.Add(ref bodyReferencesBase, i);
                int count = GetCountInBundle(ref typeBatch, i);
                ref var wsvA = ref jacobiResultsBundlesA[i];
                ref var wsvB = ref jacobiResultsBundlesB[i];
                ref var wsvC = ref jacobiResultsBundlesC[i];
                bodies.GatherState(ref bodyReferences, count,
                    out var orientationA, out wsvA, out var inertiaA,
                    out var ab, out var orientationB, out wsvB, out var inertiaB,
                    out var ac, out var orientationC, out wsvC, out var inertiaC);
                function.WarmStart(ref wsvA, ref wsvB, ref wsvC, ref projection, ref accumulatedImpulses);
            }
        }
        public unsafe override void JacobiSolveIteration(ref TypeBatch typeBatch, Bodies bodies, ref FallbackTypeBatchResults jacobiResults, int startBundle, int exclusiveEndBundle)
        {
            ref var bodyReferencesBase = ref Unsafe.AsRef<ThreeBodyReferences>(typeBatch.BodyReferences.Memory);
            ref var accumulatedImpulsesBase = ref Unsafe.AsRef<TAccumulatedImpulse>(typeBatch.AccumulatedImpulses.Memory);
            ref var projectionBase = ref Unsafe.AsRef<TProjection>(typeBatch.Projection.Memory);
            var function = default(TConstraintFunctions);
            ref var jacobiResultsBundlesA = ref jacobiResults.GetVelocitiesForBody(0);
            ref var jacobiResultsBundlesB = ref jacobiResults.GetVelocitiesForBody(1);
            ref var jacobiResultsBundlesC = ref jacobiResults.GetVelocitiesForBody(2);
            for (int i = startBundle; i < exclusiveEndBundle; ++i)
            {
                ref var projection = ref Unsafe.Add(ref projectionBase, i);
                ref var accumulatedImpulses = ref Unsafe.Add(ref accumulatedImpulsesBase, i);
                ref var bodyReferences = ref Unsafe.Add(ref bodyReferencesBase, i);
                int count = GetCountInBundle(ref typeBatch, i);
                ref var wsvA = ref jacobiResultsBundlesA[i];
                ref var wsvB = ref jacobiResultsBundlesB[i];
                ref var wsvC = ref jacobiResultsBundlesC[i];
                bodies.GatherState(ref bodyReferences, count,
                    out var orientationA, out wsvA, out var inertiaA,
                    out var ab, out var orientationB, out wsvB, out var inertiaB,
                    out var ac, out var orientationC, out wsvC, out var inertiaC);
                function.Solve(ref wsvA, ref wsvB, ref wsvC, ref projection, ref accumulatedImpulses);
            }
        }


        public unsafe override void SolveStep(ref TypeBatch typeBatch, Bodies bodies, float dt, float inverseDt, int startBundle, int exclusiveEndBundle)
        {
            ref var prestepBase = ref Unsafe.AsRef<TPrestepData>(typeBatch.PrestepData.Memory);
            ref var bodyReferencesBase = ref Unsafe.AsRef<ThreeBodyReferences>(typeBatch.BodyReferences.Memory);
            ref var accumulatedImpulsesBase = ref Unsafe.AsRef<TAccumulatedImpulse>(typeBatch.AccumulatedImpulses.Memory);
            ref var projectionBase = ref Unsafe.AsRef<TProjection>(typeBatch.Projection.Memory);
            var function = default(TConstraintFunctions);
            for (int i = startBundle; i < exclusiveEndBundle; ++i)
            {
                ref var prestep = ref Unsafe.Add(ref prestepBase, i);
                ref var accumulatedImpulses = ref Unsafe.Add(ref accumulatedImpulsesBase, i);
                ref var bodyReferences = ref Unsafe.Add(ref bodyReferencesBase, i);
                ref var references = ref Unsafe.Add(ref bodyReferencesBase, i);
                var count = GetCountInBundle(ref typeBatch, i);
                bodies.GatherState(ref references, count,
                    out var orientationA, out var wsvA, out var inertiaA,
                    out var ab, out var orientationB, out var wsvB, out var inertiaB,
                    out var ac, out var orientationC, out var wsvC, out var inertiaC);
                function.Prestep(orientationA, inertiaA, ab, orientationB, inertiaB, ac, orientationC, inertiaC, dt, inverseDt, ref prestep, out var projection);
                function.WarmStart(ref wsvA, ref wsvB, ref wsvC, ref projection, ref accumulatedImpulses);
                function.Solve(ref wsvA, ref wsvB, ref wsvC, ref projection, ref accumulatedImpulses);
                bodies.ScatterVelocities(ref wsvA, ref wsvB, ref wsvC, ref bodyReferences, count);
            }
        }

        public unsafe override void JacobiSolveStep(ref TypeBatch typeBatch, Bodies bodies, ref FallbackBatch jacobiBatch, ref FallbackTypeBatchResults jacobiResults, float dt, float inverseDt, int startBundle, int exclusiveEndBundle)
        {
            ref var prestepBase = ref Unsafe.AsRef<TPrestepData>(typeBatch.PrestepData.Memory);
            ref var bodyReferencesBase = ref Unsafe.AsRef<ThreeBodyReferences>(typeBatch.BodyReferences.Memory);
            ref var accumulatedImpulsesBase = ref Unsafe.AsRef<TAccumulatedImpulse>(typeBatch.AccumulatedImpulses.Memory);
            ref var projectionBase = ref Unsafe.AsRef<TProjection>(typeBatch.Projection.Memory);
            var function = default(TConstraintFunctions);
            ref var jacobiResultsBundlesA = ref jacobiResults.GetVelocitiesForBody(0);
            ref var jacobiResultsBundlesB = ref jacobiResults.GetVelocitiesForBody(1);
            ref var jacobiResultsBundlesC = ref jacobiResults.GetVelocitiesForBody(2);
            for (int i = startBundle; i < exclusiveEndBundle; ++i)
            {
                ref var prestep = ref Unsafe.Add(ref prestepBase, i);
                ref var accumulatedImpulses = ref Unsafe.Add(ref accumulatedImpulsesBase, i);
                ref var bodyReferences = ref Unsafe.Add(ref bodyReferencesBase, i);
                ref var references = ref Unsafe.Add(ref bodyReferencesBase, i);
                var count = GetCountInBundle(ref typeBatch, i);
                ref var wsvA = ref jacobiResultsBundlesA[i];
                ref var wsvB = ref jacobiResultsBundlesB[i];
                ref var wsvC = ref jacobiResultsBundlesC[i];
                bodies.GatherState(ref references, count,
                    out var orientationA, out wsvA, out var inertiaA,
                    out var ab, out var orientationB, out wsvB, out var inertiaB,
                    out var ac, out var orientationC, out wsvC, out var inertiaC);
                //Jacobi batches split affected bodies into multiple pieces to guarantee convergence.
                jacobiBatch.GetJacobiScaleForBodies(ref references, count, out var jacobiScaleA, out var jacobiScaleB, out var jacobiScaleC);
                Symmetric3x3Wide.Scale(inertiaA.InverseInertiaTensor, jacobiScaleA, out inertiaA.InverseInertiaTensor);
                inertiaA.InverseMass *= jacobiScaleA;
                Symmetric3x3Wide.Scale(inertiaB.InverseInertiaTensor, jacobiScaleB, out inertiaB.InverseInertiaTensor);
                inertiaB.InverseMass *= jacobiScaleB;
                Symmetric3x3Wide.Scale(inertiaC.InverseInertiaTensor, jacobiScaleC, out inertiaC.InverseInertiaTensor);
                inertiaC.InverseMass *= jacobiScaleC;
                function.Prestep(orientationA, inertiaA, ab, orientationB, inertiaB, ac, orientationC, inertiaC, dt, inverseDt, ref prestep, out var projection);
                function.WarmStart(ref wsvA, ref wsvB, ref wsvC, ref projection, ref accumulatedImpulses);
                function.Solve(ref wsvA, ref wsvB, ref wsvC, ref projection, ref accumulatedImpulses);
            }
        }

        public override void WarmStart2<TIntegratorCallbacks, TBatchIntegrationMode>(ref TypeBatch typeBatch, ref Buffer<IndexSet> integrationFlags, Bodies bodies, ref TIntegratorCallbacks poseIntegratorCallbacks, 
            float dt, float inverseDt, int startBundle, int exclusiveEndBundle, int workerIndex)
        {
            throw new NotImplementedException();
        }
        public override void SolveStep2(ref TypeBatch typeBatch, Bodies bodies, float dt, float inverseDt, int startBundle, int exclusiveEndBundle)
        {
            throw new NotImplementedException();
        }

        public override void Prestep2(ref TypeBatch typeBatch, Bodies bodies, float dt, float inverseDt, int startBundle, int exclusiveEndBundle)
        {
            throw new NotImplementedException();
        }
        public override void JacobiPrestep2(ref TypeBatch typeBatch, Bodies bodies, ref FallbackBatch jacobiBatch, ref FallbackTypeBatchResults jacobiResults, float dt, float inverseDt, int startBundle, int exclusiveEndBundle)
        {
            throw new NotImplementedException();
        }
    }
}
