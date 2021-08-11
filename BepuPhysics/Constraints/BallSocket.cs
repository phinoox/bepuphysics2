﻿using BepuPhysics.CollisionDetection;
using BepuUtilities;
using BepuUtilities.Memory;
using System;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using static BepuUtilities.GatherScatter;
namespace BepuPhysics.Constraints
{
    /// <summary>
    /// Constrains a point on one body to a point on another body.
    /// </summary>
    public struct BallSocket : ITwoBodyConstraintDescription<BallSocket>
    {
        /// <summary>
        /// Local offset from the center of body A to its attachment point.
        /// </summary>
        public Vector3 LocalOffsetA;
        /// <summary>
        /// Local offset from the center of body B to its attachment point.
        /// </summary>
        public Vector3 LocalOffsetB;
        /// <summary>
        /// Spring frequency and damping parameters.
        /// </summary>
        public SpringSettings SpringSettings;

        public readonly int ConstraintTypeId
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                return BallSocketTypeProcessor.BatchTypeId;
            }
        }

        public readonly Type TypeProcessorType => typeof(BallSocketTypeProcessor);

        public readonly void ApplyDescription(ref TypeBatch batch, int bundleIndex, int innerIndex)
        {
            ConstraintChecker.AssertValid(SpringSettings, nameof(BallSocket));
            Debug.Assert(ConstraintTypeId == batch.TypeId, "The type batch passed to the description must match the description's expected type.");
            ref var target = ref GetOffsetInstance(ref Buffer<BallSocketPrestepData>.Get(ref batch.PrestepData, bundleIndex), innerIndex);
            Vector3Wide.WriteFirst(LocalOffsetA, ref target.LocalOffsetA);
            Vector3Wide.WriteFirst(LocalOffsetB, ref target.LocalOffsetB);
            SpringSettingsWide.WriteFirst(SpringSettings, ref target.SpringSettings);
        }

        public readonly void BuildDescription(ref TypeBatch batch, int bundleIndex, int innerIndex, out BallSocket description)
        {
            Debug.Assert(ConstraintTypeId == batch.TypeId, "The type batch passed to the description must match the description's expected type.");
            ref var source = ref GetOffsetInstance(ref Buffer<BallSocketPrestepData>.Get(ref batch.PrestepData, bundleIndex), innerIndex);
            Vector3Wide.ReadFirst(source.LocalOffsetA, out description.LocalOffsetA);
            Vector3Wide.ReadFirst(source.LocalOffsetB, out description.LocalOffsetB);
            SpringSettingsWide.ReadFirst(source.SpringSettings, out description.SpringSettings);
        }
    }

    public struct BallSocketPrestepData
    {
        public Vector3Wide LocalOffsetA;
        public Vector3Wide LocalOffsetB;
        public SpringSettingsWide SpringSettings;
    }

    public struct BallSocketProjection
    {
        public Vector3Wide OffsetA;
        public Vector3Wide OffsetB;
        public Vector3Wide BiasVelocity;
        public Symmetric3x3Wide EffectiveMass;
        public Vector<float> SoftnessImpulseScale;
        public BodyInertiaWide InertiaA;
        public BodyInertiaWide InertiaB;
    }

    public struct BallSocketFunctions : IConstraintFunctions<BallSocketPrestepData, BallSocketProjection, Vector3Wide>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Prestep(in QuaternionWide orientationA, in BodyInertiaWide inertiaA, in Vector3Wide ab, in QuaternionWide orientationB, in BodyInertiaWide inertiaB,
            float dt, float inverseDt, ref BallSocketPrestepData prestep, out BallSocketProjection projection)
        {
            projection.InertiaA = inertiaA;
            projection.InertiaB = inertiaB;

            //Note that we must reconstruct the world offsets from the body orientations since we do not store world offsets.
            QuaternionWide.TransformWithoutOverlap(prestep.LocalOffsetA, orientationA, out projection.OffsetA);
            QuaternionWide.TransformWithoutOverlap(prestep.LocalOffsetB, orientationB, out projection.OffsetB);
            SpringSettingsWide.ComputeSpringiness(prestep.SpringSettings, dt, out var positionErrorToVelocity, out var effectiveMassCFMScale, out projection.SoftnessImpulseScale);
            BallSocketShared.ComputeEffectiveMass(inertiaA, inertiaB, ref projection.OffsetA, ref projection.OffsetB, ref effectiveMassCFMScale, out projection.EffectiveMass);

            //Compute the position error and bias velocities. Note the order of subtraction when calculating error- we want the bias velocity to counteract the separation.
            Vector3Wide.Add(ab, projection.OffsetB, out var anchorB);
            Vector3Wide.Subtract(anchorB, projection.OffsetA, out var error);
            Vector3Wide.Scale(error, positionErrorToVelocity, out projection.BiasVelocity);
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void WarmStart(ref BodyVelocityWide velocityA, ref BodyVelocityWide velocityB, ref BallSocketProjection projection, ref Vector3Wide accumulatedImpulse)
        {
            BallSocketShared.ApplyImpulse(ref velocityA, ref velocityB, projection.OffsetA, projection.OffsetB, projection.InertiaA, projection.InertiaB, accumulatedImpulse);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Solve(ref BodyVelocityWide velocityA, ref BodyVelocityWide velocityB, ref BallSocketProjection projection, ref Vector3Wide accumulatedImpulse)
        {
            BallSocketShared.Solve(ref velocityA, ref velocityB, projection.OffsetA, projection.OffsetB, projection.BiasVelocity, projection.EffectiveMass, projection.SoftnessImpulseScale, ref accumulatedImpulse, projection.InertiaA, projection.InertiaB);
        }

        public void WarmStart2(in QuaternionWide orientationA, in BodyInertiaWide inertiaA, in Vector3Wide ab, in QuaternionWide orientationB, in BodyInertiaWide inertiaB,
            in BallSocketPrestepData prestep, in Vector3Wide accumulatedImpulses, ref BodyVelocityWide wsvA, ref BodyVelocityWide wsvB)
        {
            //Note that we must reconstruct the world offsets from the body orientations since we do not store world offsets.
            QuaternionWide.TransformWithoutOverlap(prestep.LocalOffsetA, orientationA, out var offsetA);
            QuaternionWide.TransformWithoutOverlap(prestep.LocalOffsetB, orientationB, out var offsetB);
            BallSocketShared.ApplyImpulse(ref wsvA, ref wsvB, offsetA, offsetB, inertiaA, inertiaB, accumulatedImpulses);
        }

        public void Solve2(in QuaternionWide orientationA, in BodyInertiaWide inertiaA, in Vector3Wide ab, in QuaternionWide orientationB, in BodyInertiaWide inertiaB, float dt, float inverseDt, 
            in BallSocketPrestepData prestep, ref Vector3Wide accumulatedImpulses, ref BodyVelocityWide wsvA, ref BodyVelocityWide wsvB)
        {    
            //Note that we must reconstruct the world offsets from the body orientations since we do not store world offsets.
            QuaternionWide.TransformWithoutOverlap(prestep.LocalOffsetA, orientationA, out var offsetA);
            QuaternionWide.TransformWithoutOverlap(prestep.LocalOffsetB, orientationB, out var offsetB);
            SpringSettingsWide.ComputeSpringiness(prestep.SpringSettings, dt, out var positionErrorToVelocity, out var effectiveMassCFMScale, out var softnessImpulseScale);
            BallSocketShared.ComputeEffectiveMass(inertiaA, inertiaB, ref offsetA, ref offsetB, ref effectiveMassCFMScale, out var effectiveMass);

            //Compute the position error and bias velocities. Note the order of subtraction when calculating error- we want the bias velocity to counteract the separation.
            Vector3Wide.Add(ab, offsetB, out var anchorB);
            Vector3Wide.Subtract(anchorB, offsetA, out var error);
            Vector3Wide.Scale(error, positionErrorToVelocity, out var biasVelocity);

            BallSocketShared.Solve(ref wsvA, ref wsvB, offsetA, offsetB, biasVelocity, effectiveMass, softnessImpulseScale, ref accumulatedImpulses, inertiaA, inertiaB);

        }
    }


    /// <summary>
    /// Handles the solve iterations of a bunch of ball socket constraints.
    /// </summary>
    public class BallSocketTypeProcessor : TwoBodyTypeProcessor<BallSocketPrestepData, BallSocketProjection, Vector3Wide, BallSocketFunctions>
    {
        public const int BatchTypeId = 22;
    }
}
